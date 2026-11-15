using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "seiza_model_list_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ListModels(string store, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_catalog_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetModelCatalog(string? options, out nint error);

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

    [LibraryImport(LibraryName, EntryPoint = "seiza_model_backends_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetRestorationBackends(string options, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_noise_model_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenNoiseModel(string model, string options, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_noise_model_free")]
    internal static partial void FreeNoiseModel(nint model);

    [LibraryImport(LibraryName, EntryPoint = "seiza_noise_model_denoise_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint RunRestoration(SafeNoiseModelHandle model, string input,
        [MarshalAs(UnmanagedType.U1)] bool center, delegate* unmanaged[Cdecl]<nuint, nuint, nuint, nuint, nint, void> progress,
        SafeCancelSignalHandle cancel, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_file_restoration_write", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint BlendRestoration(SafeRestorationHandle result, string output, string? original,
        float high, float low, float scalePx, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_file_restoration_free")]
    internal static partial void FreeRestoration(nint result);
}

internal sealed class SafeNoiseModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeNoiseModelHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeNoiseModel(handle);
        return true;
    }
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
