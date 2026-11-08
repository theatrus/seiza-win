using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

/// <summary>Encodes CPU pixels without allocating a full-image Direct2D target.</summary>
internal static class Bgra8Encoder
{
    public static async Task WriteAsync(IRandomAccessStream stream, byte[] pixels, int width, int height,
        Guid encoderId, float? quality = null)
    {
        if (width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("The image has an invalid BGRA pixel buffer.", nameof(pixels));
        stream.Seek(0); stream.Size = 0;
        var options = new BitmapPropertySet();
        if (quality is float value) options.Add("ImageQuality", new BitmapTypedValue(value, PropertyType.Single));
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
