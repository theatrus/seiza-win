using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App.Services;

internal static class ImageFileService
{
    public static async Task<string?> PickImageListAsync(nint ownerWindow)
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<IReadOnlyList<string>> PickImagesAsync(nint ownerWindow)
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };

        foreach (string extension in ImageFileTypes.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".csv");

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
        return Task.Run(() => ImageDirectoryScanner.Scan(
            folderPath, includeSubfolders: false, cancellationToken).Paths, cancellationToken);
    }

    public static Task<ImageDirectoryScanResult> BrowseImagesAsync(
        string folderPath,
        bool includeSubfolders,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImageDirectoryScanner.Scan(
            folderPath, includeSubfolders, cancellationToken), cancellationToken);

    public static bool IsSupportedImage(string path) =>
        ImageFileTypes.IsSupportedImage(path);

    public static bool IsAstronomyImage(string path) =>
        ImageFileTypes.IsAstronomyImage(path);

    public static bool IsStackableImage(string path) => IsAstronomyImage(path);

    public static bool IsSupportedExtension(string? extension) =>
        ImageFileTypes.IsSupportedExtension(extension);
}
