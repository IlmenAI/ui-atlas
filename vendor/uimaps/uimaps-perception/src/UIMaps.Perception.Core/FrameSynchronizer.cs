using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class FrameSynchronizer(PerceptionPolicy policy)
{
    public (Frame Frame, long AnchorTicks) Synchronize(SensorEpisodeBundle bundle, IReadOnlyList<Observation> observations)
    {
        var screenshot = bundle.SensorPackets.Single(x => x.StreamKind == SensorStreamKind.Screenshot);
        var anchor = screenshot.CaptureInterval.StartedAtMonotonicTicks + ((screenshot.CaptureInterval.EndedAtMonotonicTicks - screenshot.CaptureInterval.StartedAtMonotonicTicks) / 2);
        var frame = new Frame
        {
            FrameId = Guid.NewGuid().ToString("N"),
            EpisodeId = bundle.EpisodeId,
            AnchorMonotonicTicks = anchor,
            DisplayId = bundle.DisplayDescriptor.DisplayId,
            CoordinateSpace = new CoordinateSpace { Kind = CoordinateSpaceKind.PhysicalDesktopPixels, Description = "Windows virtual desktop physical pixels" },
            CapturedAt = screenshot.CaptureInterval.EndedAtUtc,
            Health = bundle.Health.Clone(),
        };
        frame.ObservationIds.AddRange(observations.Select(x => x.ObservationId));
        var stale = false;
        foreach (var packet in bundle.SensorPackets)
        {
            var startDistance = anchor < packet.CaptureInterval.StartedAtMonotonicTicks ? packet.CaptureInterval.StartedAtMonotonicTicks - anchor : 0;
            var endDistance = anchor > packet.CaptureInterval.EndedAtMonotonicTicks ? anchor - packet.CaptureInterval.EndedAtMonotonicTicks : 0;
            var skewMs = Math.Max(startDistance, endDistance) * 1000d / bundle.ClockMetadata.MonotonicFrequencyHz;
            var within = skewMs <= policy.FrameSynchronizationTolerance.TotalMilliseconds;
            stale |= !within;
            frame.PacketSkew.Add(new PacketSkew { PacketId = packet.PacketId, SkewMs = skewMs, WithinTolerance = within });
        }
        if (stale)
        {
            frame.Health.State = HealthState.Partial;
            frame.Health.Issues.Add(new HealthIssue
            {
                State = HealthState.Stale,
                Code = "PERCEPTION_SYNC_DEGRADED",
                SafeMessage = "One or more evidence packets are outside the synchronization tolerance.",
                Source = "frame_synchronizer",
            });
        }
        return (frame, anchor);
    }
}
