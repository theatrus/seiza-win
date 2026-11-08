using Seiza.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Seiza.App.Windows.Tests;

public sealed class LargeImageEncodingTests
{
    [Theory]
    [InlineData(20001, 7, false)]
    [InlineData(7, 20003, false)]
    [InlineData(20001, 7, true)]
    [InlineData(7, 20003, true)]
    public async Task PreservesLargeDimensionsAndExactDisplayPixelsWithoutAGpuSurface(int width, int height, bool tiff)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(index % 251); pixels[index + 1] = 83;
            pixels[index + 2] = (byte)((index / 4) % 239); pixels[index + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        await Bgra8Encoder.WriteAsync(stream, pixels, width, height,
            tiff ? BitmapEncoder.TiffEncoderId : BitmapEncoder.PngEncoderId);
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal((uint)width, decoder.PixelWidth); Assert.Equal((uint)height, decoder.PixelHeight);
        PixelDataProvider result = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        Assert.Equal(pixels, result.DetachPixelData());
    }
}
