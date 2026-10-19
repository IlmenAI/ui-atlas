using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

public sealed record AtlasRecordingReceipt(string Format, string RequestId, int ProcessId,
    DateTimeOffset ProcessStartedUtc, long WindowHandle, string MapPath, string ImportRequestPath, string ImportRequestSha256);

public static class AtlasRecordingHandoff
{
    public static void Complete(string requestId, string resultPath, WindowTarget target, string mapPath)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("A unique handoff request id is required.");
        resultPath = Path.GetFullPath(resultPath);
        if (File.Exists(resultPath)) throw new IOException("A handoff receipt already exists; use a new request directory.");
        var directory = Path.GetDirectoryName(resultPath)!;
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, requestId + ".atlasmap");
        AtlasMapPackage.Export(mapPath, archivePath, true);
        var request = new ImportMappingWorkspaceRequest { RequestId = requestId, SourceFormat = AtlasMapWorkspace.Format,
            SourceArchivePath = archivePath, SourceArchiveSha256 = HashFile(archivePath) };
        // Use the export's manifest, taken under its write lease, rather than
        // re-reading potentially edited drafts after that lease was released.
        using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
        using (var stream = archive.GetEntry("map.db.uimaps/mapping-snapshots.pb")!.Open())
            request.Snapshots.AddRange(ImportMappingWorkspaceRequest.Parser.ParseFrom(stream).Snapshots);
        var importPath = Path.Combine(directory, requestId + ".import.pb");
        using (var output = new FileStream(importPath, FileMode.CreateNew)) request.WriteTo(output);
        var receipt = new AtlasRecordingReceipt("ui-atlas.recording-result/1", requestId, target.ProcessId,
            target.ProcessStartedUtc, target.Hwnd, Path.GetFullPath(mapPath), importPath, HashFile(importPath));
        var temporary = resultPath + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(receipt)); File.Move(temporary, resultPath); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
