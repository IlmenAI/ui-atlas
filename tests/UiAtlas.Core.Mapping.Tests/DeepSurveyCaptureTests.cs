using Google.Protobuf;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class DeepSurveyCaptureTests
{
    [Fact]
    public async Task ExhaustivePassKeepsRetriesAndAllThreeProviderResponses()
    {
        var runner = new SurveyRunner(failFirstUia: true);
        var providers = new FixtureProviders();
        var service = new EpisodeCaptureService(providers, providers, providers,
            new IsolatedAccessibilityProbe(runner), new SystemRecorderClock());
        var effective = Policy();
        var episode = await service.CaptureAsync("synthetic-display", new CapturePolicyRequest { RequestedProfileId = "deep-survey" },
            effective, CancellationToken.None);
        Assert.Equal(new[] { "native-accessibility", "uia", "uia", "msaa" }, runner.Calls.Select(c => c.Request.Operation));
        Assert.All(runner.Calls, call => {
            Assert.Equal(Timeout.InfiniteTimeSpan, call.Timeout);
            Assert.Equal(int.MaxValue, call.Request.MaxNodes);
            if (call.Request.Operation != "native-accessibility")
                Assert.Equal(int.MaxValue, call.Request.MaxDepth);
        });
        var packet = Assert.Single(episode.SensorPackets, p => p.Accessibility is not null).Accessibility;
        Assert.Contains(packet.RawProviderResponses, r => r.Attempt.ProviderId == "uia" && r.Attempt.Status == "provider_failed");
        Assert.Contains(packet.RawProviderResponses, r => r.Attempt.ProviderId == "uia" && r.Attempt.Status == "completed" && r.Packet.Nodes.Count == 1);
        Assert.Contains(packet.RawProviderResponses, r => r.Attempt.ProviderId == "msaa" && r.Packet.Nodes.Count == 1);
        Assert.Contains(packet.RawProviderResponses, r => r.Attempt.ProviderId == "native-hwnd" && r.Packet.Nodes.Count == 1);
        Assert.DoesNotContain(packet.TraversalFrontier, f => f.StopReason is "depth_limit" or "per_window_node_limit");
    }

    [Fact]
    public async Task UnlimitedAccessibilityStillCancelsWhenTheUserStops()
    {
        var runner = new WaitingRunner();
        using var cancellation = new CancellationTokenSource();
        var capture = new IsolatedAccessibilityProbe(runner).CaptureAsync([Window()], RecorderPolicy.FromEffective(Policy()), cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static EffectiveCapturePolicy Policy() => new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(
        RecordingProfileSettingsStore.Request(new()), RecordingProfileSettingsStore.Consumer);

    [Fact]
    public async Task ConfiguredSizeIsEnforcedAfterCaptureAndCanBeRaised()
    {
        var providers = new FixtureProviders { ImageBytes = ByteString.CopyFrom(new byte[2 * CaptureSizeLimits.MiB]) };
        var service = new EpisodeCaptureService(providers, providers, providers,
            new IsolatedAccessibilityProbe(new SurveyRunner(false)), new SystemRecorderClock());
        var request = RecordingProfileSettingsStore.Request(new());
        request.UseCustomLimits = true;
        request.CustomLimits = RecordingProfileSettingsStore.Profiles().Single(p => p.ProfileId == "deep-survey").DefaultLimits.Clone();
        var resolver = new CapturePolicyResolver(new CaptureProfileCatalogService());
        request.CustomLimits.MaxBytesPerEpisode = CaptureSizeLimits.MiB;
        var failure = await Assert.ThrowsAsync<RecorderException>(() => service.CaptureAsync("synthetic-display", request,
            resolver.Resolve(request, RecordingProfileSettingsStore.Consumer), CancellationToken.None));
        Assert.Equal("RECORDER_CAPTURE_TOO_LARGE", failure.Code);
        request.CustomLimits.MaxBytesPerEpisode = 4 * CaptureSizeLimits.MiB;
        var episode = await service.CaptureAsync("synthetic-display", request,
            resolver.Resolve(request, RecordingProfileSettingsStore.Consumer), CancellationToken.None);
        Assert.InRange(episode.CalculateSize(), 2 * CaptureSizeLimits.MiB, 4 * CaptureSizeLimits.MiB);
        Assert.Equal(4 * CaptureSizeLimits.MiB, episode.EffectiveCapturePolicy.MaxBytesPerEpisode);
    }

    private static WindowObservationRecord Window() => new()
    {
        WindowEvidenceId = "selected-window", CaptureWindowId = "selected-window", WindowHandleEvidence = "123",
        ProcessId = 10, ProcessInstanceEvidence = "test-process", SelectionEligibility = "eligible", IsVisible = true,
        BoundsPx = new RectPx { Width = 1, Height = 1 }, IntersectionWithDisplayPx = new RectPx { Width = 1, Height = 1 },
    };

    private sealed class SurveyRunner(bool failFirstUia) : IProviderProcessRunner
    {
        public List<(ProbeRequest Request, TimeSpan Timeout)> Calls { get; } = [];
        private bool _failed;
        public Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken cancellationToken)
            where T : class, IMessage<T>
        {
            Calls.Add((request, timeout));
            if (request.Operation == "uia" && failFirstUia && !_failed)
            {
                _failed = true;
                throw new RecorderException("RECORDER_PROVIDER_FAILED", "Transient provider failure", true);
            }
            var provider = request.Operation == "native-accessibility" ? "native-hwnd" : request.Operation;
            var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete(), NodesIncluded = 1, NodesObserved = 1 };
            packet.Nodes.Add(new AccessibilityNodeRecord {
                AccessibilityEvidenceId = provider + ":root", WindowEvidenceId = request.WindowEvidenceId,
                ProviderId = provider, Name = provider, ControlType = "Window", BoundsPx = new RectPx { Width = 1, Height = 1 },
                Health = ProtocolHealth.Complete(),
            });
            if (provider == "native-hwnd")
                packet.ProviderAttempts.Add(new ProviderAttemptOutcome { ProviderId = provider, AttemptId = "native", Status = "completed", CaptureWindowId = "selected-window" });
            return Task.FromResult(parser.ParseFrom(packet.ToByteArray()));
        }
    }

    private sealed class WaitingRunner : IProviderProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken cancellationToken)
            where T : class, IMessage<T>
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not propagate.");
        }
    }

    private sealed class FixtureProviders : IDisplayCatalog, IScreenCaptureProvider, IWindowTopologyProvider
    {
        public ByteString? ImageBytes { get; init; }
        public Task<IReadOnlyList<DisplayDescriptor>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DisplayDescriptor>>([EpisodeFixture.Create().DisplayDescriptor]);
        public Task<ScreenshotPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken)
        {
            var screenshot = EpisodeFixture.Create().SensorPackets.Single(p => p.Screenshot is not null).Screenshot;
            if (ImageBytes is not null) screenshot.ImageBlob = ImageBytes;
            return Task.FromResult(screenshot);
        }
        Task<WindowTopologyPacket> IWindowTopologyProvider.CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(new WindowTopologyPacket { Health = ProtocolHealth.Complete(), WindowsObserved = 1, WindowsIncluded = 1, Windows = { Window() } });
    }
}
