namespace Seiza.App.Models;

public sealed record RenderedImage16Data(
    byte[] RgbaBytes,
    int Width,
    int Height,
    ImageMetadata Metadata);
