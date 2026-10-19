using System.IO;
using System.Windows;
using UiAtlas.Shell.Catalog;
using UIMaps.Application.Core;

namespace UiAtlas.Launcher;

public sealed class AtlasCapabilityLauncher(AtlasCatalogClient catalog) : IUiMapsCapabilityLauncher
{
    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools");
    public bool DiagnosticsAvailable => File.Exists(Path.Combine(ToolsDirectory, "native-recorder", "UIMaps.Recorder.ProviderProbe.dll"));
    public bool SupportsFullDiagnosticsWorkspace => false;
    public CapabilityLaunchResult OpenUiMapTools() => catalog.Start("recording", "start");
    public CapabilityLaunchResult? OpenMap(MapsLibraryRecord record) => catalog.OpenMap(record);
    public Task<CapabilityLaunchResult> RunUiMapRecordingAsync(RecordableApplicationTarget target, CancellationToken token) => catalog.RecordAsync(target, token);
    public CapabilityLaunchResult OpenDiagnostics()
    {
        var picker = new UIMaps.Application.NewUiMapWindow { Title = "Choose an application to diagnose" };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) picker.Owner = owner;
        return picker.ShowDialog() == true && picker.SelectedTarget is { } target
            ? OpenAmbientDiagnostics(target) : new(false, "Diagnostics cancelled.", Cancelled: true);
    }
    public CapabilityLaunchResult OpenAmbientDiagnostics(RecordableApplicationTarget target)
    {
        if (!DiagnosticsAvailable) return new(false, "The native diagnostic worker is missing. Rebuild or reinstall the complete Launcher.");
        new AtlasDiagnosticsWindow(target).Show();
        return new(true, "Application diagnostics opened.");
    }
}
