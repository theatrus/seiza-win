using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static unsafe class SeizaCore
{
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
        uint maxDimension = 0,
        FitsImageProcessingConfiguration? processing = null)
    {
        nint error = 0;
        nint rawHandle = IsFits(path)
            ? NativeMethods.OpenRenderedImageWithStretchConfiguration(
                path,
                (processing ?? FitsImageProcessingConfiguration.Default).ToJson(),
                maxDimension,
                out error)
            : NativeMethods.OpenRenderedImage(
                path,
                0.2,
                -2.8,
                maxDimension,
                0,
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
        ImageMetadata metadata = JsonSerializer.Deserialize(
            metadataJson,
            SeizaJsonSerializerContext.Default.ImageMetadata)
            ?? throw new SeizaCoreException("The Seiza native core returned invalid metadata.");

        return new RenderedImageData(bgra, width, height, metadata);
    }

    private static bool IsFits(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".fits" or ".fit" or ".fts";

    public static CatalogStatus GetCatalogStatus(string? catalogDirectory)
    {
        nint error = 0;
        nint json = NativeMethods.GetCatalogStatusJson(catalogDirectory, out error);
        if (json == 0)
        {
            throw ReadError(error, "The Seiza native core could not inspect the catalog.");
        }

        try
        {
            string value = Marshal.PtrToStringUTF8(json)
                ?? throw new SeizaCoreException("The Seiza native core returned invalid catalog status.");
            return JsonSerializer.Deserialize(
                value,
                SeizaJsonSerializerContext.Default.CatalogStatus)
                ?? throw new SeizaCoreException("The Seiza native core returned invalid catalog status.");
        }
        finally
        {
            NativeMethods.FreeString(json);
        }
    }

    public static SolveResult Solve(
        string path,
        string? catalogDirectory,
        double minimumScaleArcsecPerPixel = 0.1,
        double maximumScaleArcsecPerPixel = 20.0,
        byte sipOrder = 0)
    {
        nint error = 0;
        nint json = NativeMethods.SolveImageJson(
            path,
            catalogDirectory,
            minimumScaleArcsecPerPixel,
            maximumScaleArcsecPerPixel,
            sipOrder,
            out error);
        if (json == 0)
        {
            throw ReadError(error, "The Seiza native core could not solve the image.");
        }

        try
        {
            string value = Marshal.PtrToStringUTF8(json)
                ?? throw new SeizaCoreException("The Seiza native core returned an invalid solve result.");
            return JsonSerializer.Deserialize(
                value,
                SeizaJsonSerializerContext.Default.SolveResult)
                ?? throw new SeizaCoreException("The Seiza native core returned an invalid solve result.");
        }
        finally
        {
            NativeMethods.FreeString(json);
        }
    }

    public static void SetupCatalog(
        string? catalogDirectory,
        CatalogSetupPreset preset,
        Action<CatalogSetupProgress> progress)
    {
        GCHandle callbackState = GCHandle.Alloc(progress);
        nint error = 0;
        try
        {
            bool succeeded = NativeMethods.SetupCatalog(
                catalogDirectory,
                (uint)preset,
                &CatalogProgressCallback,
                GCHandle.ToIntPtr(callbackState),
                out error);
            if (!succeeded)
            {
                throw ReadError(error, "The Seiza native core could not set up the catalog.");
            }
        }
        finally
        {
            callbackState.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CatalogProgressCallback(nint json, nint context)
    {
        try
        {
            if (json == 0 || context == 0)
            {
                return;
            }

            string? value = Marshal.PtrToStringUTF8(json);
            CatalogSetupProgress? update = value is null
                ? null
                : JsonSerializer.Deserialize(
                    value,
                    SeizaJsonSerializerContext.Default.CatalogSetupProgress);
            if (update is null)
            {
                return;
            }

            GCHandle callbackState = GCHandle.FromIntPtr(context);
            if (callbackState.Target is Action<CatalogSetupProgress> progress)
            {
                progress(update);
            }
        }
        catch
        {
            // Managed exceptions must never cross the native callback boundary.
        }
    }

    private static SeizaCoreException ReadError(nint error, string fallbackMessage = "The Seiza native core could not render the image.")
    {
        if (error == 0)
        {
            return new SeizaCoreException(fallbackMessage);
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
