using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_stars_detect_path_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint DetectStarsPathJson(
        string path,
        string optionsJson,
        out nint error);
}
