using Google.Protobuf;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Windows.Tests;

public sealed class NativeRecorderScopeTests
{
    [Fact]
    public void OwnedNormalWindowsAndNestedDialogsSurviveWithTheirOriginalNativeEvidence()
    {
        var target = new WindowTarget(20, 10, 42, "fixture", DateTimeOffset.Parse("2026-10-23T00:00:00Z"),
            "Arrival", "ProMainWin", new RectI(0, 0, 800, 600));
        WindowObservationRecord Window(string handle, string root, params string[] owners)
        {
            var window = new WindowObservationRecord
            {
                WindowHandleEvidence = handle, RootOwnerHandleEvidence = root, ProcessId = 42,
                ProcessInstanceEvidence = $"pid:42:started:{target.ProcessStartedUtc.UtcDateTime:O}",
                WindowEvidenceId = "window-" + handle, Title = "window " + handle
            };
            window.OwnerChainEvidence.AddRange(owners);
            return window;
        }
        var foreign = Window("60", "10", "20"); foreign.ProcessId = 43;
        var reused = Window("70", "10", "20"); reused.ProcessInstanceEvidence = "pid:42:started:older";
        // Deliberately reverse the ownership order. 40 is owned by booking 30;
        // Exercise deeper owner links even when GA_ROOTOWNER stops at each window.
        var windows = new[] { Window("40", "40", "30"), Window("30", "30", "20"),
            Window("20", "20", "10"), Window("10", "10"), Window("50", "50"), foreign, reused,
            Window("80", "80", "70"), Window("90", "90", "91"), Window("91", "91", "90") };
        var before = windows.Select(window => window.ToByteArray()).ToArray();

        var selected = NativeRecorderCapture.SelectTargetWindows(windows, target);

        Assert.Equal(new[] { "40", "30", "20", "10" }, selected.Select(window => window.WindowHandleEvidence));
        for (var index = 0; index < windows.Length; index++) Assert.Equal(before[index], windows[index].ToByteArray());
        Assert.Equal("30", selected[1].RootOwnerHandleEvidence);
        Assert.Equal("20", Assert.Single(selected[1].OwnerChainEvidence));
    }
}
