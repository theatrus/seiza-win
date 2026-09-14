namespace Seiza.App.Models;

internal static class ImageSampling
{
    // Keep reduced live previews filtered even when enlarged. Only a full-size
    // bitmap can show source pixels at 1:1 or higher without interpolation.
    public static bool NeedsFiltering(
        double bitmapWidth, double bitmapHeight,
        double sourceWidth, double sourceHeight,
        double drawingWidth, double drawingHeight,
        double displayScale) =>
        bitmapWidth < sourceWidth || bitmapHeight < sourceHeight ||
        drawingWidth * displayScale < bitmapWidth ||
        drawingHeight * displayScale < bitmapHeight;
}
