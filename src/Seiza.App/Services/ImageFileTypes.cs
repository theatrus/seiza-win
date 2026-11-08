namespace Seiza.App.Services;

internal static class ImageFileTypes
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        Array.AsReadOnly(new[]
        {
            ".fits", ".fit", ".fts", ".xisf", ".jpg", ".jpeg", ".jfif", ".png", ".tif", ".tiff",
        });

    private static readonly HashSet<string> SupportedExtensionSet =
        new(SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AstronomyExtensions { get; } = Array.AsReadOnly(new[] { ".fits", ".fit", ".fts", ".xisf" });

    private static readonly HashSet<string> AstronomyExtensionSet = new(
        AstronomyExtensions,
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportedImage(string path) =>
        IsSupportedExtension(Path.GetExtension(path));

    public static bool IsOpenableFile(string path) =>
        IsSupportedImage(path) || IsSupportedImageList(path);

    public static bool IsSupportedImageList(string path) =>
        Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public static bool IsAstronomyImage(string path) =>
        AstronomyExtensionSet.Contains(Path.GetExtension(path));

    public static bool IsSupportedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && SupportedExtensionSet.Contains(extension);
}
