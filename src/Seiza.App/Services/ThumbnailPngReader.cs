using Windows.Storage.Streams;

namespace Seiza.App.Services;

internal static class ThumbnailPngReader
{
    public static async Task WithStreamAsync(
        byte[] png,
        Func<IRandomAccessStream, Task> readAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(readAsync);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        // The managed adapter owns the WinRT stream, so it must outlive the reader.
        using Stream destination = stream.AsStreamForWrite();
        await destination.WriteAsync(png, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);

        // Preserve the caller's context: BitmapImage readers run on the UI thread.
        await readAsync(stream);
    }
}
