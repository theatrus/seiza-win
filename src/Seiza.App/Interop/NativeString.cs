using System.Runtime.InteropServices;
using Seiza.App.Services;

namespace Seiza.App.Interop;

internal static class NativeString
{
    internal static string TakeOwned(nint value, string fallback)
    {
        if (value == 0)
        {
            return fallback;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value) ?? fallback;
        }
        finally
        {
            NativeMethods.FreeString(value);
        }
    }

    internal static SeizaCoreException TakeError(nint error, string fallback) =>
        new(TakeOwned(error, fallback));
}
