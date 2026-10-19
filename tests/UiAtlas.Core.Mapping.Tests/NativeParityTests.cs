using Google.Protobuf;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class NativeParityTests
{
    [Fact]
    public void ProjectionPreservesSeparateProviderAttemptsAndOriginalParentsBeforeCanonicalization()
    {
        var episode = EpisodeFixture.Create();
        var payload = episode.SensorPackets.Single(packet => packet.Accessibility is not null).Accessibility;
        var original = payload.Clone();
        original.Nodes[0].ParentEvidenceId = "original-root";
        var parent = original.Nodes[0].Clone(); parent.AccessibilityEvidenceId = "original-root"; parent.ParentEvidenceId = "";
        original.Nodes.Insert(0, parent); original.NodesObserved = original.NodesIncluded = 2;
        payload.RawProviderResponses.Add(new RawProviderResponse { Attempt = new ProviderAttemptOutcome
            { AttemptId = "original-attempt", ProviderId = "uia", Status = "completed" },
            WindowEvidenceId = "selected-window", Packet = original });
        payload.RawProviderResponses.Add(new RawProviderResponse { Attempt = new ProviderAttemptOutcome
            { AttemptId = "failed-attempt", ProviderId = "msaa", Status = "timed_out" },
            WindowEvidenceId = "selected-window", Packet = new AccessibilityPacket { Health = new Health { State = HealthState.TimedOut }, TimeLimited = true } });
        payload.Nodes[0].Name = "Derived replacement"; payload.Nodes[0].ParentEvidenceId = "";
        var serialized = episode.ToByteArray();
        var frame = NativeRecorderCapture.Project(SensorEpisodeBundle.Parser.ParseFrom(serialized), Target, 1, "fixture", new());
        var attempts = frame.RawStreams!.Where(stream => stream.TargetWindowHwnd == 123).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal("original-root", attempts[0].Controls[1].ParentRuntimeId);
        Assert.Equal("Save", attempts[0].Controls[1].Name);
        Assert.Empty(attempts[1].Controls);
        Assert.Equal("timeout", attempts[1].Status);
        Assert.True(attempts[1].Truncated);
        Assert.Equal(serialized, episode.ToByteArray());
    }

    [Theory]
    [InlineData("stable", true)]
    [InlineData("changed", false)]
    [InlineData("unverified", false)]
    public void CaptureCoverageAndTemporalPairingRemainIndependent(string consistency, bool pairs)
    {
        var episode = EpisodeFixture.Create();
        episode.Health.State = HealthState.Partial;
        episode.ObservationConsistency = new ObservationConsistency { Status = consistency, Reason = "fixture" };
        var frame = NativeRecorderCapture.Project(episode, Target, 1, "fixture", new());
        Assert.Equal(pairs, RawVariantPresentation.AllowsOverlay(frame));
        Assert.True(RawVariantPresentation.PolicyFor(UiMapProjectionMode.Controls, frame).ShowsControlCrops);
        Assert.True(RawVariantPresentation.PolicyFor(UiMapProjectionMode.Overlay, frame).ShowsControlCrops);
        Assert.Equal(pairs, RawVariantPresentation.CaptureWarning(frame) is null);
        Assert.False(RawVariantPresentation.PolicyFor(UiMapProjectionMode.Structure, frame).ShowsControlCrops);
    }

    [Fact]
    public void NativeWindowsCarryChromeMetadataAndDistinctDialogAndPopupKinds()
    {
        var episode = EpisodeFixture.Create();
        var topology = episode.SensorPackets.Single(packet => packet.WindowTopology is not null).WindowTopology;
        topology.Windows.RemoveAt(1);
        topology.Windows[0].WindowStyle = 0x00C40000; topology.Windows[0].Dpi = 144;
        var dialog = topology.Windows[0].Clone(); dialog.WindowEvidenceId = "dialog"; dialog.WindowHandleEvidence = "124";
        dialog.ClassName = "bosa_sdm_msword"; dialog.OwnerChainEvidence.Add("123");
        var popup = dialog.Clone(); popup.WindowEvidenceId = "popup"; popup.WindowHandleEvidence = "125";
        popup.ClassName = "Net UI Tool Window"; popup.WindowStyle = 0x80000000; popup.WindowExStyle = 0x80; popup.IsToolWindow = true;
        topology.Windows.AddRange([dialog, popup]);
        var frame = NativeRecorderCapture.Project(episode, Target, 1, "fixture", new());
        Assert.Equal(new[] { "normal-window", "dialog-window", "borderless-tool-window" }, frame.ScopedWindows!.Select(RawObservationSemantics.NativeKind));
        Assert.All(frame.ScopedWindows!, window => Assert.Equal(144, window.Dpi));
        Assert.Equal(123, frame.ScopedWindows![2].OwnerHwnd);
    }

    [Fact]
    public void MappingProfileIsBoundedSnapshotAndTopologyIdentityCannotBeReused()
    {
        var request = new CapturePolicyRequest { RequestedProfileId = "mapping-checkpoint",
            RequestedScope = CapturePolicyResolver.PassiveScope("test") };
        var policy = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(request, new ConsumerLimits(16 * 1024 * 1024, 128 * 1024 * 1024, true));
        Assert.Equal(CaptureSemantics.Snapshot, policy.CaptureSemantics);
        Assert.InRange(policy.HardWallClockBudgetMs, 1, 20_000);
        Assert.Equal(3_000, policy.ProviderBudgets.Single(budget => budget.ProviderId == "screen").HardDurationMs);
        Assert.Equal(1, policy.ProviderBudgets.Single(budget => budget.ProviderId == "msaa").MaxAttempts);
        var window = EpisodeFixture.Create().SensorPackets.Single(packet => packet.WindowTopology is not null).WindowTopology.Windows[0];
        var reused = window.Clone(); reused.ProcessInstanceEvidence = "different-process";
        Assert.False(MappingObservationEvidence.ScopeMatches([window], [reused]));
        var reordered = window.Clone(); reordered.ZOrder += 20;
        Assert.True(MappingObservationEvidence.ScopeMatches([window], [reordered]));
    }

    private static WindowTarget Target => new(123, 123, 10, "synthetic-app", DateTimeOffset.UnixEpoch,
        "Synthetic", "SyntheticWindow", new(0, 0, 1, 1));

    [Fact]
    public void PixelVerificationRejectsChangedSurfaceButToleratesACaret()
    {
        static byte[] Pixels(bool changed, bool caret)
        {
            using var bitmap = new System.Drawing.Bitmap(200, 200);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.White);
            if (changed) graphics.FillRectangle(System.Drawing.Brushes.Red, 40, 40, 60, 60);
            if (caret) graphics.FillRectangle(System.Drawing.Brushes.Black, 100, 100, 1, 15);
            using var bytes = new MemoryStream(); bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png); return bytes.ToArray();
        }
        Assert.True(MappingObservationEvidence.PixelsMatch(Pixels(false, false), Pixels(false, true)));
        Assert.False(MappingObservationEvidence.PixelsMatch(Pixels(false, false), Pixels(true, false)));
    }

    [Fact]
    public void ImportRejectsInventedStabilityAndRecursiveProviderArchives()
    {
        var validator = new UIMaps.Perception.Core.PacketValidator(UIMaps.Perception.Core.PerceptionPolicy.Default);
        var episode = EpisodeFixture.Create();
        episode.ObservationConsistency = new ObservationConsistency { Status = "stable", PixelsMatched = true, TopologyMatched = true };
        Assert.Throws<UIMaps.Perception.Core.PerceptionException>(() => validator.Validate(episode));
        episode.ObservationConsistency = null;
        var packet = episode.SensorPackets.Single(packet => packet.Accessibility is not null).Accessibility;
        var nested = new AccessibilityPacket(); nested.RawProviderResponses.Add(new RawProviderResponse());
        packet.RawProviderResponses.Add(new RawProviderResponse { Attempt = new ProviderAttemptOutcome { AttemptId = "one", ProviderId = "uia" },
            WindowEvidenceId = "selected-window", Packet = nested });
        EpisodeFixture.Seal(episode);
        Assert.Throws<UIMaps.Perception.Core.PerceptionException>(() => validator.Validate(episode));
    }
}
