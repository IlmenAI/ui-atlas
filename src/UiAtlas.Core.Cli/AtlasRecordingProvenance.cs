using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasRecordingProvenance
{
    internal static CatalogRevisionRecordings Read(AtlasMapWorkspace workspace, UiMapRevision revision, string mapId, CancellationToken token)
    {
        var pins = revision.Sources.ToDictionary(s => s.SourceObservationSetRef, StringComparer.Ordinal);
        var retained = workspace.Index.Sources.Where(s => pins.ContainsKey(s.SourceRef)).ToArray();
        if (retained.Length != pins.Count)
            throw new InvalidDataException("An observation in the exact revision has no retained Atlas recording.");
        var files = workspace.Index.BundleFiles.ToHashSet(StringComparer.Ordinal);
        var result = new List<CatalogRecordingSource>();
        var clicks = new List<string>();
        var interactions = revision.Interactions.Where(i => i.Kind == MapInteractionKind.Unspecified).ToLookup(i => i.RecordingSessionRef);
        foreach (var group in retained.GroupBy(s => s.BundleHash, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var hash = group.Key;
            var relative = $"recordings/{hash}.mlrec";
            if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) || !files.Contains(relative))
                throw new InvalidDataException("The exact revision has no matching retained recording bundle.");
            var path = workspace.Resolve(relative);
            using var file = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != hash)
                throw new InvalidDataException("The retained Atlas recording no longer matches its source hash.");
            using var bundle = RecordingBundle.Open(path);
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            var nativeOrigins = new Dictionary<long, string?>();
            foreach (var source in group.OrderBy(s => s.Frame))
            {
                token.ThrowIfCancellationRequested();
                var frame = bundle.ReadJson<FrameObservation>($"raw/observations/frame-{source.Frame:D6}.json");
                if (string.IsNullOrWhiteSpace(manifest.SessionId) || manifest.SessionId != source.BundleId || frame.Sequence != source.Frame)
                    throw new InvalidDataException("The retained Atlas recording does not contain the indexed observation.");
                var origin = NativeOrigin(bundle, manifest, frame, pins[source.SourceRef].EpisodeId, nativeOrigins);
                nativeOrigins.Add(source.Frame, origin);
                result.Add(new(source.SourceRef, manifest.SessionId, hash, origin));
            }
            var candidates = interactions["atlas-recording:" + hash];
            if (candidates.Any() && bundle.Entries.Contains("raw/interactions.jsonl"))
                AddRecordedClicks(bundle, hash, group.ToDictionary(s => s.SourceRef, s => s.Frame), candidates, clicks, token);
        }
        return new(mapId, revision.MapRevisionRef, Path.GetFileName(workspace.DirectoryPath), result.OrderBy(s => s.SourceRef, StringComparer.Ordinal).ToArray())
        { SingleClickEventRefs = clicks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }

    internal static string? NativeOrigin(RecordingBundle bundle, RecordingManifest manifest, FrameObservation frame,
        string publishedEpisodeId, IReadOnlyDictionary<long, string?> priorOrigins)
    {
        if (frame.NativeEpisodeEntry == null)
        {
            // The attended recorder saves prepared popup pixels and controls as a delta of its observed base.
            if (frame is not { Trigger: "adaptive-popup", ObservationScope: "popup-delta", BaseFrameSequence: long parent }
                || parent >= frame.Sequence || !priorOrigins.TryGetValue(parent, out var origin) || origin == null)
                return null;
            if (AtlasEpisodeAdapter.Convert(bundle, manifest, frame).EpisodeId != publishedEpisodeId)
                throw new InvalidDataException("The recorded popup does not match its published observation.");
            return origin;
        }
        if (frame.NativeEpisodeEntry != $"raw/native/frame-{frame.Sequence:D6}.episode.pb")
            throw new InvalidDataException("The recording's native episode path does not correspond to its frame.");
        var episode = SensorEpisodeBundle.Parser.ParseFrom(bundle.ReadBytes(frame.NativeEpisodeEntry));
        var target = manifest.Target;
        var process = $"pid:{target.ProcessId}:started:{target.ProcessStartedUtc.UtcDateTime:O}";
        var scope = episode.EffectiveCapturePolicy?.CaptureScope;
        if (episode.Producer != "uimaps-recorder" || frame.Acquisition?.CaptureMethod != "UIMaps.Recorder.Windows"
            || scope?.TargetProcessInstanceEvidence != process
            || !scope.TargetWindowHandleEvidence.Contains(target.SelectedHwnd.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            return null;
        new PacketValidator(PerceptionPolicy.Default).Validate(episode);
        if (episode.EpisodeId != publishedEpisodeId
            || !episode.SensorPackets.Single(p => p.Screenshot != null).Screenshot.ImageBlob.Span.SequenceEqual(bundle.ReadBytes(frame.FrameEntry)))
            throw new InvalidDataException("The captured native episode differs from its recorded frame or published observation.");
        return episode.EpisodeId;
    }

    private static void AddRecordedClicks(RecordingBundle bundle, string hash, Dictionary<string, long> frames,
        IEnumerable<ObservedMapInteraction> candidates, List<string> clicks, CancellationToken token)
    {
        var actions = candidates.ToLookup(i => i.ActionEventRef, StringComparer.Ordinal);
        using var reader = new StreamReader(bundle.OpenEntry("raw/interactions.jsonl"));
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var raw = JsonSerializer.Deserialize<InteractionObservation>(line, JsonDefaults.Options);
            if (raw is not { Actor: InteractionActor.User, Gesture: InteractionGestureKind.Click }) continue;
            var id = "atlas-action:" + hash + ":" + raw.Sequence;
            if (actions[id].Any(i => frames.TryGetValue(i.SourceObservationSetRef, out var before) && before == raw.SourceFrameSequence
                && frames.TryGetValue(i.ResultObservationSetRef, out var after) && raw.ResultFrameSequences.Contains(after))) clicks.Add(id);
        }
    }
}
