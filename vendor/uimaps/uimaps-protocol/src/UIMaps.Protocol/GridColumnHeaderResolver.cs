namespace UIMaps.Protocol.V2_1;

public static class GridColumnHeaderResolver
{
    public static string Resolve(GridAnalysisResult analysis, GridColumnCandidate column)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(column);

        var observed = analysis.TextObservations.FirstOrDefault(value =>
            string.Equals(value.TextObservationRef, column.HeaderTextRef, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(value.OriginalText));
        if (observed is not null) return observed.OriginalText;

        var alias = column.Aliases.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return alias ?? $"Column {column.Ordinal + 1}";
    }
}
