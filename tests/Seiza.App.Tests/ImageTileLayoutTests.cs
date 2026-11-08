using Seiza.App.Rendering;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageTileLayoutTests
{
    [Theory]
    [InlineData(20001, 17)]
    [InlineData(13, 20003)]
    [InlineData(8191, 8193)]
    public void TilesCoverEveryPixelExactlyOnceAndStayInsideTheDeviceLimit(int width, int height)
    {
        ImagePixelRegion[] tiles = ImageTileLayout.Create(width, height, 4096).ToArray();
        Assert.Equal((long)width * height, tiles.Sum(tile => (long)tile.Width * tile.Height));
        foreach (ImagePixelRegion tile in tiles)
        {
            Assert.InRange(tile.Width, 1, 4096);
            Assert.InRange(tile.Height, 1, 4096);
            Assert.InRange(tile.X, 0, width - 1);
            Assert.InRange(tile.Y, 0, height - 1);
            Assert.InRange(tile.Right, tile.X + 1, width);
            Assert.InRange(tile.Bottom, tile.Y + 1, height);
            foreach (ImagePixelRegion other in tiles.Where(other => other != tile))
                Assert.True(tile.Right <= other.X || other.Right <= tile.X ||
                    tile.Bottom <= other.Y || other.Bottom <= tile.Y);
        }
        foreach (var row in tiles.GroupBy(tile => tile.Y))
        {
            int next = 0;
            foreach (ImagePixelRegion tile in row) { Assert.Equal(next, tile.X); next = tile.Right; }
            Assert.Equal(width, next);
        }
    }

    [Fact]
    public void CopiesTilePixelsAndSamplesAcrossBoundariesWithoutChangingColors()
    {
        const int width = 20001, height = 3;
        byte[] pixels = Enumerable.Range(0, width * height * 4).Select(index => (byte)(index % 251)).ToArray();
        foreach (ImagePixelRegion tile in ImageTileLayout.Create(width, height, 4096))
        {
            byte[] copied = ImageTileLayout.CopyPixels(pixels, width, height, tile);
            for (int row = 0; row < tile.Height; row++)
                Assert.Equal(pixels.AsSpan(((tile.Y + row) * width + tile.X) * 4, tile.Width * 4).ToArray(),
                    copied.AsSpan(row * tile.Width * 4, tile.Width * 4).ToArray());
        }
        Assert.Equal(pixels.AsSpan((width + 4091) * 4, 12).ToArray(),
            ImageTileLayout.CopyPixels(pixels, width, height, new(4091, 1, 3, 1)));
    }

    [Fact]
    public void RejectsInvalidBuffersAndRegions()
    {
        Assert.Throws<ArgumentException>(() => ImageTileLayout.CopyPixels(new byte[3], 1, 1, new(0, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTileLayout.CopyPixels(new byte[4], 1, 1, new(1, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTileLayout.Create(100, 100, 0).ToArray());
    }

    [Fact]
    public void EightBitOverlayPreservesOpaqueColorsAndCompositesPremultipliedCoverage()
    {
        byte[] image = [100, 150, 200, 255, 20, 40, 60, 255, 1, 2, 3, 255];
        Bgra8Compositor.CompositePremultiplied(image, [0, 0, 0, 0, 25, 50, 75, 128, 7, 8, 9, 255]);
        Assert.Equal(new byte[] { 100, 150, 200, 255, 35, 70, 105, 255, 7, 8, 9, 255 }, image);
    }
}
