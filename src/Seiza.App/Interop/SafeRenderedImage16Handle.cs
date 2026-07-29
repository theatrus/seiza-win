using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeRenderedImage16Handle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeRenderedImage16Handle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeRenderedImage16(handle);
        return true;
    }
}
