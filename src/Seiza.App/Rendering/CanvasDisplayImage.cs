using Microsoft.Graphics.Canvas;
using Seiza.App.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace Seiza.App.Rendering;

/// <summary>Full-resolution display with automatic GPU tiling for large mosaics.</summary>
internal sealed class CanvasDisplayImage : IDisposable
{
    private RenderedImageData? _image;
    private ICanvasImage? _source;
    private IRandomAccessStream? _stream;
    public int Width { get; }
    public int Height { get; }

    private CanvasDisplayImage(RenderedImageData image)
    {
        Width = image.Width; Height = image.Height;
        if (Width <= 0 || Height <= 0 || image.Bgra.LongLength != (long)Width * Height * 4)
            throw new ArgumentException("The image has an invalid BGRA pixel buffer.", nameof(image));
        _image = image;
    }

    public static async Task<CanvasDisplayImage> CreateAsync(ICanvasResourceCreator creator, RenderedImageData image,
        bool forceVirtual = false, CancellationToken cancellationToken = default)
    {
        var result = new CanvasDisplayImage(image);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int maximum = creator.Device.MaximumBitmapSizeInPixels;
            if (!forceVirtual && image.Width <= maximum && image.Height <= maximum)
                result._source = CanvasBitmap.CreateFromBytes(creator, image.Bgra, image.Width, image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            else
            {
                // Direct2D caches only requested regions. The seekable BMP view
                // borrows the CPU pixels instead of encoding another huge image.
                result._stream = new BgraBitmapStream(image.Bgra, image.Width, image.Height).AsRandomAccessStream();
                result._source = await CanvasVirtualBitmap.LoadAsync(creator, result._stream,
                    CanvasVirtualBitmapOptions.CacheOnDemand, CanvasAlphaMode.Ignore);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    // This immutable CPU snapshot survives disposal of GPU resources on navigation.
    public RenderedImageData Snapshot() => _image ?? throw new ObjectDisposedException(nameof(CanvasDisplayImage));
    public byte[] GetPixelBytes(int x, int y, int width, int height) =>
        ImageTileLayout.CopyPixels(Snapshot().Bgra, Width, Height, new(x, y, width, height));

    public void Draw(CanvasDrawingSession drawing, Rect destination, CanvasImageInterpolation interpolation)
    {
        ObjectDisposedException.ThrowIf(_image is null, this);
        drawing.DrawImage(_source, destination, new Rect(0, 0, Width, Height), 1, interpolation);
    }
    public void Dispose()
    {
        (_source as IDisposable)?.Dispose(); _source = null;
        _stream?.Dispose(); _stream = null; _image = null;
    }
}
