using System.Text.Json;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class CalibrationLibraryPreferencesStore
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Seiza", "graded-calibration-v1.json");
    public static CalibrationLibraryPreferences Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path ?? DefaultPath), LibraryJsonContext.Default.CalibrationLibraryPreferences) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(CalibrationLibraryPreferences preferences, string? path = null)
    {
        string target = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, LibraryJsonContext.Default.CalibrationLibraryPreferences));
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
