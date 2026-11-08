using System.Buffers.Binary;
using Seiza.App.Rendering;
using Xunit;

namespace Seiza.App.Tests;

public sealed class BgraBitmapStreamTests
{
    [Fact]
    public void RandomReadsCrossTheHeaderAndPixelBoundaryWithoutCopyingOrFlippingRows()
    {
        byte[] pixels = [11, 22, 33, 255, 44, 55, 66, 255];
        using var stream = new BgraBitmapStream(pixels, 1, 2);
        byte[] file = new byte[stream.Length];
        stream.ReadExactly(file);
        Assert.Equal("BM", System.Text.Encoding.ASCII.GetString(file, 0, 2));
        Assert.Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(22)));
        Assert.Equal(pixels, file.AsSpan(54).ToArray());
        stream.Seek(50, SeekOrigin.Begin);
        byte[] crossing = new byte[8]; stream.ReadExactly(crossing);
        Assert.Equal(file.AsSpan(50, 8).ToArray(), crossing);
        stream.Seek(-4, SeekOrigin.End);
        byte[] last = new byte[4]; stream.ReadExactly(last);
        Assert.Equal(pixels.AsSpan(4).ToArray(), last);
        Assert.Equal(0, stream.Read(last));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Read(last));
        Assert.Equal(new byte[] { 11, 22, 33, 255, 44, 55, 66, 255 }, pixels);
    }
}
