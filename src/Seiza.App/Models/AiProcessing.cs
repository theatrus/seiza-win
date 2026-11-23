using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal sealed record AiProcessing
{
    public string? Model { get; init; }
    public string? Backend { get; init; }
    public string? Precision { get; init; }
    public int? Tile { get; init; }
    public int? DeviceId { get; init; }
    public double High { get; init; } = 1;
    public double Low { get; init; } = 1;
    public double Scale { get; init; } = 5;

    [JsonIgnore]
    public string? ValidationMessage =>
        !double.IsFinite(High) || High is < 0 or > 1 || !double.IsFinite(Low) || Low is < 0 or > 1
            ? "Grain and mottle reduction must be between 0 and 1."
        : !double.IsFinite(Scale) || Scale is <= 0 or > 100 ? "Noise scale must be greater than 0 and at most 100 pixels."
        : Tile is <= 0 || DeviceId is < 0 ? "Tile size must be positive; device index cannot be negative."
        : Backend is not (null or "cpu" or "directml" or "cuda" or "tensorrt" or "tensorrt-rtx" or "webgpu")
            ? "Choose a supported AI backend."
        : Precision is not (null or "f32" or "f16") ? "Choose automatic, f32 or f16 precision." : null;

    internal void WriteJson(Utf8JsonWriter writer) => JsonSerializer.Serialize(writer, this, AiProcessingJsonContext.Default.AiProcessing);
    internal static AiProcessing ReadJson(JsonElement value) => value.Deserialize(AiProcessingJsonContext.Default.AiProcessing)
        ?? throw new FormatException("Invalid AI processing settings.");
}

// A preview is editor state, never an export crop or part of the saved recipe.
internal sealed record ProcessingRegion(int X, int Y, int Width, int Height)
{
    internal static ProcessingRegion Center(int width, int height) =>
        new((width - Math.Min(width, 768)) / 2, (height - Math.Min(height, 768)) / 2, Math.Min(width, 768), Math.Min(height, 768));

    internal static ProcessingRegion FromDrag(double x1, double y1, double x2, double y2, int width, int height)
    {
        int left = (int)Math.Clamp(Math.Floor(Math.Min(x1, x2)), 0, width - 1);
        int top = (int)Math.Clamp(Math.Floor(Math.Min(y1, y2)), 0, height - 1);
        int right = (int)Math.Clamp(Math.Ceiling(Math.Max(x1, x2)), left + 1, width);
        int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(y1, y2)), top + 1, height);
        return new(left, top, right - left, bottom - top);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AiProcessing))]
internal sealed partial class AiProcessingJsonContext : JsonSerializerContext;
