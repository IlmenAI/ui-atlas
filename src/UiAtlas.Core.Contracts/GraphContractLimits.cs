namespace UiAtlas.Core.Contracts;

/// <summary>Portable graph read/validation ceilings, independent of local build preferences.</summary>
public static class GraphContractLimits
{
    public const int MaxNodes = 2_000_000;
    public const int MaxEdges = 2_000_000;
    // One logical map can retain multiple application runs and their surveys.
    public const long MaxSqliteBytes = 2L * 1024 * 1024 * 1024;
}
