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
        // Desktop fixtures must not inherit a real user's automatic cloud-reading
        // preference or read/write the production recorder settings file.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UIATLAS_HEADER_SETTINGS_PATH")))
            Environment.SetEnvironmentVariable("UIATLAS_HEADER_SETTINGS_PATH", System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "uiatlas-desktop-tests", Guid.NewGuid().ToString("N"), "header-settings.json"));
        // Match the production manifest for every desktop fixture, including default
        // placement. Otherwise Show/layout can revirtualize a hit HWND after a native
        // physical move on monitors with different scales.
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
