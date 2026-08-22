using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeStackExportSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeStackExportSnapshotHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeStackExportSnapshot(handle);
        return true;
    }
}
