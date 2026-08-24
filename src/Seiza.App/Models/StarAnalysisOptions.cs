using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

public sealed record StarAnalysisOptions
{
    /// <summary>
    /// Interactive default chosen to keep large, dense astronomy frames responsive while
    /// preserving source-pixel measurements and enough fitted stars for the nine-cell tilt grid.
    /// The native core still derives the telescope preset from image headers. When the strict
    /// first pass measures fewer than the requested target, the core retries with its bounded
    /// adaptive ladder and returns one internally consistent pass.
    /// </summary>
    internal static StarAnalysisOptions InteractiveDefault { get; } = new()
    {
        DetectionBinning = 2,
        Sensitivity = 30,
        PsfType = StarPsfType.Moffat4,
        TriangleAngleDegrees = 0,
        TargetStarCount = 200,
    };

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StarDetectionPreset? Preset { get; init; }

    /// <summary>
    /// Optional caller override for the focal length used to classify the
    /// detector. When omitted, the path API resolves it from image headers.
    /// This value may be supplied independently of <see cref="PixelSizeUm"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FocalLengthMm { get; init; }

    /// <summary>
    /// Optional caller override for the pixel size used to classify the
    /// detector. When omitted, the path API resolves it from image headers.
    /// This value may be supplied independently of <see cref="FocalLengthMm"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PixelSizeUm { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StarPsfType? PsfType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StarStructureRemoval? StructureRemoval { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DetectionBinning { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? KeepSaturated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NoiseReductionRadius { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Sensitivity { get; init; }

    /// <summary>
    /// Requests an additional three-sector radial tilt analysis rotated by
    /// this clockwise angle from image-up. The native core normalizes any
    /// finite value to [0, 360).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TriangleAngleDegrees { get; init; }

    /// <summary>
    /// When positive, asks the native detector to retry with progressively more permissive
    /// settings until it measures at least this many stars, returning the best complete pass
    /// otherwise. Omit this value to preserve single-pass detection.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetStarCount { get; init; }

    internal string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, SeizaJsonSerializerContext.Default.StarAnalysisOptions);
    }

    public void Validate()
    {
        RequireDefined(Preset, nameof(Preset));
        RequireDefined(PsfType, nameof(PsfType));
        RequireDefined(StructureRemoval, nameof(StructureRemoval));

        if (Preset.HasValue &&
            (FocalLengthMm.HasValue || PixelSizeUm.HasValue))
        {
            throw new ArgumentException(
                "Choose a detector preset or provide focal length and/or pixel size, not both.");
        }

        RequirePositiveFinite(FocalLengthMm, nameof(FocalLengthMm));
        RequirePositiveFinite(PixelSizeUm, nameof(PixelSizeUm));

        if (DetectionBinning is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DetectionBinning),
                "Detection binning must be between 1 and 16.");
        }

        if (NoiseReductionRadius is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NoiseReductionRadius),
                "Noise reduction radius must be between 0 and 64 pixels.");
        }

        if (Sensitivity is double sensitivity &&
            (!double.IsFinite(sensitivity) || sensitivity <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Sensitivity),
                "Sensitivity must be a finite positive value.");
        }

        if (TriangleAngleDegrees is double triangleAngle &&
            !double.IsFinite(triangleAngle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TriangleAngleDegrees),
                "The triangle angle must be finite.");
        }

        if (TargetStarCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetStarCount),
                "The target star count must be positive.");
        }
    }

    private static void RequirePositiveFinite(double? value, string parameterName)
    {
        if (value is double number && (!double.IsFinite(number) || number <= 0))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The value must be finite and positive.");
        }
    }

    private static void RequireDefined<TEnum>(TEnum? value, string parameterName)
        where TEnum : struct, Enum
    {
        if (value.HasValue && !Enum.IsDefined(value.Value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The value is not a supported option.");
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<StarDetectionPreset>))]
public enum StarDetectionPreset
{
    [JsonStringEnumMemberName("widefield")]
    Widefield,

    [JsonStringEnumMemberName("standard")]
    Standard,

    [JsonStringEnumMemberName("longfocal")]
    LongFocal,
}

[JsonConverter(typeof(JsonStringEnumConverter<StarPsfType>))]
public enum StarPsfType
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("gaussian")]
    Gaussian,

    [JsonStringEnumMemberName("moffat4")]
    Moffat4,
}

[JsonConverter(typeof(JsonStringEnumConverter<StarStructureRemoval>))]
public enum StarStructureRemoval
{
    [JsonStringEnumMemberName("filtered")]
    Filtered,

    [JsonStringEnumMemberName("atrous")]
    Atrous,
}
