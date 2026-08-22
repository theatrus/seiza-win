using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>
/// Serialized owner of one native live-stacker handle.
/// </summary>
/// <remarks>
/// Native calls are synchronous and a stacker is mutable. Every operation is
/// therefore queued behind one gate and performed on a worker thread. Native
/// borrowed views are copied before the gate is released.
/// </remarks>
internal sealed class ImageStackSession : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeLiveStackerHandle? _handle;
    private bool _disposed;
    private bool _finished;

    private ImageStackSession(SafeLiveStackerHandle handle)
    {
        _handle = handle;
    }

    public static Task<ImageStackSession> OpenAsync(
        string referencePath,
        ImageStackOptions options,
        ImageStackCalibration calibration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calibration);

        string configurationJson = options.ToJson();
        NativeCalibration nativeCalibration = NativeCalibration.From(calibration);
        string fullReferencePath = Path.GetFullPath(referencePath);

        return Task.Run(() =>
        {
            nint error = 0;
            nint rawHandle = NativeMethods.OpenLiveStacker(
                fullReferencePath,
                nativeCalibration.BiasPath,
                nativeCalibration.DarkPath,
                nativeCalibration.FlatPath,
                nativeCalibration.DarkExposureSeconds,
                configurationJson,
                out error);
            if (rawHandle == 0)
            {
                throw NativeString.TakeError(
                    error,
                    "The Seiza core could not open the reference image.");
            }
            FreeUnexpectedError(error);
            return new ImageStackSession(new SafeLiveStackerHandle(rawHandle));
        }, cancellationToken);
    }

    public static Task<ImageStackSession> ResumeAsync(
        string contextPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        string fullContextPath = Path.GetFullPath(contextPath);

        return Task.Run(() =>
        {
            nint error = 0;
            nint rawHandle = NativeMethods.OpenLiveStackerContext(fullContextPath, out error);
            if (rawHandle == 0)
            {
                throw NativeString.TakeError(
                    error,
                    "The Seiza core could not resume the saved stack.");
            }
            FreeUnexpectedError(error);
            return new ImageStackSession(new SafeLiveStackerHandle(rawHandle));
        }, cancellationToken);
    }

    public static unsafe IReadOnlyList<int> GetSnrMeasurementDepths(int totalFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalFrames);
        nuint count = NativeMethods.GetStackSnrCheckpointDepths((nuint)totalFrames, null, 0);
        if (count == 0)
        {
            return [];
        }

        int length = checked((int)count);
        var nativeDepths = new nuint[length];
        fixed (nuint* output = nativeDepths)
        {
            nuint actual = NativeMethods.GetStackSnrCheckpointDepths(
                (nuint)totalFrames,
                output,
                (nuint)nativeDepths.Length);
            if (actual != count)
            {
                throw new SeizaCoreException(
                    "The Seiza core returned an inconsistent SNR measurement schedule.");
            }
        }

        return nativeDepths.Select(depth => checked((int)depth)).ToArray();
    }

    public Task<ImageStackPushResult> PushFrameAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return RunAsync(handle => PushFrame(handle, fullPath), cancellationToken);
    }

    public Task<ImageStackPipelineResult> PushFramesAsync(
        IReadOnlyList<string> paths,
        nuint workers = 0,
        nuint maxInFlightBytes = 0,
        float normalizedFullScale = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!float.IsFinite(normalizedFullScale) || normalizedFullScale < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalizedFullScale),
                "The normalized full scale must be finite and non-negative.");
        }

        string[] fullPaths = paths.Select(path =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Path.GetFullPath(path);
        }).ToArray();
        if (fullPaths.Length == 0)
        {
            return Task.FromResult(new ImageStackPipelineResult([], 0, 0, 0));
        }

        string pathsJson = JsonSerializer.Serialize(
            fullPaths,
            SeizaJsonSerializerContext.Default.StringArray);
        return RunAsync(
            handle => PushFrames(
                handle,
                pathsJson,
                workers,
                maxInFlightBytes,
                normalizedFullScale),
            cancellationToken);
    }

    public Task<LiveStackNativeState> GetStateAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(ReadState, cancellationToken);

    public Task<ImageStackSessionCounts> GetCountsAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(handle => new ImageStackSessionCounts(
            checked((int)NativeMethods.GetLiveStackerAcceptedFrames(handle.DangerousGetHandle())),
            checked((int)NativeMethods.GetLiveStackerRejectedFrames(handle.DangerousGetHandle()))),
            cancellationToken);

    public Task SaveContextAsync(
        string contextPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        string fullContextPath = Path.GetFullPath(contextPath);
        return RunAsync(
            handle => SaveContext(handle, fullContextPath),
            cancellationToken);
    }

    /// <summary>
    /// Checkpoints and reads the matching native identity under one session
    /// gate, so a calibration swap or frame push cannot make the returned
    /// state describe a later accumulator than the file on disk.
    /// </summary>
    public Task<LiveStackNativeState> SaveContextAndGetStateAsync(
        string contextPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        string fullContextPath = Path.GetFullPath(contextPath);
        return RunAsync(handle =>
        {
            SaveContext(handle, fullContextPath);
            return ReadState(handle);
        }, cancellationToken);
    }

    public Task SetCalibrationAsync(
        ImageStackCalibration calibration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        NativeCalibration nativeCalibration = NativeCalibration.From(calibration);
        return RunAsync(handle =>
        {
            nint error = 0;
            if (!NativeMethods.SetLiveStackerCalibration(
                    handle.DangerousGetHandle(),
                    nativeCalibration.BiasPath,
                    nativeCalibration.DarkPath,
                    nativeCalibration.FlatPath,
                    nativeCalibration.DarkExposureSeconds,
                    out error))
            {
                throw NativeString.TakeError(
                    error,
                    "The Seiza core could not change the calibration masters.");
            }
            FreeUnexpectedError(error);
        }, cancellationToken);
    }

    public Task<ImageStackSnrSample?> MeasureDepthAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(MeasureDepth, cancellationToken);

    public Task<ImageStackLiveView> CopyLiveViewAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(CopyLiveView, cancellationToken);

    public Task<RenderedImageData> RenderPreviewAsync(
        string processingConfigurationJson,
        uint maxDimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processingConfigurationJson);
        ArgumentOutOfRangeException.ThrowIfZero(maxDimension);
        return RunAsync(
            handle => RenderPreview(handle, processingConfigurationJson, maxDimension),
            cancellationToken);
    }

    public Task<ImageStackSnapshot> SnapshotAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(CreateSnapshot, cancellationToken);

    public Task<ImageStackExportSnapshot> ExportSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(CreateExportSnapshot, cancellationToken);

    public async Task<ImageStackSnapshot> FinishAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeLiveStackerHandle liveHandle = RequireHandle();
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                nint error = 0;
                nint rawSnapshot;
                try
                {
                    rawSnapshot = liveHandle.Finish(out error);
                }
                finally
                {
                    _handle = null;
                    _finished = true;
                    liveHandle.Dispose();
                }

                if (rawSnapshot == 0)
                {
                    throw NativeString.TakeError(
                        error,
                        "The Seiza core could not finish the image stack.");
                }
                FreeUnexpectedError(error);
                return CreateOwnedSnapshot(rawSnapshot);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            DisposeHandle();
        }
        finally
        {
            _gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeHandle();
        }
        finally
        {
            _gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private async Task<T> RunAsync<T>(
        Func<SafeLiveStackerHandle, T> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeLiveStackerHandle handle = RequireHandle();
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(
                () => operation(handle),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(
        Action<SafeLiveStackerHandle> operation,
        CancellationToken cancellationToken)
    {
        await RunAsync(handle =>
        {
            operation(handle);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private SafeLiveStackerHandle RequireHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The live stack has already been finalized.");
        }
        return _handle
            ?? throw new ObjectDisposedException(nameof(ImageStackSession));
    }

    private void DisposeHandle()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _handle?.Dispose();
        _handle = null;
    }

    private static ImageStackPushResult PushFrame(
        SafeLiveStackerHandle handle,
        string path)
    {
        nint error = 0;
        nint response = NativeMethods.PushLiveStackerFrameJson(
            handle.DangerousGetHandle(),
            path,
            out error);
        if (response == 0)
        {
            return new ImageStackPushResult(
                new ImageStackDisposition(
                    path,
                    false,
                    NativeString.TakeOwned(error, "The frame could not be read.")),
                NativeFailure: true);
        }

        FreeUnexpectedError(error);
        string json = NativeString.TakeOwned(
            response,
            "The Seiza core returned an invalid stacking result.");
        ImageStackDisposition disposition = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.ImageStackDisposition)
            ?? throw new SeizaCoreException(
                "The Seiza core returned an invalid stacking result.");
        return new ImageStackPushResult(disposition, NativeFailure: false);
    }

    private static ImageStackPipelineResult PushFrames(
        SafeLiveStackerHandle handle,
        string pathsJson,
        nuint workers,
        nuint maxInFlightBytes,
        float normalizedFullScale)
    {
        nint error = 0;
        nint response = NativeMethods.PushLiveStackerFramesJson(
            handle.DangerousGetHandle(),
            pathsJson,
            workers,
            maxInFlightBytes,
            normalizedFullScale,
            out error);
        if (response == 0)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not process the stack frames.");
        }

        FreeUnexpectedError(error);
        string json = NativeString.TakeOwned(
            response,
            "The Seiza core returned an invalid pipelined stacking result.");
        return JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.ImageStackPipelineResult)
            ?? throw new SeizaCoreException(
                "The Seiza core returned an invalid pipelined stacking result.");
    }

    private static void SaveContext(SafeLiveStackerHandle handle, string contextPath)
    {
        nint error = 0;
        if (!NativeMethods.SaveLiveStackerContext(
                handle.DangerousGetHandle(),
                contextPath,
                out error))
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not checkpoint the live stack.");
        }
        FreeUnexpectedError(error);
    }

    private static LiveStackNativeState ReadState(SafeLiveStackerHandle handle)
    {
        nint error = 0;
        nint response = NativeMethods.GetLiveStackerStateJson(
            handle.DangerousGetHandle(),
            out error);
        if (response == 0)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not describe the live stack.");
        }

        FreeUnexpectedError(error);
        string json = NativeString.TakeOwned(
            response,
            "The Seiza core returned invalid live-stack state.");
        LiveStackNativeState state = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.LiveStackNativeState)
            ?? throw new SeizaCoreException(
                "The Seiza core returned invalid live-stack state.");
        if (state.SchemaVersion != LiveStackNativeState.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(state.CoreVersion) ||
            state.Width <= 0 ||
            state.Height <= 0 ||
            state.Channels is not (1 or 3) ||
            state.AcceptedFrames <= 0 ||
            state.ConfigurationFingerprint.Length != 64 ||
            state.ConfigurationFingerprint.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            state.InputMode is not ("calibrate-and-prepare" or "prepared-only") ||
            state.InputPaths is null ||
            state.InputPaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new SeizaCoreException(
                "The Seiza core returned invalid live-stack state.");
        }
        return state;
    }

    private static unsafe ImageStackSnrSample? MeasureDepth(SafeLiveStackerHandle handle)
    {
        NativeSnrSample native = default;
        nint error = 0;
        int result = NativeMethods.MeasureLiveStackerDepth(
            handle.DangerousGetHandle(),
            &native,
            out error);
        if (result == 0)
        {
            if (error != 0)
            {
                throw NativeString.TakeError(
                    error,
                    "The Seiza core could not measure stack depth.");
            }
            return null;
        }
        if (result != 1)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not measure stack depth.");
        }
        FreeUnexpectedError(error);

        int channelCount = checked((int)native.ChannelCount);
        if (channelCount is < 0 or > NativeSnrSample.MaximumChannels)
        {
            throw new SeizaCoreException(
                "The Seiza core returned an invalid SNR channel count.");
        }

        var channelNoise = new double[channelCount];
        for (int index = 0; index < channelCount; index++)
        {
            channelNoise[index] = native.ChannelNoise[index];
        }
        return new ImageStackSnrSample(
            native.Frames,
            native.Noise,
            native.Background,
            native.Signal,
            native.Snr,
            channelNoise);
    }

    private static unsafe ImageStackLiveView CopyLiveView(SafeLiveStackerHandle handle)
    {
        nint stacker = handle.DangerousGetHandle();
        int width = checked((int)NativeMethods.GetLiveStackerWidth(stacker));
        int height = checked((int)NativeMethods.GetLiveStackerHeight(stacker));
        int channels = checked((int)NativeMethods.GetLiveStackerChannels(stacker));
        int length = checked((int)NativeMethods.GetLiveStackerDataLength(stacker));
        int expectedLength = checked(width * height * channels);
        nint meanPointer = NativeMethods.GetLiveStackerMean(stacker);
        nint coveragePointer = NativeMethods.GetLiveStackerCoverage(stacker);
        nint rejectedPointer = NativeMethods.GetLiveStackerRejectedSamples(stacker);
        if (width <= 0 || height <= 0 || channels <= 0 ||
            length != expectedLength || meanPointer == 0 ||
            coveragePointer == 0 || rejectedPointer == 0)
        {
            throw new SeizaCoreException(
                "The Seiza core returned an invalid live-stack view.");
        }

        var mean = GC.AllocateUninitializedArray<float>(length);
        var coverage = GC.AllocateUninitializedArray<uint>(length);
        var rejectedSamples = GC.AllocateUninitializedArray<uint>(length);
        Marshal.Copy(meanPointer, mean, 0, length);
        new ReadOnlySpan<uint>((void*)coveragePointer, length).CopyTo(coverage);
        new ReadOnlySpan<uint>((void*)rejectedPointer, length).CopyTo(rejectedSamples);

        return new ImageStackLiveView(
            width,
            height,
            channels,
            checked((int)NativeMethods.GetLiveStackerAcceptedFrames(stacker)),
            checked((int)NativeMethods.GetLiveStackerRejectedFrames(stacker)),
            mean,
            coverage,
            rejectedSamples);
    }

    private static RenderedImageData RenderPreview(
        SafeLiveStackerHandle handle,
        string processingConfigurationJson,
        uint maxDimension)
    {
        nint error = 0;
        nint rawImage = NativeMethods.RenderLiveStackerPreview(
            handle.DangerousGetHandle(),
            processingConfigurationJson,
            maxDimension,
            out error);
        if (rawImage == 0)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not render the live stack preview.");
        }

        FreeUnexpectedError(error);
        using var image = new SafeRenderedImageHandle(rawImage);
        return SeizaCore.CopyRenderedImage(image);
    }

    private static ImageStackSnapshot CreateSnapshot(SafeLiveStackerHandle handle)
    {
        nint error = 0;
        nint rawSnapshot = NativeMethods.SnapshotLiveStacker(
            handle.DangerousGetHandle(),
            out error);
        if (rawSnapshot == 0)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not snapshot the live stack.");
        }

        FreeUnexpectedError(error);
        return CreateOwnedSnapshot(rawSnapshot);
    }

    private static ImageStackExportSnapshot CreateExportSnapshot(
        SafeLiveStackerHandle handle)
    {
        nint stacker = handle.DangerousGetHandle();
        int acceptedFrames = checked((int)NativeMethods.GetLiveStackerAcceptedFrames(stacker));
        int rejectedFrames = checked((int)NativeMethods.GetLiveStackerRejectedFrames(stacker));
        nint error = 0;
        nint rawSnapshot = NativeMethods.ExportLiveStackerSnapshot(stacker, out error);
        if (rawSnapshot == 0)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza core could not create a lightweight stack export snapshot.");
        }

        FreeUnexpectedError(error);
        var exportHandle = new SafeStackExportSnapshotHandle(rawSnapshot);
        try
        {
            return new ImageStackExportSnapshot(
                exportHandle,
                acceptedFrames,
                rejectedFrames);
        }
        catch
        {
            exportHandle.Dispose();
            throw;
        }
    }

    private static ImageStackSnapshot CreateOwnedSnapshot(nint rawSnapshot)
    {
        var handle = new SafeStackSnapshotHandle(rawSnapshot);
        try
        {
            return new ImageStackSnapshot(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void FreeUnexpectedError(nint error)
    {
        if (error != 0)
        {
            _ = NativeString.TakeOwned(error, string.Empty);
        }
    }

    private readonly record struct NativeCalibration(
        string? BiasPath,
        string? DarkPath,
        string? FlatPath,
        double DarkExposureSeconds)
    {
        public static NativeCalibration From(ImageStackCalibration calibration)
        {
            if (calibration.OverridesDarkExposure && calibration.DarkPath is null)
            {
                throw new ArgumentException(
                    "Choose a master dark before overriding its exposure.",
                    nameof(calibration));
            }
            if (calibration.OverridesDarkExposure &&
                (!double.IsFinite(calibration.DarkExposureSeconds) ||
                 calibration.DarkExposureSeconds <= 0))
            {
                throw new ArgumentException(
                    "The master-dark exposure must be positive.",
                    nameof(calibration));
            }

            return new NativeCalibration(
                FullPathOrNull(calibration.BiasPath),
                FullPathOrNull(calibration.DarkPath),
                FullPathOrNull(calibration.FlatPath),
                calibration.OverridesDarkExposure
                    ? calibration.DarkExposureSeconds
                    : 0);
        }

        private static string? FullPathOrNull(string? path) =>
            string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }
}
