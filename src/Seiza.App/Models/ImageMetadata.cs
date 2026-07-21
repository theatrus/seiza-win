using System.Text.Json;

namespace Seiza.App.Models;

public sealed record ImageMetadata(
    int Width,
    int Height,
    int Planes,
    string Format,
    string ColorKind,
    string? RgbStretchMode,
    ImageStatistics Statistics,
    IReadOnlyDictionary<string, JsonElement> Headers);

public sealed record ImageStatistics(
    int Minimum,
    int Maximum,
    double Mean,
    int Median,
    double Mad);
