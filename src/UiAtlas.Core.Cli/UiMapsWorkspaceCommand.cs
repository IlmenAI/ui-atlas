using Google.Protobuf;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

public static class UiMapsWorkspaceCommand
{
    public static bool Handles(string verb) => verb.ToLowerInvariant() is "drafts" or "review" or "publish" or "validate-drafts" or "export-revision" or "import-revision" or "compare-revisions" or "convert-recording" or "export-workspace" or "import-workspace";
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (args.Length < 2) throw new ArgumentException("A map path is required.");
        args = [args[0].ToLowerInvariant(), .. args[1..]];
        if (args[0] is "export-workspace" or "import-workspace")
        {
            if (args.Length != 5 || args[2] != "--out" || args[4] != "--allow-sensitive-evidence")
                throw new ArgumentException("map <export-workspace|import-workspace> <source> --out <new-destination> --allow-sensitive-evidence");
            if (args[0] == "export-workspace") { AtlasMapPackage.Export(args[1], args[3], true); await output.WriteLineAsync(Path.GetFullPath(args[3])); }
            else await output.WriteLineAsync(AtlasMapPackage.Import(args[1], args[3], true));
            return 0;
        }
        if (args[0] == "convert-recording")
        {
            if (args.Length != 4 || args[2] != "--out") throw new ArgumentException("map convert-recording <bundle.mlrec> --out <new-map.db>");
            if (File.Exists(args[3]) || Directory.Exists(AtlasMapWorkspace.RootFor(args[3]))) throw new IOException("Choose a new output path; conversion preserves existing artifacts.");
            var converted = await AtlasMapWorkspace.BuildAsync([args[1]], args[3], cancellationToken: cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { converted.Metadata.GraphId, converted.Metadata.SemanticHash, Backend = "UIMaps", ReviewRequired = true }));
            return 0;
        }
        UiAtlas.Core.Mapping.IMappingReviewStore workspace = Directory.Exists(args[1])
            ? new UiAtlas.Core.Mapping.EpisodeMappingReviewStore(args[1]) : AtlasMapWorkspace.Open(args[1]);
        switch (args[0])
        {
            case "drafts" when args.Length == 2:
                foreach (var draft in workspace.Drafts) await output.WriteLineAsync(JsonFormatter.Default.Format(draft));
                return 0;
            case "validate-drafts" when args.Length == 2:
                var reports = workspace.Validate();
                foreach (var report in reports) await output.WriteLineAsync(JsonFormatter.Default.Format(report));
                return reports.All(report => report.PublicationEligible) ? 0 : 4;
            case "review" when args.Length >= 8 && args[5] == "--etag" && args[7] == "--reviewer":
                if (args.Length is not (9 or 11) || args.Length == 11 && args[9] != "--value") throw new ArgumentException(ReviewUsage);
                var kind = args[3].ToLowerInvariant() switch
                {
                    "accept" => ReviewCommandKind.ReviewAcceptEntity,
                    "reject" => ReviewCommandKind.ReviewRejectEntity,
                    "rename" => ReviewCommandKind.ReviewRenameEntity,
                    "parent" => ReviewCommandKind.ReviewSetParent,
                    "accept-all" => ReviewCommandKind.ReviewAcceptAllBlocking,
                    "accept-interaction" => ReviewCommandKind.ReviewAcceptInteraction,
                    "reject-interaction" => ReviewCommandKind.ReviewRejectInteraction,
                    _ => throw new ArgumentException(ReviewUsage),
                };
                if ((kind is ReviewCommandKind.ReviewRenameEntity or ReviewCommandKind.ReviewSetParent) && args.Length != 11) throw new ArgumentException("Rename and parent require --value.");
                var result = workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = args[2],
                    ExpectedEtag = args[6], CommandKind = kind, SubjectRef = args[4] == "-" ? "" : args[4],
                    Value = args.Length == 11 ? args[10] : "", SecondaryRef = kind == ReviewCommandKind.ReviewSetParent ? args[10] : "",
                    ReviewedBy = args[8], Reason = "Explicit Atlas CLI review." });
                await output.WriteLineAsync(JsonFormatter.Default.Format(result));
                return 0;
            case "publish" when args.Length == 4 && args[2] == "--reviewer":
                foreach (var revision in workspace.Publish(args[3])) await output.WriteLineAsync(JsonFormatter.Default.Format(revision));
                return 0;
            case "export-revision" when args.Length == 5 && args[3] == "--out":
                var exported = workspace.Service.Export(args[2], false);
                await using (var file = new FileStream(args[4], FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await file.WriteAsync(exported.PackageBytes.Memory, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(exported.PackageHash);
                return 0;
            case "import-revision" when args.Length == 3:
                var info = new FileInfo(args[2]);
                if (info.Length is <= 0 or > 128 * 1024 * 1024) throw new InvalidDataException("Revision exceeds the import size limit.");
                var imported = workspace.ImportRevision(ByteString.CopyFrom(await File.ReadAllBytesAsync(args[2], cancellationToken).ConfigureAwait(false)));
                await output.WriteLineAsync(JsonFormatter.Default.Format(imported));
                return imported.Valid ? 0 : 4;
            case "compare-revisions" when args.Length == 4:
                await output.WriteLineAsync(JsonFormatter.Default.Format(workspace.Service.Compare(args[2], args[3])));
                return 0;
            default: throw new ArgumentException("Use map drafts, validate-drafts, review, publish, export-revision, import-revision, compare-revisions, or convert-recording. " + ReviewUsage);
        }
    }
    public const string ReviewUsage = "map review <map-path> <draft-ref> <accept|reject|rename|parent|accept-all|accept-interaction|reject-interaction> <subject-ref|-> --etag <draft-etag> --reviewer <name> [--value <text-or-parent-ref>]";
}
