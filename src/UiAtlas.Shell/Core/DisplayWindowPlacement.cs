using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Size = System.Drawing.Size;
using Screen = System.Windows.Forms.Screen;

namespace UIMaps.Application.Core;

public static class DisplayWindowPlacement
{
    public static void CenterWindow(Window window, string? displayName = null)
    {
        var screen = Screen.AllScreens.FirstOrDefault(value => value.DeviceName == displayName) ?? Screen.PrimaryScreen;
        var handle = new WindowInteropHelper(window).Handle;
        if (screen is null || !GetWindowRect(handle, out var bounds)) return;
        var rectangle = Center(screen.WorkingArea, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        SetWindowPos(handle, 0, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, 0x14);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    public static Rectangle Center(Rectangle displayBounds, Size requestedSize)
    {
        if (displayBounds.Width <= 0 || displayBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayBounds), "Display bounds must have a positive size.");

        var width = Math.Clamp(requestedSize.Width, 1, displayBounds.Width);
        var height = Math.Clamp(requestedSize.Height, 1, displayBounds.Height);
        var x = displayBounds.Left + ((displayBounds.Width - width) / 2);
        var y = displayBounds.Top + ((displayBounds.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }
}
