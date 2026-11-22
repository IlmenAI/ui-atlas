using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class DataGridRegistrationTests
{
    [Fact]
    public void TableCaptureImageLimitDefaultsTo100AndPersistsForBothCaptureModes()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-capture-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = System.IO.Path.Combine(directory, "settings.json");
            var store = new TableCaptureSettingsStore(path);
            Assert.Equal(100, store.Load().MaximumImages);
            foreach (var readTable in new[] { false, true })
                Assert.Equal(100, store.Load().Limits(60_000, readTable).MaxTiles);
            store.Save(new(150));
            var saved = new TableCaptureSettingsStore(path).Load();
            foreach (var readTable in new[] { false, true })
            {
                var limits = saved.Limits(60_000, readTable);
                Assert.Equal(150, limits.MaxTiles);
                Assert.True(limits.MaxMovements >= limits.MaxTiles);
                Assert.Equal(60_000, limits.MaxDurationMs);
            }
            foreach (var invalid in new[] { 0, 1, 1001 })
                Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new(invalid)));
            Assert.Equal(150, store.Load().MaximumImages);
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("ShellHandwritingCanvas {18E91349-E229-4995-8049-7437243BBCC3}", 0x0A0800A8L, true)]
    [InlineData("ShellHandwritingCanvas {18E91349-E229-4995-8049-7437243BBCC3}", 0x08080000L, false)]
    [InlineData("ShellHandwritingCanvas {18E91349-E229-4995-8049-7437243BBCC3}", 0x08000020L, false)]
    [InlineData("ShellHandwritingCanvas {18E91349-E229-4995-8049-7437243BBCC3}", 0x00080020L, false)]
    [InlineData("ShellHandwritingCanvas", 0x0A0800A8L, false)]
    [InlineData("Popup", 0x0A0800A8L, false)]
    [InlineData("#32770", 0L, false)]
    public void OnlyPassiveShellHandwritingCanvasIsExemptFromRectangleOcclusion(string className, long styles, bool expected)
    {
        Assert.Equal(expected, DataGridTargetBinding.IsPassiveHandwritingCanvas(className, styles));
    }

    [Fact]
    public void PassiveOverlayComparisonChecksEveryVisibleTablePixelAtItsScreenPosition()
    {
        var window = new RectI(-6, 10, 8, 6);
        var host = new RectI(-4, 11, 5, 4);
        var table = new RectI(-3, 12, 3, 2);
        var pixels = Enumerable.Range(0, 8 * 6 * 4).Select(i => (byte)i).ToArray();
        var crop = new byte[5 * 4 * 4];
        for (var row = 0; row < host.Height; row++)
            Array.Copy(pixels, ((row + 1) * 8 + 2) * 4, crop, row * 5 * 4, 5 * 4);
        var isolated = new OpaqueSurfaceScanner.PixelFrame(8, 6, pixels);
        var screen = new OpaqueSurfaceScanner.PixelFrame(5, 4, crop);
        crop[0] ^= 1; // Outside the table.
        crop[(1 * 5 + 1) * 4 + 3] ^= 1; // Alpha does not change displayed RGB.
        Assert.True(DataGridTargetBinding.TablePixelsMatch(table, window, isolated, host, screen));
        crop[(2 * 5 + 3) * 4 + 2] ^= 1; // Last pixel of the table.
        Assert.False(DataGridTargetBinding.TablePixelsMatch(table, window, isolated, host, screen));
        Assert.False(DataGridTargetBinding.TablePixelsMatch(host with { Width = 7 }, window, isolated, host, screen));
        Assert.False(DataGridTargetBinding.TablePixelsMatch(table, window with { Width = 9 }, isolated, host, screen));
    }

    [Theory]
    [InlineData(GridScrollAxis.Horizontal, 31)]
    [InlineData(GridScrollAxis.Horizontal, 7)]
    [InlineData(GridScrollAxis.Vertical, 29)]
    [InlineData(GridScrollAxis.Vertical, 5)]
    [InlineData(GridScrollAxis.Vertical, -5)]
    public void DistinctiveOverlapMeasuresActualIncludingShortTerminalDisplacement(GridScrollAxis axis, int displacement)
    {
        var before = View(40, 40);
        var after = View(40 + (axis == GridScrollAxis.Horizontal ? displacement : 0),
            40 + (axis == GridScrollAxis.Vertical ? displacement : 0));
        var result = DataGridRegistration.Register(before, after, axis, Math.Sign(displacement));
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(axis == GridScrollAxis.Horizontal ? displacement : 0, result.DisplacementX);
        Assert.Equal(axis == GridScrollAxis.Vertical ? displacement : 0, result.DisplacementY);
    }

    [Fact]
    public void MultipleRepetitiveContentAlignmentsAreRejected()
    {
        var before = View(0, 0, period: 16);
        var after = View(0, 5, period: 16);
        var result = DataGridRegistration.Register(before, after, GridScrollAxis.Vertical, 1);
        Assert.False(result.Accepted);
        Assert.True(result.CompatibleCandidates > 1);
        Assert.Equal("ambiguous-repeated-content", result.Reason);
    }

    [Fact]
    public void GridRulesAloneNeverEstablishMovement()
    {
        var before = View(0, 0, rulesOnly: true);
        var after = View(0, 5, rulesOnly: true);
        var result = DataGridRegistration.Register(before, after, GridScrollAxis.Vertical, 1);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void WrongAxisAndGapsAreRejected()
    {
        Assert.False(DataGridRegistration.Register(View(0, 0), View(4, 20), GridScrollAxis.Vertical, 1).Accepted);
        Assert.False(DataGridRegistration.Register(View(0, 0), View(0, 90), GridScrollAxis.Vertical, 1).Accepted);
    }

    [Fact]
    public void IdenticalPixelsDoNotByThemselvesProveTerminalBoundary()
    {
        var result = DataGridRegistration.Register(View(0, 0), View(0, 0), GridScrollAxis.Vertical, 1);
        Assert.True(result.NoChange);
        Assert.False(result.Accepted);
    }

    private static GridPixelBuffer View(int x, int y, int period = 0, bool rulesOnly = false)
    {
        var pixels = new byte[96 * 80];
        for (var py = 0; py < 80; py++)
        for (var px = 0; px < 96; px++)
        {
            var row = period == 0 ? py + y : (py + y) % period;
            pixels[py * 96 + px] = rulesOnly ? (byte)(row % 16 == 0 ? 10 : 250) : Pixel(px + x, row);
        }
        return new(96, 80, pixels);
    }

    internal static byte Pixel(int x, int y)
    {
        var hash = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
        hash ^= hash >> 13;
        return (byte)(hash % 256);
    }
}
