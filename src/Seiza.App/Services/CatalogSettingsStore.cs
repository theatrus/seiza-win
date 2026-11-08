using System.Text.Json;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class CatalogSettingsStore
{
    public static event EventHandler? CatalogDirectoryChanged;
    public static event EventHandler? RcAstroExecutableChanged;
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

    public static ImageBrowserGrouping LoadBrowserGrouping(string? path = null)
    {
        lock (SyncRoot)
        {
            return Enum.TryParse(LoadSettings(path).BrowserGrouping, out ImageBrowserGrouping value) && Enum.IsDefined(value)
                ? value : ImageBrowserGrouping.Folder;
        }
    }

    public static void SaveBrowserGrouping(ImageBrowserGrouping grouping, string? path = null)
    {
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        lock (SyncRoot)
            SaveSettings(LoadSettings(path) with { BrowserGrouping = grouping.ToString() }, path);
    }

    public static string? LoadRcAstroExecutable()
    {
        lock (SyncRoot)
        {
            return LoadSettings().RcAstroExecutable;
        }
    }

    public static void SaveRcAstroExecutable(string? path)
    {
        string? fullPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (fullPath is not null && !File.Exists(fullPath))
        {
            throw new FileNotFoundException("The RC-Astro executable was not found.", fullPath);
        }
        lock (SyncRoot)
        {
            SaveSettings(LoadSettings() with { RcAstroExecutable = fullPath });
        }
        RcAstroExecutableChanged?.Invoke(null, EventArgs.Empty);
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
        CatalogDirectoryChanged?.Invoke(null, EventArgs.Empty);
    }

    public static bool LoadAutomaticallyCheckForUpdates()
    {
        lock (SyncRoot)
        {
            return LoadSettings().AutomaticallyCheckForUpdates;
        }
    }

    public static bool LoadUseGpuForStacking(string? path = null)
    {
        lock (SyncRoot) return LoadSettings(path).UseGpuForStacking;
    }

    public static void SaveUseGpuForStacking(bool value, string? path = null)
    {
        lock (SyncRoot)
            SaveSettings(LoadSettings(path) with { UseGpuForStacking = value }, path);
    }

    public static int? LoadStackingGpuMemoryMib(string? path = null)
    {
        lock (SyncRoot)
        {
            int? value = LoadSettings(path).StackingGpuMemoryMib;
            return value is >= 1 and <= ImageStackOptions.MaximumGpuMemoryMib ? value : null;
        }
    }

    public static void SaveStackingGpuMemoryMib(int? value, string? path = null)
    {
        if (value is < 1 or > ImageStackOptions.MaximumGpuMemoryMib)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (SyncRoot)
            SaveSettings(LoadSettings(path) with { StackingGpuMemoryMib = value }, path);
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

    internal static Settings LoadSettings(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                return new Settings();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    internal static void SaveSettings(Settings settings, string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temporaryPath = path + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal sealed record Settings
    {
        public string? CatalogDirectory { get; init; }
        public string? RcAstroExecutable { get; init; }
        public string BrowserGrouping { get; init; } = "Folder";
        public bool UseGpuForStacking { get; init; } = true;
        public int? StackingGpuMemoryMib { get; init; }

        public bool AutomaticallyCheckForUpdates { get; init; } = true;

        public string? SkippedUpdateVersion { get; init; }
    }
}
