using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Seiza.App.Services;

internal static class WindowsShortcutTarget
{
    public static string? TryGetImagePath(string path)
    {
        if (!OperatingSystem.IsWindows() ||
            !Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        object? shortcut = null;
        try
        {
            shortcut = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!);
            ((IPersistFile)shortcut!).Load(path, 0);
            StringBuilder target = new(32768);
            // Read the saved target without Resolve: no search, UI, or link updates.
            ((IShellLinkW)shortcut).GetPath(target, target.Capacity, IntPtr.Zero, 0);
            string targetPath = target.ToString();
            return Path.IsPathFullyQualified(targetPath) &&
                ImageFileTypes.IsSupportedImage(targetPath) && File.Exists(targetPath)
                ? Path.GetFullPath(targetPath)
                : null;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (shortcut is not null)
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
    }

    // Only the first IShellLinkW method is needed; IPersistFile loads the link.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
            int capacity, IntPtr findData, uint flags);
    }
}
