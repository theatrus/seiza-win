using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeRenderedImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeRenderedImageHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeRenderedImage(handle);
        return true;
    }
}
