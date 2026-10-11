using System.Text.Json;

namespace Seiza.App.Services;

internal static class ParallaxCatalogStatusPresentation
{
    public static (string Directory, string Description) Format(JsonElement status, double magnitude)
    {
        JsonElement stars = status.GetProperty("stars"), objects = status.GetProperty("objects");
        string starText = stars.GetProperty("available").GetBoolean()
            ? $"{stars.GetProperty("starCount").GetUInt64():N0} stellar distances, G≤{stars.GetProperty("maxMagnitude").GetDouble():0.#}"
            : $"stellar distances {DescribeUnavailable(stars)}";
        string objectText = objects.GetProperty("available").GetBoolean()
            ? "object distances ready"
            : $"object distances {DescribeUnavailable(objects)}";
        string warning = stars.TryGetProperty("maxMagnitude", out var depth) && magnitude > depth.GetDouble()
            ? " Selected Gaia depth exceeds offline coverage."
            : "";
        return (status.GetProperty("directory").GetString()!, $"{starText}; {objectText}.{warning}");
    }

    private static string DescribeUnavailable(JsonElement component)
    {
        string? resolutionError = ReadString(component, "resolutionError");
        string? error = ReadString(component, "error");
        string? detail = resolutionError ?? error;
        string? path = ReadString(component, "path");
        string location = string.IsNullOrWhiteSpace(path) ? "" : $" ({path})";
        string description = detail is null ? $"not installed{location}" : $"unavailable{location} — {detail}";
        if (ReadString(component, "overrideVariable") is string variable)
        {
            return $"{description}. Check or clear {variable}; downloading to the catalogue location does not change this override";
        }
        if (error is not null)
        {
            return $"{description}. Download Offline Distances to install matching catalogue files";
        }
        return resolutionError is null ? description : $"{description}. Check the catalogue location";
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
}
