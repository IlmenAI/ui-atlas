using Google.Protobuf;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class OptimizedCaptureProfileTests
{
    [Fact]
    public void CandidatePreservesStructuralLimitsAndDefaultsWithMeasuredCachedTraversalAllowance()
    {
        var catalog = new CaptureProfileCatalogService();
        var resolver = new CapturePolicyResolver(catalog);
        var consumer = new ConsumerLimits(16 * 1024 * 1024, CaptureSizeLimits.DefaultEpisodeBytes, true);
        var baseline = resolver.Resolve(new CapturePolicyRequest { RequestedProfileId = "mapping-checkpoint" }, consumer);
        var candidate = resolver.Resolve(new CapturePolicyRequest { RequestedProfileId = "capture-optimized" }, consumer);
        Assert.Equal(baseline.HardWallClockBudgetMs, candidate.HardWallClockBudgetMs);
        Assert.Equal(baseline.MaxDepth, candidate.MaxDepth);
        Assert.Equal(baseline.MaxWindows, candidate.MaxWindows);
        Assert.Equal(baseline.MaxNodesPerWindow, candidate.MaxNodesPerWindow);
        Assert.Equal(baseline.MaxNodesPerEpisode, candidate.MaxNodesPerEpisode);
        Assert.Equal(baseline.MaxBytesPerEpisode, candidate.MaxBytesPerEpisode);
        Assert.Equal(3000, baseline.ProviderBudgets.Single(p => p.ProviderId == "uia").HardDurationMs);
        Assert.Equal(10000, candidate.ProviderBudgets.Single(p => p.ProviderId == "uia").HardDurationMs);
        Assert.Equal(10000, candidate.ProviderBudgets.Single(p => p.ProviderId == "msaa").HardDurationMs);
        Assert.Equal(baseline.ProviderBudgets.Where(p => p.ProviderId is not ("uia" or "msaa")),
            candidate.ProviderBudgets.Where(p => p.ProviderId is not ("uia" or "msaa")));
        Assert.Equal(baseline.SchedulingPolicy, candidate.SchedulingPolicy);
        Assert.Equal(baseline.RetryPolicy, candidate.RetryPolicy);
        Assert.True(RecorderPolicy.FromEffective(candidate).UseCachedUia);
        Assert.True(RecorderPolicy.FromEffective(candidate).ReuseMsaaClientRoot);
        Assert.True(RecorderPolicy.FromEffective(candidate).CaptureMsaa);
        Assert.False(RecorderPolicy.FromEffective(baseline).UseCachedUia);
        Assert.False(RecorderPolicy.FromEffective(baseline).ReuseMsaaClientRoot);
        foreach (var profile in catalog.GetCatalog().Profiles.Where(p => p.ProfileId != "capture-optimized"))
            Assert.False(RecorderPolicy.FromEffective(resolver.Resolve(
                new CapturePolicyRequest { RequestedProfileId = profile.ProfileId }, consumer)).UseCachedUia);
        Assert.Equal("balanced", catalog.GetCatalog().DefaultProfileRef.ProfileId);
        Assert.Equal("deep-survey", new UiAtlas.Core.Recording.Windows.RecordingProfileSettings().ActiveProfileId);
    }

    [Theory]
    [InlineData("mapping-checkpoint", "uia")]
    [InlineData("capture-optimized", "uia-cached")]
    public async Task CandidateActuallyDispatchesCachedWorkerAndRetainsNativeAndMsaa(string id, string operation)
    {
        var policy = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(
            new CapturePolicyRequest { RequestedProfileId = id },
            new ConsumerLimits(16 * 1024 * 1024, CaptureSizeLimits.DefaultEpisodeBytes, true));
        var runner = new Runner();
        var windows = new[] { new WindowObservationRecord { WindowEvidenceId = "window", CaptureWindowId = "window",
            WindowHandleEvidence = "123", ProcessId = 1, ProcessInstanceEvidence = "process", IsVisible = true } };
        await new IsolatedAccessibilityProbe(runner).CaptureAsync(windows, RecorderPolicy.FromEffective(policy), CancellationToken.None);
        Assert.Equal(new[] { "native-accessibility", operation, id == "capture-optimized" ? "msaa-root-reuse" : "msaa" }, runner.Operations);
    }

    [Theory]
    [InlineData(false, 10, 1)]
    [InlineData(true, 10, 4)]
    [InlineData(true, 3, 3)]
    public async Task PartialCachedTreeDoesNotSuppressIndependentMsaaEvidence(bool candidate, int limit, int expectedNodes)
    {
        var policy = RecorderPolicy.Default with { UseCachedUia = candidate, CaptureMsaa = true,
            MaxUiaNodes = limit, MaxNodesPerEpisode = limit };
        var window = new WindowObservationRecord { WindowEvidenceId = "window", CaptureWindowId = "window",
            WindowHandleEvidence = "123", ProcessId = 1, IntersectionWithDisplayPx = new RectPx { Width = 100, Height = 100 } };
        var result = await new IsolatedAccessibilityProbe(new PartialRunner()).CaptureAsync([window], policy, CancellationToken.None);
        Assert.Equal(expectedNodes, result.Nodes.Count);
        Assert.Contains(result.TraversalFrontier, f => f.ProviderId == "uia" && f.StopReason == "cooperative_deadline");
        Assert.NotEqual(HealthState.Complete, result.Health.State);
        Assert.Equal(candidate && limit == 3, result.NodeLimited);
        if (candidate)
        {
            Assert.Contains(result.Nodes, n => n.AccessibilityEvidenceId == "msaa-child" && n.ParentEvidenceId == "msaa-root");
            Assert.All(result.Nodes.Where(n => n.ParentEvidenceId.Length > 0),
                n => Assert.Contains(result.Nodes, parent => parent.AccessibilityEvidenceId == n.ParentEvidenceId));
        }
        if (candidate && limit == 3)
            Assert.Contains(result.TraversalFrontier, f => f.ProviderId == "msaa" && f.StopReason == "cross_provider_node_limit");
    }

    private sealed class PartialRunner : IProviderProcessRunner
    {
        public Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken token)
            where T : class, IMessage<T>
        {
            var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
            if (request.Operation is "uia" or "uia-cached")
            {
                packet.Nodes.Add(new AccessibilityNodeRecord { AccessibilityEvidenceId = "uia-root", ProviderId = "uia" });
                packet.TimeLimited = true;
                packet.Health = ProtocolHealth.Single(HealthState.Partial, "TEST_DEADLINE", "Observed prefix only.", "accessibility");
                packet.TraversalFrontier.Add(new TraversalFrontier { ProviderId = "uia", ParentEvidenceId = "uia-root", StopReason = "cooperative_deadline" });
            }
            else if (request.Operation == "msaa")
            {
                packet.Nodes.Add(new AccessibilityNodeRecord { AccessibilityEvidenceId = "msaa-root", ProviderId = "msaa" });
                packet.Nodes.Add(new AccessibilityNodeRecord { AccessibilityEvidenceId = "msaa-child", ParentEvidenceId = "msaa-root", ProviderId = "msaa" });
                packet.Nodes.Add(new AccessibilityNodeRecord { AccessibilityEvidenceId = "msaa-leaf", ParentEvidenceId = "msaa-child", ProviderId = "msaa" });
            }
            packet.NodesObserved = packet.NodesIncluded = packet.Nodes.Count;
            return Task.FromResult(parser.ParseFrom(packet.ToByteArray()));
        }
    }

    private sealed class Runner : IProviderProcessRunner
    {
        public List<string> Operations { get; } = [];
        public Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken token)
            where T : class, IMessage<T>
        {
            Operations.Add(request.Operation);
            return Task.FromResult(parser.ParseFrom(new AccessibilityPacket { Health = ProtocolHealth.Complete() }.ToByteArray()));
        }
    }
}
