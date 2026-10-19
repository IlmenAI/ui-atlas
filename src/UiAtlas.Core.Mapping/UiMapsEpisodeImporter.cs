using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using UIMaps.Map.Core;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping;

public sealed record UiMapsEpisodeImportResult(
    string Format, string Status, string EpisodeId, string SourceSha256,
    string MappingSessionRef, string MapDraftRef, string AcquisitionSemantic,
    string Health, int EntityCount, int BlockingReviewCount,
    bool NotAuthorization, bool RetainsFullEpisode);

/// <summary>
/// Offline bridge into the UI Maps foundation. Original sensor bytes, perception
/// publication, and a reviewable Map draft remain separate artifacts. Import does
/// not capture the desktop, approve a review item, or publish an executable plan.
/// </summary>
public sealed class UiMapsEpisodeImporter
{
    public const int MaximumEpisodeBytes = CaptureSizeLimits.MaximumEpisodeBytes;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<UiMapsEpisodeImportResult> ImportAsync(
        string episodePath, string windowEvidenceId, string outputDirectory,
        bool allowSensitiveEvidence, CancellationToken cancellationToken = default)
    {
        if (!allowSensitiveEvidence)
            throw new ArgumentException("Import retains the full source episode, including display pixels and text outside the selected application. Explicit sensitive-evidence acknowledgement is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(windowEvidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("The output path already exists. Choose a new directory; existing maps are never overwritten.");

        var bytes = await ReadBoundedAsync(episodePath, cancellationToken).ConfigureAwait(false);
        SensorEpisodeBundle episode;
        try { episode = SensorEpisodeBundle.Parser.ParseFrom(bytes); }
        catch (InvalidProtocolBufferException ex) { throw new InvalidDataException("The source is not a valid UI Maps sensor episode.", ex); }

        try
        {
            new PacketValidator(PerceptionPolicy.Default).Validate(episode);
            ValidateEvidence(episode);
            var target = SelectTarget(episode, windowEvidenceId);
            var perception = new PerceptionEngine();
            var ingested = await perception.IngestAsync(episode, cancellationToken).ConfigureAwait(false);
            var publication = perception.GetPublication(ingested.PublicationId);
            cancellationToken.ThrowIfCancellationRequested();

            var parent = Path.GetDirectoryName(destination)
                ?? throw new ArgumentException("The output directory must have a parent directory.");
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, ".ui-atlas-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var sourceHash = Hash(bytes);
                var map = new UiMapService(Path.Combine(staging, "map"));
                var created = map.CreateSession(new CreateMappingSessionRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Target = target,
                    PrivacyScope = new PrivacyScopeAcknowledgement
                    {
                        ScopeVersion = "ui-atlas.episode-import/1",
                        ScopeHash = Hash(System.Text.Encoding.UTF8.GetBytes(sourceHash + "\n" + windowEvidenceId)),
                        AcknowledgedBy = "local-user:explicit-episode-import",
                        AcknowledgedAt = DateTimeOffset.UtcNow.ToString("O"),
                        ScreenshotsPermitted = true,
                        PrintableTextPermitted = true,
                        ProtectedValuesExcluded = true,
                        TargetInputProhibited = true,
                    },
                });
                var request = new AttachObservationRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    MappingSessionRef = created.Session.MappingSessionRef,
                    ExpectedEtag = created.Session.Etag,
                    PublicationRef = ingested.PublicationId,
                };
                if (publication.Snapshot is not null)
                    request.Snapshot = publication.Snapshot.WorldStateSnapshot.Clone();
                else if (publication.Survey is not null)
                    request.Survey = publication.Survey.Clone();
                else
                    throw new InvalidDataException("Perception returned neither a Snapshot nor a Survey publication.");

                var attached = map.AttachObservation(request);
                if (!attached.Draft.Entities.Any(entity => entity.EntityKind == MapEntityKind.MapSurface))
                    throw new InvalidDataException("The selected application produced no map surface; its identity could not be bound to the perceived evidence.");
                var validation = map.ValidateDraft(attached.Draft.MapDraftRef, attached.Draft.Etag);
                var result = new UiMapsEpisodeImportResult(
                    "ui-atlas.uimaps-import/1", "draft-review-required", episode.EpisodeId, sourceHash,
                    attached.Session.MappingSessionRef, attached.Draft.MapDraftRef,
                    ingested.PublicationKind.ToString(), ingested.Health.State.ToString(),
                    attached.Draft.Entities.Count, attached.Session.BlockingReviewCount, true, true);

                await File.WriteAllBytesAsync(Path.Combine(staging, "episode.pb"), bytes, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(staging, "perception.pb"), publication.ToByteArray(), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "draft.json"), JsonFormatter.Default.Format(attached.Draft), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "validation.json"), JsonFormatter.Default.Format(validation), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(result, JsonOptions), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(staging, destination);
                return result;
            }
            finally
            {
                // This exact, random sibling directory was created by this invocation.
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        catch (PerceptionException ex) { throw new InvalidDataException($"{ex.Code}: {ex.Message}", ex); }
        catch (UiMapException ex) { throw new InvalidDataException($"{ex.Code}: {ex.Message}", ex); }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumEpisodeBytes)
            throw new InvalidDataException($"The sensor episode must be non-empty and at most {MaximumEpisodeBytes / CaptureSizeLimits.MiB} MiB.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static void ValidateEvidence(SensorEpisodeBundle episode)
    {
        // The service validator checks hashes; the file boundary additionally
        // requires one manifest entry per packet and complete structural fields.
        var packetIds = episode.SensorPackets.Select(packet => packet.PacketId).ToHashSet(StringComparer.Ordinal);
        var manifestIds = episode.SensorManifest.Entries.Select(entry => entry.PacketId).ToArray();
        if (manifestIds.Distinct(StringComparer.Ordinal).Count() != manifestIds.Length
            || !packetIds.SetEquals(manifestIds))
            throw new InvalidDataException("The sensor manifest must account for every packet exactly once.");
        if (episode.Health is null || episode.SensorPackets.Any(packet => packet.Health is null))
            throw new InvalidDataException("The episode and its packets must declare capture health.");
        var windows = episode.SensorPackets.SelectMany(packet => packet.WindowTopology?.Windows ?? []).ToArray();
        var nodes = episode.SensorPackets.SelectMany(packet => packet.Accessibility?.Nodes ?? []).ToArray();
        if (windows.Length + nodes.Length + 2 > PerceptionPolicy.Default.MaximumObjectCount)
            throw new InvalidDataException("The episode exceeds the supported observation count.");
        if (windows.Any(window => window.BoundsPx is null || window.IntersectionWithDisplayPx is null
                || string.IsNullOrWhiteSpace(window.WindowEvidenceId))
            || nodes.Any(node => node.BoundsPx is null || string.IsNullOrWhiteSpace(node.AccessibilityEvidenceId)))
            throw new InvalidDataException("Native and accessibility records require their original identities and geometry.");
        if (nodes.Any(node => node.IsPassword && !string.IsNullOrEmpty(node.ValueEvidence)))
            throw new InvalidDataException("The episode contains a protected accessibility value. Import requires source-side exclusion of protected values.");
    }

    private static TargetApplicationIdentity SelectTarget(SensorEpisodeBundle episode, string windowEvidenceId)
    {
        var candidates = episode.SensorPackets.SelectMany(packet => packet.WindowTopology?.Windows ?? [])
            .Where(window => string.Equals(window.WindowEvidenceId, windowEvidenceId, StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException("Select exactly one native window evidence ID from the source episode.");
        var window = candidates[0];
        if (!window.IsVisible || window.IsMinimized || window.IsCloakedOrUnavailable
            || (!string.IsNullOrEmpty(window.SelectionEligibility) && window.SelectionEligibility != "eligible")
            || string.IsNullOrWhiteSpace(window.ProcessInstanceEvidence)
            || string.IsNullOrWhiteSpace(window.ProcessName)
            || string.IsNullOrWhiteSpace(window.WindowHandleEvidence))
            throw new InvalidDataException("The selected window lacks an eligible visible target with exact process-instance and native-window evidence.");
        return new TargetApplicationIdentity
        {
            ApplicationRef = "uimaps:app:" + Guid.NewGuid().ToString("N"),
            DisplayName = window.ProcessName,
            ExecutableName = Path.GetFileName(window.ProcessName),
            ProcessIdentityEvidence = { window.ProcessInstanceEvidence },
            WindowIdentityEvidence = { window.WindowHandleEvidence },
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
