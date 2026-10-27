using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_set_match_tolerances")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool SetLiveStackerMatchTolerances(nint stacker, NativeMatchTolerances* tolerances, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_set_light_rotation")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SetLiveStackerLightRotation(nint stacker, uint basis, double degrees, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_open_prepared_fits", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenPreparedLiveStacker(string path, string? optionsJson, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_width")]
    internal static partial nuint GetStackSnapshotWidth(nint snapshot);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_data_length")]
    internal static partial nuint GetStackSnapshotDataLength(nint snapshot);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_image")]
    internal static partial nint GetStackSnapshotImage(nint snapshot);
}
