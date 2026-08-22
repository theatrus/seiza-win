using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal sealed record CalibrationPreparationRequest
{
    public required CalibrationFrameProbe Reference { get; init; }
    public IReadOnlyList<CalibrationFrameProbe> TargetLights { get; init; } = [];
    public IReadOnlyList<string> ProtectedMasterPaths { get; init; } = [];
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public required string CacheDirectory { get; init; }
    public CalibrationPreparationOptions Options { get; init; } = new();
}

internal sealed record CalibrationPreparationOptions
{
    public int MinimumBiasFrames { get; init; } = 2;
    public int MinimumDarkFrames { get; init; } = 2;
    public int MinimumDarkFlatFrames { get; init; } = 2;
    public int MinimumFlatFrames { get; init; } = 2;
    public int MaximumProbeConcurrency { get; init; } = 4;
    public long? MaximumCacheBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public TimeSpan? MaximumCacheAge { get; init; } = TimeSpan.FromDays(30);
    public CalibrationPlanTolerances Tolerances { get; init; } = new();
    public CalibrationMasterRejection Rejection { get; init; } = new();
    public CalibrationDefectSuppression? FlatDefectSuppression { get; init; } = new();
}

internal enum CalibrationPreparationStage
{
    Discovering,
    Probing,
    Planning,
    Building,
    Completed,
}

internal sealed record CalibrationPreparationProgress(
    CalibrationPreparationStage Stage,
    string Message,
    int Completed = 0,
    int Total = 0,
    string? Kind = null,
    string? Path = null);

internal sealed record CalibrationPreparationKindSummary
{
    public required string Kind { get; init; }
    public required CalibrationPlanResult Plan { get; init; }
    public CalibrationMasterBuildResult? Build { get; init; }
    public string? MasterPath { get; init; }
    public string? Fingerprint { get; init; }
    public bool CacheReused { get; init; }
    public string? Warning { get; init; }
}

internal sealed class CalibrationPreparationResult : IDisposable
{
    private IDisposable? _retentionLease;

    public required ImageStackCalibration Calibration { get; init; }
    public required CalibrationPreparationKindSummary[] Summaries { get; init; }
    public required string[] Warnings { get; init; }
    public int DiscoveredFiles { get; init; }
    public int ProbedFiles { get; init; }

    internal IDisposable? RetentionLease
    {
        init => _retentionLease = value;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _retentionLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static class CalibrationPreparationWarningText
{
    private const int MaximumDisplayedWarnings = 12;

    public static string Format(IEnumerable<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        string[] distinct = warnings
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string content = string.Join(
            Environment.NewLine,
            distinct.Take(MaximumDisplayedWarnings));
        if (distinct.Length > MaximumDisplayedWarnings)
        {
            content += $"{Environment.NewLine}…and " +
                $"{distinct.Length - MaximumDisplayedWarnings} more warning(s).";
        }
        return content;
    }
}

internal sealed record CalibrationMasterCacheReport
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Kind { get; init; }
    public required string Fingerprint { get; init; }
    public required string CoreVersion { get; init; }
    public required string MasterPath { get; init; }
    public long MasterLength { get; init; }
    public long MasterLastWriteUtcTicks { get; init; }
    public required CalibrationMasterBuildResult Build { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CalibrationMasterCacheReport))]
internal sealed partial class CalibrationPreparationJsonContext : JsonSerializerContext
{
}
