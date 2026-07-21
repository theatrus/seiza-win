using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;

namespace Seiza.App.Services;

internal static class CatalogDirectoryPicker
{
    private const string FutureAccessToken = "SeizaCatalogDirectory";

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
        if (folder is not null)
        {
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(FutureAccessToken, folder);
        }

        return folder?.Path;
    }

    public static void ClearAccess()
    {
        if (StorageApplicationPermissions.FutureAccessList.ContainsItem(FutureAccessToken))
        {
            StorageApplicationPermissions.FutureAccessList.Remove(FutureAccessToken);
        }
    }
}
