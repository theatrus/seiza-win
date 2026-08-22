using System.Security.Cryptography;
using System.Text;

namespace Seiza.App.Services;

internal static class CalibrationCachePaths
{
    public static string ForLibrary(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        string normalized = Path.GetFullPath(libraryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        string id = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).AsSpan(0, 6))
            .ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Seiza",
            "CalibrationMasters",
            id);
    }
}
