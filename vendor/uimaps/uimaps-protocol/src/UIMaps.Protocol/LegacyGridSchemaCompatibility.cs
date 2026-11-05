namespace UIMaps.Protocol;

/// <summary>Compatibility for legacy header-only maps; never for reviewed image geometry.</summary>
public static class LegacyGridSchemaCompatibility
{
    // A missing named or interior column must never shift the remaining identities.
    // Callers must additionally prove complete capture and a non-table right margin.
    public static bool IsTrailingBlankPrefix(IReadOnlyList<string> expected, IReadOnlyList<string> observed) =>
        observed.Count > 0 && observed.Count < expected.Count
        && observed.SequenceEqual(expected.Take(observed.Count), StringComparer.Ordinal)
        && expected.Skip(observed.Count).Select((label, offset) =>
            label == $"Unlabeled column {observed.Count + offset + 1}").All(value => value);

    public static string MarginEvidence(int expectedCount, int observedCount) =>
        $"Legacy grid schema: retained {expectedCount} header slots; observed {observedCount} physical columns. "
        + "Only trailing unlabeled slots were excluded after full-width ruled-table geometry proved a non-table right margin.";
}
