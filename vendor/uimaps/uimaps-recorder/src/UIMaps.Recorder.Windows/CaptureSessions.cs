using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed class CaptureSessionManager(
    EpisodeCaptureService captureService,
    CaptureProfileCatalogService profiles,
    CapturePolicyResolver resolver)
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public CaptureProfileCatalog GetProfiles() => profiles.GetCatalog();

    public CaptureSessionDescriptor Start(string displayId, CapturePolicyRequest request, ConsumerLimits consumer, CancellationToken hostToken)
    {
        request = request.Clone();
        request.RequestedScope ??= CapturePolicyResolver.PassiveScope(displayId);
        request.RequestedScope.SelectedDisplayId = displayId;
        var effective = resolver.Resolve(request, consumer);
        if (!_sessionGate.Wait(0)) throw new RecorderException("CAPTURE_SESSION_BUSY", "A capture session is already active.", true);
        var session = new Session(displayId, request, effective, hostToken);
        if (!_sessions.TryAdd(session.Descriptor.SessionId, session)) throw new InvalidOperationException("Unable to allocate capture session.");
        session.Work = RunAsync(session);
        return session.Descriptor.Clone();
    }

    public (CaptureSessionDescriptor Descriptor, CaptureProgress Progress) Get(string sessionId)
    {
        var session = Find(sessionId);
        lock (session.Sync) return (session.Descriptor.Clone(), session.Progress.Clone());
    }

    public string Cancel(string sessionId)
    {
        var session = Find(sessionId);
        lock (session.Sync)
        {
            if (IsTerminal(session.Descriptor.State)) return session.Descriptor.State;
            session.Progress.CancellationRequested = true;
            session.Descriptor.State = "cancellation_requested";
            session.Progress.State = session.Descriptor.State;
            session.Cancellation.Cancel();
            return session.Descriptor.State;
        }
    }

    public SensorEpisodeBundle Result(string sessionId)
    {
        var session = Find(sessionId);
        lock (session.Sync)
        {
            if (session.Bundle is null) throw new RecorderException("CAPTURE_RESULT_NOT_READY", "Capture result is not finalized yet.", true);
            return session.Bundle.Clone();
        }
    }

    public IReadOnlyList<CaptureSegmentDescriptor> Segments(string sessionId)
    {
        var session = Find(sessionId);
        lock (session.Sync) return session.Segments.Select(x => x.Descriptor.Clone()).ToList();
    }

    public (CaptureSegmentDescriptor Descriptor, byte[] Payload) ReadSegment(string sessionId, string segmentId)
    {
        var session = Find(sessionId);
        lock (session.Sync)
        {
            var segment = session.Segments.SingleOrDefault(x => x.Descriptor.SegmentId == segmentId)
                ?? throw new RecorderException("CAPTURE_SEGMENT_NOT_FOUND", "Capture segment was not found.");
            return (segment.Descriptor.Clone(), segment.Bytes.ToArray());
        }
    }

    private async Task RunAsync(Session session)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Update(session, "running", "census", 1);
            var bundle = await captureService.CaptureAsync(session.DisplayId, session.Request, session.EffectivePolicy, session.Cancellation.Token, stage =>
            {
                lock (session.Sync)
                {
                    session.Progress.SequenceNumber++; session.Progress.CurrentPass = stage.Pass;
                    session.Progress.CatalogedWindows = stage.CatalogedWindows; session.Progress.EligibleWindows = stage.EligibleWindows;
                    session.Progress.WindowsTerminal = stage.WindowsTerminal; session.Progress.NodesIncluded = stage.NodesIncluded;
                    session.Progress.ElapsedMs = started.ElapsedMilliseconds;
                }
            }).ConfigureAwait(false);
            var cancellation = session.Cancellation.IsCancellationRequested;
            var budget = !cancellation && bundle.SensorPackets.Any(x => x.Health.Issues.Any(i => i.Code == "RECORDER_PROVIDER_CANCELLED"));
            var state = cancellation ? "cancelled_partial" : budget ? "budget_exhausted_partial" : EpisodeCaptureService.TerminalStateFor(bundle.CoverageReport);
            bundle.SessionTerminalState = state;
            bundle.CoverageReport.SessionTerminalState = state;
            if (cancellation) bundle.CoverageReport.ConvergenceState = ConvergenceState.ConvergenceCancelled;
            if (budget) bundle.CoverageReport.ConvergenceState = ConvergenceState.ConvergenceBudgetExhausted;
            var segments = BuildSegments(bundle, session.EffectivePolicy.MaxBytesPerSegment);
            bundle.SegmentDescriptors.Clear();
            bundle.SegmentDescriptors.AddRange(segments.Select(x => x.Descriptor.Clone()));
            lock (session.Sync)
            {
                session.Bundle = bundle.Clone(); session.Segments.AddRange(segments);
                session.Descriptor.State = state; session.Progress.State = state;
                session.Progress.CurrentPass = "finalized"; session.Progress.SequenceNumber++;
                CopyCoverage(session.Progress, bundle.CoverageReport);
                session.Progress.ElapsedMs = started.ElapsedMilliseconds;
                session.Progress.SegmentCount = segments.Count;
                session.Progress.CheckpointCount = session.EffectivePolicy.CheckpointPolicy?.Enabled == true ? Math.Max(1, bundle.CoverageReport.PassCount) : 0;
            }
        }
        catch (OperationCanceledException)
        {
            lock (session.Sync) { session.Descriptor.State = "cancelled_without_evidence"; session.Progress.State = session.Descriptor.State; }
        }
        catch (RecorderException ex)
        {
            lock (session.Sync) { session.Descriptor.State = "failed:" + ex.Code; session.Progress.State = session.Descriptor.State; }
        }
        catch
        {
            lock (session.Sync) { session.Descriptor.State = "failed:RECORDER_INTERNAL_ERROR"; session.Progress.State = session.Descriptor.State; }
        }
        finally
        {
            session.Cancellation.Dispose();
            _sessionGate.Release();
        }
    }

    private static List<Segment> BuildSegments(SensorEpisodeBundle bundle, long limit)
    {
        var bytes = bundle.ToByteArray();
        var size = (int)Math.Clamp(limit, 64 * 1024, 16L * 1024 * 1024);
        var result = new List<Segment>();
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var payload = bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)).ToArray();
            var descriptor = new CaptureSegmentDescriptor
            {
                SegmentId = Guid.NewGuid().ToString("N"), EpisodeId = bundle.EpisodeId,
                SequenceNumber = (ulong)result.Count, ByteLength = payload.LongLength,
                Checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                PacketTimeRange = $"{bundle.StartedAtUtc}/{bundle.EndedAtUtc}", CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            result.Add(new Segment(descriptor, payload));
        }
        return result;
    }

    private static void Update(Session session, string state, string pass, ulong sequence)
    {
        lock (session.Sync)
        {
            session.Descriptor.State = state; session.Progress.State = state;
            session.Progress.CurrentPass = pass; session.Progress.SequenceNumber = sequence;
        }
    }

    private static void CopyCoverage(CaptureProgress progress, CoverageReport report)
    {
        progress.CatalogedWindows = report.CatalogedWindows; progress.EligibleWindows = report.EligibleWindows;
        progress.WindowsCompleted = report.WindowsCompleted; progress.WindowsTerminal = report.WindowOutcomes.Count;
        progress.NodesObserved = report.NodesObserved; progress.NodesIncluded = report.NodesIncluded;
        progress.CurrentConvergenceState = report.ConvergenceState;
    }

    private Session Find(string id) => _sessions.TryGetValue(id, out var value) ? value : throw new RecorderException("CAPTURE_SESSION_NOT_FOUND", "Capture session was not found.");
    private static bool IsTerminal(string state) => state == "completed" || state.EndsWith("_partial", StringComparison.Ordinal) || state == "cancelled_without_evidence" || state.StartsWith("failed:", StringComparison.Ordinal);

    private sealed class Session
    {
        public Session(string displayId, CapturePolicyRequest request, EffectiveCapturePolicy effective, CancellationToken hostToken)
        {
            DisplayId = displayId; Request = request; EffectivePolicy = effective; HostToken = hostToken;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            Descriptor = new CaptureSessionDescriptor
            {
                SessionId = Guid.NewGuid().ToString("N"), EpisodeId = Guid.NewGuid().ToString("N"), SelectedDisplayId = displayId,
                RequestedProfileRef = effective.RequestedProfileRef.Clone(), EffectivePolicy = effective.Clone(), State = "starting", StartedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            Progress = new CaptureProgress { SessionId = Descriptor.SessionId, State = "starting", HardBudgetMs = effective.HardWallClockBudgetMs, CurrentConvergenceState = ConvergenceState.ConvergenceInProgress };
        }
        public object Sync { get; } = new(); public string DisplayId { get; } public CapturePolicyRequest Request { get; }
        public EffectiveCapturePolicy EffectivePolicy { get; } public CancellationToken HostToken { get; }
        public CancellationTokenSource Cancellation { get; } public CaptureSessionDescriptor Descriptor { get; }
        public CaptureProgress Progress { get; } public SensorEpisodeBundle? Bundle { get; set; }
        public List<Segment> Segments { get; } = []; public Task? Work { get; set; }
    }

    private sealed record Segment(CaptureSegmentDescriptor Descriptor, byte[] Bytes);
}
