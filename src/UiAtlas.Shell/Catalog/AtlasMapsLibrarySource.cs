using Google.Protobuf;
using UiAtlas.Catalog;
using UIMaps.Application.Core;
using UIMaps.Protocol.V3;

namespace UiAtlas.Shell.Catalog;

public sealed class AtlasMapsLibrarySource(AtlasCatalogClient client) : IMapsLibrarySource
{
    private string? _version;
    private IReadOnlyList<MapsLibraryRecord> _records = [];
    public async Task<IReadOnlyList<MapsLibraryRecord>> LoadAsync(CancellationToken token)
    {
        var reply = await client.RequestAsync(new("maps-summary", client.Root, KnownCatalogVersion: _version), token).ConfigureAwait(false);
        if (reply.NotModified) return _records;
        var records = reply.Maps.Select(Decode).ToArray();
        _records = records;
        _version = reply.CatalogVersion;
        return records;
    }
    public async Task<MapsLibraryRecord> LoadDetailsAsync(MapsLibraryRecord record, CancellationToken token)
    {
        var reply = await client.RequestAsync(new("map-detail", client.Root, MapId: record.CatalogMapId,
            Generation: record.CatalogGeneration, Entry: Convert.ToBase64String(record.Entry.ToByteArray())), token).ConfigureAwait(false);
        return Decode(reply.Maps.Single());
    }
    private static MapsLibraryRecord Decode(CatalogMap map) => new(
            MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(map.Entry)),
            map.Session is null ? null : MappingSessionDescriptor.Parser.ParseFrom(Convert.FromBase64String(map.Session)),
            map.Draft is null ? null : UiMapDraft.Parser.ParseFrom(Convert.FromBase64String(map.Draft)),
            map.Revision is null ? null : UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(map.Revision)))
        { LocalPath = map.Path, CatalogMapId = map.Id, CatalogGeneration = map.Generation, DisplayApplicationRef = map.ApplicationGroup,
            IsSummary = map.SummaryOnly, ApplicationInfo = map.Application };
    public async Task<byte[]?> LoadPreviewAsync(MapsLibraryRecord record, CancellationToken token)
    {
        var reply = await client.RequestAsync(new("map-preview", client.Root, MapId: record.CatalogMapId,
            Generation: record.CatalogGeneration, Entry: Convert.ToBase64String(record.Entry.ToByteArray())), token);
        return reply.PreviewPng is null ? null : Convert.FromBase64String(reply.PreviewPng);
    }
    private Task<CatalogReply> Change(string action, MapsLibraryRecord record, CancellationToken token) => client.RequestAsync(
        new(action, client.Root, MapId: record.CatalogMapId, DraftRef: record.Entry.MapDraftRef,
            Entry: Convert.ToBase64String(record.Entry.ToByteArray()), Generation: record.CatalogGeneration), token);
    public Task ArchiveAsync(MapsLibraryRecord record, CancellationToken token) => Change("archive", record, token);
    public Task DeleteAsync(MapsLibraryRecord record, CancellationToken token) => Change("map-delete", record, token);
    public Task RestoreAsync(MapsLibraryRecord record, CancellationToken token) => Change("restore", record, token);
    public Task MarkReadyAsync(MapsLibraryRecord record, CancellationToken token) => Change("ready", record, token);
    public Task MarkDraftAsync(MapsLibraryRecord record, CancellationToken token) => Change("draft", record, token);
    public async Task<UiMapRevision> PublishAsync(MapsLibraryRecord record, CancellationToken token) =>
        UiMapRevision.Parser.ParseFrom(Convert.FromBase64String((await Change("publish", record, token)).Revision!));
}
