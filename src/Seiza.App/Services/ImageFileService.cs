using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App.Services;

internal static class ImageFileService
{
    private static readonly string[] SupportedExtensions =
    [
        ".fits",
        ".fit",
        ".fts",
        ".xisf",
        ".jpg",
        ".jpeg",
        ".jfif",
        ".png",
        ".tif",
        ".tiff",
    ];

    private static readonly HashSet<string> SupportedExtensionSet =
        new(SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> StackableExtensionSet = new(
        [".fits", ".fit", ".fts", ".xisf"],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<string>> PickImagesAsync(nint ownerWindow)
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };

        foreach (string extension in SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        return files.Select(file => file.Path).ToArray();
    }

    public static async Task<string?> PickFolderAsync(nint ownerWindow)
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static Task<IReadOnlyList<string>> GetImagesAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<string> paths = Directory
                .EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedImage)
                .ToList();

            paths.Sort((left, right) => NaturalStringComparer.Instance.Compare(
                Path.GetFileName(left),
                Path.GetFileName(right)));

            cancellationToken.ThrowIfCancellationRequested();
            return paths;
        }, cancellationToken);
    }

    public static bool IsSupportedImage(string path) =>
        SupportedExtensionSet.Contains(Path.GetExtension(path));

    public static bool IsAstronomyImage(string path) =>
        StackableExtensionSet.Contains(Path.GetExtension(path));

    public static bool IsStackableImage(string path) => IsAstronomyImage(path);

    public static bool IsSupportedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && SupportedExtensionSet.Contains(extension);
}
