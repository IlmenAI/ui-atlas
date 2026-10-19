using System.Runtime.InteropServices;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class PopupWindowEventMonitorTests
{
    [Fact]
    public void NativeEventsSurviveCollectionAndRepeatedRestart()
    {
        // A tiny off-screen synthetic HWND: do not activate or edit a user application.
        var hwnd = CreateWindowExW(0x08000080, "STATIC", "UiAtlas synthetic popup",
            0x90000000, -32000, -32000, 1, 1, 0, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                using var observed = new ManualResetEventSlim();
                uint installThread = 0;
                uint callbackThread = 0;
                uint removeThread = 0;
                var removed = false;
                using var monitor = new PopupWindowEventMonitor(window =>
                {
                    if (window != hwnd.ToInt64()) return;
                    callbackThread = NativeMethods.GetCurrentThreadId();
                    observed.Set();
                }, callback =>
                {
                    installThread = NativeMethods.GetCurrentThreadId();
                    // Include this synthetic test process; production skips its own process.
                    return NativeMethods.SetWinEventHook(NativeMethods.EventObjectShow,
                        NativeMethods.EventObjectShow, 0, callback, (uint)Environment.ProcessId,
                        0, NativeMethods.WineventOutofcontext);
                }, hook =>
                {
                    removeThread = NativeMethods.GetCurrentThreadId();
                    removed = NativeMethods.UnhookWinEvent(hook);
                    return removed;
                });
                monitor.Start();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                NotifyWinEvent(NativeMethods.EventObjectShow, hwnd, NativeMethods.ObjidWindow, 0);
                Assert.True(observed.Wait(TimeSpan.FromSeconds(5)), "The native event loop did not deliver the popup.");
                monitor.Dispose();
                Assert.True(removed, "Native hook removal failed.");
                Assert.Equal(installThread, callbackThread);
                Assert.Equal(installThread, removeThread);
                Assert.NotEqual(NativeMethods.GetCurrentThreadId(), installThread);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                // Exercise the same message-pumping boundary in the attached crash log.
                _ = NativeMethods.PeekMessageW(out _, 0, 0, 0, NativeMethods.PmNoRemove);
                _ = WindowCatalog.ListTopLevelWindows();
            }
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void FailedHookInstallationStopsThreadAndReportsFailure()
    {
        var removals = 0;
        using var monitor = new PopupWindowEventMonitor(_ => { }, _ => 0,
            _ => { removals++; return true; });
        Assert.Throws<InvalidOperationException>(monitor.Start);
        monitor.Dispose();
        Assert.Equal(0, removals);
        Assert.Throws<InvalidOperationException>(monitor.Start);
    }

    [Fact]
    public void DisposedMonitorCannotInstallAnotherHook()
    {
        var installs = 0;
        using var monitor = new PopupWindowEventMonitor(_ => { },
            _ => { installs++; return 1; }, _ => true);
        monitor.Dispose();
        Assert.Throws<InvalidOperationException>(monitor.Start);
        Assert.Equal(0, installs);
    }

    [Fact]
    public void CrossThreadDisposeRemovesHookOnInstallingThread()
    {
        uint installThread = 0;
        uint removeThread = 0;
        var removals = 0;
        using var monitor = new PopupWindowEventMonitor(_ => { },
            _ => { installThread = NativeMethods.GetCurrentThreadId(); return 1; },
            _ => { removeThread = NativeMethods.GetCurrentThreadId(); removals++; return true; });
        monitor.Start();
        var disposer = new Thread(monitor.Dispose);
        disposer.Start();
        Assert.True(disposer.Join(TimeSpan.FromSeconds(10)));

        Assert.NotEqual(0u, installThread);
        Assert.Equal(installThread, removeThread);
        monitor.Dispose();
        Assert.Equal(1, removals);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern void NotifyWinEvent(uint eventType, nint hwnd, int objectId, int childId);
}
