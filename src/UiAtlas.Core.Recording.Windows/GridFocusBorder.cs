namespace UiAtlas.Core.Recording.Windows;

/// <summary>Comparison-only mask for a thin patterned selection outline, never its row contents.</summary>
internal static class GridFocusBorder
{
    internal static bool[] Rows(GridPixelBuffer before, GridPixelBuffer after, int padding = 0)
    {
        var rows = new bool[before.Height];
        if (before.Width != after.Width || before.Height != after.Height || before.Width < 96) return rows;
        var first = Summarize(before); var second = Summarize(after);
        for (var y = 1; y < rows.Length - 1; y++)
        {
            // Many transitions distributed over the width distinguish dashes from cell
            // dividers or text. Both surrounding fills must agree in both observations.
            if (!Patterned(before, y) && !Patterned(after, y)) continue;
            var top = y - 1;
            while (top >= Math.Max(0, y - 4) && !CommonFill(top)) top--;
            var bottom = y + 1;
            while (bottom <= Math.Min(rows.Length - 1, y + 4) && !CommonFill(bottom)) bottom++;
            if (top < Math.Max(0, y - 4) || bottom > Math.Min(rows.Length - 1, y + 4)) continue;
            var dark = Math.Min(first[top].Shade, first[bottom].Shade);
            var light = Math.Max(first[top].Shade, first[bottom].Shade);
            if (dark is < 48 or > 200 || light < 215 || light - dark < 40) continue;
            for (var row = Math.Max(0, top + 1 - padding); row < Math.Min(rows.Length, bottom + padding); row++) rows[row] = true;
        }
        return rows;

        bool CommonFill(int y) => first[y].Uniform && second[y].Uniform && Math.Abs(first[y].Shade - second[y].Shade) <= 2;
    }

    private static (int Shade, bool Uniform)[] Summarize(GridPixelBuffer image)
    {
        var result = new (int, bool)[image.Height];
        var histogram = new int[256];
        for (var y = 0; y < image.Height; y++)
        {
            Array.Clear(histogram);
            for (var x = 0; x < image.Width; x++) histogram[image[x, y]]++;
            var shade = 0;
            for (var value = 1; value < 256; value++) if (histogram[value] > histogram[shade]) shade = value;
            result[y] = (shade, histogram[shade] >= image.Width * .85);
        }
        return result;
    }

    private static bool Patterned(GridPixelBuffer image, int y)
    {
        // Every quarter must contain repeated transitions; a long reference/name does
        // not qualify just because one part of the row contains many glyph edges.
        for (var quarter = 0; quarter < 4; quarter++)
        {
            var left = quarter * image.Width / 4;
            var right = (quarter + 1) * image.Width / 4;
            var transitions = 0;
            for (var x = left + 1; x < right; x++) if (Math.Abs(image[x, y] - image[x - 1, y]) > 8) transitions++;
            if (transitions < (right - left) / 8) return false;
        }
        return true;
    }
}
