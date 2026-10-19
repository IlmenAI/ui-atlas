using System.Runtime.InteropServices;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed class NativeDisplayCatalog : IDisplayCatalog
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public Task<IReadOnlyList<DisplayDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var displays = new List<DisplayDescriptor>();
        var index = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var width = info.Monitor.Right - info.Monitor.Left;
            var height = info.Monitor.Bottom - info.Monitor.Top;
            var dpiX = 96u;
            var dpiY = 96u;
            _ = NativeMethods.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);
            var display = new DisplayDescriptor
            {
                DisplayId = $"{_sessionId}:{index++}",
                BoundsPx = NativeMethods.ToRect(info.Monitor),
                WidthPx = width,
                HeightPx = height,
                DpiX = dpiX,
                DpiY = dpiY,
                ScaleFactorX = dpiX / 96d,
                ScaleFactorY = dpiY / 96d,
                IsPrimary = (info.Flags & 1) != 0,
                Health = ProtocolHealth.Complete(),
            };
            display.DeviceEvidence.Add(new StringProperty { Key = "deviceName", Value = info.DeviceName.TrimEnd('\0') });
            var friendlyName = NativeMethods.GetMonitorFriendlyName(info.DeviceName);
            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                display.DeviceEvidence.Add(new StringProperty { Key = "monitorFriendlyName", Value = friendlyName });
            }
            displays.Add(display);
            return true;
        }, IntPtr.Zero);

        return Task.FromResult<IReadOnlyList<DisplayDescriptor>>(displays);
    }
}

internal static class NativeMethods
{
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(string deviceName, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    internal static string? GetMonitorFriendlyName(string deviceName)
    {
        string? firstDescription = null;
        for (uint index = 0; index < 16; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(deviceName, index, ref device, 0)) break;

            var description = device.DeviceString?.TrimEnd('\0').Trim();
            if (string.IsNullOrWhiteSpace(description)) continue;
            firstDescription ??= description;
            if ((device.StateFlags & 1) != 0) return description;
        }

        return firstDescription;
    }

    internal static RectPx ToRect(NativeRect value) => new()
    {
        X = value.Left,
        Y = value.Top,
        Width = Math.Max(0, value.Right - value.Left),
        Height = Math.Max(0, value.Bottom - value.Top),
    };
}
