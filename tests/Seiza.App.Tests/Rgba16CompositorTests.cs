using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class Rgba16CompositorTests
{
    [Fact]
    public void TransparentOverlayPreservesSubEightBitSamples()
    {
        ushort[] pixels = [32_768, 32_769, 32_774, ushort.MaxValue];
        byte[] overlay = [0, 0, 0, 0];

        Rgba16Compositor.CompositePremultipliedBgra8(pixels, overlay);

        Assert.Equal([32_768, 32_769, 32_774, ushort.MaxValue], pixels);
    }

    [Fact]
    public void PremultipliedOverlayBlendsBgraIntoRgba16()
    {
        ushort[] pixels = [10_000, 20_000, 30_000, ushort.MaxValue];
        byte[] overlay = [25, 50, 100, 128];

        Rgba16Compositor.CompositePremultipliedBgra8(pixels, overlay);

        Assert.Equal((ushort)(100 * 257 + (10_000 * 127 + 127) / 255), pixels[0]);
        Assert.Equal((ushort)(50 * 257 + (20_000 * 127 + 127) / 255), pixels[1]);
        Assert.Equal((ushort)(25 * 257 + (30_000 * 127 + 127) / 255), pixels[2]);
        Assert.Equal(ushort.MaxValue, pixels[3]);
    }

    [Fact]
    public void RejectsMismatchedBuffers()
    {
        Assert.Throws<ArgumentException>(() =>
            Rgba16Compositor.CompositePremultipliedBgra8(
                new ushort[4],
                new byte[8]));
    }
}
