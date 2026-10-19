using System.Diagnostics;
using System.Globalization;
using System.IO;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal static class DesktopShellCommand
{
    internal static int Open(string[] args)
    {
        if (args.Length != 0 && !(args.Length == 2 && args[0] == "--workspace" && args[1].ToLowerInvariant() is "maps" or "diagnostics"))
            throw new ArgumentException("Usage: ui-atlas app [--workspace maps|diagnostics]");
        var assembly = ResolveLauncher(AppContext.BaseDirectory);
        var executable = Path.ChangeExtension(assembly, ".exe");
        var start = new ProcessStartInfo(File.Exists(executable) ? executable : "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(assembly)! };
        if (!File.Exists(executable)) start.ArgumentList.Add(assembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("UI Atlas could not start.");
        return 0;
    }

    internal static string ResolveLauncher(string baseDirectory)
    {
        var start = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        var candidates = new List<string> { Path.Combine(start.FullName, "launcher", "UiAtlas.dll"),
            Path.Combine(start.FullName, "UiAtlas.dll"), Path.Combine(start.Parent?.FullName ?? start.FullName, "UiAtlas.dll") };
        var configuration = start.FullName.Split(Path.DirectorySeparatorChar).Contains("Release") ? "Release" : "Debug";
        for (var current = start; current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "UiAtlas.Core.slnx")))
                candidates.Add(Path.Combine(current.FullName, "src", "Launcher", "bin", configuration, "net10.0-windows10.0.19041.0", "UiAtlas.dll"));
        return candidates.FirstOrDefault(path => File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
            ?? throw new FileNotFoundException("The UI Atlas desktop is not installed. Build the Launcher project or install the complete UI Atlas package.");
    }

    internal static WindowTarget CheckedTarget(long hwnd, string processId, string processStarted)
    {
        var target = WindowCatalog.Resolve(hwnd);
        if (target.ProcessId != int.Parse(processId, CultureInfo.InvariantCulture)
            || target.ProcessStartedUtc != DateTimeOffset.Parse(processStarted, CultureInfo.InvariantCulture))
            throw new InvalidOperationException("The selected application instance changed. Select it again.");
        return target;
    }

    internal static Task<int> SettingsAsync()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { new System.Windows.Application().Run(new AtlasSettingsWindow()); completion.SetResult(0); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
