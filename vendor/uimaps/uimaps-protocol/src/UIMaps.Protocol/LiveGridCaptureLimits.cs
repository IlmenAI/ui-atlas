namespace UIMaps.Protocol;

public static class LiveGridCaptureLimits
{
    // A mapped two-dimensional grid can require 17 horizontal viewports for each
    // vertical band. 110 covers six such bands, including horizontal resets and
    // vertical advances, while remaining bounded by the explicit 500-step cap.
    public const int DefaultScrollMutations = 110;
    public const int MaximumScrollMutations = 500;

    // Zero means omitted by older clients, not unlimited.
    public static int ResolveScrollMutations(int requested) => requested == 0 ? DefaultScrollMutations
        : requested is >= 1 and <= MaximumScrollMutations ? requested
        : throw new System.ArgumentOutOfRangeException(nameof(requested), "Scroll limit must be between 1 and 500.");
}
