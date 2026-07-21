using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    private const string LibraryName = "seiza_cabi";

    [LibraryImport(LibraryName, EntryPoint = "seiza_core_version")]
    internal static partial nint GetCoreVersion();

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

    [LibraryImport(LibraryName, EntryPoint = "seiza_string_free")]
    internal static partial void FreeString(nint value);
}
