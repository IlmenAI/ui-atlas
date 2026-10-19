using System.IO;
using Google.Protobuf;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class RecordingProfileSettingsTests
{
    [Fact]
    public void NewMappingRecordingUsesDeepSurveyWithoutCoverageOrAccessibilityTimeLimits()
    {
        var store = new RecordingProfileSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profiles.json"));
        var recorder = new NativeRecorderCapture(typeof(NativeRecorderCapture).Assembly.Location, store);
        var scope = CapturePolicyResolver.PassiveScope("test-display");
        scope.TargetWindowHandleEvidence.Add("123"); scope.TargetProcessInstanceEvidence = "test-process";
        var effective = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(recorder.CreateRequest(scope), RecordingProfileSettingsStore.Consumer);
        var policy = RecorderPolicy.FromEffective(effective);
        Assert.Equal("deep-survey", effective.ResolvedProfileRef.ProfileId);
        Assert.Equal(CaptureSemantics.Survey, effective.CaptureSemantics);
        Assert.True(effective.ExhaustiveCoverage);
        Assert.Equal(0, effective.HardWallClockBudgetMs);
        Assert.Equal(0, effective.MaxNodesPerWindow);
        Assert.Equal(0, effective.MaxNodesPerEpisode);
        Assert.Equal(0, effective.MaxDepth);
        Assert.Equal(256L * 1024 * 1024, effective.MaxBytesPerEpisode);
        Assert.Equal(Timeout.InfiniteTimeSpan, policy.UiaPerWindowDeadline);
        Assert.Equal(Timeout.InfiniteTimeSpan, policy.UiaAggregateDeadline);
        Assert.True(policy.CaptureMsaa);
        Assert.Contains(effective.ProviderBudgets, budget => budget.ProviderId == "msaa" && budget.HardDurationMs == 0);
        Assert.Equal("123", Assert.Single(policy.TargetWindowHandleEvidence));
        Assert.Equal("test-process", policy.TargetProcessInstanceEvidence);
    }

    [Fact]
    public void OnlyDeepSurveyAcceptsAnUnlimitedAccessibilityWorker()
    {
        var resolver = new CapturePolicyResolver(new CaptureProfileCatalogService());
        foreach (var id in new[] { "deep-survey", "mapping-checkpoint" })
        {
            var limits = RecordingProfileSettingsStore.Profiles().Single(p => p.ProfileId == id).DefaultLimits.Clone();
            limits.UiaPerWindowDeadlineMs = 0;
            var request = new CapturePolicyRequest { RequestedProfileId = id, UseCustomLimits = true, CustomLimits = limits };
            if (id == "deep-survey")
                Assert.Equal(Timeout.InfiniteTimeSpan, RecorderPolicy.FromEffective(resolver.Resolve(request, RecordingProfileSettingsStore.Consumer)).UiaPerWindowDeadline);
            else
                Assert.Throws<RecorderException>(() => resolver.Resolve(request, RecordingProfileSettingsStore.Consumer));
        }
    }

    [Fact]
    public void RecordingFreezesProfileSettingsAndKeepsItsTargetScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new RecordingProfileSettingsStore(Path.Combine(directory, "profiles.json"));
            var profile = RecordingProfileSettingsStore.Profiles().Single(p => p.ProfileId == "mapping-checkpoint");
            var limits = profile.DefaultLimits.Clone(); limits.MaxNodesPerWindow = 12345;
            store.Save(new("mapping-checkpoint", new() { [profile.ProfileId] = JsonFormatter.Default.Format(limits) }));
            var recorder = new NativeRecorderCapture(typeof(NativeRecorderCapture).Assembly.Location, store);
            store.Save(new("fast"));
            var scope = CapturePolicyResolver.PassiveScope("test-display");
            scope.TargetWindowHandleEvidence.Add("123"); scope.TargetProcessInstanceEvidence = "test-process";
            var request = recorder.CreateRequest(scope);
            var policy = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(request, RecordingProfileSettingsStore.Consumer);
            Assert.Equal("mapping-checkpoint", policy.ResolvedProfileRef.ProfileId);
            Assert.Equal(12345, policy.MaxNodesPerWindow);
            Assert.Equal("123", Assert.Single(policy.CaptureScope.TargetWindowHandleEvidence));
            Assert.Equal("test-process", policy.CaptureScope.TargetProcessInstanceEvidence);
            request.CustomLimits.MaxNodesPerWindow = 1; request.RequestedScope.TargetWindowHandleEvidence.Clear();
            Assert.Equal(12345, recorder.CreateRequest(scope).CustomLimits.MaxNodesPerWindow);
            Assert.Single(scope.TargetWindowHandleEvidence);
            Assert.Equal("fast", new NativeRecorderCapture(typeof(NativeRecorderCapture).Assembly.Location, store).CreateRequest(scope).RequestedProfileId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidProfileOverridesDoNotOverwriteSavedSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new RecordingProfileSettingsStore(Path.Combine(directory, "profiles.json"));
            store.Save(new("balanced"));
            var limits = RecordingProfileSettingsStore.Profiles().Single(p => p.ProfileId == "balanced").DefaultLimits.Clone();
            limits.MaxConcurrentUiaWorkers = 17;
            Assert.Throws<RecorderException>(() => store.Save(new("balanced", new() { ["balanced"] = JsonFormatter.Default.Format(limits) })));
            Assert.Throws<RecorderException>(() => store.Save(new("missing-profile")));
            Assert.Equal("balanced", store.Load().ActiveProfileId);
            Assert.Null(store.Load().Overrides);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(384)]
    [InlineData(512)]
    public void SavedCaptureSizeReachesTheRecorderWithoutAHiddenClamp(int mib)
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecordingProfileSettingsStore(Path.Combine(directory, "profiles.json"));
            var limits = RecordingProfileSettingsStore.Profiles().Single(p => p.ProfileId == "deep-survey").DefaultLimits.Clone();
            limits.MaxBytesPerEpisode = (long)mib * CaptureSizeLimits.MiB;
            store.Save(new("deep-survey", new() { ["deep-survey"] = JsonFormatter.Default.Format(limits) }));
            var recorder = new NativeRecorderCapture(typeof(NativeRecorderCapture).Assembly.Location, store);
            store.Save(new()); // Existing recordings keep the size selected when they started.
            var policy = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(
                recorder.CreateRequest(CapturePolicyResolver.PassiveScope("test")), RecordingProfileSettingsStore.Consumer);
            Assert.Equal(limits.MaxBytesPerEpisode, RecorderPolicy.FromEffective(policy).MaxBundleSize);
            Assert.DoesNotContain(policy.Adjustments, a => a.Code == "consumer_episode_limit");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void UnsupportedCaptureSizesAreRejected(int mib)
    {
        var limits = RecordingProfileSettingsStore.Profiles().Single(p => p.ProfileId == "deep-survey").DefaultLimits.Clone();
        limits.MaxBytesPerEpisode = (long)mib * CaptureSizeLimits.MiB;
        Assert.Throws<RecorderException>(() => RecordingProfileSettingsStore.Validate(
            new("deep-survey", new() { ["deep-survey"] = JsonFormatter.Default.Format(limits) })));
    }
}
