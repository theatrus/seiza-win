namespace Seiza.App.Services;

internal static class Bgra8Compositor
{
    public static void CompositePremultiplied(Span<byte> destination, ReadOnlySpan<byte> overlay)
    {
        if (destination.Length != overlay.Length || destination.Length % 4 != 0)
            throw new ArgumentException("The image and overlay must contain matching complete BGRA pixels.");
        for (int index = 0; index < destination.Length; index += 4)
        {
            int inverse = 255 - overlay[index + 3];
            for (int channel = 0; channel < 3; channel++)
                destination[index + channel] = (byte)Math.Min(255,
                    overlay[index + channel] + (destination[index + channel] * inverse + 127) / 255);
            destination[index + 3] = 255;
        }
    }
}
