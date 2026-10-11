using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_prepare_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint PrepareParallaxJson(string request, SafeCancelSignalHandle cancel,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_plan_tour_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint PlanParallaxTourJson(string request,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_reconfigure_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint ReconfigureParallaxJson(SafeParallaxVideoHandle video, string settings, SafeCancelSignalHandle cancel,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_summary_json")]
    internal static partial nint GetParallaxSummaryJson(SafeParallaxVideoHandle video, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_render_frame")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool RenderParallaxFrame(SafeParallaxVideoHandle video, uint index, uint format,
        byte* buffer, nuint bufferLength, nuint stride, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_free")]
    internal static partial void FreeParallax(nint video);
}
