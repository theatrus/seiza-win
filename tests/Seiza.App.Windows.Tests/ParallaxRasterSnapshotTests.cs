using System.Buffers.Binary;
using Seiza.App.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace Seiza.App.Windows.Tests;

public sealed class ParallaxRasterSnapshotTests
{
    [Fact]
    public async Task SixteenBitTiffSnapshotRetainsSubEightBitDifferencesWithoutChangingSource()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "source.tiff");
        string snapshot = Path.Combine(directory.Path, "snapshot.png");
        ushort[] rgba = [12340, 23450, 34560, 65535, 12341, 23451, 34561, 65535];
        await WriteImageAsync(source, BitmapEncoder.TiffEncoderId, 2, 1, ToBytes(rgba));
        byte[] original = await File.ReadAllBytesAsync(source);

        (int width, int height) = await ParallaxRasterSnapshot.SaveAsync(source, snapshot);

        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        DecodedImage decoder = await ReadImageAsync(snapshot);
        byte[] pixels = decoder.Pixels;
        Assert.Equal(BitmapDecoder.PngDecoderId, decoder.CodecId);
        Assert.Equal((uint)2, decoder.Width);
        Assert.Equal((uint)1, decoder.Height);
        Assert.Equal(ToBytes(rgba), pixels);
        Assert.Equal(1, ReadChannel(pixels, 1, 0) - ReadChannel(pixels, 0, 0));
    }

    [Fact]
    public async Task ExifRotatedTiffSnapshotHasOrientedGeometryAndPixels()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "oriented.tiff");
        string snapshot = Path.Combine(directory.Path, "snapshot.png");
        // A red pixel followed by a green one. EXIF6 rotates 90 degrees CW.
        ushort[] rgba = [65535, 0, 0, 65535, 0, 65535, 0, 65535];
        await WriteImageAsync(source, BitmapEncoder.TiffEncoderId, 2, 1, ToBytes(rgba), orientation: 6);
        byte[] original = await File.ReadAllBytesAsync(source);
        DecodedImage sourceDecoder = await ReadImageAsync(source);
        Assert.Equal((uint)1, sourceDecoder.OrientedWidth);
        Assert.Equal((uint)2, sourceDecoder.OrientedHeight);

        (int width, int height) = await ParallaxRasterSnapshot.SaveAsync(source, snapshot);

        Assert.Equal(1, width);
        Assert.Equal(2, height);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        DecodedImage decoder = await ReadImageAsync(snapshot);
        Assert.Equal((uint)1, decoder.Width);
        Assert.Equal((uint)2, decoder.Height);
        Assert.Equal(ToBytes(rgba), decoder.Pixels);
    }

    [Fact]
    public async Task ExifRotatedJpegSnapshotMatchesNativeOrientedDecodeWithoutChangingSource()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "oriented.jpg");
        string snapshot = Path.Combine(directory.Path, "snapshot.png");
        var rgba = new byte[32 * 16 * 4];
        for (int pixel = 0; pixel < 32 * 16; pixel++)
        {
            bool left = pixel % 32 < 16;
            rgba[pixel * 4] = left ? (byte)240 : (byte)40;
            rgba[pixel * 4 + 1] = left ? (byte)80 : (byte)200;
            rgba[pixel * 4 + 2] = left ? (byte)50 : (byte)70;
            rgba[pixel * 4 + 3] = 255;
        }
        await WriteImageAsync(source, BitmapEncoder.JpegEncoderId, 32, 16, rgba,
            orientation: 6, pixelFormat: BitmapPixelFormat.Rgba8);
        byte[] original = await File.ReadAllBytesAsync(source);
        DecodedImage expected = await ReadImageAsync(source, ExifOrientationMode.RespectExifOrientation);
        Assert.Equal((uint)16, expected.OrientedWidth);
        Assert.Equal((uint)32, expected.OrientedHeight);

        (int width, int height) = await ParallaxRasterSnapshot.SaveAsync(source, snapshot);

        Assert.Equal(16, width);
        Assert.Equal(32, height);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        DecodedImage actual = await ReadImageAsync(snapshot);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Fact]
    public async Task ExistingDestinationIsNotOverwritten()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "source.png");
        string snapshot = Path.Combine(directory.Path, "snapshot.png");
        await WriteImageAsync(source, BitmapEncoder.PngEncoderId, 1, 1, ToBytes([100, 200, 300, 65535]));
        byte[] existing = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(snapshot, existing);

        await Assert.ThrowsAnyAsync<Exception>(() => ParallaxRasterSnapshot.SaveAsync(source, snapshot));

        Assert.Equal(existing, await File.ReadAllBytesAsync(snapshot));
    }

    private static async Task WriteImageAsync(string path, Guid format, uint width, uint height, byte[] rgba,
        ushort? orientation = null, BitmapPixelFormat pixelFormat = BitmapPixelFormat.Rgba16)
    {
        using (File.Create(path)) { }
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(format, stream);
        encoder.SetPixelData(pixelFormat, BitmapAlphaMode.Straight, width, height, 96, 96, rgba);
        if (orientation is ushort value)
        {
            string query = format == BitmapEncoder.JpegEncoderId ? "/app1/ifd/{ushort=274}" : "/ifd/{ushort=274}";
            var metadata = new BitmapPropertySet { [query] = new BitmapTypedValue(value, PropertyType.UInt16) };
            await encoder.BitmapProperties.SetPropertiesAsync(metadata);
        }
        await encoder.FlushAsync();
    }

    private static async Task<DecodedImage> ReadImageAsync(string path,
        ExifOrientationMode orientation = ExifOrientationMode.IgnoreExifOrientation)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba16,
            BitmapAlphaMode.Straight, new BitmapTransform(), orientation,
            ColorManagementMode.DoNotColorManage);
        return new(decoder.DecoderInformation.CodecId, decoder.PixelWidth, decoder.PixelHeight,
            decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, provider.DetachPixelData());
    }

    private sealed record DecodedImage(Guid CodecId, uint Width, uint Height, uint OrientedWidth,
        uint OrientedHeight, byte[] Pixels);

    private static byte[] ToBytes(ushort[] channels)
    {
        var result = new byte[channels.Length * 2];
        for (int index = 0; index < channels.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(index * 2, 2), channels[index]);
        return result;
    }

    private static ushort ReadChannel(byte[] data, int pixel, int channel) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pixel * 8 + channel * 2, 2));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Seiza-RasterSnapshot-Test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
