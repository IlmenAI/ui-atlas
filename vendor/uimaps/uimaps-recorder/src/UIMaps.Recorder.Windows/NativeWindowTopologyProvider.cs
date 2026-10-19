using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed class NativeWindowTopologyProvider : IWindowTopologyProvider
{
    public Task<WindowTopologyPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken)
        => CaptureWindowsAsync(display, policy, null, cancellationToken);

    public Task<WindowTopologyPacket> CaptureWindowAsync(DisplayDescriptor display, long windowHandle, RecorderPolicy policy, CancellationToken cancellationToken)
        => CaptureWindowsAsync(display, policy, new IntPtr(windowHandle), cancellationToken);

    private static Task<WindowTopologyPacket> CaptureWindowsAsync(DisplayDescriptor display, RecorderPolicy policy,
        IntPtr? exactWindow, CancellationToken cancellationToken)
    {
        var packet = new WindowTopologyPacket { Health = ProtocolHealth.Complete() };
        var observed = 0;
        var zOrder = 0;
        var catalog = new List<WindowObservationRecord>();
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var processEvidence = new Dictionary<uint, ProcessEvidence>();
        bool ObserveWindow(IntPtr hwnd, IntPtr unused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed++;
            if (!NativeWindowMethods.GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            var windowBounds = NativeMethods.ToRect(rect);
            var extendedFrameResult = NativeWindowMethods.DwmGetWindowAttribute(
                hwnd, NativeWindowMethods.DwmwaExtendedFrameBounds, out NativeMethods.NativeRect extendedFrameRect,
                Marshal.SizeOf<NativeMethods.NativeRect>());
            var bounds = ResolveVisualBounds(windowBounds,
                extendedFrameResult == 0 ? NativeMethods.ToRect(extendedFrameRect) : null);
            var intersection = Intersect(bounds, display.BoundsPx);
            NativeWindowMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var title = ReadText(hwnd, NativeWindowMethods.GetWindowTextLength, NativeWindowMethods.GetWindowText);
            var className = ReadClass(hwnd);
            if (!processEvidence.TryGetValue(pid, out var evidence))
            {
                evidence = ReadProcessEvidence(pid, currentSessionId);
                processEvidence[pid] = evidence;
            }

            var cloaked = 0;
            _ = NativeWindowMethods.DwmGetWindowAttribute(hwnd, NativeWindowMethods.DwmwaCloaked, out cloaked, sizeof(int));
            var owner = NativeWindowMethods.GetWindow(hwnd, 4);
            var rootOwner = NativeWindowMethods.GetAncestor(hwnd, 3);
            var visible = NativeWindowMethods.IsWindowVisible(hwnd);
            var minimized = NativeWindowMethods.IsIconic(hwnd);
            var intersects = IsMeaningfulIntersection(intersection);
            var foreground = NativeWindowMethods.GetForegroundWindow() == hwnd;
            var style = NativeWindowMethods.GetWindowLongPtr(hwnd, -20).ToInt64();
            var eligibility = !evidence.SameSession ? "excluded_wrong_session"
                : !intersects && owner == IntPtr.Zero ? "excluded_not_intersecting_display"
                : minimized ? "not_observable_minimized"
                : cloaked != 0 ? "not_observable_cloaked"
                : !visible ? "catalog_only_owner_context"
                : "eligible";
            var record = new WindowObservationRecord
            {
                WindowEvidenceId = Guid.NewGuid().ToString("N"),
                CaptureWindowId = Guid.NewGuid().ToString("N"),
                ProcessId = pid,
                ProcessName = evidence.ProcessName,
                ExecutablePath = evidence.ExecutablePath,
                WindowHandleEvidence = hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                Title = title,
                ClassName = className,
                BoundsPx = bounds,
                IntersectionWithDisplayPx = intersection,
                ZOrder = zOrder,
                IsVisible = visible,
                IsMinimized = minimized,
                IsCloakedOrUnavailable = cloaked != 0,
                ProcessStartTimeEvidence = evidence.ProcessStartTime,
                ProcessInstanceEvidence = evidence.ProcessInstance,
                ApplicationUserModelId = evidence.ApplicationUserModelId,
                PackageFamilyName = evidence.PackageFamilyName,
                RootOwnerHandleEvidence = rootOwner.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsForeground = foreground,
                IsActive = foreground,
                IsToolWindow = (style & 0x80L) != 0,
                WindowStyle = NativeWindowMethods.GetWindowLongPtr(hwnd, -16).ToInt64(),
                WindowExStyle = style,
                Dpi = GetDpiForWindow(hwnd),
                IsEnabled = IsWindowEnabled(hwnd),
                IsOwnedWindow = owner != IntPtr.Zero,
                SelectionEligibility = eligibility,
                CaptureRelevance = eligibility == "eligible" ? "active_probe" : "catalog_only",
                Health = ProtocolHealth.Complete(),
            };
            if (owner != IntPtr.Zero) record.OwnerChainEvidence.Add(owner.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            catalog.Add(record);
            zOrder++;
            return true;
        }

        if (exactWindow is { } target)
        {
            // A fresh native observation; no cached identity or desktop census.
            if (target != IntPtr.Zero) ObserveWindow(target, IntPtr.Zero);
        }
        else NativeWindowMethods.EnumWindows(ObserveWindow, IntPtr.Zero);

        var included = ApplyPolicy(catalog, policy);
        packet.Windows.AddRange(included);
        packet.WindowLimited = included.Count < catalog.Count;
        packet.WindowsObserved = observed;
        packet.WindowsIncluded = packet.Windows.Count;
        if (packet.WindowLimited)
        {
            packet.Health = ProtocolHealth.Single(HealthState.WindowLimited, "RECORDER_WINDOW_LIMIT_REACHED", "Window capture reached its configured limit.", "window_topology");
        }
        return Task.FromResult(packet);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    internal static List<WindowObservationRecord> ApplyPolicy(IEnumerable<WindowObservationRecord> windows, RecorderPolicy policy)
    {
        var ordered = windows
            .OrderByDescending(x => policy.TargetWindowHandleEvidence.Contains(x.WindowHandleEvidence))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(policy.TargetProcessInstanceEvidence)
                && string.Equals(x.ProcessInstanceEvidence, policy.TargetProcessInstanceEvidence, StringComparison.Ordinal))
            // The bounded slots drive downstream probes. Catalog-only HWNDs must not crowd
            // observable display windows out merely because EnumWindows returned them first.
            .ThenByDescending(IsActiveProbeCandidate)
            .ThenBy(x => x.ZOrder)
            .Select(x => x.Clone())
            .ToList();
        if (policy.ExhaustiveCoverage) return ordered;
        var mandatory = ordered.Where(x => IsApplicationMemberCandidate(x, policy)).ToList();
        return mandatory.Concat(ordered.Where(x => !IsApplicationMemberCandidate(x, policy)))
            .Take(Math.Max(policy.MaxWindows, mandatory.Count)).ToList();
    }

    private static bool IsApplicationMemberCandidate(WindowObservationRecord window, RecorderPolicy policy) =>
        policy.TargetWindowHandleEvidence.Contains(window.WindowHandleEvidence)
        || (!string.IsNullOrWhiteSpace(policy.TargetProcessInstanceEvidence)
            && string.Equals(window.ProcessInstanceEvidence, policy.TargetProcessInstanceEvidence, StringComparison.Ordinal))
        || policy.TargetWindowHandleEvidence.Contains(window.RootOwnerHandleEvidence)
        || window.OwnerChainEvidence.Any(policy.TargetWindowHandleEvidence.Contains);

    private static bool IsActiveProbeCandidate(WindowObservationRecord window) =>
        string.IsNullOrWhiteSpace(window.SelectionEligibility)
        || string.Equals(window.SelectionEligibility, "eligible", StringComparison.Ordinal);

    private static ProcessEvidence ReadProcessEvidence(uint pid, int currentSessionId)
    {
        var processName = string.Empty;
        var executablePath = string.Empty;
        var processStart = string.Empty;
        var processInstance = $"pid:{pid}:unknown-start";
        var applicationUserModelId = string.Empty;
        var packageFamilyName = string.Empty;
        var sameSession = false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
            sameSession = process.SessionId == currentSessionId;
            try
            {
                processStart = process.StartTime.ToUniversalTime().ToString("O");
                processInstance = $"pid:{pid}:started:{processStart}";
            }
            catch { }
            try { executablePath = process.MainModule?.FileName ?? string.Empty; } catch { }
            var queryHandle = NativeWindowMethods.OpenProcess(0x1000, false, pid);
            if (queryHandle != IntPtr.Zero)
            {
                try
                {
                    applicationUserModelId = ReadApplicationUserModelId(queryHandle);
                    packageFamilyName = ReadPackageFamilyName(queryHandle);
                }
                finally
                {
                    _ = NativeWindowMethods.CloseHandle(queryHandle);
                }
            }
        }
        catch { }
        return new ProcessEvidence(processName, executablePath, processStart, processInstance,
            applicationUserModelId, packageFamilyName, sameSession);
    }

    private static string ReadApplicationUserModelId(IntPtr processHandle)
    {
        uint length = 0;
        var result = NativeWindowMethods.GetApplicationUserModelId(processHandle, ref length, null);
        if (result != 122 || length == 0) return string.Empty;
        var value = new StringBuilder((int)length);
        return NativeWindowMethods.GetApplicationUserModelId(processHandle, ref length, value) == 0
            ? value.ToString()
            : string.Empty;
    }

    private static string ReadPackageFamilyName(IntPtr processHandle)
    {
        uint length = 0;
        var result = NativeWindowMethods.GetPackageFamilyName(processHandle, ref length, null);
        if (result != 122 || length == 0) return string.Empty;
        var value = new StringBuilder((int)length);
        return NativeWindowMethods.GetPackageFamilyName(processHandle, ref length, value) == 0
            ? value.ToString()
            : string.Empty;
    }

    private sealed record ProcessEvidence(
        string ProcessName,
        string ExecutablePath,
        string ProcessStartTime,
        string ProcessInstance,
        string ApplicationUserModelId,
        string PackageFamilyName,
        bool SameSession);

    private static RectPx Intersect(RectPx left, RectPx right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var r = Math.Min(left.X + left.Width, right.X + right.Width);
        var b = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new RectPx { X = x, Y = y, Width = Math.Max(0, r - x), Height = Math.Max(0, b - y) };
    }

    internal static RectPx ResolveVisualBounds(RectPx windowBounds, RectPx? extendedFrameBounds) =>
        extendedFrameBounds is { Width: > 0, Height: > 0 } ? extendedFrameBounds.Clone() : windowBounds.Clone();

    internal static bool IsMeaningfulIntersection(RectPx intersection) =>
        intersection.Width >= 16 && intersection.Height >= 16;

    private static string ReadText(IntPtr hwnd, Func<IntPtr, int> length, Func<IntPtr, StringBuilder, int, int> read)
    {
        var builder = new StringBuilder(Math.Min(length(hwnd) + 1, 32_768));
        _ = read(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ReadClass(IntPtr hwnd)
    {
        var builder = new StringBuilder(512);
        _ = NativeWindowMethods.GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
}

internal static class NativeWindowMethods
{
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(NativeMethods.EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.NativeRect rect);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder value, int maxCount);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.NativeRect value, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(IntPtr processHandle, ref uint applicationUserModelIdLength, StringBuilder? applicationUserModelId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPackageFamilyName(IntPtr processHandle, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}
