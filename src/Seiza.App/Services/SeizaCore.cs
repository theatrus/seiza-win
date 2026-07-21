using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class SeizaCore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Version
    {
        get
        {
            nint value = NativeMethods.GetCoreVersion();
            return Marshal.PtrToStringUTF8(value) ?? "unknown";
        }
    }

    public static RenderedImageData Render(
        string path,
        double targetMedian = 0.2,
        double shadowsClip = -2.8,
        uint maxDimension = 0,
        uint rgbStretchMode = 0)
    {
        nint error = 0;
        nint rawHandle = NativeMethods.OpenRenderedImage(
            path,
            targetMedian,
            shadowsClip,
            maxDimension,
            rgbStretchMode,
            out error);

        if (rawHandle == 0)
        {
            throw ReadError(error);
        }

        using SafeRenderedImageHandle image = new(rawHandle);
        nint handle = image.DangerousGetHandle();
        int width = checked((int)NativeMethods.GetRenderedImageWidth(handle));
        int height = checked((int)NativeMethods.GetRenderedImageHeight(handle));
        int expectedLength = checked(width * height * 4);
        int byteLength = checked((int)NativeMethods.GetRenderedImageBgraLength(handle));
        nint pixelPointer = NativeMethods.GetRenderedImageBgra(handle);
        nint metadataPointer = NativeMethods.GetRenderedImageMetadataJson(handle);

        if (width <= 0 || height <= 0 || byteLength != expectedLength ||
            pixelPointer == 0 || metadataPointer == 0)
        {
            throw new SeizaCoreException("The Seiza native core returned an invalid image.");
        }

        byte[] bgra = GC.AllocateUninitializedArray<byte>(byteLength);
        Marshal.Copy(pixelPointer, bgra, 0, byteLength);

        string metadataJson = Marshal.PtrToStringUTF8(metadataPointer)
            ?? throw new SeizaCoreException("The Seiza native core returned invalid metadata.");
        ImageMetadata metadata = JsonSerializer.Deserialize<ImageMetadata>(metadataJson, JsonOptions)
            ?? throw new SeizaCoreException("The Seiza native core returned invalid metadata.");

        return new RenderedImageData(bgra, width, height, metadata);
    }

    private static SeizaCoreException ReadError(nint error)
    {
        if (error == 0)
        {
            return new SeizaCoreException("The Seiza native core could not render the image.");
        }

        try
        {
            string message = Marshal.PtrToStringUTF8(error)
                ?? "The Seiza native core returned an unknown error.";
            return new SeizaCoreException(message);
        }
        finally
        {
            NativeMethods.FreeString(error);
        }
    }
}
