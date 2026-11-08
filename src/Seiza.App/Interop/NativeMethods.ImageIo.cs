using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "seiza_read_image_header_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ReadImageHeaderJson(string path, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_read_image_window", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool ReadImageWindow(string path, nuint x, nuint y, nuint width, nuint height,
        float normalizedFullScale, float* values, nuint valuesLength, out NativeImageWindow window, out nint error);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImageWindow
{
    public nuint ImageWidth, ImageHeight, Planes, X, Y, Width, Height;
}
