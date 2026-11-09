using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using UiAtlas.Catalog;
using UIMaps.Application.Core;

namespace UiAtlas.Shell.Catalog;

/// <summary>Runs the matching Atlas deployment in its own process and dependency context.</summary>
public sealed class AtlasCatalogClient(string? root = null, string? executable = null)
{
    public string Root { get; } = AtlasCatalogLocation.Resolve(root);
    public string Executable => ResolveExecutable(executable);
    public static string ResolveExecutable(string? explicitPath = null, string? baseDirectory = null)
    {
        var supplied = explicitPath ?? Environment.GetEnvironmentVariable("UIMAPS_ATLAS_EXE");
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var candidates = supplied is null
            ? new[] { Path.Combine(directory, "tools", "ui-atlas.dll"), Path.Combine(directory, "atlas", "ui-atlas.dll"), Path.Combine(directory, "ui-atlas.dll") }
            : new[] { supplied };
        foreach (var value in candidates)
        {
            var path = Path.GetFullPath(value);
            var parent = Path.GetDirectoryName(path)!;
            if (Path.GetFileName(path) is not ("ui-atlas.dll" or "ui-atlas.exe")) continue;
            if (File.Exists(path) && File.Exists(Path.Combine(parent, "ui-atlas.runtimeconfig.json"))
                && File.Exists(Path.Combine(parent, "ui-atlas.deps.json"))
                && File.Exists(Path.Combine(parent, "native-recorder", "UIMaps.Recorder.ProviderProbe.dll"))
                && (File.Exists(Path.Combine(parent, "UiAtlas.Core.Desktop.dll")) || File.Exists(Path.Combine(parent, "viewer", "UiAtlas.Core.Desktop.dll")))) return path;
        }
        throw new FileNotFoundException("The complete UI Atlas tools deployment is missing. Rebuild or reinstall the launcher, or set UIMAPS_ATLAS_EXE.");
    }
    public ProcessStartInfo StartInfo(params string[] arguments)
    {
        var path = Executable;
        var start = new ProcessStartInfo(path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(path);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["UI-ATLAS_DATA_HOME"] = Root;
        if (UiAtlas.Presentation.AtlasSettingsNavigation.CurrentPipeName is { } settingsPipe)
            start.Environment[UiAtlas.Presentation.AtlasSettingsNavigation.EnvironmentKey] = settingsPipe;
        return start;
    }
    public async Task<CatalogReply> RequestAsync(CatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var start = StartInfo("catalog");
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = new UTF8Encoding(false);
        using var process = Process.Start(start) ?? throw new IOException("UI Atlas catalog could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request with { Root = Root })).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        // Cancellation is cooperative so Atlas can unwind a build without interrupting publication.
        using var registration = token.Register(() =>
        {
            try { process.StandardInput.WriteLine("cancel"); process.StandardInput.Flush(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { }
        });
        await process.WaitForExitAsync().ConfigureAwait(false);
        var text = await output.ConfigureAwait(false); var errorText = await errors.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        CatalogReply? result;
        try { result = JsonSerializer.Deserialize<CatalogReply>(text); }
        catch (JsonException) { throw new IOException("UI Atlas catalog returned an invalid response. " + errorText); }
        if (result is null || result.Version != 1) throw new IOException("The Atlas tools and shell use incompatible catalog versions. Reinstall a matching deployment.");
        if (!result.Success || process.ExitCode != 0) throw new IOException(result.Message);
        return result;
    }
    public CapabilityLaunchResult Start(params string[] arguments)
    {
        try
        {
            using var process = Process.Start(StartInfo(arguments));
            return process is null ? new(false, "UI Atlas did not start.") : new(true, "UI Atlas opened.");
        }
        catch (Exception error) { return new(false, error.GetBaseException().Message); }
    }
    public ProcessStartInfo RecordingStartInfo(RecordableApplicationTarget target, DateTimeOffset started) =>
        StartInfo("recording", "start", target.WindowHandle.ToString(CultureInfo.InvariantCulture),
            "--expected-process-id", target.ProcessId.ToString(CultureInfo.InvariantCulture), "--expected-process-start", started.ToString("O", CultureInfo.InvariantCulture));
    public CapabilityLaunchResult OpenMap(MapsLibraryRecord record) => record.LocalPath is { } path
        ? Start("open", path) : new(false, "Refresh Maps to resolve its Atlas workspace.");
    public CapabilityLaunchResult OpenLibrary()
    {
        try
        {
            var shell = Environment.GetEnvironmentVariable("UIMAPS_SHELL_EXE");
            if (string.IsNullOrEmpty(shell))
            {
                for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null && shell is null; directory = directory.Parent)
                    shell = new[] { Path.Combine(directory.FullName, "MapLock.exe"), Path.Combine(directory.FullName, "application", "MapLock.exe"),
                        Path.Combine(directory.FullName, "uimaps-application", "src", "Launcher", "bin", "Debug", "net10.0-windows10.0.19041.0", "MapLock.exe"),
                        Path.Combine(directory.FullName, "uimaps-application", "src", "Launcher", "bin", "Release", "net10.0-windows10.0.19041.0", "MapLock.exe") }.FirstOrDefault(File.Exists);
            }
            if (shell is null || !File.Exists(shell)) return new(false, "Open Maps from the UI Atlas or MapLock Launcher. The old Diagnostics map authoring workflow has been retired.");
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--workspace"); start.ArgumentList.Add("maps");
            start.Environment["UI-ATLAS_DATA_HOME"] = Root;
            using var process = Process.Start(start);
            return process is null ? new(false, "The map library did not open.") : new(true, "Shared Atlas map library opened.");
        }
        catch (Exception error) { return new(false, error.GetBaseException().Message); }
    }
    public async Task<CapabilityLaunchResult> RecordAsync(RecordableApplicationTarget target, CancellationToken token)
    {
        try
        {
            if (target.ProcessStartedUtc is not { } expected) return new(false, "Refresh the application list to verify this application's identity.");
            using var targetProcess = Process.GetProcessById(target.ProcessId);
            if (new DateTimeOffset(targetProcess.StartTime.ToUniversalTime()) != expected) return new(false, "The application instance changed. Refresh the application list.");
            var before = (await RequestAsync(new("maps-summary", Root), token)).Maps.Select(map => map.Draft).ToHashSet();
            var start = RecordingStartInfo(target, expected);
            using var recorder = Process.Start(start) ?? throw new IOException("UI Atlas recording did not start.");
            await recorder.WaitForExitAsync(); // Recorder owns its save/cancel workflow.
            if (recorder.ExitCode == 3) return new(false, "Recording cancelled.", Cancelled: true);
            if (recorder.ExitCode != 0) return new(false, $"UI Atlas exited with status {recorder.ExitCode}. Saved evidence remains in Recordings.");
            var after = await RequestAsync(new("maps-summary", Root), token);
            var drafts = after.Maps.Where(map => !before.Contains(map.Draft)).Select(map =>
                UIMaps.Protocol.V3.MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(map.Entry)).MapDraftRef).Where(value => value.Length > 0).ToArray();
            return drafts.Length == 0 ? new(false, "Recording closed without a new map. Saved evidence is available in Recordings.", Cancelled: true)
                : new(true, "Recording and map saved in the shared UI Atlas library.", MapDraftRefs: drafts);
        }
        catch (Exception error) { return new(false, error.GetBaseException().Message); }
    }
}
