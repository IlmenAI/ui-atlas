using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed class EpisodeCaptureService(
    IDisplayCatalog displayCatalog,
    IScreenCaptureProvider screenCapture,
    IWindowTopologyProvider windowTopology,
    IAccessibilityProbe accessibility,
    IRecorderClock clock)
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private IReadOnlyList<DisplayDescriptor> _lastDisplays = [];

    public async Task<IReadOnlyList<DisplayDescriptor>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        _lastDisplays = await displayCatalog.ListAsync(cancellationToken).ConfigureAwait(false);
        return _lastDisplays.Select(x => x.Clone()).ToList();
    }

    public async Task<SensorEpisodeBundle> CaptureAsync(string displayId, RecorderPolicy policy, CancellationToken cancellationToken, Action<CaptureStage>? progress = null)
    {
        if (!await _captureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new RecorderException("RECORDER_CAPTURE_BUSY", "A capture is already in progress.", true);
        }

        try
        {
            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!policy.ExhaustiveCoverage)
                overallCts.CancelAfter(policy.OverallCaptureDeadline);
            var token = overallCts.Token;
            var displays = _lastDisplays.Count == 0 ? await ListDisplaysAsync(token).ConfigureAwait(false) : _lastDisplays;
            var display = displays.FirstOrDefault(x => string.Equals(x.DisplayId, displayId, StringComparison.Ordinal));
            if (display is null)
            {
                _lastDisplays = await displayCatalog.ListAsync(token).ConfigureAwait(false);
                throw new RecorderException("RECORDER_DISPLAY_NOT_FOUND", "The selected display is no longer available.");
            }

            var episodeId = Guid.NewGuid().ToString("N");
            var startedAt = clock.UtcNow;
            progress?.Invoke(new CaptureStage("visual_topology", 0, 0, 0, 0));
            var screenshotTask = CapturePacketAsync(
                episodeId, "screen", SensorStreamKind.Screenshot, 0,
                async ct =>
                {
                    var result = await screenCapture.CaptureAsync(display, policy, ct).ConfigureAwait(false);
                    result.Display = display.Clone();
                    return (IMessage)result;
                }, token);
            var windowsTask = CapturePacketAsync(
                episodeId, "window_topology", SensorStreamKind.WindowTopology, 0,
                async ct => await windowTopology.CaptureAsync(display, policy, ct).ConfigureAwait(false), token);

            await Task.WhenAll(screenshotTask, windowsTask).ConfigureAwait(false);
            var screenshotPacket = await screenshotTask.ConfigureAwait(false);
            var topologyPacket = await windowsTask.ConfigureAwait(false);
            var windows = topologyPacket.WindowTopology?.Windows.Select(x => x.Clone()).ToList() ?? [];
            progress?.Invoke(new CaptureStage("census", windows.Count,
                windows.Count(x => string.IsNullOrWhiteSpace(x.SelectionEligibility) || x.SelectionEligibility == "eligible"), 0, 0));
            var accessibilityPackets = new List<SensorPacket>();
            if (policy.CaptureSemantics == CaptureSemantics.Survey)
            {
                var shallowDepth = Math.Min(5, policy.MaxUiaDepth);
                // Exhaustive mapping must reach the full frontier without an artificial
                // shallow/node-limited pass or silently reducing the configured retries.
                var passes = new List<(string Name, RecorderPolicy Policy)>();
                if (policy.ExhaustiveCoverage)
                    passes.Add(("exhaustive", policy));
                else
                {
                    passes.Add(("shallow_checkpoint", policy with
                    {
                        MaxUiaNodes = Math.Min(2_500, policy.MaxUiaNodes),
                        MaxUiaDepth = shallowDepth,
                        MaxAttempts = 1,
                    }));
                    if (policy.MaxUiaDepth > shallowDepth)
                        passes.Add(("depth_recovery", policy with { MaxAttempts = 1 }));
                }
                ulong sequence = 0;
                var remainingEpisodeNodes = policy.MaxNodesPerEpisode;
                foreach (var pass in passes)
                {
                    if (token.IsCancellationRequested) break;
                    progress?.Invoke(new CaptureStage(pass.Name, windows.Count,
                        windows.Count(x => string.IsNullOrWhiteSpace(x.SelectionEligibility) || x.SelectionEligibility == "eligible"), 0,
                        accessibilityPackets.Sum(x => x.Accessibility?.NodesIncluded ?? 0)));
                    var passPolicy = pass.Policy with { MaxNodesPerEpisode = Math.Max(1, Math.Min(pass.Policy.MaxNodesPerEpisode, remainingEpisodeNodes)) };
                    var capturedPass = await CapturePacketAsync(
                        episodeId, "accessibility:" + pass.Name, SensorStreamKind.Accessibility, sequence++,
                        async ct => await accessibility.CaptureAsync(windows, passPolicy, ct).ConfigureAwait(false), token).ConfigureAwait(false);
                    accessibilityPackets.Add(capturedPass);
                    remainingEpisodeNodes = Math.Max(0, remainingEpisodeNodes - (capturedPass.Accessibility?.NodesIncluded ?? 0));
                    if (remainingEpisodeNodes == 0) break;
                }
                DeduplicateAccessibility(accessibilityPackets);
            }
            else
            {
                accessibilityPackets.Add(await CapturePacketAsync(
                    episodeId, "accessibility", SensorStreamKind.Accessibility, 0,
                    async ct => await accessibility.CaptureAsync(windows, policy, ct).ConfigureAwait(false), token).ConfigureAwait(false));
            }
            progress?.Invoke(new CaptureStage(policy.CaptureSemantics == CaptureSemantics.Survey ? "convergence" : "snapshot", windows.Count,
                windows.Count(x => string.IsNullOrWhiteSpace(x.SelectionEligibility) || x.SelectionEligibility == "eligible"),
                accessibilityPackets.SelectMany(x => x.Accessibility?.ProviderAttempts ?? []).Select(x => x.CaptureWindowId).Distinct().Count(),
                accessibilityPackets.Sum(x => x.Accessibility?.NodesIncluded ?? 0)));

            var bundle = new SensorEpisodeBundle
            {
                FormatVersion = "1.0",
                EpisodeId = episodeId,
                Producer = "uimaps-recorder",
                ProducerVersion = "0.1.0",
                ClockMetadata = new ClockMetadata
                {
                    ClockId = "recorder-stopwatch-v1",
                    MonotonicFrequencyHz = clock.Frequency,
                    StartedAtUtc = startedAt.ToString("O"),
                },
                DisplayDescriptor = display.Clone(),
                StartedAtUtc = startedAt.ToString("O"),
                EndedAtUtc = clock.UtcNow.ToString("O"),
                EffectivePolicy = policy.ToProtocol(),
            };
            bundle.SensorPackets.Add(screenshotPacket);
            bundle.SensorPackets.Add(topologyPacket);
            bundle.SensorPackets.AddRange(accessibilityPackets);
            bundle.Health = ProtocolHealth.Aggregate(bundle.SensorPackets.Select(x => x.Health));
            bundle.SensorManifest = BuildManifest(bundle.SensorPackets);
            var manifestBytes = bundle.SensorManifest.ToByteArray();
            bundle.IntegrityMetadata = new IntegrityMetadata
            {
                Algorithm = "SHA-256",
                ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            };
            if (bundle.CalculateSize() > policy.MaxBundleSize)
            {
                throw new RecorderException("RECORDER_CAPTURE_TOO_LARGE", "The completed capture exceeds the configured or negotiated episode byte limit. Coverage was not reported as complete.");
            }
            return bundle;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecorderException("RECORDER_CAPTURE_TIMEOUT", "Capture exceeded its overall deadline.", true);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async Task<SensorEpisodeBundle> CaptureAsync(
        string displayId,
        CapturePolicyRequest request,
        EffectiveCapturePolicy effectivePolicy,
        CancellationToken cancellationToken,
        Action<CaptureStage>? progress = null)
    {
        var started = clock.Timestamp;
        var bundle = await CaptureAsync(displayId, RecorderPolicy.FromEffective(effectivePolicy), cancellationToken, progress).ConfigureAwait(false);
        bundle.FormatVersion = "1.1";
        bundle.ProducerVersion = "0.2.0";
        bundle.RequestedPolicy = request.Clone();
        bundle.EffectiveCapturePolicy = effectivePolicy.Clone();
        bundle.SessionTerminalState = cancellationToken.IsCancellationRequested ? "cancelled_partial" : "completed";
        bundle.CoverageReport = BuildCoverage(bundle, started, clock.Timestamp, clock.Frequency, bundle.SessionTerminalState);
        if (!cancellationToken.IsCancellationRequested)
        {
            bundle.SessionTerminalState = TerminalStateFor(bundle.CoverageReport);
            bundle.CoverageReport.SessionTerminalState = bundle.SessionTerminalState;
        }
        bundle.Health = ProtocolHealth.Aggregate(bundle.SensorPackets.Select(x => x.Health).Append(CoverageHealth(bundle.CoverageReport)));
        bundle.SensorManifest = BuildManifest(bundle.SensorPackets);
        bundle.IntegrityMetadata.ManifestSha256 = Convert.ToHexString(SHA256.HashData(bundle.SensorManifest.ToByteArray())).ToLowerInvariant();
        return bundle;
    }

    private async Task<SensorPacket> CapturePacketAsync(
        string episodeId,
        string streamId,
        SensorStreamKind kind,
        ulong sequence,
        Func<CancellationToken, Task<IMessage>> capture,
        CancellationToken cancellationToken)
    {
        var started = clock.Timestamp;
        IMessage? payload = null;
        Health health;
        try
        {
            payload = await capture(cancellationToken).ConfigureAwait(false);
            health = payload switch
            {
                ScreenshotPacket value => value.Health,
                WindowTopologyPacket value => value.Health,
                AccessibilityPacket value => value.Health,
                _ => ProtocolHealth.Single(HealthState.Invalid, "RECORDER_UNKNOWN_PAYLOAD", "Provider returned an unsupported payload.", streamId),
            };
        }
        catch (RecorderException ex)
        {
            var state = ex.Code.Contains("TIMEOUT", StringComparison.Ordinal) ? HealthState.TimedOut : HealthState.ProviderFailed;
            health = ProtocolHealth.Single(state, MapProviderCode(kind, ex.Code), ex.Message, streamId, ex.Retryable);
            payload = kind switch
            {
                SensorStreamKind.Screenshot => new ScreenshotPacket { Health = health.Clone() },
                SensorStreamKind.WindowTopology => new WindowTopologyPacket { Health = health.Clone() },
                SensorStreamKind.Accessibility => new AccessibilityPacket { Health = health.Clone() },
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            health = ProtocolHealth.Single(HealthState.Partial, "RECORDER_PROVIDER_CANCELLED", "Provider work stopped at the capture deadline or cancellation boundary.", streamId);
            payload = kind switch
            {
                SensorStreamKind.Screenshot => new ScreenshotPacket { Health = health.Clone() },
                SensorStreamKind.WindowTopology => new WindowTopologyPacket { Health = health.Clone() },
                SensorStreamKind.Accessibility => new AccessibilityPacket { TimeLimited = true, Health = health.Clone() },
                _ => null,
            };
        }

        var packet = new SensorPacket
        {
            ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
            PacketId = Guid.NewGuid().ToString("N"),
            EpisodeId = episodeId,
            StreamId = streamId,
            StreamKind = kind,
            SequenceNumber = sequence,
            CaptureInterval = new CaptureInterval
            {
                StartedAtMonotonicTicks = started,
                EndedAtMonotonicTicks = clock.Timestamp,
                EndedAtUtc = clock.UtcNow.ToString("O"),
            },
            Health = health.Clone(),
        };
        switch (payload)
        {
            case ScreenshotPacket value: packet.Screenshot = value; break;
            case WindowTopologyPacket value: packet.WindowTopology = value; break;
            case AccessibilityPacket value: packet.Accessibility = value; break;
        }
        return packet;
    }

    private static string MapProviderCode(SensorStreamKind kind, string code)
    {
        if (code != "RECORDER_PROVIDER_TIMEOUT" && code != "RECORDER_PROVIDER_FAILED") return code;
        return kind switch
        {
            SensorStreamKind.Screenshot => code.EndsWith("TIMEOUT", StringComparison.Ordinal) ? "RECORDER_SCREEN_TIMEOUT" : "RECORDER_SCREEN_UNAVAILABLE",
            SensorStreamKind.WindowTopology => code.EndsWith("TIMEOUT", StringComparison.Ordinal) ? "RECORDER_WINDOW_ENUM_TIMEOUT" : "RECORDER_WINDOW_ENUM_FAILED",
            SensorStreamKind.Accessibility => code.EndsWith("TIMEOUT", StringComparison.Ordinal) ? "RECORDER_UIA_TIMEOUT" : "RECORDER_UIA_PROVIDER_FAILED",
            _ => code,
        };
    }

    private static SensorManifest BuildManifest(IEnumerable<SensorPacket> packets)
    {
        var manifest = new SensorManifest();
        foreach (var packet in packets)
        {
            var bytes = packet.ToByteArray();
            manifest.Entries.Add(new SensorManifestEntry
            {
                PacketId = packet.PacketId,
                StreamKind = packet.StreamKind,
                SerializedSize = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Health = packet.Health.Clone(),
            });
        }
        manifest.PacketCount = manifest.Entries.Count;
        return manifest;
    }

    private static void DeduplicateAccessibility(IReadOnlyList<SensorPacket> packets)
    {
        var occurrences = packets
            .SelectMany((packet, packetIndex) => packet.Accessibility?.Nodes
                .Select((node, nodeIndex) => new AccessibilityOccurrence(packetIndex, nodeIndex, node, MergeKey(node, packetIndex, nodeIndex)))
                ?? [])
            .ToList();

        var canonicalByMergeKey = occurrences
            .GroupBy(x => x.MergeKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(x => x.Node.ChildCountIncluded)
                    .ThenByDescending(x => x.Node.ChildCountObserved)
                    .ThenByDescending(x => x.Node.PropertyCoverage?.Read ?? 0)
                    .ThenBy(x => x.Node.PropertyCoverage?.Failed ?? 0)
                    .ThenByDescending(x => x.PacketIndex)
                    .ThenByDescending(x => x.NodeIndex)
                    .First(),
                StringComparer.Ordinal);

        var canonicalPositions = canonicalByMergeKey.Values
            .Select(x => (x.PacketIndex, x.NodeIndex))
            .ToHashSet();
        var canonicalIdByOccurrence = occurrences
            .GroupBy(x => ScopedEvidenceId(x.Node.WindowEvidenceId, x.Node.AccessibilityEvidenceId), StringComparer.Ordinal)
            .Select(group => new
            {
                EvidenceId = group.Key,
                CanonicalIds = group
                    .Select(x => canonicalByMergeKey[x.MergeKey].Node.AccessibilityEvidenceId)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.EvidenceId) && x.CanonicalIds.Count == 1)
            .ToDictionary(x => x.EvidenceId, x => x.CanonicalIds[0], StringComparer.Ordinal);
        var canonicalIdByUnscopedOccurrence = occurrences
            .Where(x => !string.IsNullOrWhiteSpace(x.Node.AccessibilityEvidenceId))
            .GroupBy(x => x.Node.AccessibilityEvidenceId, StringComparer.Ordinal)
            .Select(group => new
            {
                EvidenceId = group.Key,
                CanonicalIds = group
                    .Select(x => canonicalByMergeKey[x.MergeKey].Node.AccessibilityEvidenceId)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            })
            .Where(x => x.CanonicalIds.Count == 1)
            .ToDictionary(x => x.EvidenceId, x => x.CanonicalIds[0], StringComparer.Ordinal);

        foreach (var occurrence in canonicalByMergeKey.Values)
        {
            var node = occurrence.Node;
            if (!string.IsNullOrWhiteSpace(node.ParentEvidenceId)
                && canonicalIdByOccurrence.TryGetValue(ScopedEvidenceId(node.WindowEvidenceId, node.ParentEvidenceId), out var canonicalParentId))
            {
                node.ParentEvidenceId = canonicalParentId;
            }
        }

        for (var packetIndex = 0; packetIndex < packets.Count; packetIndex++)
        {
            var packet = packets[packetIndex];
            if (packet.Accessibility is null) continue;
            for (var index = packet.Accessibility.Nodes.Count - 1; index >= 0; index--)
            {
                if (!canonicalPositions.Contains((packetIndex, index))) packet.Accessibility.Nodes.RemoveAt(index);
            }
            foreach (var frontier in packet.Accessibility.TraversalFrontier)
            {
                if (!string.IsNullOrWhiteSpace(frontier.ParentEvidenceId)
                    && canonicalIdByUnscopedOccurrence.TryGetValue(frontier.ParentEvidenceId, out var canonicalParentId))
                {
                    frontier.ParentEvidenceId = canonicalParentId;
                }
            }
            packet.Accessibility.NodesIncluded = packet.Accessibility.Nodes.Count;
        }

        for (var packetIndex = 0; packetIndex < packets.Count - 1; packetIndex++)
        {
            var packet = packets[packetIndex];
            if (packet.Accessibility is null) continue;
            foreach (var frontier in packet.Accessibility.TraversalFrontier.Where(value =>
                         value.TerminalState != "exhausted"
                         && value.ProviderId == "uia"
                         && value.StopReason == "depth_limit"))
            {
                var recovered = packets.Skip(packetIndex + 1)
                    .Where(value => value.Accessibility is not null)
                    .SelectMany(value => value.Accessibility!.ProviderAttempts)
                    .Any(attempt => attempt.CaptureWindowId == frontier.CaptureWindowId
                        && attempt.ProviderId == "uia"
                        && attempt.Status == "completed");
                if (recovered) frontier.TerminalState = "exhausted";
            }

            if (packet.Accessibility.TraversalFrontier.All(value => value.TerminalState == "exhausted")
                && packet.Accessibility.Health.Issues.All(issue => issue.Code == "RECORDER_UIA_DEPTH_LIMIT"))
            {
                packet.Accessibility.DepthLimited = false;
                packet.Accessibility.Health = ProtocolHealth.Complete();
                packet.Health = ProtocolHealth.Complete();
            }
        }

        var retainedIds = packets
            .Where(x => x.Accessibility is not null)
            .SelectMany(x => x.Accessibility!.Nodes)
            .Select(x => ScopedEvidenceId(x.WindowEvidenceId, x.AccessibilityEvidenceId))
            .ToHashSet(StringComparer.Ordinal);
        var danglingParent = packets
            .Where(x => x.Accessibility is not null)
            .SelectMany(x => x.Accessibility!.Nodes)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ParentEvidenceId)
                && !retainedIds.Contains(ScopedEvidenceId(x.WindowEvidenceId, x.ParentEvidenceId)));
        if (danglingParent is not null)
        {
            throw new RecorderException(
                "RECORDER_UIA_PARENT_DANGLING",
                "Survey accessibility merge produced an unresolved parent reference.");
        }
    }

    private static string MergeKey(AccessibilityNodeRecord node, int packetIndex, int nodeIndex) =>
        node.RuntimeIdEvidence.Count > 0 && !string.IsNullOrWhiteSpace(node.AccessibilityEvidenceId)
            ? $"runtime\u001f{node.WindowEvidenceId}\u001f{string.Join('.', node.RuntimeIdEvidence)}"
            : $"occurrence\u001f{packetIndex}\u001f{nodeIndex}\u001f{node.WindowEvidenceId}\u001f{node.AccessibilityEvidenceId}";

    private static string ScopedEvidenceId(string windowEvidenceId, string accessibilityEvidenceId) =>
        string.IsNullOrWhiteSpace(accessibilityEvidenceId)
            ? string.Empty
            : $"{windowEvidenceId}\u001f{accessibilityEvidenceId}";

    private sealed record AccessibilityOccurrence(
        int PacketIndex,
        int NodeIndex,
        AccessibilityNodeRecord Node,
        string MergeKey);

    private static CoverageReport BuildCoverage(SensorEpisodeBundle bundle, long started, long ended, long frequency, string terminalState)
    {
        var topologyPacket = bundle.SensorPackets.FirstOrDefault(x => x.StreamKind == SensorStreamKind.WindowTopology);
        var topology = topologyPacket?.WindowTopology ?? new WindowTopologyPacket();
        var topologyBlocked = topologyPacket is null || HasBlockingProviderFailure(topologyPacket.Health);
        var topologyLimited = topology.WindowLimited || topologyPacket?.Health.State == HealthState.WindowLimited;
        var accessibilityPackets = bundle.SensorPackets.Where(x => x.StreamKind == SensorStreamKind.Accessibility && x.Accessibility is not null).Select(x => x.Accessibility).ToList();
        var accessibility = new AccessibilityPacket();
        foreach (var packet in accessibilityPackets)
        {
            accessibility.Nodes.AddRange(packet.Nodes.Select(x => x.Clone()));
            accessibility.ProviderAttempts.AddRange(packet.ProviderAttempts.Select(x => x.Clone()));
            accessibility.TraversalFrontier.AddRange(packet.TraversalFrontier.Select(x => x.Clone()));
            accessibility.NodesObserved += packet.NodesObserved; accessibility.NodesIncluded += packet.NodesIncluded;
            accessibility.NodeLimited |= packet.NodeLimited; accessibility.DepthLimited |= packet.DepthLimited; accessibility.TimeLimited |= packet.TimeLimited;
        }
        var report = new CoverageReport
        {
            CatalogedWindows = topology.Windows.Count,
            NodesObserved = accessibility.NodesObserved,
            NodesIncluded = accessibility.NodesIncluded,
            TruncatedSubtrees = accessibility.TraversalFrontier.Count,
            RemainingFrontierCount = accessibility.TraversalFrontier.Count(x => x.TerminalState != "exhausted"),
            TotalDurationMs = frequency <= 0 ? 0 : (long)((ended - started) * 1000d / frequency),
            SessionTerminalState = terminalState,
            PassCount = bundle.EffectiveCapturePolicy?.CaptureSemantics == CaptureSemantics.Survey ? 1 + accessibilityPackets.Count : 1,
            PropertyCoverage = new PropertyCoverage(),
            ScreenshotCoverage = BuildScreenshotCoverage(bundle),
        };
        report.ConvergenceState = bundle.EffectiveCapturePolicy?.CaptureSemantics != CaptureSemantics.Survey
            ? ConvergenceState.ConvergenceNotApplicable
            : topologyBlocked ? ConvergenceState.ConvergenceProviderBlocked
            : topologyLimited ? ConvergenceState.ConvergenceLimitReached
            : report.RemainingFrontierCount == 0 ? ConvergenceState.ConvergenceConverged : ConvergenceState.ConvergenceFrontierRemaining;
        foreach (var node in accessibility.Nodes)
        {
            report.PropertyCoverage.Attempted += node.PropertyCoverage?.Attempted ?? 0;
            report.PropertyCoverage.Read += node.PropertyCoverage?.Read ?? 0;
            report.PropertyCoverage.Failed += node.PropertyCoverage?.Failed ?? 0;
            report.PropertyCoverage.Unsupported += node.PropertyCoverage?.Unsupported ?? 0;
            report.PropertyCoverage.ProtectedOrRedacted += node.PropertyCoverage?.ProtectedOrRedacted ?? 0;
        }
        foreach (var group in topology.Windows.Where(x => x.SelectionEligibility != "eligible").GroupBy(x => string.IsNullOrWhiteSpace(x.SelectionEligibility) ? "excluded_unknown" : x.SelectionEligibility))
            report.ExcludedWindowCountsByReason.Add(new ReasonCount { Reason = group.Key, Count = group.Count() });
        var eligible = topology.Windows.Where(x => string.IsNullOrWhiteSpace(x.SelectionEligibility) || x.SelectionEligibility == "eligible").ToList();
        report.EligibleWindows = eligible.Count;
        foreach (var window in eligible)
        {
            var id = string.IsNullOrWhiteSpace(window.CaptureWindowId) ? window.WindowEvidenceId : window.CaptureWindowId;
            var attempts = accessibility.ProviderAttempts.Where(x => x.CaptureWindowId == id).Select(x => x.Clone()).ToList();
            var frontiers = accessibility.TraversalFrontier.Count(x => x.CaptureWindowId == id && x.TerminalState != "exhausted");
            var status = attempts.Any(x => x.Status == "completed") ? (frontiers == 0 ? "completed" : "partial")
                : attempts.Any(x => x.Status == "timed_out") ? "timed_out"
                : attempts.Any(x => x.Status == "provider_failed") ? "provider_failed"
                : terminalState == "cancelled_partial" ? "cancelled" : "native_only";
            var outcome = new WindowCaptureOutcome
            {
                CaptureWindowId = id, TerminalStatus = status, IdentityStatus = "stable",
                NodesObserved = attempts.Sum(x => x.NodesObserved), NodesIncluded = accessibility.Nodes.Count(x => x.WindowEvidenceId == window.WindowEvidenceId),
                TruncatedSubtreeCount = frontiers, RemainingFrontierCount = frontiers,
                FirstObservationAt = bundle.StartedAtUtc, LastObservationAt = bundle.EndedAtUtc,
                TerminalReason = status, Health = status == "completed" ? ProtocolHealth.Complete() : ProtocolHealth.Single(HealthState.Partial, "RECORDER_WINDOW_" + status.ToUpperInvariant(), "Window capture ended with accounted partial coverage.", "coverage"),
                PropertyCoverage = new PropertyCoverage
                {
                    Attempted = accessibility.Nodes.Where(x => x.WindowEvidenceId == window.WindowEvidenceId).Sum(x => x.PropertyCoverage?.Attempted ?? 0),
                    Read = accessibility.Nodes.Where(x => x.WindowEvidenceId == window.WindowEvidenceId).Sum(x => x.PropertyCoverage?.Read ?? 0),
                    Failed = accessibility.Nodes.Where(x => x.WindowEvidenceId == window.WindowEvidenceId).Sum(x => x.PropertyCoverage?.Failed ?? 0),
                },
            };
            outcome.ProviderAttempts.AddRange(attempts); outcome.ProvidersAttempted.AddRange(attempts.Select(x => x.ProviderId).Distinct());
            outcome.ProvidersSucceeded.AddRange(attempts.Where(x => x.Status is "completed" or "partial").Select(x => x.ProviderId).Distinct());
            report.WindowOutcomes.Add(outcome);
        }
        report.WindowsAttempted = report.WindowOutcomes.Count(x => x.ProviderAttempts.Count > 0);
        report.WindowsCompleted = report.WindowOutcomes.Count(x => x.TerminalStatus == "completed");
        report.WindowsPartial = report.WindowOutcomes.Count(x => x.TerminalStatus == "partial");
        report.WindowsTimedOut = report.WindowOutcomes.Count(x => x.TerminalStatus == "timed_out");
        report.WindowsProviderFailed = report.WindowOutcomes.Count(x => x.TerminalStatus == "provider_failed");
        report.WindowsNativeOnly = report.WindowOutcomes.Count(x => x.TerminalStatus == "native_only");
        report.WindowsCancelled = report.WindowOutcomes.Count(x => x.TerminalStatus == "cancelled");
        report.UnexplainedOmissionCount = Math.Max(topologyBlocked ? 1 : 0, report.EligibleWindows - report.WindowOutcomes.Count);
        report.ProviderDurations.Add(new ProviderDuration { ProviderId = "capture-total", DurationMs = report.TotalDurationMs });
        if (report.ConvergenceState == ConvergenceState.ConvergenceConverged && (report.RemainingFrontierCount > 0 || report.WindowOutcomes.Any(x => x.TerminalStatus is not "completed")))
            report.ConvergenceState = ConvergenceState.ConvergenceProviderBlocked;
        return report;
    }

    private static bool HasBlockingProviderFailure(Health? health) =>
        health is null
        || health.State is HealthState.TimedOut or HealthState.ProviderFailed or HealthState.SensorUnavailable or HealthState.Invalid
        || health.Issues.Any(issue => issue.State is HealthState.TimedOut or HealthState.ProviderFailed or HealthState.SensorUnavailable or HealthState.Invalid);

    internal static string TerminalStateFor(CoverageReport report) => report.ConvergenceState switch
    {
        ConvergenceState.ConvergenceProviderBlocked => "provider_blocked_partial",
        ConvergenceState.ConvergenceFrontierRemaining => "frontier_remaining_partial",
        ConvergenceState.ConvergenceLimitReached => "limit_reached_partial",
        ConvergenceState.ConvergenceBudgetExhausted => "budget_exhausted_partial",
        ConvergenceState.ConvergenceCancelled => "cancelled_partial",
        _ => "completed",
    };

    private static ScreenshotCoverage BuildScreenshotCoverage(SensorEpisodeBundle bundle)
    {
        var screen = bundle.SensorPackets.FirstOrDefault(x => x.StreamKind == SensorStreamKind.Screenshot)?.Screenshot;
        var total = (long)bundle.DisplayDescriptor.WidthPx * bundle.DisplayDescriptor.HeightPx;
        var missing = screen?.ProtectedOrMissingRegions.Sum(x => (long)x.BoundsPx.Width * x.BoundsPx.Height) ?? total;
        var value = new ScreenshotCoverage { SelectedDisplayAreaPx = total, CapturedAreaPx = screen?.ImageBlob.IsEmpty == false ? Math.Max(0, total - missing) : 0, ProtectedOrMissingAreaPx = missing, Status = screen?.ImageBlob.IsEmpty == false ? "captured" : "missing" };
        if (screen is not null) value.ProtectedOrMissingRegions.AddRange(screen.ProtectedOrMissingRegions.Select(x => x.BoundsPx.Clone()));
        return value;
    }

    private static Health CoverageHealth(CoverageReport report) => report.UnexplainedOmissionCount == 0 && report.RemainingFrontierCount == 0
        ? ProtocolHealth.Complete()
        : ProtocolHealth.Single(HealthState.Partial, "RECORDER_COVERAGE_PARTIAL", "Capture coverage contains explicit omissions or unfinished frontier.", "coverage");
}

public sealed record CaptureStage(string Pass, int CatalogedWindows, int EligibleWindows, int WindowsTerminal, long NodesIncluded);
