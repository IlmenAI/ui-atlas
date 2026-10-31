using Google.Protobuf;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class TargetWindowRecoveryTests
{
    [Theory]
    [InlineData("none", 2)]
    [InlineData("process", 0)]
    [InlineData("hidden", 0)]
    [InlineData("minimized", 0)]
    [InlineData("missing", 0)]
    public async Task RootAndModalRecoveryUsesFreshIdentityAndOneSharedBudget(string fault, int expected)
    {
        var runner = new Runner(fault);
        var policy = Policy() with { TargetWindowHandleEvidence = new HashSet<string> { "100", "200" } };
        var packet = await new IsolatedWindowTopologyProvider(runner).CaptureAsync(Display(), policy, CancellationToken.None);
        Assert.Equal(new[] { "windows", "target-window", "target-window" }, runner.Requests.Select(r => r.Operation));
        Assert.Equal(new long[] { 100, 200 }, runner.Requests.Skip(1).Select(r => r.WindowHandle));
        Assert.Equal(expected, packet.Windows.Count);
        Assert.True(runner.Timeouts[2] < runner.Timeouts[1]);
        Assert.All(runner.Timeouts.Skip(1), remaining => Assert.InRange(remaining, TimeSpan.Zero, policy.WindowTopologyDeadline));
        Assert.True(packet.WindowLimited); Assert.Equal(HealthState.Partial, packet.Health.State);
    }

    [Theory]
    [InlineData(3, 10)]
    [InlineData(2, 1)]
    public async Task RecoveryNeverExpandsTheRequestedWindowBound(int requested, int allowed)
    {
        var runner = new Runner("none");
        var policy = Policy() with { MaxWindows = allowed, TargetWindowHandleEvidence = Enumerable.Range(1, requested).Select(i => (100*i).ToString()).ToHashSet() };
        await Assert.ThrowsAsync<RecorderException>(() => new IsolatedWindowTopologyProvider(runner).CaptureAsync(Display(), policy, CancellationToken.None));
        Assert.Single(runner.Requests);
    }
    [Theory]
    [InlineData("none", 1)]
    [InlineData("process", 0)]
    [InlineData("handle", 0)]
    [InlineData("hidden", 0)]
    [InlineData("minimized", 0)]
    [InlineData("missing", 0)]
    public async Task DesktopTimeoutUsesOnlyFreshExactEligibleTarget(string fault, int expectedWindows)
    {
        var runner = new Runner(fault);
        var packet = await new IsolatedWindowTopologyProvider(runner).CaptureAsync(Display(), Policy(), CancellationToken.None);
        Assert.Equal(new[] { "windows", "target-window" }, runner.Requests.Select(request => request.Operation));
        Assert.Equal(100, runner.Requests[1].WindowHandle);
        Assert.Equal(1, runner.Requests[1].MaxWindows);
        Assert.Equal(expectedWindows, packet.Windows.Count);
        Assert.Equal(expectedWindows, packet.WindowsIncluded);
        Assert.True(packet.WindowLimited);
        Assert.Equal(HealthState.Partial, packet.Health.State);
        Assert.Contains(packet.Health.Issues, issue => issue.Code == "RECORDER_WINDOW_ENUM_TARGET_RECOVERY");
    }

    [Theory]
    [InlineData("inventory", 1)]
    [InlineData("unbound", 1)]
    [InlineData("cancelled", 1)]
    [InlineData("target-timeout", 2)]
    public async Task RecoveryIsBoundedAndRequiresAnExplicitProcessBoundTarget(string fault, int expectedCalls)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner(fault, cancellation);
        var policy = fault == "inventory" ? RecorderPolicy.Default
            : fault == "unbound" ? Policy() with { TargetProcessInstanceEvidence = "" } : Policy();
        await Assert.ThrowsAnyAsync<Exception>(() => new IsolatedWindowTopologyProvider(runner)
            .CaptureAsync(Display(), policy, cancellation.Token));
        Assert.Equal(expectedCalls, runner.Requests.Count);
    }

    private static DisplayDescriptor Display() => new() { WidthPx = 800, HeightPx = 600,
        BoundsPx = new RectPx { Width = 800, Height = 600 } };
    private static RecorderPolicy Policy() => RecorderPolicy.Default with {
        TargetWindowHandleEvidence = new HashSet<string> { "100" }, TargetProcessInstanceEvidence = "pid:42:started:1" };

    private sealed class Runner(string fault, CancellationTokenSource? cancellation = null) : IProviderProcessRunner
    {
        public List<ProbeRequest> Requests { get; } = [];
        public List<TimeSpan> Timeouts { get; } = [];
        public Task<T> RunAsync<T>(ProbeRequest request, MessageParser<T> parser, TimeSpan timeout, CancellationToken token)
            where T : class, IMessage<T>
        {
            Requests.Add(request);
            Timeouts.Add(timeout);
            if (request.Operation == "windows" || fault == "target-timeout")
            {
                if (fault == "cancelled") cancellation!.Cancel();
                throw new RecorderException("RECORDER_PROVIDER_TIMEOUT", "Injected desktop census timeout.", true);
            }
            var packet = new WindowTopologyPacket();
            if (fault != "missing") packet.Windows.Add(new WindowObservationRecord {
                WindowHandleEvidence = fault == "handle" ? "101" : request.WindowHandle.ToString(),
                ProcessInstanceEvidence = fault == "process" ? "pid:42:started:2" : "pid:42:started:1",
                IsVisible = fault != "hidden", IsMinimized = fault == "minimized", SelectionEligibility = "eligible" });
            return Task.FromResult((T)(IMessage)packet);
        }
    }
}
