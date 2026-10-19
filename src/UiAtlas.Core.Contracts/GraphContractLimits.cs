namespace UiAtlas.Core.Contracts;

/// <summary>Portable graph read/validation ceilings, independent of local build preferences.</summary>
public static class GraphContractLimits
{
    public const int MaxNodes = 2_000_000;
    public const int MaxEdges = 2_000_000;
}
