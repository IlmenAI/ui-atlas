using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Read-only header-band detection. Never infers data values or fills an unruled body with rows.</summary>
internal static class GridImageLayout
{
    internal static (RectI Header, RectI Body) Detect(byte[] png, RectI table, IReadOnlyList<RectI>? nativeHeaders = null)
    {
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(png);
        if (!TableCellOcrReader.Contains(frame, table) || table.Width < 40 || table.Height < 24)
            throw new InvalidOperationException("table-bounds-unproven");
        if (nativeHeaders is { Count: > 0 })
        {
            var visible = nativeHeaders.Where(h => h.IsValid && h.Y >= table.Y && h.Y < table.Y + table.Height / 3 &&
                h.X < table.X + table.Width && h.X + h.Width > table.X).ToArray();
            if (visible.Length > 0 && visible.Max(h => h.Y) - visible.Min(h => h.Y) <= 2 &&
                visible.Max(h => h.Y + h.Height) - visible.Min(h => h.Y + h.Height) <= 2)
            {
                var y = visible.Min(h => h.Y);
                // UIA includes the shared lower grid line in some header rectangles.
                // Keep that changing body boundary out of the fixed header anchor.
                var bottom = visible.Max(h => h.Y + h.Height) - 1;
                var left = Math.Max(table.X, visible.Min(h => h.X));
                var right = Math.Min(table.X + table.Width, visible.Max(h => h.X + h.Width));
                if (bottom < table.Y + table.Height - 4)
                    return (new(left, y, right - left, bottom - y),
                        TrimBlankFooter(frame, new(left, bottom, right - left, table.Y + table.Height - bottom - 1)));
            }
        }
        // Some opaque controls include a caption above the column headers. Only
        // skip that band when two-dimensional ruled-table geometry proves the
        // start of the table nearby; never scan arbitrary body rows for a header.
        var starts = new List<int> { table.Y + 1 };
        var ruled = VisualSurfaceScanner.FindBorderedTable(frame, table);
        if (ruled is not null && ruled.Y > table.Y && ruled.Y - table.Y <= 64 &&
            ruled.Width >= table.Width * .8 && ruled.Height >= table.Height * .6)
            starts.Add(ruled.Y + 1);
        var search = table with { Height = Math.Min(table.Height, 130) };
        var rules = TableViewportDescriber.FindRules(frame, search, vertical: false);
        if (starts.Count > 1)
        {
            // The caption itself can have a border at the same pitch as body rows.
            // Permit only its first lower boundary, never an unbounded row search.
            var captionBottom = rules.Where(y => y - ruled!.Y >= 12 && y - table.Y <= 64)
                .Select(y => (int?)y).FirstOrDefault();
            if (captionBottom is { } edge) starts.Add(edge + 1);
        }
        foreach (var top in starts.Distinct())
        foreach (var bottom in rules.Where(y => y - top >= 12 && y - top <= 64).Take(1))
        {
            var header = new RectI(table.X + 1, top, table.Width - 2, bottom - top);
            var dividers = TableViewportDescriber.FindRules(frame, header, vertical: true);
            if (dividers.Count < 2 && (ruled is null || !HasLabelsOverBodyColumns(frame, table, header, bottom))) continue;
            return (header, TrimBlankFooter(frame, new(header.X, bottom + 1, header.Width, table.Y + table.Height - bottom - 1)));
        }
        throw new InvalidOperationException("header-row-not-identified");
    }

    private static bool HasLabelsOverBodyColumns(OpaqueSurfaceScanner.PixelFrame frame, RectI table, RectI header, int bottom)
    {
        // Progress browse controls draw separators in the body but not the header.
        // Require several independently labelled body columns; one centred caption
        // or an empty strip cannot establish a header. This proves geometry only.
        var body = new RectI(header.X, bottom + 3, header.Width,
            Math.Min(header.Height * 3, table.Y + table.Height - bottom - 3));
        if (body.Height < 24) return false;
        var columns = TableViewportDescriber.FindRules(frame, body, vertical: true);
        if (columns.Count < 4) return false;
        var edges = new[] { header.X }.Concat(columns).Append(header.X + header.Width).ToArray();
        var labelled = 0;
        foreach (var (left, right) in edges.Zip(edges.Skip(1)))
        {
            var cell = new RectI(left + 3, header.Y + 3, right - left - 6, header.Height - 6);
            if (cell.Width >= 12 && cell.Height >= 6 && AzureOpenAiTableReader.HasInk(frame, cell)) labelled++;
        }
        return labelled >= 3;
    }

    internal static RectI TrimBlankFooter(OpaqueSurfaceScanner.PixelFrame frame, RectI body)
    {
        var rules = TableViewportDescriber.FindRules(frame, body, false);
        if (rules.Count < 5) return body;
        var gaps = rules.Zip(rules.Skip(1), (a, b) => b - a).ToArray();
        var pitch = gaps.Order().ElementAt(gaps.Length / 2);
        if (pitch < 8 || gaps.Take(gaps.Length - 1).Any(g => Math.Abs(g - pitch) > 2)) return body;
        var last = gaps[^1] < pitch - 2 ? rules[^2] : rules[^1];
        var remainder = body.Y + body.Height - last - 1;
        if (remainder is < 3 || remainder > pitch || AzureOpenAiTableReader.HasInk(frame,
            body with { Y = last + 2, Height = Math.Max(0, remainder - 1) })) return body;
        return body with { Height = last + 1 - body.Y };
    }

    internal static Task<GridViewportDescription> DescribeAsync(GridViewportRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(request.Png);
        var valid = TableCellOcrReader.Contains(frame, request.HeaderBounds) &&
            TableCellOcrReader.Contains(frame, request.BodyBounds) &&
            request.HeaderBounds.X == request.BodyBounds.X && request.HeaderBounds.Width == request.BodyBounds.Width &&
            request.HeaderBounds.Y + request.HeaderBounds.Height <= request.BodyBounds.Y;
        // Pixel registration checks the fixed header and body. No fabricated row pitch,
        // column labels, or business cells are published by this image-only operation.
        return Task.FromResult(new GridViewportDescription(request.Schema, [], [], 1, valid, false,
            "image-only", null, valid ? [] : ["table-header-geometry-invalid"]));
    }
}
