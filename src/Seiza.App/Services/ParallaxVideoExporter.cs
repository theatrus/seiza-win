using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Seiza.App.Models;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

/// <summary>Native, silent MP4 encoding with a bounded pool and atomic publication.</summary>
internal static partial class ParallaxVideoExporter
{
    internal const int MaximumBufferedFrames = 6;

    internal delegate Task<PrepareTranscodeResult> PrepareEncoding(
        MediaStreamSource source,
        IRandomAccessStream destination,
        ParallaxVideoCodec codec,
        IParallaxVideoFrameSource video,
        CancellationToken cancellationToken);

    public static Task ExportAsync(
        IParallaxVideoFrameSource video,
        string destinationPath,
        ParallaxVideoCodec codec,
        IProgress<ParallaxExportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExportWithPreparationAsync(video, destinationPath, codec, PrepareEncodingAsync,
            progress, cancellationToken);

    // Per-export injection keeps setup failures testable without mutable process-wide state.
    internal static async Task ExportWithPreparationAsync(
        IParallaxVideoFrameSource video,
        string destinationPath,
        ParallaxVideoCodec codec,
        PrepareEncoding prepareEncoding,
        IProgress<ParallaxExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(prepareEncoding);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        Validate(video, codec);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The movie needs an output directory.", nameof(destinationPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The movie output directory does not exist.");
        }
        string staging = Path.Combine(directory, $".seiza-parallax-{Guid.NewGuid():N}.mp4");
        bool ownsStaging = false;
        try
        {
            // CreateNew prevents a collision from overwriting any unrelated file.
            using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsStaging = true;
            }
            StorageFile output = await StorageFile.GetFileFromPathAsync(staging);
            using (IRandomAccessStream stream = await output.OpenAsync(FileAccessMode.ReadWrite))
            {
                await using var job = new EncodingJob(video, progress, prepareEncoding, cancellationToken);
                await job.EncodeAsync(stream, codec).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // The sibling staging file is complete and closed before this same-volume rename.
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (ownsStaging)
            {
                try { File.Delete(staging); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static TimeSpan FrameTime(int index, int fps) =>
        TimeSpan.FromTicks(checked((long)index * TimeSpan.TicksPerSecond) / fps);

    private static void Validate(IParallaxVideoFrameSource video, ParallaxVideoCodec codec)
    {
        if (video.Width < 16 || video.Height < 16 || video.Width > 3840 || video.Height > 3840 ||
            video.Width % 2 != 0 || video.Height % 2 != 0 ||
            video.FramesPerSecond is < 1 or > 60 || video.FrameCount is < 2 or > 36_000)
        {
            throw new ArgumentException("Unsupported movie dimensions, frame rate, or duration.", nameof(video));
        }
        if (!Enum.IsDefined(codec))
        {
            throw new ArgumentOutOfRangeException(nameof(codec));
        }
    }

    private static void SetDisplayColorProperties(VideoEncodingProperties properties)
    {
        // MF_MT_VIDEO_PRIMARIES / TRANSFER_FUNCTION / YUV_MATRIX (Windows SDK mfapi.h).
        // Shared-core display pixels are sRGB: do not relabel their transfer as Rec.709.
        properties.Properties[new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = 2u;
        properties.Properties[new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = 7u;
        properties.Properties[new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")] = 1u;
    }

    private static async Task<PrepareTranscodeResult> PrepareEncodingAsync(
        MediaStreamSource source,
        IRandomAccessStream destination,
        ParallaxVideoCodec codec,
        IParallaxVideoFrameSource video,
        CancellationToken cancellationToken)
    {
        // Keep Windows' codec-specific profile defaults (including HEVC profile
        // attributes) instead of replacing Video with a minimally initialized type.
        var profile = codec == ParallaxVideoCodec.Hevc
            ? MediaEncodingProfile.CreateHevc(VideoEncodingQuality.HD1080p)
            : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)video.Width;
        profile.Video.Height = (uint)video.Height;
        profile.Video.FrameRate.Numerator = (uint)video.FramesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Max(2_000_000L,
            (long)video.Width * video.Height * video.FramesPerSecond / 2);
        SetDisplayColorProperties(profile.Video);
        // Request no B-frame lookahead. Hardware encoders may ignore optional
        // codec properties; the sample pool remains capped independently.
        profile.Video.Properties[new Guid("8d390aac-dc5c-4200-b57f-814d04babab2")] = 0u;

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        return await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, destination, profile)
            .AsTask(cancellationToken).ConfigureAwait(false);
    }

    private sealed class EncodingJob : IAsyncDisposable
    {
        private readonly IParallaxVideoFrameSource _video;
        private readonly PrepareEncoding _prepareEncoding;
        private readonly IProgress<ParallaxExportProgress>? _progress;
        private readonly CancellationToken _callerCancellation;
        private readonly CancellationTokenSource _stop;
        private readonly SemaphoreSlim _producer = new(1, 1);
        private readonly SemaphoreSlim _slots = new(MaximumBufferedFrames, MaximumBufferedFrames);
        private readonly ConcurrentQueue<byte[]> _buffers = new();
        private readonly ConcurrentDictionary<MediaStreamSample, byte[]> _inFlight = new();
        private readonly object _lifecycle = new();
        private TaskCompletionSource? _idle;
        private ExceptionDispatchInfo? _failure;
        private int _activeCallbacks;
        private int _index;
        private int _allocatedBuffers;
        private bool _disposing;

        internal EncodingJob(
            IParallaxVideoFrameSource video,
            IProgress<ParallaxExportProgress>? progress,
            PrepareEncoding prepareEncoding,
            CancellationToken cancellationToken)
        {
            _video = video;
            _prepareEncoding = prepareEncoding;
            _progress = progress;
            _callerCancellation = cancellationToken;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var input = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)video.Width, (uint)video.Height);
            input.FrameRate.Numerator = (uint)video.FramesPerSecond;
            input.FrameRate.Denominator = 1;
            input.PixelAspectRatio.Numerator = 1;
            input.PixelAspectRatio.Denominator = 1;
            // MF_MT_DEFAULT_STRIDE: shared-core BGRA rows are top-down (positive
            // stride). Without this, system-memory RGB defaults to bottom-up.
            input.Properties[new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = checked((uint)video.Width * 4);
            SetDisplayColorProperties(input);
            Source = new MediaStreamSource(new VideoStreamDescriptor(input))
            {
                CanSeek = false,
                BufferTime = TimeSpan.Zero,
                Duration = FrameTime(video.FrameCount, video.FramesPerSecond),
            };
            Source.Starting += Source_Starting;
            Source.SampleRequested += Source_SampleRequested;
        }

        private MediaStreamSource Source { get; }

        internal async Task EncodeAsync(IRandomAccessStream destination, ParallaxVideoCodec codec)
        {
            try
            {
                PrepareTranscodeResult prepared;
                try
                {
                    prepared = await _prepareEncoding(Source, destination, codec, _video, _stop.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is COMException or NotSupportedException)
                {
                    // Windows can reject a profile or fail asynchronous preparation before
                    // returning CanTranscode/FailureReason. Do not infer a specific missing
                    // codec from an unknown HRESULT, or relabel render/transcode failures.
                    _callerCancellation.ThrowIfCancellationRequested();
                    Volatile.Read(ref _failure)?.Throw();
                    throw new InvalidOperationException(
                        $"Windows cannot encode this movie as {(codec == ParallaxVideoCodec.Hevc ? "HEVC" : "H.264")} " +
                        $"(Windows encoder preparation failed; HRESULT 0x{exception.HResult:X8}). " +
                        "Try H.264 at 1080p; HEVC requires an available Windows encoder.", exception);
                }
                if (!prepared.CanTranscode)
                {
                    throw new InvalidOperationException(
                        $"Windows cannot encode this movie as {(codec == ParallaxVideoCodec.Hevc ? "HEVC" : "H.264")} " +
                        $"({prepared.FailureReason}). Try H.264 at 1080p; HEVC requires an available Windows encoder.");
                }
                await prepared.TranscodeAsync().AsTask(_stop.Token).ConfigureAwait(false);
                _callerCancellation.ThrowIfCancellationRequested();
                Volatile.Read(ref _failure)?.Throw();
                if (Volatile.Read(ref _index) != _video.FrameCount)
                {
                    throw new InvalidOperationException("The Windows encoder did not consume the complete movie.");
                }
                await destination.FlushAsync().AsTask(_stop.Token).ConfigureAwait(false);
            }
            catch
            {
                _callerCancellation.ThrowIfCancellationRequested();
                Volatile.Read(ref _failure)?.Throw();
                throw;
            }
        }

        private void Source_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            if (!TryBeginCallback()) { return; }
            try { args.Request.SetActualStartPosition(TimeSpan.Zero); }
            catch (Exception exception) { Fail(exception); }
            finally { EndCallback(); }
        }

        private void Source_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            MediaStreamSourceSampleRequestDeferral deferral = args.Request.GetDeferral();
            if (!TryBeginCallback())
            {
                // The pipeline may deliver one already-queued request during shutdown.
                try { deferral.Complete(); }
                catch (Exception) { }
                return;
            }
            // Native pixel rendering must never block the UI or a Media Foundation callback.
            _ = Task.Run(() => ProduceAsync(args.Request, deferral));
        }

        private async Task ProduceAsync(
            MediaStreamSourceSampleRequest request,
            MediaStreamSourceSampleRequestDeferral deferral)
        {
            bool ownsProducer = false;
            bool ownsSlot = false;
            byte[]? buffer = null;
            MediaStreamSample? sample = null;
            try
            {
                await _producer.WaitAsync(_stop.Token).ConfigureAwait(false);
                ownsProducer = true;
                _stop.Token.ThrowIfCancellationRequested();
                if (_index == _video.FrameCount)
                {
                    request.Sample = null;
                    return;
                }
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                ownsSlot = true;
                if (!_buffers.TryDequeue(out buffer))
                {
                    buffer = GC.AllocateUninitializedArray<byte>(checked(_video.Width * _video.Height * 4));
                    _allocatedBuffers++;
                }
                int index = _index;
                _video.RenderBgraFrame(index, buffer, _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                TimeSpan timestamp = FrameTime(index, _video.FramesPerSecond);
                sample = MediaStreamSample.CreateFromBuffer(buffer.AsBuffer(), timestamp);
                sample.Duration = FrameTime(index + 1, _video.FramesPerSecond) - timestamp;
                sample.Discontinuous = index == 0;
                sample.Processed += Sample_Processed;
                _inFlight[sample] = buffer;
                buffer = null;
                ownsSlot = false; // Ownership now belongs to Sample_Processed (or teardown).
                request.Sample = sample;
                _index++;
                _progress?.Report(new ParallaxExportProgress(_index, _video.FrameCount, _allocatedBuffers));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                NotifyStopped();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            finally
            {
                if (buffer is not null)
                {
                    _buffers.Enqueue(buffer);
                }
                if (ownsSlot)
                {
                    _slots.Release();
                }
                if (ownsProducer)
                {
                    _producer.Release();
                }
                try
                {
                    try { deferral.Complete(); }
                    catch (Exception exception)
                    {
                        if (!_stop.IsCancellationRequested)
                        {
                            Fail(exception);
                        }
                    }
                }
                finally
                {
                    EndCallback();
                }
            }
        }

        private bool TryBeginCallback()
        {
            lock (_lifecycle)
            {
                if (_disposing) { return false; }
                _activeCallbacks++;
                return true;
            }
        }

        private void EndCallback()
        {
            lock (_lifecycle)
            {
                if (--_activeCallbacks == 0) { _idle?.TrySetResult(); }
            }
        }

        private void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
            _stop.Cancel();
            NotifyStopped();
        }

        private void NotifyStopped()
        {
            try { Source.NotifyError(MediaStreamSourceErrorStatus.Other); }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }

        private void Sample_Processed(MediaStreamSample sample, object args)
        {
            if (_inFlight.TryRemove(sample, out byte[]? buffer))
            {
                sample.Processed -= Sample_Processed;
                _buffers.Enqueue(buffer);
                _slots.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task idle;
            lock (_lifecycle)
            {
                _disposing = true;
                idle = _activeCallbacks == 0
                    ? Task.CompletedTask
                    : (_idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            Source.Starting -= Source_Starting;
            Source.SampleRequested -= Source_SampleRequested;
            _stop.Cancel();
            await idle.ConfigureAwait(false);
            // Processed is not guaranteed after a failed/canceled transcode. No borrowed
            // source pixels survive this point, and all producer callbacks have exited.
            foreach (MediaStreamSample sample in _inFlight.Keys)
            {
                Sample_Processed(sample, null!);
            }
            _stop.Dispose();
            // A late informational Processed event may still arrive: keep its synchronization
            // primitives valid rather than racing Dispose against the Windows pipeline.
        }
    }
}
