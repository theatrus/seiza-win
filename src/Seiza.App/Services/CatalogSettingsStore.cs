using System.Text.Json;

namespace Seiza.App.Services;

internal static class CatalogSettingsStore
{
    private static readonly object SyncRoot = new();
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Seiza");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string? LoadCatalogDirectory()
    {
        lock (SyncRoot)
        {
            return LoadSettings().CatalogDirectory;
        }
    }

    public static void SaveCatalogDirectory(string? path)
    {
        lock (SyncRoot)
        {
            Settings settings = LoadSettings() with
            {
                CatalogDirectory = string.IsNullOrWhiteSpace(path) ? null : path,
            };
            SaveSettings(settings);
        }
    }

    public static bool LoadAutomaticallyCheckForUpdates()
    {
        lock (SyncRoot)
        {
            return LoadSettings().AutomaticallyCheckForUpdates;
        }
    }

    public static void SaveAutomaticallyCheckForUpdates(bool value)
    {
        lock (SyncRoot)
        {
            SaveSettings(LoadSettings() with { AutomaticallyCheckForUpdates = value });
        }
    }

    public static string? LoadSkippedUpdateVersion()
    {
        lock (SyncRoot)
        {
            return LoadSettings().SkippedUpdateVersion;
        }
    }

    public static void SaveSkippedUpdateVersion(string? version)
    {
        lock (SyncRoot)
        {
            SaveSettings(LoadSettings() with
            {
                SkippedUpdateVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            });
        }
    }

    private static Settings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new Settings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    private static void SaveSettings(Settings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);

        string temporaryPath = SettingsPath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record Settings
    {
        public string? CatalogDirectory { get; init; }

        public bool AutomaticallyCheckForUpdates { get; init; } = true;

        public string? SkippedUpdateVersion { get; init; }
    }
}
