using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

/// <summary>Native bias/dark evidence in source sample units, with no host rescaling.</summary>
internal sealed record DarkQualityMeasurement
{
    public required int AlgorithmVersion { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Level { get; init; }
    public required double Sigma { get; init; }
    public required double Noise { get; init; }
    public required double RowBanding { get; init; }
    public required double ColumnBanding { get; init; }
    public required ulong HotPixels { get; init; }
    public required double HotThreshold { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// Null asks the shared core for its default. Threshold policy stays there.
internal sealed record DarkQualityMeasureRequest(string Path, double? HotSigma = null);
internal sealed record CalibrationQualityScreenRequest(DarkQualityMeasurement[] Measurements);
internal sealed record CalibrationQualityFinding(string Code, string Grade, double Value, double Limit)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
internal sealed record CalibrationQualityVerdict(string Grade, CalibrationQualityFinding[] Findings);
internal sealed record CalibrationQualityReport(int SchemaVersion, CalibrationQualityVerdict[] Frames);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DarkQualityMeasureRequest))]
[JsonSerializable(typeof(DarkQualityMeasurement))]
[JsonSerializable(typeof(CalibrationQualityScreenRequest))]
[JsonSerializable(typeof(CalibrationQualityReport))]
internal sealed partial class CalibrationQualityJsonContext : JsonSerializerContext;
