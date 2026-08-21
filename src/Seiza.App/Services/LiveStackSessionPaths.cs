using System.Security.Cryptography;
using System.Text;

namespace Seiza.App.Services;

internal static class LiveStackSessionPaths
{
    public static string ForWatchFolder(string watchFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFolder);
        string normalized = Path.GetFullPath(watchFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string readable = string.Concat(Path.GetFileName(normalized).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')).Trim('-');
        if (string.IsNullOrWhiteSpace(readable))
        {
            readable = "capture";
        }
        string id = Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Seiza",
            "LiveStacks",
            $"{readable}-{id}");
    }
}
