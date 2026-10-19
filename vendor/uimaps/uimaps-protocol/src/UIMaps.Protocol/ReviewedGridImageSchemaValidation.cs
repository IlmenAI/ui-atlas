using System;
using System.Linq;
using System.Security.Cryptography;
using UIMaps.Protocol.V2_1;

namespace UIMaps.Protocol;

public static class ReviewedGridImageSchemaValidation
{
    public static string ColumnKey(ReviewedGridImageSchema schema, int ordinal)
    {
        var label = schema.Columns[ordinal].Aliases.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? $"Unlabeled column {ordinal + 1}";
        return schema.Columns.Count(column => column.Aliases.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) == label) > 1
            ? $"{label} [column {ordinal + 1}]" : label;
    }

    public static void Validate(ReviewedGridImageSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var band = schema.HeaderBand;
        if (schema.HostRelativeTop < 0 || schema.ImageWidth <= 0 || schema.ImageHeight <= 0 || band is null
            || band.X != 0 || band.Width != schema.ImageWidth || band.Y < 0
            || band.Height <= 0 || (long)band.Y + band.Height > schema.ImageHeight
            || schema.Columns.Count == 0 || string.IsNullOrWhiteSpace(schema.ReviewedBy))
            throw new InvalidOperationException("Reviewed image schema requires a valid image, header band, columns and reviewer.");
        if (schema.ImagePng.IsEmpty || !string.Equals(Convert.ToHexString(SHA256.HashData(schema.ImagePng.ToByteArray())), schema.ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reviewed schema image evidence does not match its hash.");
        var png = schema.ImagePng.ToByteArray();
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != schema.ImageWidth
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != schema.ImageHeight)
            throw new InvalidOperationException("Reviewed schema dimensions do not match the retained PNG.");
        var previousRight = 0;
        for (var i = 0; i < schema.Columns.Count; i++)
        {
            var column = schema.Columns[i];
            var bounds = column.BoundsPx;
            if (column.Ordinal != i || bounds is null || bounds.X < previousRight || bounds.Width <= 0
                || (long)bounds.X + bounds.Width > schema.ImageWidth)
                throw new InvalidOperationException("Reviewed physical columns must be ordered, non-overlapping and inside the image.");
            previousRight = bounds.X + bounds.Width;
        }
    }
}
