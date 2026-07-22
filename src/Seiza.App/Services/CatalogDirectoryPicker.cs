using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App.Services;

internal static class CatalogDirectoryPicker
{
    public static async Task<string?> PickAsync(nint ownerWindow)
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
