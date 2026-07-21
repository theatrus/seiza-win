using Windows.Storage;

namespace Seiza.App.Services;

internal static class CatalogSettingsStore
{
    private const string CatalogDirectoryKey = "CatalogDirectory";

    public static string? LoadCatalogDirectory()
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[CatalogDirectoryKey] as string;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCatalogDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ApplicationData.Current.LocalSettings.Values.Remove(CatalogDirectoryKey);
        }
        else
        {
            ApplicationData.Current.LocalSettings.Values[CatalogDirectoryKey] = path;
        }
    }
}
