using Seiza.App.Interop;

namespace Seiza.App.Services;

/// <summary>
/// Owns only the copied live mean and output metadata needed for a
/// non-destructive FITS export. It deliberately omits variance and sample maps
/// so a large live stack can keep ingesting without a multi-gigabyte clone.
/// </summary>
internal sealed class ImageStackExportSnapshot : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeStackExportSnapshotHandle? _handle;
    private bool _disposed;

    internal ImageStackExportSnapshot(
        SafeStackExportSnapshotHandle handle,
        int acceptedFrames,
        int rejectedFrames)
    {
        _handle = handle;
        AcceptedFrames = acceptedFrames;
        RejectedFrames = rejectedFrames;
    }

    public int AcceptedFrames { get; }

    public int RejectedFrames { get; }

    public async Task WriteFitsAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeStackExportSnapshotHandle handle = RequireHandle();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                AtomicOutputFile.Write(
                    outputPath,
                    stagingPath =>
                    {
                        nint error = 0;
                        if (!NativeMethods.WriteStackExportSnapshotFits(
                                handle.DangerousGetHandle(),
                                stagingPath,
                                out error))
                        {
                            throw NativeString.TakeError(
                                error,
                                "The Seiza core could not write the live-stack snapshot.");
                        }
                    },
                    cancellationToken);
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

    private SafeStackExportSnapshotHandle RequireHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _handle
            ?? throw new ObjectDisposedException(nameof(ImageStackExportSnapshot));
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
}
