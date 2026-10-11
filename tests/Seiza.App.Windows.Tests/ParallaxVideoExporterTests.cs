using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace Seiza.App.Windows.Tests;

public sealed class ParallaxVideoExporterTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void RationalTimestampsDoNotAccumulateRoundingError(int fps)
    {
        TimeSpan sum = TimeSpan.Zero;
        for (int index = 0; index < 36_000; index++)
        {
            TimeSpan start = ParallaxVideoExporter.FrameTime(index, fps);
            TimeSpan next = ParallaxVideoExporter.FrameTime(index + 1, fps);
            Assert.True(next > start);
            sum += next - start;
        }
        Assert.Equal(TimeSpan.FromSeconds(36_000 / fps), sum);
    }

    [Fact]
    public async Task H264MovieUsesNativeEncoderAndCanBeDecoded()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "movie.mp4");
        var source = new SyntheticFrames();
        var reports = new List<ParallaxExportProgress>();
        await ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.H264,
            new InlineProgress<ParallaxExportProgress>(reports.Add));

        Assert.Equal(Enumerable.Range(0, source.FrameCount), source.RenderedIndices);
        Assert.Equal(source.FrameCount, reports[^1].CompletedFrames);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.InRange(source.MaximumConcurrentRenders, 1, 1);
        Assert.InRange(reports.Max(report => report.AllocatedBuffers), 1, ParallaxVideoExporter.MaximumBufferedFrames);
        Assert.Empty(Directory.GetFiles(directory.Path, ".seiza-parallax-*.mp4"));
        Assert.True(new FileInfo(path).Length > 1000);

        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        MediaClip clip = await MediaClip.CreateFromFileAsync(file);
        VideoEncodingProperties video = clip.GetVideoEncodingProperties();
        Assert.Equal((uint)source.Width, video.Width);
        Assert.Equal((uint)source.Height, video.Height);
        Assert.Equal(MediaEncodingSubtypes.H264, video.Subtype, ignoreCase: true);
        Assert.Equal((double)source.FramesPerSecond,
            (double)video.FrameRate.Numerator / video.FrameRate.Denominator, precision: 4);
        Assert.InRange(clip.OriginalDuration.TotalSeconds, 0.49, 0.51);
        Assert.Equal(source.FrameCount, CountVideoSamples(await File.ReadAllBytesAsync(path)));
        Assert.Empty(clip.EmbeddedAudioTracks);

        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using ImageStream thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero,
            source.Width, source.Height, VideoFramePrecision.NearestFrame);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] bgra = pixels.DetachPixelData();
        int center = checked(((int)decoder.PixelHeight / 2 * (int)decoder.PixelWidth + (int)decoder.PixelWidth / 2) * 4);
        Assert.InRange(bgra[center], 0, 40);
        Assert.InRange(bgra[center + 1], 0, 50);
        Assert.InRange(bgra[center + 2], 175, 225);
    }

    [Fact]
    public async Task H264PreservesTopDownBgraRows()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "orientation.mp4");
        var source = new SyntheticFrames { AsymmetricRows = true };
        await ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.H264);
        MediaClip clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using ImageStream thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero,
            source.Width, source.Height, VideoFramePrecision.NearestFrame);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
        byte[] bgra = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        int top = ((source.Height / 4) * source.Width + source.Width / 2) * 4;
        int bottom = ((source.Height * 3 / 4) * source.Width + source.Width / 2) * 4;
        // The source is red above, blue below. Uniform frames cannot detect an
        // accidental bottom-up RGB interpretation in the Media Foundation path.
        Assert.InRange(bgra[top], 0, 40);
        Assert.InRange(bgra[top + 2], 175, 225);
        Assert.InRange(bgra[bottom], 175, 225);
        Assert.InRange(bgra[bottom + 2], 0, 40);
    }

    [Fact]
    public async Task CancellationDuringProductionPreservesExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "movie.mp4");
        byte[] previous = [1, 3, 5, 7, 9];
        await File.WriteAllBytesAsync(path, previous);
        using var cancellation = new CancellationTokenSource();
        var source = new SyntheticFrames { FrameCount = 1200 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.H264,
                new InlineProgress<ParallaxExportProgress>(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.InRange(source.RenderedIndices.Count, 1, ParallaxVideoExporter.MaximumBufferedFrames);
        Assert.Empty(Directory.GetFiles(directory.Path, ".seiza-parallax-*.mp4"));
    }

    [Fact]
    public async Task HevcEncodesOrReportsUnavailableWithoutReplacingDestination()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "movie.mp4");
        byte[] previous = [4, 8, 12];
        await File.WriteAllBytesAsync(path, previous);
        var source = new SyntheticFrames();
        try
        {
            await ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.Hevc);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Windows cannot encode this movie as HEVC", StringComparison.Ordinal))
        {
            output.WriteLine(exception.Message);
            Assert.Contains("Try H.264", exception.Message, StringComparison.Ordinal);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory.Path, ".seiza-parallax-*.mp4"));
            return;
        }

        MediaClip clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        Assert.Equal(MediaEncodingSubtypes.Hevc, clip.GetVideoEncodingProperties().Subtype, ignoreCase: true);
        Assert.Empty(clip.EmbeddedAudioTracks);
        Assert.Equal(source.FrameCount, CountVideoSamples(await File.ReadAllBytesAsync(path)));
        Assert.InRange(clip.OriginalDuration.TotalSeconds, 0.49, 0.51);
        output.WriteLine("HEVC was encoded successfully by this Windows installation.");
        Assert.Empty(Directory.GetFiles(directory.Path, ".seiza-parallax-*.mp4"));
    }

    [Fact]
    public async Task CancellationUnblocksAProducerWhileEncoderIsWaitingForItsSample()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "movie.mp4");
        byte[] previous = [16, 32, 64];
        await File.WriteAllBytesAsync(path, previous);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SyntheticFrames
        {
            BeforeRender = token =>
            {
                started.TrySetResult();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
            },
        };
        Task encoding = ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.H264,
            cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoding.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Empty(source.RenderedIndices);
        Assert.Empty(Directory.GetFiles(directory.Path, ".seiza-parallax-*.mp4"));
    }

    [Fact]
    public async Task ProducerFailurePreservesDestinationAndReportsOriginalFailure()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "movie.mp4");
        byte[] previous = [2, 4, 6];
        await File.WriteAllBytesAsync(path, previous);
        var expected = new InvalidOperationException("Synthetic frame failure.");
        var source = new SyntheticFrames { RenderFailure = expected };
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.H264));
        Assert.Same(expected, actual);
        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, ".seiza-parallax-*.mp4"));
    }

    [Fact]
    public async Task AlreadyCanceledDoesNotTouchDestinationOrRequestFrames()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "movie.mp4");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new SyntheticFrames();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ParallaxVideoExporter.ExportAsync(source, path, ParallaxVideoCodec.H264,
                cancellationToken: cancellation.Token));
        Assert.Empty(source.RenderedIndices);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(15, 48, 24, 12)]
    [InlineData(63, 48, 24, 12)]
    [InlineData(64, 3842, 24, 12)]
    [InlineData(64, 48, 61, 12)]
    [InlineData(64, 48, 24, 1)]
    [InlineData(64, 48, 24, 36001)]
    public async Task UnsupportedVideoIsRejectedBeforeWriting(int width, int height, int fps, int frames)
    {
        using var directory = new TemporaryDirectory();
        var source = new SyntheticFrames { Width = width, Height = height, FramesPerSecond = fps, FrameCount = frames };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ParallaxVideoExporter.ExportAsync(source, Path.Combine(directory.Path, "movie.mp4"), ParallaxVideoCodec.H264));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    internal static int CountVideoSamples(byte[] mp4)
    {
        // MP4 stts records the exact encoded sample count (not a rounded duration).
        int count = 0;
        Visit(0, mp4.Length);
        return count;

        void Visit(int start, int end)
        {
            for (int offset = start; offset + 8 <= end;)
            {
                int size = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(offset, 4)));
                Assert.InRange(size, 8, end - offset);
                string kind = System.Text.Encoding.ASCII.GetString(mp4, offset + 4, 4);
                if (kind is "moov" or "trak" or "mdia" or "minf" or "stbl")
                {
                    Visit(offset + 8, offset + size);
                }
                else if (kind == "stts")
                {
                    int entries = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(offset + 12, 4)));
                    for (int index = 0; index < entries; index++)
                    {
                        count += checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(offset + 16 + index * 8, 4)));
                    }
                }
                offset += size;
            }
        }
    }

    private sealed class SyntheticFrames : IParallaxVideoFrameSource
    {
        private int _concurrent;
        public int Width { get; init; } = 64;
        public int Height { get; init; } = 48;
        public int FramesPerSecond { get; init; } = 24;
        public int FrameCount { get; init; } = 12;
        public Exception? RenderFailure { get; init; }
        public Action<CancellationToken>? BeforeRender { get; init; }
        public bool AsymmetricRows { get; init; }
        public List<int> RenderedIndices { get; } = [];
        public int MaximumConcurrentRenders { get; private set; }

        public void RenderBgraFrame(int index, Span<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumConcurrentRenders = Math.Max(MaximumConcurrentRenders, Interlocked.Increment(ref _concurrent));
            try
            {
                BeforeRender?.Invoke(cancellationToken);
                if (RenderFailure is not null) { throw RenderFailure; }
                RenderedIndices.Add(index);
                for (int pixel = 0; pixel < destination.Length; pixel += 4)
                {
                    bool lowerHalf = AsymmetricRows && pixel / (Width * 4) >= Height / 2;
                    destination[pixel] = lowerHalf ? (byte)200 : (byte)(10 + index * 3 % 100);
                    destination[pixel + 1] = 20;
                    destination[pixel + 2] = lowerHalf ? (byte)10 : (byte)200;
                    destination[pixel + 3] = 255;
                }
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "SeizaParallaxEncoderTests-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
