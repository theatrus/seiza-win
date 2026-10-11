using Microsoft.Win32.SafeHandles;

namespace Seiza.App.Interop;

internal sealed class SafeParallaxVideoHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeParallaxVideoHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        NativeMethods.FreeParallax(handle);
        return true;
    }
}
