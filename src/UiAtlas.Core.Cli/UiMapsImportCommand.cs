using System.Text.Json;
using UiAtlas.Core.Mapping;

namespace UiAtlas.Core.Cli;

public static class UiMapsImportCommand
{
    public const string Usage = "ui-atlas map import-episode <episode.pb> --window <window-evidence-id> --output <new-directory> --allow-sensitive-evidence";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (args.Length != 6 || args[1] != "--window" || args[3] != "--output"
            || args[5] != "--allow-sensitive-evidence")
        {
            await error.WriteLineAsync(Usage);
            await error.WriteLineAsync("Retains the complete source episode, including display pixels and text outside the selected application. Produces an unreviewed draft; no desktop input is performed.");
            return 2;
        }
        var result = await new UiMapsEpisodeImporter().ImportAsync(args[0], args[2], args[4],
            allowSensitiveEvidence: true, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(result));
        return 0;
    }
}
