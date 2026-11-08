using Microsoft.Graphics.Canvas;
using Seiza.App.Models;
using Seiza.App.Rendering;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Xunit;

namespace Seiza.App.Windows.Tests;

public sealed class CanvasDisplayImageTests
{
    private static RenderedImageData Image(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x % 251); pixels[offset + 1] = (byte)(y % 239);
                pixels[offset + 2] = 117; pixels[offset + 3] = 255;
            }
        return new(pixels, width, height, new(width, height, 3, "test", "planar-rgb", null,
            new(0, 1, .5, .5, .1), new Dictionary<string, System.Text.Json.JsonElement>()));
    }

    [Theory]
    [InlineData(20001, 17, 3992, 0, 201, 17, false)]
    [InlineData(20001, 17, 19800, 0, 201, 17, false)]
    [InlineData(17, 20003, 0, 3992, 17, 201, false)]
    [InlineData(17, 20003, 0, 19802, 17, 201, false)]
    [InlineData(20001, 17, 3992, 0, 201, 17, true)]
    [InlineData(20001, 17, 19800, 0, 201, 17, true)]
    [InlineData(17, 20003, 0, 3992, 17, 201, true)]
    [InlineData(17, 20003, 0, 19802, 17, 201, true)]
    public async Task DrawsNativePixelsAcrossTileSeamsAndAtTheFarEdge(int width, int height, int x, int y, int viewWidth, int viewHeight, bool software)
    {
        using var device = new CanvasDevice(software);
        var image = Image(width, height);
        using var tiled = await CanvasDisplayImage.CreateAsync(device, image);
        using var target = new CanvasRenderTarget(device, viewWidth, viewHeight, 96);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
            tiled.Draw(drawing, new Rect(-x, -y, width, height), CanvasImageInterpolation.NearestNeighbor);
        }
        Assert.Equal(ImageTileLayout.CopyPixels(image.Bgra, width, height, new(x, y, viewWidth, viewHeight)), target.GetPixelBytes());
        var snapshot = tiled.Snapshot();
        tiled.Dispose();
        Assert.Equal(image.Bgra, snapshot.Bgra);
        Assert.Throws<ObjectDisposedException>(() => tiled.Snapshot());
    }

    [Fact]
    public async Task CancellationBeforeVirtualLoadingLeavesTheCpuSnapshotIntact()
    {
        using var device = new CanvasDevice();
        var image = Image(20001, 3);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CanvasDisplayImage.CreateAsync(device, image, cancellationToken: cancellation.Token));
        Assert.Equal((byte)117, image.Bgra[2]);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(.75)]
    [InlineData(.2)]
    [InlineData(.05)]
    public async Task FilteredFractionalZoomMatchesASingleBitmapWithoutTileSeams(double scale)
    {
        using var device = new CanvasDevice();
        var image = Image(511, 257);
        // Smooth gradients expose cache/tile seams without assuming that WIC's
        // virtual-image prefilter and the resident-bitmap prefilter use identical
        // kernels at discontinuities in a synthetic sawtooth signal.
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int offset = (y * image.Width + x) * 4;
                image.Bgra[offset] = (byte)(20 + x * 200 / image.Width);
                image.Bgra[offset + 1] = (byte)(20 + y * 200 / image.Height);
            }
        using var tiled = await CanvasDisplayImage.CreateAsync(device, image, forceVirtual: true);
        using var bitmap = CanvasBitmap.CreateFromBytes(device, image.Bgra, image.Width, image.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
        using var expected = new CanvasRenderTarget(device, 520, 270, 96);
        using var actual = new CanvasRenderTarget(device, 520, 270, 96);
        var destination = new Rect(2.35, 2.35, image.Width * scale, image.Height * scale);
        using (var drawing = expected.CreateDrawingSession())
        {
            drawing.Clear(global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
            drawing.DrawImage(bitmap, destination, bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
        }
        using (var drawing = actual.CreateDrawingSession())
        {
            drawing.Clear(global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
            tiled.Draw(drawing, destination, CanvasImageInterpolation.HighQualityCubic);
        }
        byte[] a = actual.GetPixelBytes(), b = expected.GetPixelBytes();
        int maximumDifference = 0;
        // Compare interior pixels, excluding the image's antialiased outer edge.
        for (int y = (int)Math.Ceiling(destination.Top) + 4; y < destination.Bottom - 4; y++)
            for (int x = (int)Math.Ceiling(destination.Left) + 4; x < destination.Right - 4; x++)
                for (int channel = 0; channel < 3; channel++)
                {
                    int offset = (y * 520 + x) * 4 + channel;
                    maximumDifference = Math.Max(maximumDifference, Math.Abs(a[offset] - b[offset]));
                }
        Assert.True(maximumDifference <= 3, $"Maximum channel difference at scale {scale}: {maximumDifference}");
    }

    [Fact]
    public void OverlayGeometryCrossesTilesAndReachesBeyondTheSingleTextureLimit()
    {
        using var device = new CanvasDevice();
        const int width = 20001, height = 11;
        byte[] pixels = new byte[width * height * 4];
        TiledOverlayRasterizer.Draw(device, width, height,
            drawing => drawing.FillRectangle(0, 4, width, 3, global::Windows.UI.Color.FromArgb(255, 233, 71, 19)),
            (region, bytes) => {
                for (int row = 0; row < region.Height; row++)
                    bytes.AsSpan(row * region.Width * 4, region.Width * 4).CopyTo(
                        pixels.AsSpan(((region.Y + row) * width + region.X) * 4, region.Width * 4));
            });
        foreach (int x in new[] { 0, 4095, 4096, 16383, 16384, 20000 })
            Assert.Equal(new byte[] { 19, 71, 233, 255 }, pixels.AsSpan((5 * width + x) * 4, 4).ToArray());
        Assert.All(pixels.AsSpan(0, width * 4).ToArray(), value => Assert.Equal((byte)0, value));
    }
}
