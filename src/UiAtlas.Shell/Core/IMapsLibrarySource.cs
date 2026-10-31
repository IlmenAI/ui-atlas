using UIMaps.Protocol.V3;

namespace UIMaps.Application.Core;

public sealed record MapsLibraryRecord(
    MapLibraryEntry Entry,
    MappingSessionDescriptor? Session,
    UiMapDraft? Draft,
    UiMapRevision? Revision)
{
    public string? LocalPath { get; init; }
    public string? CatalogMapId { get; init; }
    public string? CatalogGeneration { get; init; }
    public string? DisplayApplicationRef { get; init; }
    public bool IsSummary { get; init; }
    public UiAtlas.Catalog.CatalogApplicationInfo? ApplicationInfo { get; init; }
    public string ApplicationGroupRef => DisplayApplicationRef ?? Entry.ApplicationRef;
}

public interface IMapsLibrarySource
{
    Task<IReadOnlyList<MapsLibraryRecord>> LoadAsync(CancellationToken cancellationToken);
    Task<byte[]?> LoadPreviewAsync(MapsLibraryRecord record, CancellationToken token) => Task.FromResult<byte[]?>(null);
    Task<UiMapRevision> PublishAsync(MapsLibraryRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Map Library source does not support publishing.");
    Task ArchiveAsync(MapsLibraryRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Map Library source does not support archiving.");
    Task DeleteAsync(MapsLibraryRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Map Library source does not support deletion.");
    async Task ArchiveManyAsync(IReadOnlyCollection<MapsLibraryRecord> records, CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ArchiveAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }
    Task RestoreAsync(MapsLibraryRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Map Library source does not support restoring archived maps.");
    async Task RestoreManyAsync(IReadOnlyCollection<MapsLibraryRecord> records, CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RestoreAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }
    Task MarkReadyAsync(MapsLibraryRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Map Library source does not support Ready selection.");
    Task MarkDraftAsync(MapsLibraryRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Map Library source does not support Ready selection.");
}

