namespace UIMaps.Application.Core;

public sealed record CapabilityLaunchResult(bool Success, string Message, bool Cancelled = false, IReadOnlyList<string>? MapDraftRefs = null);

public sealed record DiagnosticsCapability(bool Available, string? ExecutablePath, string Message);

public interface IUiMapsCapabilityLauncher
{
    bool DiagnosticsAvailable { get; }
    bool SupportsFullDiagnosticsWorkspace => true;
    CapabilityLaunchResult? OpenMap(MapsLibraryRecord record) => null;
    CapabilityLaunchResult OpenUiMapTools();
    CapabilityLaunchResult OpenDiagnostics();
    CapabilityLaunchResult OpenAmbientDiagnostics(RecordableApplicationTarget target);
    Task<CapabilityLaunchResult> RunUiMapRecordingAsync(RecordableApplicationTarget target, CancellationToken cancellationToken);
}

