namespace Seiza.App.Models;

public sealed record RenderedImageData(
    byte[] Bgra,
    int Width,
    int Height,
    ImageMetadata Metadata);
