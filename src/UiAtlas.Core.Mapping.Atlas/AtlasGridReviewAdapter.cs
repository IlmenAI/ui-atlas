using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UIMaps.Map.Core.Grid;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V2_1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping.Atlas;

public sealed record AtlasGridReview(string GridId, string Name, string ReviewFile, string ImageFile,
    string Status, string Detail);

/// <summary>Only explicit, uniquely bound header reviews become Map schema.
/// Retained images and unresolved reviews remain inspectable independently.</summary>
internal static class AtlasGridReviewAdapter
{
    internal static AtlasGridReview Retain(SavedDataGridReview grid, string output)
    {
        var review = grid.Review;
        if (review.ReviewedUtc is null || review.Schema.Columns.Count == 0)
            throw new InvalidDataException("Only reviewed grid headers can be retained as grid reviews.");
        var bytes = ReadImage(review.ImagePath, review.ImageSha256);
        var stem = "grid-reviews/" + AtlasEpisodeAdapter.Hash(Encoding.UTF8.GetBytes(grid.GridId + ":" + review.Schema.Revision));
        Directory.CreateDirectory(Path.Combine(output, "grid-reviews"));
        File.WriteAllBytes(Path.Combine(output, stem + ".png"), bytes);
        var manifest = "";
        if (File.Exists(review.CaptureManifestPath))
        {
            if (new FileInfo(review.CaptureManifestPath).Length > 16 * 1024 * 1024) throw new InvalidDataException("Grid capture manifest exceeds its limit.");
            manifest = stem + ".capture.json";
            var capture = JsonSerializer.Deserialize<CapturedGrid>(File.ReadAllText(review.CaptureManifestPath), JsonDefaults.Options)
                ?? throw new InvalidDataException("Grid capture manifest is empty.");
            if (capture.Tiles.Count > 256) throw new InvalidDataException("Grid capture has too many tiles.");
            long total = 0;
            var tiles = capture.Tiles.Select(tile =>
            {
                var source = Path.GetFullPath(tile.PngPath, Path.GetDirectoryName(Path.GetFullPath(review.CaptureManifestPath))!);
                total += new FileInfo(source).Length;
                if (total > 256L * 1024 * 1024) throw new InvalidDataException("Grid capture tiles exceed the size limit.");
                var png = ReadImage(source, tile.Sha256);
                var relative = stem + ".tiles/" + tile.Sha256.ToLowerInvariant() + ".png";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(output, relative))!);
                File.WriteAllBytes(Path.Combine(output, relative), png);
                // Tile paths are relative to the retained capture manifest.
                return tile with { PngPath = Path.GetRelativePath(Path.GetDirectoryName(manifest)!, relative).Replace('\\', '/') };
            }).ToArray();
            File.WriteAllText(Path.Combine(output, manifest), JsonSerializer.Serialize(capture with
                { Tiles = tiles, ManifestPath = Path.GetFileName(manifest) }, JsonDefaults.Options));
        }
        var retained = grid with { Review = review with { ImagePath = stem + ".png", CaptureManifestPath = manifest } };
        File.WriteAllText(Path.Combine(output, stem + ".review.json"), JsonSerializer.Serialize(retained, new JsonSerializerOptions { WriteIndented = true }));
        return new(grid.GridId, grid.DisplayName, stem + ".review.json", stem + ".png", "pending-host-binding",
            "Header schema retained. Recording evidence must identify one exact host before it enters Semantic World; this review does not qualify row reading.");
    }

    internal static byte[] ReadImage(string path, string expectedHash)
    {
        if (new FileInfo(path).Length is < 24 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Grid image exceeds the size limit or is empty.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Grid image failed its PNG identity check.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width == 0 || height == 0 || (ulong)width * height > 32_000_000)
            throw new InvalidDataException("Grid image exceeds the pixel limit.");
        return bytes;
    }

    internal static void ValidateCapture(string manifestPath, Func<string, string> resolve)
    {
        var path = resolve(manifestPath);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Grid capture manifest exceeds its limit.");
        var capture = JsonSerializer.Deserialize<CapturedGrid>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException("Grid capture manifest is empty.");
        if (capture.Tiles.Count > 256) throw new InvalidDataException("Grid capture has too many tiles.");
        long total = 0;
        foreach (var tile in capture.Tiles)
        {
            var tilePath = resolve(Path.Combine(Path.GetDirectoryName(manifestPath)!, tile.PngPath));
            total += new FileInfo(tilePath).Length;
            if (total > 256L * 1024 * 1024) throw new InvalidDataException("Grid capture tiles exceed the size limit.");
            _ = ReadImage(tilePath, tile.Sha256);
        }
    }

    internal static DataGridMapPackage? Bind(IReadOnlyList<SavedDataGridReview> reviews, List<AtlasGridReview> retained,
        string bundleId, long frame, GetPublicationResponse publication, TargetApplicationIdentity target)
    {
        var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.ToArray()
            ?? publication.Survey.WorldObjects.Select(value => value.WorldObject).ToArray();
        var package = new DataGridMapPackage { SchemaVersion = "2.1", AppRef = target.ApplicationRef,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"), PublisherIdentity = "local-user:header-review" };
        foreach (var grid in reviews.Where(value => value.Context.SourceBundleId == bundleId && value.Context.SourceFrame == frame
                     && !string.IsNullOrWhiteSpace(value.Context.SourceEvidenceId)))
        {
            var hosts = objects.Where(value => value.ObjectKind == WorldObjectKind.AccessibilityElement
                && value.Properties.Any(property => property.Key == "sourceAccessibilityEvidenceId" && property.Value == grid.Context.SourceEvidenceId)).ToArray();
            if (hosts.Length != 1) continue;
            var host = hosts[0];
            var surfaceRef = "atlas:grid-surface:" + grid.GridId;
            package.Surfaces.Add(new MappedSurface { SurfaceRef = surfaceRef, ApplicationRef = target.ApplicationRef });
            var mapped = new MappedDataGrid { DataGridRef = "atlas:grid:" + grid.GridId, SurfaceRef = surfaceRef,
                DisplayName = grid.DisplayName, ExpectedRelativeBounds = new RectPx { Width = host.BoundsPx.Width, Height = host.BoundsPx.Height },
                SourceEvidenceRefs = { host.WorldObjectId }, GeometryTemplate = grid.Review.Schema.Layout, SchemaPartial = !grid.Review.Schema.IsComplete,
                ReviewerAssertions = { "headers-reviewed:" + grid.Review.ReviewedUtc!.Value.ToString("O") },
                KnownLimitations = { "Reviewed schema only; row acquisition and complete data coverage have not been qualified." } };
            if (!grid.Review.Schema.IsComplete) mapped.KnownLimitations.Add("Partial-width schema; additional physical columns may exist.");
            foreach (var column in grid.Review.Schema.Columns.OrderBy(value => value.Ordinal))
            {
                mapped.ExpectedHeaderSet.Add(column.Label);
                mapped.Columns.Add(new MappedGridColumn { GridColumnRef = "atlas:grid-column:" + grid.GridId + ":" + column.ColumnKey,
                    DataGridRef = mapped.DataGridRef, Ordinal = column.Ordinal, CanonicalDisplayName = column.Label,
                    ExpectedWidthRatio = (column.EndX - column.StartX) / Math.Max(1, grid.Review.HeaderBounds.Width),
                    HeaderMatchers = { column.Label }, SourceEvidenceRefs = { grid.Review.ImageSha256, column.ColumnKey } });
            }
            package.DataGrids.Add(mapped);
            var index = retained.FindIndex(value => value.GridId == grid.GridId);
            retained[index] = retained[index] with { Status = "mapped-schema", Detail = "Reviewed columns attached to the exact recorded host. Current rows and reading qualification are separate." };
        }
        if (package.DataGrids.Count == 0) return null;
        package.CanonicalPayloadHash = DataGridMapStore.HashCanonical(package);
        package.MapRevisionRef = "uimaps:grid-map-revision:sha256:" + package.CanonicalPayloadHash;
        return package;
    }
}
