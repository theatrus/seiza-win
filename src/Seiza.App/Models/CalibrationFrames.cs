namespace Seiza.App.Models;

internal static class CalibrationFrameRoles
{
    public const string Bias = "bias";
    public const string Dark = "dark";
    public const string DarkFlat = "dark-flat";
    public const string Flat = "flat";
    public const string Light = "light";
    public const string Unknown = "unknown";
}

internal sealed record CalibrationFrameSignature
{
    public string? Camera { get; init; }
    public string? Telescope { get; init; }
    public long? Width { get; init; }
    public long? Height { get; init; }
    public long? Channels { get; init; }
    public long? BinningX { get; init; }
    public long? BinningY { get; init; }
    public long? Gain { get; init; }
    public long? Offset { get; init; }
    public long? ReadoutMode { get; init; }
    public string? BayerPattern { get; init; }
    public string? Filter { get; init; }
    public double? FocalLengthMm { get; init; }
    public double? RotationDeg { get; init; }
    public double? ExposureSeconds { get; init; }
    public double? CameraTempC { get; init; }
    public long? CapturedAtUnix { get; init; }
}

internal sealed record CalibrationFrameState
{
    public bool BiasSubtracted { get; init; }
    public bool DarkSubtracted { get; init; }
    public bool FlatNormalized { get; init; }
}

internal sealed record CalibrationFrameProbe
{
    public int SchemaVersion { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string Role { get; init; } = CalibrationFrameRoles.Unknown;
    public string? RawImageType { get; init; }
    public bool IsMaster { get; init; }
    public CalibrationFrameSignature Signature { get; init; } = new();
    public CalibrationFrameState CalibrationState { get; init; } = new();
}

/// <summary>
/// Enriches a target light used for calibration planning. A FITS header remains
/// authoritative. When FILTER is absent, a recognized filename token supplies
/// its actual conventional header spelling (for example Ha), never a private
/// managed-only identity. Calibration candidates are deliberately not enriched:
/// the native builder rereads their files and must see the same metadata that
/// the planner used.
/// </summary>
/// <summary>
/// Splits a group's probed lights into calibration-matching targets and
/// set-aside frames. A frame that cannot serve as a target — a master, a
/// non-light, a preprocessed light — is a warning, not a reason to refuse
/// the whole batch: the native stacker's per-frame admission remains
/// authoritative when the frame is pushed.
/// </summary>
internal static class CalibrationTargetSelection
{
    public sealed record Partition(
        IReadOnlyList<CalibrationFrameProbe> Eligible,
        IReadOnlyList<string> Warnings);

    public static Partition Split(IReadOnlyList<CalibrationFrameProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var eligible = new List<CalibrationFrameProbe>(probes.Count);
        var warnings = new List<string>();
        foreach (CalibrationFrameProbe probe in probes)
        {
            string? reason = CalibrationLightEligibility.GetIneligibilityReason(probe);
            if (reason is null)
            {
                eligible.Add(probe);
            }
            else
            {
                warnings.Add(
                    $"Set aside {Path.GetFileName(probe.Path)} for calibration matching; {reason}.");
            }
        }
        return new Partition(eligible, warnings);
    }
}

internal static class CalibrationTargetMetadata
{
    public static CalibrationFrameProbe Enrich(CalibrationFrameProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!string.IsNullOrWhiteSpace(probe.Signature.Filter))
        {
            return probe;
        }

        ImageFilenameFilter? filter = ImageFilenameFilter.Detect(probe.Path);
        return filter is null
            ? probe
            : probe with
            {
                Signature = probe.Signature with { Filter = filter.FilenameSuffix },
            };
    }
}

internal static class CalibrationLightEligibility
{
    public static bool IsEligible(CalibrationFrameProbe probe) =>
        GetIneligibilityReason(probe) is null;

    public static string? GetIneligibilityReason(CalibrationFrameProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.IsMaster)
        {
            return "the frame is already a master";
        }
        if (!string.Equals(probe.Role, CalibrationFrameRoles.Light, StringComparison.Ordinal))
        {
            return "the frame is not a light frame";
        }
        if (probe.CalibrationState.BiasSubtracted ||
            probe.CalibrationState.DarkSubtracted ||
            probe.CalibrationState.FlatNormalized)
        {
            return "the light frame is already preprocessed";
        }
        return null;
    }

    public static void Validate(CalibrationFrameProbe probe, string paramName)
    {
        string? reason = GetIneligibilityReason(probe);
        if (reason is not null)
        {
            throw new ArgumentException(
                $"Automatic calibration requires a raw light frame; {reason}.",
                paramName);
        }
    }
}

internal sealed record CalibrationPlanRecord(
    string Path,
    string Role,
    CalibrationFrameSignature Signature)
{
    public static CalibrationPlanRecord From(CalibrationFrameProbe probe) =>
        new(probe.Path, probe.Role, probe.Signature);
}

internal sealed record CalibrationPlanTolerances
{
    public double? ExposureSeconds { get; init; }
    public double? ExposureFraction { get; init; }
    public double? DarkTemperatureC { get; init; }
    public double? MasterTemperatureC { get; init; }
    public double? RotationDeg { get; init; }
    public double? FocalLengthMm { get; init; }
    public ulong? FlatSessionSeconds { get; init; }
}

internal sealed record CalibrationPlanRequest(
    string Kind,
    CalibrationPlanRecord Reference,
    IReadOnlyList<CalibrationPlanRecord> Candidates,
    int Minimum,
    CalibrationPlanTolerances Tolerances)
{
    public IReadOnlyList<CalibrationPlanRecord> References { get; init; } = [];
    public CalibrationPlanDependencies Dependencies { get; init; } = new();
}

internal sealed record CalibrationPlanDependencies
{
    public bool BiasAvailable { get; init; }
}

internal sealed record CalibrationPlanExclusion(string Path, string Reason);

internal sealed record CalibrationPlanResult
{
    public int SchemaVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public int Minimum { get; init; }
    public bool Ready { get; init; }
    public string[] MatchedPaths { get; init; } = [];
    public string[] SelectedPaths { get; init; } = [];
    public CalibrationPlanExclusion[] Excluded { get; init; } = [];
}

internal sealed record CalibrationMasterRejection(
    double LowSigma = 3,
    double HighSigma = 3);

internal sealed record CalibrationDefectSuppression(
    double LowSigma = 16,
    double HighSigma = 16);

internal sealed record CalibrationMasterBuildRequest
{
    public string Kind { get; init; } = string.Empty;
    public IReadOnlyList<string> Inputs { get; init; } = [];
    public string Output { get; init; } = string.Empty;
    public string? Bias { get; init; }
    public string? Dark { get; init; }
    public double? DarkExposureSeconds { get; init; }
    public double? ExposureSeconds { get; init; }
    public CalibrationMasterRejection Rejection { get; init; } = new();
    public CalibrationDefectSuppression? DefectSuppression { get; init; }
}

internal sealed record CalibrationMasterInputResult
{
    public string Path { get; init; } = string.Empty;
    public ulong AcceptedSamples { get; init; }
    public ulong RejectedSamples { get; init; }
}

internal sealed record CalibrationMasterSkippedInputResult
{
    public string Path { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

internal sealed record CalibrationMasterBuildResult
{
    public int SchemaVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Channels { get; init; }
    public int RequestedFrames { get; init; }
    public int InputFrames { get; init; }
    public ulong AcceptedSamples { get; init; }
    public ulong RejectedSamples { get; init; }
    public ulong FallbackPixels { get; init; }
    public ulong DefectPixelsReplaced { get; init; }
    public bool BiasSubtracted { get; init; }
    public bool DarkSubtracted { get; init; }
    public bool Normalized { get; init; }
    public double? OutputExposureSeconds { get; init; }
    public CalibrationMasterRejection Rejection { get; init; } = new();
    public CalibrationMasterInputResult[] Inputs { get; init; } = [];
    public CalibrationMasterSkippedInputResult[] SkippedInputs { get; init; } = [];
}
