using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UiAtlas.Catalog;

public interface IAtlasCatalogReader
{
    string Root { get; }
    Task<CatalogReply> RequestAsync(CatalogRequest request, CancellationToken token = default);
}

/// <summary>Headless client for the Atlas-owned catalog and scoped parameter review.</summary>
public sealed class AtlasCatalogProcessClient(string? root = null, string? executable = null) : IAtlasCatalogReader
{
    public string Root { get; } = AtlasCatalogLocation.Resolve(root);
    public string Executable => ResolveExecutable(executable);
    public static string ResolveExecutable(string? explicitPath = null)
    {
        var supplied = explicitPath ?? Environment.GetEnvironmentVariable("UIMAPS_ATLAS_EXE");
        if (supplied is not null) return Validate(supplied);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            foreach (var relative in new[] { "atlas/ui-atlas.dll", "tools/ui-atlas.dll", "ui-atlas.dll" })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return Validate(candidate);
            }
        throw new FileNotFoundException("The packaged Atlas catalog is missing. Reinstall MapLock or configure UIMAPS_ATLAS_EXE.");
    }
    private static string Validate(string value)
    {
        var path = Path.GetFullPath(value);
        if (Path.GetFileName(path) is not ("ui-atlas.dll" or "ui-atlas.exe") || !File.Exists(path)
            || !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "ui-atlas.runtimeconfig.json")))
            throw new FileNotFoundException("The configured Atlas catalog deployment is invalid.", path);
        return path;
    }
    public async Task<CatalogReply> RequestAsync(CatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request.Operation is not ("maps-summary" or "revision-read" or "profile-read"
            or "parameter-review-read" or "parameter-confirm" or "identity-review-read" or "identity-confirm"))
            throw new ArgumentException("This catalog adapter supports reads and scoped parameter confirmation only.");
        var path = Executable;
        var start = new ProcessStartInfo(path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(path);
        start.ArgumentList.Add("catalog"); start.Environment["UI-ATLAS_DATA_HOME"] = Root;
        using var process = Process.Start(start) ?? throw new IOException("Atlas catalog did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var stop = deadline.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        // Both streams are drained concurrently, including on rejected requests.
        var output = ReadBoundedAsync(process.StandardOutput, 768 * 1024 * 1024, deadline);
        var errors = ReadBoundedAsync(process.StandardError, 1024 * 1024, deadline);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request with { Root = Root }));
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<CatalogReply>(await output.ConfigureAwait(false));
            _ = await errors.ConfigureAwait(false);
            if (result is null || result.Version != 1) throw new InvalidDataException("Atlas catalog contract is incompatible.");
            if (!result.Success || process.ExitCode != 0) throw new IOException(result.Message);
            return result;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationTokenSource deadline)
    {
        try
        {
            var buffer = new char[16 * 1024]; var result = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (result.Length > maximum - count) throw new InvalidDataException("Atlas catalog response exceeds the advertised limit.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        catch { deadline.Cancel(); throw; }
    }
}
