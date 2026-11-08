using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal sealed record ImageSourceHeader(IReadOnlyDictionary<string, JsonElement> Headers, string[] History, string[] Comments)
{
    public string? Text(string key) => Headers.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
}

internal sealed record ImageSourceWindow(int ImageWidth, int ImageHeight, int Planes,
    int X, int Y, int Width, int Height, float[] Values);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageSourceHeader))]
internal sealed partial class ImageIoJsonContext : JsonSerializerContext;
