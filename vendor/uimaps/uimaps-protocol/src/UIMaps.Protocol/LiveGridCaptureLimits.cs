namespace UIMaps.Protocol;

public static class LiveGridCaptureLimits
{
    public const int DefaultScrollMutations = 1_000;
    public const int MaximumScrollMutations = 10_000;

    // Zero means omitted by older clients, not unlimited.
    public static int ResolveScrollMutations(int requested) => requested == 0 ? DefaultScrollMutations
        : requested is >= 1 and <= MaximumScrollMutations ? requested
        : throw new System.ArgumentOutOfRangeException(nameof(requested), $"Scroll limit must be between 1 and {MaximumScrollMutations:N0}.");
}
