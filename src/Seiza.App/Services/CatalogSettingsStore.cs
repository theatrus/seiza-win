using System.Text.Json;

namespace Seiza.App.Services;

internal static class CatalogSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Seiza");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string? LoadCatalogDirectory()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<Settings>(json)?.CatalogDirectory;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCatalogDirectory(string? path)
    {
        Directory.CreateDirectory(SettingsDirectory);

        string temporaryPath = SettingsPath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(
                new Settings(string.IsNullOrWhiteSpace(path) ? null : path),
                SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record Settings(string? CatalogDirectory);
}
