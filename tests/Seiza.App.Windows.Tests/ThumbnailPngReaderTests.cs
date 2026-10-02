using Seiza.App.Services;
using Windows.Storage.Streams;
using Xunit;

namespace Seiza.App.Windows.Tests;

public sealed class ThumbnailPngReaderTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j3ioAAAAASUVORK5CYII=");

    [Fact]
    public async Task KeepsStreamOpenAndRewoundUntilAsyncReaderCompletes()
    {
        IRandomAccessStream? captured = null;
        SynchronizationContext? callerContext = SynchronizationContext.Current;

        await ThumbnailPngReader.WithStreamAsync(Png, async stream =>
        {
            captured = stream;
            Assert.Same(callerContext, SynchronizationContext.Current);
            Assert.Equal(0UL, stream.Position);
            Assert.Equal((ulong)Png.Length, stream.Size);

            await Task.Yield();
            Assert.Equal(0UL, stream.Position);
            using var reader = new DataReader(stream);
            try
            {
                Assert.Equal((uint)Png.Length, await reader.LoadAsync((uint)Png.Length));
                var actual = new byte[Png.Length];
                reader.ReadBytes(actual);
                Assert.Equal(Png, actual);
            }
            finally
            {
                reader.DetachStream();
            }

            stream.Seek(0);
            Assert.True(stream.CanRead);
        });

        AssertClosed(captured);
    }

    [Fact]
    public async Task DisposesStreamWhenAsyncReaderThrows()
    {
        IRandomAccessStream? captured = null;
        var expected = new InvalidOperationException("Reader failed.");

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThumbnailPngReader.WithStreamAsync(Png, async stream =>
            {
                captured = stream;
                await Task.Yield();
                throw expected;
            }));

        Assert.Same(expected, actual);
        AssertClosed(captured);
    }

    [Fact]
    public async Task DisposesStreamWhenAsyncReaderIsCanceled()
    {
        IRandomAccessStream? captured = null;
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ThumbnailPngReader.WithStreamAsync(Png, async stream =>
            {
                captured = stream;
                await Task.Yield();
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }, cancellation.Token));

        AssertClosed(captured);
    }

    [Fact]
    public async Task DoesNotCallReaderWhenAlreadyCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool called = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ThumbnailPngReader.WithStreamAsync(Png, _ =>
            {
                called = true;
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.False(called);
    }

    [Fact]
    public async Task DisposingManagedWriterBeforeReadingClosesWindowsStream()
    {
        using var stream = new InMemoryRandomAccessStream();
        using (Stream destination = stream.AsStreamForWrite())
        {
            await destination.WriteAsync(Png);
            await destination.FlushAsync();
        }

        AssertClosed(stream);
    }

    private static void AssertClosed(IRandomAccessStream? stream)
    {
        Assert.NotNull(stream);
        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(() => stream.Seek(0));
        Assert.Equal(unchecked((int)0x80000013), exception.HResult);
    }
}
