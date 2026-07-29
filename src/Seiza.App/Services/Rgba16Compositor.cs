namespace Seiza.App.Services;

internal static class Rgba16Compositor
{
    public static void CompositePremultipliedBgra8(
        Span<ushort> destinationRgba,
        ReadOnlySpan<byte> overlayBgra)
    {
        if (destinationRgba.Length != overlayBgra.Length)
        {
            throw new ArgumentException("The base image and overlay dimensions do not match.");
        }
        if (destinationRgba.Length % 4 != 0)
        {
            throw new ArgumentException("RGBA data must contain complete pixels.");
        }

        for (int index = 0; index < destinationRgba.Length; index += 4)
        {
            uint alpha = overlayBgra[index + 3];
            if (alpha == 0)
            {
                continue;
            }

            uint inverseAlpha = 255 - alpha;
            destinationRgba[index] = Blend(
                destinationRgba[index],
                overlayBgra[index + 2],
                inverseAlpha);
            destinationRgba[index + 1] = Blend(
                destinationRgba[index + 1],
                overlayBgra[index + 1],
                inverseAlpha);
            destinationRgba[index + 2] = Blend(
                destinationRgba[index + 2],
                overlayBgra[index],
                inverseAlpha);
            destinationRgba[index + 3] = ushort.MaxValue;
        }
    }

    private static ushort Blend(ushort background, byte premultipliedOverlay, uint inverseAlpha)
    {
        uint value = (uint)premultipliedOverlay * 257 +
            ((uint)background * inverseAlpha + 127) / 255;
        return (ushort)Math.Min(value, ushort.MaxValue);
    }
}
