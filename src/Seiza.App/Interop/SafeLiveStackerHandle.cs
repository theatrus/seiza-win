using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeLiveStackerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeLiveStackerHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    internal nint Finish(out nint error)
    {
        nint liveHandle = DangerousGetHandle();
        nint snapshot = NativeMethods.FinishLiveStacker(ref liveHandle, out error);

        // Native finalization consumes every non-null handle it accepts, even
        // when producing the snapshot fails. Honor the pointer-to-handle
        // contract so SafeHandle never releases consumed Rust state twice.
        if (liveHandle == 0)
        {
            SetHandleAsInvalid();
        }
        else
        {
            SetHandle(liveHandle);
        }

        return snapshot;
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeLiveStacker(handle);
        return true;
    }
}
