namespace Seiza.App.Rendering;

internal readonly record struct ImagePixelRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>Partitions source pixels without making the image a single GPU texture.</summary>
internal static class ImageTileLayout
{
    public static IEnumerable<ImagePixelRegion> Create(int width, int height, int maximumSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSize);
        for (int y = 0; y < height;)
        {
            int h = Math.Min(maximumSize, height - y);
            for (int x = 0; x < width;)
            {
                int w = Math.Min(maximumSize, width - x);
                yield return new(x, y, w, h);
                x += w;
            }
            y += h;
        }
    }

    public static byte[] CopyPixels(byte[] bgra, int width, int height, ImagePixelRegion region)
    {
        if (width <= 0 || height <= 0 || bgra.LongLength != (long)width * height * 4)
            throw new ArgumentException("The image has an invalid BGRA pixel buffer.");
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > width || (long)region.Y + region.Height > height)
            throw new ArgumentOutOfRangeException(nameof(region));
        byte[] pixels = new byte[checked(region.Width * region.Height * 4)];
        for (int row = 0; row < region.Height; row++)
            bgra.AsSpan(checked(((region.Y + row) * width + region.X) * 4), region.Width * 4)
                .CopyTo(pixels.AsSpan(row * region.Width * 4));
        return pixels;
    }
}
