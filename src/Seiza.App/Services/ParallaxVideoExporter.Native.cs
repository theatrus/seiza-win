using Seiza.App.Models;

namespace Seiza.App.Services;

internal static partial class ParallaxVideoExporter
{
    public static Task ExportAsync(
        ParallaxVideo video,
        string destinationPath,
        ParallaxVideoCodec codec,
        IProgress<ParallaxExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        return ExportAsync(new NativeVideoSource(video), destinationPath, codec, progress, cancellationToken);
    }

    private sealed class NativeVideoSource(ParallaxVideo video) : IParallaxVideoFrameSource
    {
        public int Width => video.Width;
        public int Height => video.Height;
        public int FramesPerSecond => video.FramesPerSecond;
        public int FrameCount => video.FrameCount;

        public void RenderBgraFrame(int index, Span<byte> destination, CancellationToken cancellationToken) =>
            video.RenderBgraFrame(index, destination, checked(Width * 4), cancellationToken);
    }
}
