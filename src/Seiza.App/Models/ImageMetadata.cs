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
    IReadOnlyDictionary<string, JsonElement> Headers,
    ImageHistogram? InputHistogram = null,
    ImageHistogram? DisplayHistogram = null);

public sealed record ImageStatistics(
    int Minimum,
    int Maximum,
    double Mean,
    int Median,
    double Mad);

public sealed record ImageHistogram(
    IReadOnlyList<ulong> Red,
    IReadOnlyList<ulong> Green,
    IReadOnlyList<ulong> Blue,
    double LowerBound,
    double UpperBound)
{
    public const int BinCount = 256;

    public bool IsValid =>
        Red.Count == BinCount &&
        Green.Count == BinCount &&
        Blue.Count == BinCount &&
        double.IsFinite(LowerBound) &&
        double.IsFinite(UpperBound) &&
        UpperBound > LowerBound;
}
