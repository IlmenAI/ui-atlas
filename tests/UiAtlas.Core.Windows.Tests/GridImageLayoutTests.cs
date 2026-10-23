using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridImageLayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(40)]
    public void HeaderCanFollowAShortCaptionWhenRuledTableGeometryProvesItsStart(int caption)
    {
        var (header, body) = GridImageLayout.Detect(Pixels(caption), new(0, 0, 600, 360));
        Assert.InRange(header.Y, caption + 1, caption + 3);
        Assert.InRange(header.Height, 29, 32);
        Assert.True(body.Y > header.Y + header.Height);
        Assert.Equal(header.X, body.X);
        Assert.Equal(header.Width, body.Width);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(28, false)]
    public void DistantTableAndHorizontalRulesAloneDoNotProveAHeader(int caption, bool columns)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            GridImageLayout.Detect(Pixels(caption, columns), new(0, 0, 600, 360)));
        Assert.Equal("header-row-not-identified", error.Message);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void UnruledHeaderNeedsSeveralLabelsAlignedWithRuledBodyColumns(int labels, bool accepted)
    {
        var png = Pixels(28, unruledHeader: true, labels: labels);
        if (accepted)
        {
            var (header, _) = GridImageLayout.Detect(png, new(0, 0, 600, 360));
            Assert.InRange(header.Y, 29, 31);
            Assert.InRange(header.Height, 29, 32);
        }
        else Assert.Throws<InvalidOperationException>(() => GridImageLayout.Detect(png, new(0, 0, 600, 360)));
    }

    private static byte[] Pixels(int caption, bool columns = true, bool unruledHeader = false, int labels = 0)
    {
        var pixels = Enumerable.Repeat((byte)255, 600 * 360 * 4).ToArray();
        for (var y = 0; y < caption; y++) for (var x = 0; x < 600; x++) Ink(x, y, 210);
        if (caption > 0) for (var x = 0; x < 600; x++) Ink(x, 2, 160);
        for (var y = caption; y < 360; y += 32) for (var x = 0; x < 600; x++) Ink(x, y, 160);
        if (columns) for (var x = 0; x < 600; x += 100) for (var y = caption + (unruledHeader ? 32 : 0); y < 360; y++) Ink(x, y, 160);
        for (var column = 0; column < labels; column++)
            for (var y = caption + 10; y < caption + 20; y++)
                for (var x = column * 100 + 12; x < column * 100 + 22; x++) Ink(x, y, 30);
        if (caption > 0) for (var y = 10; y < 20; y++) for (var x = 265; x < 285; x++) Ink(x, y, 30);
        var bitmap = BitmapSource.Create(600, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 600 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        void Ink(int x, int y, byte value)
        { var p = (y * 600 + x) * 4; pixels[p] = pixels[p + 1] = pixels[p + 2] = value; }
    }
}
