using System;
using System.Collections.Generic;
using UIMaps.Protocol.V2_1;

namespace UIMaps.Protocol;

public sealed record ReviewedGridColumnBatch(int FirstColumn, int ColumnCount, int Left, int Width);

public static class ReviewedGridTranscriptionPlan
{
    public const int RowsPerBatch = 20;
    public const int ColumnsPerBatch = 10;
    public const int MaximumPanelWidth = 1000;

    public static IReadOnlyList<ReviewedGridColumnBatch> ColumnBatches(ReviewedGridImageSchema schema)
    {
        var result = new List<ReviewedGridColumnBatch>();
        for (var first = 0; first < schema.Columns.Count;)
        {
            var left = schema.Columns[first].BoundsPx.X;
            var end = first + 1;
            while (end < schema.Columns.Count && end - first < ColumnsPerBatch
                && (long)schema.Columns[end].BoundsPx.X + schema.Columns[end].BoundsPx.Width - left <= MaximumPanelWidth)
                end++;
            var last = schema.Columns[end - 1].BoundsPx;
            result.Add(new(first, end - first, left, checked(last.X + last.Width - left)));
            first = end;
        }
        return result;
    }

    public static int RequiredRequests(ReviewedGridImageSchema schema, int rows) =>
        checked(((rows + RowsPerBatch - 1) / RowsPerBatch) * ColumnBatches(schema).Count);
}
