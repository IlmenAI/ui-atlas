using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class ObservationNormalizer
{
    public IReadOnlyList<Observation> Normalize(SensorEpisodeBundle bundle, long anchorTicks, DateTimeOffset publicationTime)
        => Normalize(bundle, anchorTicks, publicationTime, recorded: false, CancellationToken.None);

    public IReadOnlyList<Observation> NormalizeRecorded(SensorEpisodeBundle bundle, long anchorTicks, DateTimeOffset captureTime, CancellationToken cancellationToken)
        => Normalize(bundle, anchorTicks, captureTime, recorded: true, cancellationToken);

    private static IReadOnlyList<Observation> Normalize(SensorEpisodeBundle bundle, long anchorTicks, DateTimeOffset publicationTime,
        bool recorded, CancellationToken cancellationToken)
    {
        var observations = new List<Observation>();
        foreach (var packet in bundle.SensorPackets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var freshness = Freshness(packet, bundle.ClockMetadata.MonotonicFrequencyHz, anchorTicks, publicationTime);
            switch (packet.PayloadCase)
            {
                case SensorPacket.PayloadOneofCase.Screenshot:
                    if (recorded && packet.Screenshot.ImageBlob.IsEmpty) break;
                    observations.Add(Create(
                        bundle, packet, ObservationKind.VisualSurfaceObservation,
                        bundle.DisplayDescriptor.BoundsPx, "visual_surface", string.Empty,
                        "display:" + bundle.DisplayDescriptor.DisplayId, string.Empty, string.Empty,
                        [], freshness, recorded));
                    break;
                case SensorPacket.PayloadOneofCase.WindowTopology:
                    observations.AddRange(CheckCancellation(packet.WindowTopology.Windows, cancellationToken).Select(window => Create(
                        bundle, packet, ObservationKind.Window, window.BoundsPx, "window", window.Title,
                        window.WindowEvidenceId, string.Empty, window.WindowEvidenceId,
                        [
                            Property("processId", window.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("processName", window.ProcessName),
                            Property("executablePath", window.ExecutablePath),
                            Property("applicationUserModelId", window.ApplicationUserModelId),
                            Property("packageFamilyName", window.PackageFamilyName),
                            Property("windowHandleEvidence", window.WindowHandleEvidence),
                            Property("ownerWindowHandleEvidence", window.OwnerChainEvidence.FirstOrDefault() ?? string.Empty),
                            Property("rootOwnerWindowHandleEvidence", window.RootOwnerHandleEvidence),
                            Property("processStartTimeEvidence", window.ProcessStartTimeEvidence),
                            Property("className", window.ClassName),
                            Property("zOrder", window.ZOrder.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isVisible", window.IsVisible.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isMinimized", window.IsMinimized.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isCloakedOrUnavailable", window.IsCloakedOrUnavailable.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("intersectionWidthPx", window.IntersectionWithDisplayPx.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("intersectionHeightPx", window.IntersectionWithDisplayPx.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isForeground", window.IsForeground.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isActive", window.IsActive.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isToolWindow", window.IsToolWindow.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isOwnedWindow", window.IsOwnedWindow.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("selectionEligibility", window.SelectionEligibility),
                            Property("captureRelevance", window.CaptureRelevance),
                        ], freshness, recorded)));
                    break;
                case SensorPacket.PayloadOneofCase.Accessibility:
                    observations.AddRange(CheckCancellation(AccessibilityEvidence.Nodes(packet.Accessibility), cancellationToken)
                        .Where(AccessibilityGeometryEvidence.HasSpatialGeometry).Select(node => Create(
                        bundle, packet, ObservationKind.AccessibilityObservation, node.BoundsPx,
                        string.IsNullOrWhiteSpace(node.LocalizedControlType) ? node.ControlType : node.LocalizedControlType,
                        node.Name, node.AccessibilityEvidenceId, node.ParentEvidenceId, node.WindowEvidenceId,
                        [
                            Property("automationId", node.AutomationId),
                            Property("sourceAccessibilityEvidenceId", node.AccessibilityEvidenceId),
                            Property("controlType", node.ControlType),
                            Property("localizedControlType", node.LocalizedControlType),
                            Property("supportedPatterns", string.Join("|", node.SupportedPatterns.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))),
                            Property("className", node.ClassName),
                            Property("providerId", node.ProviderId),
                            Property("nativeWindowHandleEvidence", node.NativeWindowHandleEvidence),
                            Property("nativeControlIdEvidence", node.NativeControlIdEvidence),
                            Property("isEnabled", node.IsEnabled.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isOffscreen", node.IsOffscreen.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("isPassword", node.IsPassword.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("childCountObserved", node.ChildCountObserved.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("childCountIncluded", node.ChildCountIncluded.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("hostedContentOpaque", node.SupportedPatterns.Contains("NativeWindow.HostedContentOpaque").ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Property("valueEvidence", node.IsPassword ? string.Empty : node.ValueEvidence),
                        ], freshness, recorded)));
                    break;
            }
        }
        var topologyWindows = bundle.SensorPackets
            .Where(x => x.PayloadCase == SensorPacket.PayloadOneofCase.WindowTopology)
            .SelectMany(x => x.WindowTopology.Windows)
            .GroupBy(x => x.WindowEvidenceId, StringComparer.Ordinal).Select(x => x.First()).ToList();
        var processByWindow = topologyWindows.ToDictionary(x => x.WindowEvidenceId, x => x.ProcessInstanceEvidence ?? string.Empty, StringComparer.Ordinal);
        var scopeByWindow = topologyWindows.ToDictionary(x => x.WindowEvidenceId,
            x => $"{x.ProcessInstanceEvidence}|hwnd:{x.WindowHandleEvidence}|root:{x.RootOwnerHandleEvidence}|class:{x.ClassName}", StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observation.SourceInterval = observation.Freshness.SourceInterval.Clone();
            if (observation.ObservationKind == ObservationKind.Window)
                observation.ProcessInstanceEvidence = processByWindow.GetValueOrDefault(observation.SourceEvidenceId, string.Empty);
            else if (!string.IsNullOrWhiteSpace(observation.WindowEvidenceId))
                observation.ProcessInstanceEvidence = processByWindow.GetValueOrDefault(observation.WindowEvidenceId, string.Empty);
            var scopeKey = observation.ObservationKind == ObservationKind.Window ? observation.SourceEvidenceId : observation.WindowEvidenceId;
            if (scopeByWindow.TryGetValue(scopeKey, out var windowScope)) observation.Properties.Add(Property("windowScopeRef", windowScope));
        }
        return observations;
    }

    private static IEnumerable<T> CheckCancellation<T>(IEnumerable<T> source, CancellationToken cancellationToken)
    { foreach (var value in source) { cancellationToken.ThrowIfCancellationRequested(); yield return value; } }

    private static Observation Create(
        SensorEpisodeBundle bundle,
        SensorPacket packet,
        ObservationKind kind,
        RectPx bounds,
        string role,
        string name,
        string evidenceId,
        string parentId,
        string windowId,
        IEnumerable<StringProperty> properties,
        Freshness freshness,
        bool recorded)
    {
        var observation = new Observation
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            EpisodeId = bundle.EpisodeId,
            FrameCandidateId = bundle.EpisodeId,
            ObservationKind = kind,
            BoundsPx = bounds.Clone(),
            Role = role,
            ObservedName = name,
            Health = ObservationHealth(packet.Health, recorded),
            Confidence = new Confidence { State = ConfidenceState.NotComputed, Rationale = "Direct source fact" },
            Freshness = freshness.Clone(),
            Lineage = new Lineage { SourcePacketIds = { packet.PacketId } },
            ParentEvidenceId = parentId,
            SourceEvidenceId = evidenceId,
            WindowEvidenceId = windowId,
            SourceInterval = packet.CaptureInterval.Clone(),
        };
        observation.Properties.AddRange(properties.Select(x => x.Clone()));
        return observation;
    }

    private static Health ObservationHealth(Health source, bool recorded)
    {
        var result = source.Clone();
        if (!recorded) return result;
        // Provider aggregates may contain the exact same issue once per node.
        // Repeating that aggregate on every observation/object/track makes a
        // historical snapshot quadratic without adding evidence. Keep every
        // distinct issue and its state; the original packets and frame-level
        // aggregate retain the original multiplicity and source bytes.
        result.Issues.Clear();
        result.Issues.AddRange(source.Issues.Distinct().Select(issue => issue.Clone()));
        return result;
    }

    private static Freshness Freshness(SensorPacket packet, long frequency, long anchor, DateTimeOffset published)
    {
        var midpoint = packet.CaptureInterval.StartedAtMonotonicTicks + ((packet.CaptureInterval.EndedAtMonotonicTicks - packet.CaptureInterval.StartedAtMonotonicTicks) / 2);
        var skew = (midpoint - anchor) * 1000d / frequency;
        var ended = DateTimeOffset.TryParse(packet.CaptureInterval.EndedAtUtc, out var parsed) ? parsed : published;
        return new Freshness
        {
            SourceInterval = packet.CaptureInterval.Clone(),
            SkewFromAnchorMs = skew,
            AgeAtPublicationMs = Math.Max(0, (published - ended).TotalMilliseconds),
        };
    }

    private static StringProperty Property(string key, string value) => new() { Key = key, Value = value ?? string.Empty };
}
