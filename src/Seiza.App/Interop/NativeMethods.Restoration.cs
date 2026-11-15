using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    private const string RestorationLibrary = "seiza_platform";

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_list_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ListModels(string store, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_download_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint DownloadModel(string? name, string store, string? options,
        delegate* unmanaged[Cdecl]<nint, nint, void> progress, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_install_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint InstallModel(string bundle, string store, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_verify", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool VerifyModel(string bundle, string store, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_remove", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool RemoveModel(string name, string store, out nint error);

    [LibraryImport(RestorationLibrary, EntryPoint = "seiza_win_restoration_backends", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetRestorationBackends(string library, out nint error);

    [LibraryImport(RestorationLibrary, EntryPoint = "seiza_win_restoration_run", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint RunRestoration(string request,
        delegate* unmanaged[Cdecl]<nint, nint, void> progress, delegate* unmanaged[Cdecl]<nint, byte> cancel,
        nint context, out nint error);

    [LibraryImport(RestorationLibrary, EntryPoint = "seiza_win_restoration_blend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint BlendRestoration(SafeRestorationHandle result, string request, out nint error);

    [LibraryImport(RestorationLibrary, EntryPoint = "seiza_win_restoration_free")]
    internal static partial void FreeRestoration(nint result);

    [LibraryImport(RestorationLibrary, EntryPoint = "seiza_win_string_free")]
    internal static partial void FreeRestorationString(nint value);
}

internal sealed class SafeRestorationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeRestorationHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeRestoration(handle);
        return true;
    }
}
