using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    private const string LibraryName = "seiza_cabi";

    [LibraryImport(LibraryName, EntryPoint = "seiza_core_version")]
    internal static partial nint GetCoreVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_catalog_status_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetCatalogStatusJson(string? catalogDirectory, out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_solve_image_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SolveImageJson(
        string path,
        string? catalogDirectory,
        double minimumScaleArcsecPerPixel,
        double maximumScaleArcsecPerPixel,
        byte sipOrder,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_catalog_setup",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool SetupCatalog(
        string? catalogDirectory,
        uint preset,
        delegate* unmanaged[Cdecl]<nint, nint, void> progress,
        nint context,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_rendered_image_open_with_rgb_stretch",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenRenderedImage(
        string path,
        double targetMedian,
        double shadowsClip,
        uint maxDimension,
        uint rgbStretchMode,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_rendered_image_open_with_stretch_config",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenRenderedImageWithStretchConfiguration(
        string path,
        string configurationJson,
        uint maxDimension,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_width")]
    internal static partial uint GetRenderedImageWidth(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_height")]
    internal static partial uint GetRenderedImageHeight(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_bgra")]
    internal static partial nint GetRenderedImageBgra(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_bgra_length")]
    internal static partial nuint GetRenderedImageBgraLength(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_metadata_json")]
    internal static partial nint GetRenderedImageMetadataJson(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_free")]
    internal static partial void FreeRenderedImage(nint image);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_rendered_image16_open_with_stretch_config",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenRenderedImage16WithStretchConfiguration(
        string path,
        string configurationJson,
        uint maxDimension,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_width")]
    internal static partial uint GetRenderedImage16Width(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_height")]
    internal static partial uint GetRenderedImage16Height(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_rgba")]
    internal static partial nint GetRenderedImage16Rgba(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_rgba_length")]
    internal static partial nuint GetRenderedImage16RgbaLength(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_metadata_json")]
    internal static partial nint GetRenderedImage16MetadataJson(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_free")]
    internal static partial void FreeRenderedImage16(nint image);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_open_fits",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenLiveStacker(
        string referencePath,
        string? biasPath,
        string? darkPath,
        string? flatPath,
        double darkExposureSeconds,
        string optionsJson,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_push_fits_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint PushLiveStackerFrameJson(
        nint stacker,
        string path,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_accepted_frames")]
    internal static partial uint GetLiveStackerAcceptedFrames(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_rejected_frames")]
    internal static partial uint GetLiveStackerRejectedFrames(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_finish")]
    internal static partial nint FinishLiveStacker(ref nint stacker, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_free")]
    internal static partial void FreeLiveStacker(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_accepted_frames")]
    internal static partial uint GetStackSnapshotAcceptedFrames(nint snapshot);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_rejected_frames")]
    internal static partial uint GetStackSnapshotRejectedFrames(nint snapshot);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_stack_snapshot_write_fits",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool WriteStackSnapshotFits(
        nint snapshot,
        string path,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_free")]
    internal static partial void FreeStackSnapshot(nint snapshot);

    [LibraryImport(LibraryName, EntryPoint = "seiza_string_free")]
    internal static partial void FreeString(nint value);
}
