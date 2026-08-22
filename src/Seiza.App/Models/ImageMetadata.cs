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
    ImageHistogram? DisplayHistogram = null,
    ImageBackgroundProcessing? BackgroundProcessing = null);

public sealed record ImageBackgroundProcessing(
    string Mode,
    double Strength,
    string Model,
    ImageBackgroundDiagnostics Diagnostics)
{
    public string ModelTitle => Model switch
    {
        "polynomial" => "Polynomial",
        "radial_basis" => "Radial Basis",
        _ => Model,
    };
}

public sealed record ImageBackgroundDiagnostics(
    [property: System.Text.Json.Serialization.JsonPropertyName("candidate_samples")]
    int CandidateSamples,
    [property: System.Text.Json.Serialization.JsonPropertyName("accepted_samples")]
    int AcceptedSamples,
    [property: System.Text.Json.Serialization.JsonPropertyName("rejected_noise")]
    int RejectedNoise,
    [property: System.Text.Json.Serialization.JsonPropertyName("rejected_residual")]
    int RejectedResidual);

public sealed record ImageStatistics(
    double Minimum,
    double Maximum,
    double Mean,
    double Median,
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
