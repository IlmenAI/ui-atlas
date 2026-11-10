using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasSimulationEvidence
{
    internal static async Task<CatalogSimulationCapture> CaptureAsync(CatalogSimulationCaptureRequest request, string destination, CancellationToken token,
        string purpose = "simulation-fidelity", string recordingName = "capture.mlrec")
    {
        using var process = System.Diagnostics.Process.GetProcessById(request.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != request.ProcessCreatedUtcTicks)
            throw new InvalidOperationException("The simulation process changed before capture.");
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("Use a fresh capture directory.");
        Directory.CreateDirectory(destination);
        for (var parent = new DirectoryInfo(destination); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Capture paths cannot contain links.");
        if (Path.GetFileName(recordingName) != recordingName || !recordingName.EndsWith(".mlrec", StringComparison.Ordinal))
            throw new ArgumentException("Capture requires a local recording filename.");
        var recording = Path.Combine(destination, recordingName);
        var stage = "resolve owned root";
        try
        {
            var target = UiAtlas.Core.Recording.Windows.WindowCatalog.Resolve(request.RootWindow);
            if (target.ProcessId != request.ProcessId || target.ProcessStartedUtc.UtcTicks != request.ProcessCreatedUtcTicks)
                throw new InvalidOperationException("The capture window no longer belongs to the owned simulation process.");
            stage = "start recording";
            await using var session = new UiAtlas.Core.Recording.Windows.ManualRecordingSession(target, recording);
            session.FullRawObservations = true; session.Start(explicitConsent: true);
            stage = "capture observation";
            await session.CaptureFullObservationAsync(purpose, token);
            stage = "complete recording";
            session.Complete();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var diagnostic = Path.Combine(destination, "capture-failure.json");
            File.WriteAllText(diagnostic, JsonSerializer.Serialize(new
            { Stage = stage, Request = request, ProcessExited = process.HasExited, Exception = error.ToString() }));
            // The catalog returns the base exception's message. Keep the stage
            // here and the full original stack in the local diagnostic artifact.
            throw new InvalidOperationException("Simulation capture failed during " + stage + ": " + error.GetBaseException().Message + " See " + diagnostic);
        }
        var files = new List<CatalogEvidenceFile>();
        using var bundle = RecordingBundle.Open(recording);
        if (!RecordingBundleValidator.Validate(bundle).IsValid) throw new InvalidDataException("Candidate capture failed integrity validation.");
        var observation = bundle.Entries.Where(e => e.StartsWith("raw/observations/", StringComparison.Ordinal)).Order().Last();
        var frame = bundle.ReadJson<FrameObservation>(observation);
        using var json = JsonDocument.Parse(bundle.ReadBytes(observation));
        foreach (var entry in new[] { observation, "manifest.json", frame.NativeEpisodeEntry }.Where(e => !string.IsNullOrEmpty(e))
            .Concat(ImageEntries(json.RootElement)).Distinct(StringComparer.Ordinal))
        {
            var bytes = bundle.ReadBytes(entry!); var name = Path.GetFileName(entry!);
            File.WriteAllBytes(Path.Combine(destination, name), bytes);
            files.Add(new(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, entry!));
        }
        return new(destination, Path.GetFileName(observation), files.ToArray());
    }
    internal static CatalogSimulationEvidence Export(AtlasMapWorkspace workspace, UiMapRevision revision,
        string destination, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        // Never overwrite evidence or write into the source authority.
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The evidence export directory must be empty.");
        if (destination.StartsWith(workspace.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export evidence outside the source workspace.");
        Directory.CreateDirectory(destination);
        for (var parent = new DirectoryInfo(destination); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Evidence export paths cannot contain links.");
        var files = new List<CatalogEvidenceFile>(); var observations = new List<CatalogSimulationObservation>();
        var gaps = new List<string>(); var sources = workspace.Index.Sources.ToDictionary(s => s.SourceRef, StringComparer.Ordinal);
        string Write(string name, byte[] bytes, string kind)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.LongLength > RecordingContractLimits.MaxEvidenceEntryBytes) throw new InvalidDataException("Evidence entry exceeds its limit.");
            var path = Path.Combine(destination, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes); files.Add(new(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, kind)); return name;
        }
        Write("revision.pb", revision.ToByteArray(), "semantic-map");
        var bundles = new Dictionary<string, RecordingBundle>();
        try
        {
            foreach (var relative in workspace.Index.BundleFiles)
            {
                token.ThrowIfCancellationRequested(); var path = workspace.Resolve(relative);
                var bundle = RecordingBundle.Open(path);
                try
                {
                    if (!RecordingBundleValidator.Validate(bundle).IsValid) throw new InvalidDataException("A retained recording failed integrity validation.");
                    var id = bundle.ReadJson<RecordingManifest>("manifest.json").SessionId;
                    using (var file = File.OpenRead(path))
                    {
                        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
                        if (workspace.Index.Sources.Where(s => s.BundleId == id).Any(s => s.BundleHash != hash))
                            throw new InvalidDataException("The original recording no longer matches the map's retained source hash.");
                    }
                    if (!bundles.TryAdd(id, bundle)) { bundle.Dispose(); continue; }
                    Write($"recording-{bundles.Count:D3}.json", bundle.ReadBytes("manifest.json"), "capture-metadata");
                    foreach (var entry in new[] { "raw/interactions.jsonl", "raw/input-events.jsonl" }.Where(bundle.Entries.Contains))
                        Write($"recording-{bundles.Count:D3}-" + Path.GetFileName(entry), bundle.ReadBytes(entry), "interaction-evidence:" + id);
                }
                catch { bundle.Dispose(); throw; }
            }
            foreach (var source in revision.Sources)
            {
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(source.SourceObservationSetRef, out var retained)
                    || !bundles.TryGetValue(retained.BundleId, out var bundle))
                { gaps.Add(source.SourceObservationSetRef + ": original recording is missing"); continue; }
                var prefix = $"observation-{observations.Count + 1:D3}";
                var entry = $"raw/observations/frame-{retained.Frame:D6}.json";
                var frame = bundle.ReadJson<FrameObservation>(entry);
                var observationFile = Write(prefix + ".json", bundle.ReadBytes(entry), "raw-streams");
                var missing = new List<string>(); string? episodeFile = null;
                if (frame.NativeEpisodeEntry is { Length: > 0 } native && bundle.Entries.Contains(native))
                    episodeFile = Write(prefix + ".episode.pb", bundle.ReadBytes(native), "raw-native-episode");
                else missing.Add("Original native episode was not retained.");
                // Images referenced by this observation only; never borrow a nearby frame.
                using var json = JsonDocument.Parse(bundle.ReadBytes(entry));
                var images = new List<string>();
                var names = ImageEntries(json.RootElement).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var image in names)
                {
                    if (!bundle.Entries.Contains(image)) { missing.Add("Missing image: " + image); continue; }
                    images.Add(Write(prefix + "-" + Path.GetFileName(image), bundle.ReadBytes(image), "pixels:" + image));
                }
                if (images.Count == 0) missing.Add("No exact screenshot was retained.");
                // Keep Raw World separate from the original provider stream.
                var publicationBytes = File.ReadAllBytes(workspace.Resolve(retained.PublicationFile));
                var publication = UIMaps.Protocol.V1.GetPublicationResponse.Parser.ParseFrom(publicationBytes);
                IMessage payload = source.AcquisitionSemantic == UIMaps.Protocol.V3.AcquisitionSemantic.AcquisitionSnapshot
                    ? publication.Snapshot?.WorldStateSnapshot ?? throw new InvalidDataException("Missing source snapshot.")
                    : publication.Survey ?? throw new InvalidDataException("Missing source survey.");
                var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload.ToByteArray()));
                if (payloadHash != source.SourcePayloadHash || payloadHash != source.WorldStateHash)
                    throw new InvalidDataException("Raw World evidence does not match the selected revision.");
                Write(prefix + ".publication.pb", publicationBytes, "raw-world-publication");
                observations.Add(new(source.SourceObservationSetRef, retained.BundleId, retained.Frame, observationFile, episodeFile, images.ToArray(), missing.ToArray()));
            }
        }
        finally { foreach (var bundle in bundles.Values) bundle.Dispose(); }
        gaps.Add("Numeric MSAA roles/state bits, per-property HRESULTs, active pattern outcomes and event sequences are only evidenced when present in original native packets; projected labels are not substitutes.");
        var result = new CatalogSimulationEvidence(destination, workspace.Index.LogicalMapId, revision.MapRevisionRef,
            revision.CanonicalPayloadHash, Path.GetFileName(workspace.DirectoryPath), observations.ToArray(), files.ToArray(), gaps.ToArray());
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(result), Encoding.UTF8);
        return result;
    }
    private static IEnumerable<string> ImageEntries(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Name is "frameEntry" or "screenshotEntry" or "imageEntry" or "entry")
                { var name = property.Value.GetString(); if (name?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true) yield return name; }
                else foreach (var name in ImageEntries(property.Value)) yield return name;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var name in ImageEntries(item)) yield return name;
    }
}
