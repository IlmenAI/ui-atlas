using System.Diagnostics;
using System.Drawing;
using System.IO;
using UiAtlas.Shell.Windows;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;
using FormsScreen = System.Windows.Forms.Screen;

namespace UIMaps.Application.Core;

public sealed record RecordableApplicationTarget(
    long WindowHandle,
    int ProcessId,
    string DisplayName,
    string WindowTitle,
    string ExecutableName,
    string DisplayLabel,
    Rectangle Bounds,
    string Category = "Installed Apps")
{
    public bool RecordingDiagnosticsEnabled { get; init; }
    public DateTimeOffset? ProcessStartedUtc { get; init; }
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : char.ToUpperInvariant(DisplayName.Trim()[0]).ToString();
    public string ScopeSummary => $"{ExecutableName}  ·  {DisplayLabel}";
    public int CategoryOrder => Category switch
    {
        "Windows Shell" => 0,
        "Windows Inbox Apps" => 1,
        "Web Browsers" => 2,
        "Installed Apps" => 3,
        _ => 4,
    };
}

public interface IRecordableApplicationDiscovery
{
    IReadOnlyList<RecordableApplicationTarget> Discover(RecordingDisplayScope? displayScope = null);
}

public sealed class RecordableApplicationDiscovery : IRecordableApplicationDiscovery
{
    private readonly IWindowTopologyProvider _windowTopology;

    public RecordableApplicationDiscovery() : this(new NativeWindowTopologyProvider()) { }

    internal RecordableApplicationDiscovery(IWindowTopologyProvider windowTopology)
    {
        _windowTopology = windowTopology;
    }

    public IReadOnlyList<RecordableApplicationTarget> Discover(RecordingDisplayScope? displayScope = null)
    {
        var display = CreateDisplayDescriptor(displayScope);
        var packet = _windowTopology
            .CaptureAsync(display, RecorderPolicy.Default, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return Project(
            packet.Windows,
            Environment.ProcessId,
            Process.GetCurrentProcess().ProcessName + ".exe");
    }

    internal static IReadOnlyList<RecordableApplicationTarget> Project(
        IEnumerable<WindowObservationRecord> windows,
        int currentProcessId,
        string currentExecutableName = "UIMaps.Application.exe") => windows
        .Select(value => (Window: value, Classification: WindowsApplicationClassifier.Classify(
            value.ProcessName, value.ClassName, value.PackageFamilyName)))
        .Where(value => IsOnSelectedDisplay(value.Window))
        .Where(value => value.Window.ProcessId > 0 && value.Window.ProcessId != currentProcessId)
        .Where(value => !value.Classification.IsInfrastructure)
        .Where(value => value.Classification.Kind != WindowsApplicationKind.UnattributedUi)
        .Where(value => !string.IsNullOrWhiteSpace(value.Window.Title)
            || value.Classification.Kind == WindowsApplicationKind.WindowsShell)
        .Where(value => !IsUiMapsProcess(value.Window.ProcessName, currentExecutableName))
        .GroupBy(value => value.Window.WindowHandleEvidence, StringComparer.Ordinal)
        .Select(group => group.First())
        .Select(value =>
        {
            var bounds = ToRectangle(value.Window.BoundsPx);
            var executable = ExecutableName(value.Window.ProcessName);
            return new RecordableApplicationTarget(
                ParseHandle(value.Window.WindowHandleEvidence),
                unchecked((int)value.Window.ProcessId),
                FriendlyApplicationName(value.Window.Title, executable, value.Classification),
                string.IsNullOrWhiteSpace(value.Window.Title) ? value.Classification.DisplayName : value.Window.Title,
                executable,
                DisplayLabel(bounds),
                bounds,
                CategoryLabel(value.Classification.Kind))
            { ProcessStartedUtc = value.Window.ProcessInstanceEvidence.Split(":started:") is [_, var timestamp]
                && DateTimeOffset.TryParse(timestamp, out var started) ? started : null };
        })
        .OrderBy(value => value.CategoryOrder)
        .ThenBy(value => value.DisplayLabel, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.WindowTitle, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal static DisplayDescriptor CreateDisplayDescriptor(RecordingDisplayScope? displayScope)
    {
        var bounds = displayScope?.MonitoredBounds ?? VirtualDisplayBounds();
        var descriptor = new DisplayDescriptor
        {
            DisplayId = displayScope?.ScopeId ?? "all",
            BoundsPx = ToRectPx(bounds),
            WidthPx = bounds.Width,
            HeightPx = bounds.Height,
            DpiX = 96,
            DpiY = 96,
            ScaleFactorX = 1,
            ScaleFactorY = 1,
        };
        if (displayScope is not null)
        {
            descriptor.DeviceEvidence.AddRange(displayScope.DeviceNames.Select(value =>
                new StringProperty { Key = "deviceName", Value = value }));
        }
        return descriptor;
    }

    private static bool IsOnSelectedDisplay(WindowObservationRecord value) =>
        (string.IsNullOrWhiteSpace(value.SelectionEligibility)
            || string.Equals(value.SelectionEligibility, "eligible", StringComparison.Ordinal))
        && value.IsVisible
        && !value.IsMinimized
        && !value.IsCloakedOrUnavailable
        && value.IntersectionWithDisplayPx is { Width: >= 16, Height: >= 16 };

    private static string FriendlyApplicationName(
        string title,
        string executableName,
        WindowsApplicationClassification classification)
    {
        if (classification.Kind is not WindowsApplicationKind.InstalledApp and not WindowsApplicationKind.UnattributedUi)
            return classification.DisplayName;
        var separator = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (separator >= 0 && separator + 3 < title.Length)
            return title[(separator + 3)..].Trim();
        var executable = Path.GetFileNameWithoutExtension(executableName);
        return string.IsNullOrWhiteSpace(executable) ? title : executable;
    }

    private static string CategoryLabel(WindowsApplicationKind kind) => kind switch
    {
        WindowsApplicationKind.WindowsShell => "Windows Shell",
        WindowsApplicationKind.WindowsInboxApp => "Windows Inbox Apps",
        WindowsApplicationKind.WebBrowser => "Web Browsers",
        WindowsApplicationKind.InstalledApp => "Installed Apps",
        _ => "Installed Apps",
    };

    private static bool IsUiMapsProcess(string processName, string currentExecutableName)
    {
        var executableName = ExecutableName(processName);
        return executableName.StartsWith("UIMaps.", StringComparison.OrdinalIgnoreCase)
            || executableName.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("ui-atlas.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("UiAtlas.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("MapLock.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals(currentExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExecutableName(string processName)
    {
        var value = Path.GetFileName(processName.Trim());
        return string.IsNullOrWhiteSpace(value) || Path.HasExtension(value) ? value : value + ".exe";
    }

    private static long ParseHandle(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var handle)
            ? handle
            : 0;

    private static string DisplayLabel(Rectangle bounds)
    {
        var display = FormsScreen.FromRectangle(bounds);
        return display.Primary ? "This display" : display.DeviceName;
    }

    private static Rectangle VirtualDisplayBounds()
    {
        var displays = FormsScreen.AllScreens.Select(value => value.Bounds).ToArray();
        return displays.Length == 0
            ? new Rectangle(0, 0, 1, 1)
            : displays.Aggregate(Rectangle.Union);
    }

    private static Rectangle ToRectangle(RectPx? value) => value is null
        ? Rectangle.Empty
        : new Rectangle(value.X, value.Y, Math.Max(0, value.Width), Math.Max(0, value.Height));

    private static RectPx ToRectPx(Rectangle value) => new()
    {
        X = value.Left,
        Y = value.Top,
        Width = value.Width,
        Height = value.Height,
    };
}
