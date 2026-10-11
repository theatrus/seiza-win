using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

/// <summary>
/// Lossless high-depth snapshot for already-rendered raster sources. WIC performs
/// format conversion and EXIF orientation; C# does not process individual pixels.
/// </summary>
internal static class ParallaxRasterSnapshot
{
    public static async Task<(int Width, int Height)> SaveAsync(string sourcePath, string destinationPng)
    {
        StorageFile source = await StorageFile.GetFileFromPathAsync(sourcePath);
        using IRandomAccessStream input = await source.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        uint width = decoder.OrientedPixelWidth;
        uint height = decoder.OrientedPixelHeight;
        PixelDataProvider provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba16,
            BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = provider.DetachPixelData();
        if (width == 0 || height == 0 || pixels.LongLength != checked((long)width * height * 8))
            throw new InvalidDataException("The raster snapshot has invalid pixel dimensions.");

        string outputPath = Path.GetFullPath(destinationPng);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
        // This is a private, new snapshot file—not a user-selected export. Refuse
        // collisions rather than modifying an existing file (including the source).
        StorageFile destination = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.FailIfExists);
        using IRandomAccessStream output = await destination.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight, width, height,
            PositiveDpi(decoder.DpiX), PositiveDpi(decoder.DpiY), pixels);
        await encoder.FlushAsync();
        return (checked((int)width), checked((int)height));
    }

    private static double PositiveDpi(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 96;
}
