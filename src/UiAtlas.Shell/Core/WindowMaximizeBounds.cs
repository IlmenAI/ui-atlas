using System.Drawing;

namespace UIMaps.Application.Core;

internal readonly record struct WindowMaximizeBounds(Point Position, Size Size)
{
    public static WindowMaximizeBounds Calculate(Rectangle monitorBounds, Rectangle workArea)
    {
        if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(monitorBounds), "Monitor bounds must be non-empty.");
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea), "Monitor work area must be non-empty.");

        return new WindowMaximizeBounds(
            new Point(workArea.Left - monitorBounds.Left, workArea.Top - monitorBounds.Top),
            new Size(workArea.Width, workArea.Height));
    }
}
