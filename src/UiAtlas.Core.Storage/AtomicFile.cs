namespace UiAtlas.Core.Storage;

internal static class AtomicFile
{
    public static void Publish(string target, Action<string> write)
    {
        var full = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            write(temp);
            // Virus scanners and indexers can briefly retain large completed
            // databases. Retry only the atomic replacement; never delete the old map.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, full, overwrite: true); break; }
                catch (Exception error) when (attempt < 9 && error is IOException or UnauthorizedAccessException)
                { Thread.Sleep(100 * (attempt + 1)); }
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
