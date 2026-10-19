using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;

namespace UiAtlas.Core.Tests;

internal static class NativeDesktopFixture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("UI_ATLAS_NATIVE_TEST_LEFT") is null
            && Environment.GetEnvironmentVariable("UI_ATLAS_NATIVE_TEST_TOP") is null) return;
        // All fixture and overlay threads must share the production app's physical
        // coordinate context. A thread-only override leaves background probes virtualized.
        if (!SetProcessDpiAwarenessContext((nint)(-4))
            && !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), (nint)(-4)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Native qualification requires PerMonitorV2 before WPF starts.");
    }

    // Attended qualification may select a clear monitor without bypassing the
    // production visibility guard or modifying another application's windows.
    internal static void Place(Window window)
    {
        var left = Environment.GetEnvironmentVariable("UI_ATLAS_NATIVE_TEST_LEFT");
        var top = Environment.GetEnvironmentVariable("UI_ATLAS_NATIVE_TEST_TOP");
        if (left is null && top is null) return;
        if (!double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.IsFinite(x)
            || !double.TryParse(top, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(y))
            throw new InvalidOperationException("Both native test coordinates must be finite WPF desktop coordinates.");
        window.Left = x;
        window.Top = y;
        Console.WriteLine(FormattableString.Invariant($"Native fixture placement: {window.Title}; left={x}; top={y}."));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
