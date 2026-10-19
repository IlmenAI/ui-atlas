using System.IO;

namespace UiAtlas.Shell.Windows;

public enum WindowsApplicationKind
{
    WindowsShell,
    WindowsInboxApp,
    WebBrowser,
    InstalledApp,
    UnattributedUi,
}

public readonly record struct WindowsApplicationClassification(
    WindowsApplicationKind Kind,
    string DisplayName,
    bool IsInfrastructure = false);

/// <summary>
/// Shared Windows application taxonomy for Diagnostics and product-facing window selection.
/// Classification is intentionally based on stable process, package, and shell-class evidence.
/// </summary>
public static class WindowsApplicationClassifier
{
    private static readonly HashSet<string> UserFacingShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
    };

    private static readonly HashSet<string> NonContentShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW",
    };

    private static readonly Dictionary<string, string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shellexperiencehost"] = "Windows Shell Experience",
        ["startmenuexperiencehost"] = "Start",
        ["searchhost"] = "Windows Search",
        ["searchapp"] = "Windows Search",
    };

    private static readonly Dictionary<string, string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Mozilla Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["iexplore"] = "Internet Explorer",
    };

    private static readonly Dictionary<string, string> WindowsInboxProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = "File Explorer",
        ["taskmgr"] = "Task Manager",
        ["systemsettings"] = "Settings",
        ["notepad"] = "Notepad",
        ["mspaint"] = "Paint",
        ["snippingtool"] = "Snipping Tool",
        ["calculatorapp"] = "Calculator",
        ["winwordpad"] = "WordPad",
        ["photos"] = "Photos",
        ["windowscamera"] = "Camera",
    };

    private static readonly Dictionary<string, string> WindowsInboxPackagePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.WindowsCalculator_"] = "Calculator",
        ["Microsoft.Windows.Photos_"] = "Photos",
        ["Microsoft.WindowsCamera_"] = "Camera",
        ["Microsoft.WindowsNotepad_"] = "Notepad",
        ["Microsoft.Paint_"] = "Paint",
        ["Microsoft.ScreenSketch_"] = "Snipping Tool",
    };

    private static readonly HashSet<string> InfrastructureProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtimebroker", "dwm", "taskhostw", "backgroundtaskhost", "dllhost", "svchost",
        "csrss", "winlogon", "services", "crashpad_handler", "werfault", "textinputhost",
        "tabtip", "sihost", "lockapp", "clicktodo", "cpthost",
    };

    public static WindowsApplicationClassification Classify(
        string? executableName,
        string? windowClassName = null,
        string? packageFamilyName = null)
    {
        var process = NormalizeProcessName(executableName);
        var windowClass = (windowClassName ?? string.Empty).Trim();
        var packageFamily = (packageFamilyName ?? string.Empty).Trim();

        if (NonContentShellClasses.Contains(windowClass) || InfrastructureProcesses.Contains(process))
            return new(WindowsApplicationKind.UnattributedUi, FriendlyProcessName(process), true);

        if (string.Equals(process, "explorer", StringComparison.OrdinalIgnoreCase)
            && UserFacingShellClasses.Contains(windowClass))
            return new(WindowsApplicationKind.WindowsShell, "Windows Shell");

        if (ShellProcesses.TryGetValue(process, out var shellName))
            return new(WindowsApplicationKind.WindowsShell, shellName);
        if (BrowserProcesses.TryGetValue(process, out var browserName))
            return new(WindowsApplicationKind.WebBrowser, browserName);
        if (WindowsInboxProcesses.TryGetValue(process, out var inboxName))
            return new(WindowsApplicationKind.WindowsInboxApp, inboxName);

        var inboxPackage = WindowsInboxPackagePrefixes.FirstOrDefault(value =>
            packageFamily.StartsWith(value.Key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(inboxPackage.Key))
            return new(WindowsApplicationKind.WindowsInboxApp, inboxPackage.Value);
        if (string.Equals(process, "applicationframehost", StringComparison.OrdinalIgnoreCase))
            return new(WindowsApplicationKind.UnattributedUi, "Hosted Windows app");
        if (string.IsNullOrWhiteSpace(process))
            return new(WindowsApplicationKind.UnattributedUi, "Unknown application");
        return new(WindowsApplicationKind.InstalledApp, FriendlyProcessName(process));
    }

    private static string NormalizeProcessName(string? executableName) =>
        Path.GetFileNameWithoutExtension((executableName ?? string.Empty).Trim());

    private static string FriendlyProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return "Unknown application";
        return processName;
    }
}
