using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Protocol.V3;

namespace UIMaps.Protocol;

/// <summary>The published V3 atlas canonical-byte contract, shared by producers and consumers.</summary>
public static class UiMapRevisionIntegrity
{
    public static byte[] SerializeCanonical(UiMapRevision source)
    {
        var revision = source.Clone();
        revision.MapRevisionRef = "";
        revision.CanonicalPayloadHash = "";
        Sort(revision);
        return revision.ToByteArray();
    }

    public static string Hash(UiMapRevision source) =>
        Convert.ToHexString(SHA256.HashData(SerializeCanonical(source))).ToLowerInvariant();

    public static void Sort(UiMapRevision revision)
    {
        Replace(revision.ParentRevisionRefs, revision.ParentRevisionRefs.Order(StringComparer.Ordinal));
        Replace(revision.Sources, revision.Sources.OrderBy(x => x.SourceObservationSetRef, StringComparer.Ordinal));
        Replace(revision.Entities, revision.Entities.OrderBy(x => x.MapEntityRef, StringComparer.Ordinal));
        Replace(revision.Relationships, revision.Relationships.OrderBy(x => x.RelationshipKind).ThenBy(x => x.FromEntityRef, StringComparer.Ordinal).ThenBy(x => x.ToEntityRef, StringComparer.Ordinal));
        Replace(revision.DispositionSummary, revision.DispositionSummary.OrderBy(x => x.SourceObservationSetRef, StringComparer.Ordinal).ThenBy(x => x.SourceWorldObjectRef, StringComparer.Ordinal));
        Replace(revision.Interactions, revision.Interactions.OrderBy(x => x.SequenceNumber).ThenBy(x => x.InteractionRef, StringComparer.Ordinal));
        Replace(revision.GridHostDeclarations, revision.GridHostDeclarations.OrderBy(x => x.DeclarationRef, StringComparer.Ordinal));
        Replace(revision.EvidenceStreamSelections, revision.EvidenceStreamSelections.OrderBy(x => x.StreamKind));
        foreach (var source in revision.Sources)
        {
            Replace(source.EvidenceRefs, source.EvidenceRefs.Order(StringComparer.Ordinal));
            Replace(source.ProviderVersions, source.ProviderVersions.Order(StringComparer.Ordinal));
            Replace(source.CoverageLimitations, source.CoverageLimitations.Order(StringComparer.Ordinal));
            Replace(source.SurfaceObservations, source.SurfaceObservations.OrderBy(x => x.SourceWorldObjectRef, StringComparer.Ordinal));
            Replace(source.EvidenceStreamSummaries, source.EvidenceStreamSummaries.OrderBy(x => x.StreamKind));
            foreach (var summary in source.EvidenceStreamSummaries)
                Replace(summary.Limitations, summary.Limitations.Order(StringComparer.Ordinal));
        }
        foreach (var declaration in revision.GridHostDeclarations)
        {
            Replace(declaration.SurfaceVariantRefs, declaration.SurfaceVariantRefs.Order(StringComparer.Ordinal));
            Replace(declaration.EvidenceLinkRefs, declaration.EvidenceLinkRefs.Order(StringComparer.Ordinal));
            Replace(declaration.Limitations, declaration.Limitations.Order(StringComparer.Ordinal));
        }
        foreach (var entity in revision.Entities)
        {
            Replace(entity.Assertions, entity.Assertions.OrderBy(x => x.AssertionRef, StringComparer.Ordinal));
            Replace(entity.Matchers, entity.Matchers.OrderBy(x => x.MatcherRef, StringComparer.Ordinal));
            Replace(entity.EvidenceLinks, entity.EvidenceLinks.OrderBy(x => x.EvidenceLinkRef, StringComparer.Ordinal));
            Replace(entity.Limitations, entity.Limitations.Order(StringComparer.Ordinal));
            if (entity.HostedDataGrid?.ExplorationReceipt is { } exploration)
            {
                Replace(exploration.Coverage, exploration.Coverage.Order());
                Replace(exploration.Limitations, exploration.Limitations.Order(StringComparer.Ordinal));
            }
        }
    }

    private static void Replace<T>(Google.Protobuf.Collections.RepeatedField<T> field, IEnumerable<T> values)
    {
        var ordered = values.ToArray();
        field.Clear();
        field.AddRange(ordered);
    }
}
