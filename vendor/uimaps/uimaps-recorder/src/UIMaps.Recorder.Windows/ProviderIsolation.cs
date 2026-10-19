using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed record ProbeRequest(
    string Operation,
    int X,
    int Y,
    int Width,
    int Height,
    long WindowHandle,
    string WindowEvidenceId,
    int MaxNodes,
    int MaxDepth,
    int MaxWindows);

public interface IProviderProcessRunner
{
    Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class, IMessage<T>;
}

public sealed class ProviderProcessRunner(string executablePath) : IProviderProcessRunner
{
    public async Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class, IMessage<T>
    {
        var json = JsonSerializer.Serialize(request);
        var argument = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var start = new ProcessStartInfo
        {
            FileName = Path.GetExtension(executablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (Path.GetExtension(executablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(executablePath);
        start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new RecorderException("RECORDER_PROVIDER_FAILED", "Unable to start isolated provider worker.");
        using var containment = WorkerJob.Attach(process);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new RecorderException("RECORDER_PROVIDER_FAILED", string.IsNullOrWhiteSpace(error) ? "Isolated provider failed." : Sanitize(error));
            }

            var bytes = Convert.FromBase64String(output.Trim());
            return parser.ParseFrom(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            containment.Terminate();
            TryKill(process);
            throw new RecorderException("RECORDER_PROVIDER_TIMEOUT", "Isolated provider exceeded its deadline.", true);
        }
        catch (OperationCanceledException)
        {
            containment.Terminate();
            TryKill(process);
            throw;
        }
        catch (FormatException ex)
        {
            TryKill(process);
            throw new RecorderException("RECORDER_PROVIDER_FAILED", "Isolated provider returned an invalid response.", false) { Source = ex.Source };
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim() is { Length: > 512 } longValue ? longValue[..512] : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
internal sealed class WorkerJob : IDisposable
{
    private IntPtr _handle;
    private WorkerJob(IntPtr handle) => _handle = handle;

    public static WorkerJob Attach(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new RecorderException("RECORDER_WORKER_CONTAINMENT_FAILED", "Unable to create provider containment boundary.");
        var job = new WorkerJob(handle);
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x00002000u | 0x00000100u | 0x00000008u,
                    ActiveProcessLimit = 8,
                },
                ProcessMemoryLimit = new UIntPtr(512UL * 1024 * 1024),
            };
            Set(handle, 9, limits);
            var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { ControlFlags = 0x1u | 0x4u, CpuRate = 5_000 };
            Set(handle, 15, cpu);
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch
        {
            job.Terminate(); job.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new RecorderException("RECORDER_WORKER_CONTAINMENT_FAILED", "Unable to assign provider worker to its containment boundary.");
        }
    }

    public void Terminate() { if (_handle != IntPtr.Zero) _ = TerminateJobObject(_handle, 1); }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) _ = CloseHandle(handle);
    }

    private static void Set<T>(IntPtr handle, int informationClass, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            if (!SetInformationJobObject(handle, informationClass, pointer, (uint)size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public long Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags, CpuRate; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}

public sealed class IsolatedScreenCaptureProvider(IProviderProcessRunner runner) : IScreenCaptureProvider
{
    public Task<ScreenshotPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken) =>
        runner.RunAsync(
            new ProbeRequest("screen", display.BoundsPx.X, display.BoundsPx.Y, display.WidthPx, display.HeightPx, 0, string.Empty, 0, 0, 0),
            ScreenshotPacket.Parser,
            policy.ScreenCaptureDeadline,
            cancellationToken);
}

public sealed class IsolatedWindowTopologyProvider(IProviderProcessRunner runner) : IWindowTopologyProvider
{
    public async Task<WindowTopologyPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken)
    {
        // Always census the native top-level set before applying a bounded
        // profile. Otherwise an arbitrary EnumWindows/Z-order prefix can omit
        // the explicitly targeted window before target priority is evaluated.
        WindowTopologyPacket packet;
        try
        {
            packet = await runner.RunAsync(
                new ProbeRequest("windows", display.BoundsPx.X, display.BoundsPx.Y, display.WidthPx, display.HeightPx, 0, string.Empty, 0, 0, int.MaxValue),
                WindowTopologyPacket.Parser, policy.WindowTopologyDeadline, cancellationToken).ConfigureAwait(false);
        }
        catch (RecorderException error) when (error.Code == "RECORDER_PROVIDER_TIMEOUT"
            && policy.TargetWindowHandleEvidence.Count == 1
            && !string.IsNullOrWhiteSpace(policy.TargetProcessInstanceEvidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = policy.TargetWindowHandleEvidence.Single();
            if (!long.TryParse(handle, out var hwnd) || hwnd <= 0) throw;
            packet = await runner.RunAsync(new ProbeRequest("target-window", display.BoundsPx.X, display.BoundsPx.Y,
                    display.WidthPx, display.HeightPx, hwnd, string.Empty, 0, 0, 1),
                WindowTopologyPacket.Parser, policy.WindowTopologyDeadline, cancellationToken).ConfigureAwait(false);
            // A reused handle, different process, hidden/minimized window, or a
            // failed fresh probe cannot stand in for the approved target.
            var target = packet.Windows.SingleOrDefault(window => window.WindowHandleEvidence == handle
                && window.ProcessInstanceEvidence == policy.TargetProcessInstanceEvidence
                && window.IsVisible && !window.IsMinimized && !window.IsCloakedOrUnavailable
                && window.SelectionEligibility == "eligible");
            packet.Windows.Clear();
            if (target is not null) packet.Windows.Add(target);
            packet.WindowsIncluded = packet.Windows.Count;
            packet.WindowLimited = true;
            packet.Health = ProtocolHealth.Single(HealthState.Partial, "RECORDER_WINDOW_ENUM_TARGET_RECOVERY",
                "Desktop window enumeration timed out. Only the explicitly requested window was freshly probed; desktop topology remains incomplete.", "window_topology");
            return packet;
        }
        var observedCount = packet.Windows.Count;
        var included = NativeWindowTopologyProvider.ApplyPolicy(packet.Windows, policy);
        packet.Windows.Clear();
        packet.Windows.AddRange(included);
        packet.WindowsIncluded = included.Count;
        packet.WindowLimited = included.Count < observedCount;
        if (packet.WindowLimited)
            packet.Health = ProtocolHealth.Single(HealthState.WindowLimited, "RECORDER_WINDOW_LIMIT_REACHED", "Window capture reached its configured limit after target-aware prioritization.", "window_topology");
        return packet;
    }
}

public sealed class IsolatedAccessibilityProbe(IProviderProcessRunner runner) : IAccessibilityProbe
{
    public async Task<AccessibilityPacket> CaptureAsync(IReadOnlyList<WindowObservationRecord> windows, RecorderPolicy policy, CancellationToken cancellationToken)
    {
        var aggregate = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
        using var aggregateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!policy.ExhaustiveCoverage)
            aggregateCts.CancelAfter(policy.UiaAggregateDeadline);
        using var gate = new SemaphoreSlim(policy.MaxConcurrentUiaWorkers);
        var processGates = windows.Select(x => x.ProcessInstanceEvidence).Distinct(StringComparer.Ordinal)
            .ToDictionary(x => x, _ => new SemaphoreSlim(policy.PerProcessConcurrency), StringComparer.Ordinal);
        var episodeNodesRemaining = policy.MaxNodesPerEpisode;
        var nodeBudgetLock = new object();
        var eligible = windows
            .Where(x => string.IsNullOrEmpty(x.SelectionEligibility) || x.SelectionEligibility == "eligible")
            .OrderByDescending(x => policy.TargetWindowHandleEvidence.Contains(x.WindowHandleEvidence))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(policy.TargetProcessInstanceEvidence)
                && string.Equals(x.ProcessInstanceEvidence, policy.TargetProcessInstanceEvidence, StringComparison.Ordinal))
            .ThenByDescending(x => x.IsForeground || x.IsActive)
            .ThenByDescending(x => x.IsVisible && !x.IsCloakedOrUnavailable)
            .ThenByDescending(x => (long)x.IntersectionWithDisplayPx.Width * x.IntersectionWithDisplayPx.Height)
            .ToList();

        var tasks = eligible.Select(async window =>
        {
            await gate.WaitAsync(aggregateCts.Token).ConfigureAwait(false);
            var processKey = string.IsNullOrWhiteSpace(window.ProcessInstanceEvidence) ? $"pid:{window.ProcessId}" : window.ProcessInstanceEvidence;
            if (!processGates.TryGetValue(processKey, out var processGate))
            {
                lock (processGates)
                {
                    if (!processGates.TryGetValue(processKey, out processGate)) processGates[processKey] = processGate = new SemaphoreSlim(policy.PerProcessConcurrency);
                }
            }
            await processGate.WaitAsync(aggregateCts.Token).ConfigureAwait(false);
            try
            {
                if (!long.TryParse(window.WindowHandleEvidence, out var hwnd))
                {
                    return ProtocolHealth.Single(HealthState.Invalid, "RECORDER_UIA_INVALID_WINDOW", "Window identity was not valid for accessibility capture.", "accessibility");
                }

                var captureWindowId = WindowId(window);
                var healthValues = new List<Health>();
                var reservedNodes = 0;
                lock (nodeBudgetLock)
                {
                    reservedNodes = policy.ExhaustiveCoverage
                        ? policy.MaxUiaNodes
                        : Math.Min(policy.MaxUiaNodes, episodeNodesRemaining);
                    if (!policy.ExhaustiveCoverage) episodeNodesRemaining -= reservedNodes;
                }
                if (reservedNodes == 0)
                {
                    lock (aggregate) aggregate.TraversalFrontier.Add(Frontier(window, "episode_node_limit"));
                    return ProtocolHealth.Single(HealthState.NodeLimited, "RECORDER_UIA_EPISODE_NODE_LIMIT", "The episode accessibility node ceiling was reached.", "accessibility");
                }

                var nativePacket = await CaptureNativeAsync(
                    window,
                    hwnd,
                    reservedNodes,
                    policy,
                    aggregateCts.Token).ConfigureAwait(false);
                healthValues.Add(nativePacket.Health.Clone());

                AccessibilityPacket? finalPacket = null;
                var rawResponses = new List<RawProviderResponse>();
                foreach (var nativeAttempt in nativePacket.ProviderAttempts)
                    rawResponses.Add(new() { Attempt = nativeAttempt.Clone(), WindowEvidenceId = window.WindowEvidenceId,
                        Packet = nativePacket.Clone() });
                for (var attemptNumber = 1; attemptNumber <= policy.MaxAttempts; attemptNumber++)
                {
                    var started = DateTimeOffset.UtcNow;
                    var attempt = new ProviderAttemptOutcome
                    {
                        AttemptId = Guid.NewGuid().ToString("N"), CaptureWindowId = captureWindowId, ProviderId = "uia",
                        WorkerVersion = "0.9.0", WorkerArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        StartedAt = started.ToString("O"),
                    };
                    try
                    {
                        var packet = await runner.RunAsync(
                            new ProbeRequest("uia", 0, 0, 0, 0, hwnd, window.WindowEvidenceId, reservedNodes, policy.MaxUiaDepth, 0),
                            AccessibilityPacket.Parser, policy.UiaPerWindowDeadline, aggregateCts.Token).ConfigureAwait(false);
                        attempt.EndedAt = DateTimeOffset.UtcNow.ToString("O"); attempt.Status = packet.Health.State == HealthState.Complete ? "completed" : "partial";
                        attempt.NodesObserved = packet.NodesObserved; attempt.NodesIncluded = packet.NodesIncluded;
                        attempt.PropertiesAttempted = packet.Nodes.Sum(x => x.PropertyCoverage?.Attempted ?? 0);
                        attempt.PropertiesRead = packet.Nodes.Sum(x => x.PropertyCoverage?.Read ?? 0);
                        attempt.PropertiesFailed = packet.Nodes.Sum(x => x.PropertyCoverage?.Failed ?? 0);
                        attempt.FrontierAdded = packet.TraversalFrontier.Count;
                        packet.ProviderAttempts.Add(attempt.Clone());
                        rawResponses.Add(new() { Attempt = attempt.Clone(), WindowEvidenceId = window.WindowEvidenceId,
                            Packet = packet.Clone() });
                        finalPacket = AccessibilityPacketMerger.Merge(packet, nativePacket, reservedNodes);
                        healthValues.Add(packet.Health.Clone());
                        break;
                    }
                    catch (RecorderException ex)
                    {
                        attempt.EndedAt = DateTimeOffset.UtcNow.ToString("O"); attempt.Retryable = ex.Retryable;
                        attempt.Status = ex.Code.Contains("TIMEOUT", StringComparison.Ordinal) ? "timed_out" : "provider_failed";
                        attempt.FailureClass = ex.Code.Contains("TIMEOUT", StringComparison.Ordinal) ? "timeout" : "provider_failure";
                        var failed = ProtocolHealth.Single(attempt.Status == "timed_out" ? HealthState.TimedOut : HealthState.ProviderFailed,
                            attempt.Status == "timed_out" ? "RECORDER_UIA_TIMEOUT" : "RECORDER_UIA_PROVIDER_FAILED",
                            attempt.Status == "timed_out" ? "Accessibility capture exceeded its per-window deadline." : "Accessibility provider failed for a window.", "accessibility", ex.Retryable);
                        healthValues.Add(failed);
                        rawResponses.Add(new() { Attempt = attempt.Clone(), WindowEvidenceId = window.WindowEvidenceId,
                            Packet = new AccessibilityPacket { Health = failed.Clone(), TimeLimited = attempt.Status == "timed_out" } });
                        nativePacket.ProviderAttempts.Add(attempt.Clone());
                        if (attempt.Status == "timed_out") nativePacket.TimeLimited = true;
                        if (!ex.Retryable || attemptNumber == policy.MaxAttempts)
                        {
                            nativePacket.TraversalFrontier.Add(Frontier(window,
                                attempt.Status == "timed_out" ? "uia_worker_timeout" : "uia_worker_failure"));
                            finalPacket = nativePacket;
                            break;
                        }
                    }
                }

                finalPacket ??= nativePacket;
                if (policy.CaptureMsaa || policy.MappingCheckpoint)
                {
                    var attempt = new ProviderAttemptOutcome { AttemptId = Guid.NewGuid().ToString("N"),
                        CaptureWindowId = captureWindowId, ProviderId = "msaa", StartedAt = DateTimeOffset.UtcNow.ToString("O"),
                        WorkerVersion = "1.0.0", WorkerArchitecture = RuntimeInformation.ProcessArchitecture.ToString() };
                    AccessibilityPacket response;
                    try
                    {
                        response = await runner.RunAsync(new ProbeRequest("msaa", 0, 0, 0, 0, hwnd,
                            window.WindowEvidenceId, reservedNodes, policy.MaxUiaDepth, 0), AccessibilityPacket.Parser,
                            policy.UiaPerWindowDeadline, aggregateCts.Token).ConfigureAwait(false);
                        attempt.Status = response.Health.State == HealthState.Complete ? "completed" : "partial";
                        attempt.NodesObserved = response.NodesObserved; attempt.NodesIncluded = response.NodesIncluded;
                    }
                    catch (RecorderException ex)
                    {
                        attempt.Status = ex.Code.Contains("TIMEOUT", StringComparison.Ordinal) ? "timed_out" : "provider_failed";
                        attempt.FailureClass = ex.Code;
                        response = new AccessibilityPacket { Health = ProtocolHealth.Single(HealthState.Partial,
                            "RECORDER_MSAA_FAILED", "MSAA attempt could not complete; other provider evidence was retained.", "msaa"),
                            TimeLimited = attempt.Status == "timed_out" };
                    }
                    attempt.EndedAt = DateTimeOffset.UtcNow.ToString("O");
                    rawResponses.Add(new() { Attempt = attempt.Clone(), WindowEvidenceId = window.WindowEvidenceId, Packet = response.Clone() });
                    finalPacket.ProviderAttempts.Add(attempt);
                    // Supplement the derived view only when UIA could not supply a tree.
                    // Both full responses remain independently inspectable in raw evidence.
                    if (!finalPacket.Nodes.Any(node => node.ProviderId == "uia"))
                    {
                        var remaining = Math.Max(0, reservedNodes - finalPacket.Nodes.Count);
                        finalPacket.Nodes.AddRange(response.Nodes.Take(remaining).Select(node => node.Clone()));
                        finalPacket.NodesIncluded = finalPacket.Nodes.Count;
                        finalPacket.NodesObserved += response.NodesObserved;
                        finalPacket.NodeLimited |= response.Nodes.Count > remaining;
                    }
                    healthValues.Add(response.Health);
                }
                if (!policy.ExhaustiveCoverage)
                    lock (nodeBudgetLock) episodeNodesRemaining += Math.Max(0, reservedNodes - finalPacket.NodesIncluded);
                lock (aggregate)
                {
                    aggregate.RawProviderResponses.AddRange(rawResponses);
                    aggregate.Nodes.AddRange(finalPacket.Nodes.Select(x => x.Clone()));
                    aggregate.NodesObserved += finalPacket.NodesObserved;
                    aggregate.NodesIncluded += finalPacket.NodesIncluded;
                    aggregate.NodeLimited |= finalPacket.NodeLimited;
                    aggregate.DepthLimited |= finalPacket.DepthLimited;
                    aggregate.TimeLimited |= finalPacket.TimeLimited;
                    aggregate.TraversalFrontier.AddRange(finalPacket.TraversalFrontier.Select(x => x.Clone()));
                    aggregate.ProviderAttempts.AddRange(finalPacket.ProviderAttempts.Select(x => x.Clone()));
                }
                return ProtocolHealth.Aggregate(healthValues);
            }
            finally
            {
                processGate.Release();
                gate.Release();
            }
        }).ToList();

        try
        {
            var health = await Task.WhenAll(tasks).ConfigureAwait(false);
            aggregate.Health = ProtocolHealth.Aggregate(health.Append(aggregate.Health));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            aggregate.TimeLimited = true;
            aggregate.Health = ProtocolHealth.Single(HealthState.TimedOut, "RECORDER_UIA_TIMEOUT", "Accessibility capture exceeded its aggregate deadline.", "accessibility", true);
        }

        return aggregate;
    }

    private static string WindowId(WindowObservationRecord value) => string.IsNullOrWhiteSpace(value.CaptureWindowId) ? value.WindowEvidenceId : value.CaptureWindowId;

    private async Task<AccessibilityPacket> CaptureNativeAsync(
        WindowObservationRecord window,
        long hwnd,
        int maxNodes,
        RecorderPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            var packet = await runner.RunAsync(
                new ProbeRequest("native-accessibility", 0, 0, 0, 0, hwnd, window.WindowEvidenceId, maxNodes, 0, 0),
                AccessibilityPacket.Parser,
                NativeDeadline(policy),
                cancellationToken).ConfigureAwait(false);
            foreach (var attempt in packet.ProviderAttempts) attempt.CaptureWindowId = WindowId(window);
            return packet;
        }
        catch (RecorderException ex)
        {
            var timedOut = ex.Code.Contains("TIMEOUT", StringComparison.Ordinal);
            var packet = new AccessibilityPacket
            {
                Health = ProtocolHealth.Single(
                    timedOut ? HealthState.TimedOut : HealthState.ProviderFailed,
                    timedOut ? "RECORDER_NATIVE_ACCESSIBILITY_TIMEOUT" : "RECORDER_NATIVE_ACCESSIBILITY_FAILED",
                    timedOut ? "Native accessibility capture exceeded its deadline." : "Native accessibility capture failed for a window.",
                    "accessibility",
                    ex.Retryable),
                TimeLimited = timedOut,
                ProviderAttempts =
                {
                    new ProviderAttemptOutcome
                    {
                        AttemptId = Guid.NewGuid().ToString("N"),
                        CaptureWindowId = WindowId(window),
                        ProviderId = "native-hwnd",
                        WorkerVersion = "0.9.0",
                        WorkerArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        StartedAt = DateTimeOffset.UtcNow.ToString("O"),
                        EndedAt = DateTimeOffset.UtcNow.ToString("O"),
                        Status = timedOut ? "timed_out" : "provider_failed",
                        FailureClass = timedOut ? "timeout" : "native_window_enumeration",
                        Retryable = ex.Retryable,
                    },
                },
            };
            packet.TraversalFrontier.Add(new TraversalFrontier
            {
                FrontierId = Guid.NewGuid().ToString("N"),
                CaptureWindowId = WindowId(window),
                ProviderId = "native-hwnd",
                SourceElementEvidence = window.WindowEvidenceId,
                ReasonKnown = true,
                StopReason = timedOut ? "native_worker_timeout" : "native_worker_failure",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                TerminalState = "remaining",
            });
            return packet;
        }
    }

    private static TimeSpan NativeDeadline(RecorderPolicy policy) =>
        policy.UiaPerWindowDeadline == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(
            Math.Clamp(policy.UiaPerWindowDeadline.TotalMilliseconds / 4d, 250d, 2_000d));

    private static TraversalFrontier Frontier(WindowObservationRecord window, string reason) => new()
    {
        FrontierId = Guid.NewGuid().ToString("N"), CaptureWindowId = WindowId(window), ProviderId = "uia",
        SourceElementEvidence = window.WindowEvidenceId, ReasonKnown = true, StopReason = reason,
        CreatedAt = DateTimeOffset.UtcNow.ToString("O"), TerminalState = "remaining",
    };
}
