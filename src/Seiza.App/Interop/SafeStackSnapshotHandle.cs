using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeStackSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeStackSnapshotHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeStackSnapshot(handle);
        return true;
    }
}
