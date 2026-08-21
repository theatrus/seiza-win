using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeCancelSignalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeCancelSignalHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    internal void Cancel() => NativeMethods.CancelSignal(DangerousGetHandle());

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeCancelSignal(handle);
        return true;
    }
}
