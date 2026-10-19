using System.Drawing;

namespace UIMaps.Application.Core;

public static class MicroHudPlacement
{
    public static Rectangle Calculate(Rectangle displayBounds, Size requestedSize)
    {
        if (displayBounds.Width <= 0 || displayBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayBounds), "Display bounds must be non-empty.");

        var width = Math.Clamp(requestedSize.Width, 1, displayBounds.Width);
        var height = Math.Clamp(requestedSize.Height, 1, displayBounds.Height);
        var x = displayBounds.Left + ((displayBounds.Width - width) / 2);
        return new Rectangle(x, displayBounds.Top, width, height);
    }
}
