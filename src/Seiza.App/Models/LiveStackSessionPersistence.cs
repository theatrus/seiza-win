namespace Seiza.App.Models;

internal static class LiveStackPath
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static string NormalizeForComparison(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string ordinaryPath = RemoveExtendedPrefix(path);
        return Path.GetFullPath(ordinaryPath);
    }

    public static bool Equals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(
                NormalizeForComparison(left),
                NormalizeForComparison(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(
                RemoveExtendedPrefix(left),
                RemoveExtendedPrefix(right),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsWithinDirectory(string path, string directory)
    {
        try
        {
            string fullPath = NormalizeForComparison(path);
            string normalizedDirectory = NormalizeForComparison(directory);
            string fullDirectory = Path.TrimEndingDirectorySeparator(normalizedDirectory);
            string directoryPrefix = Path.EndsInDirectorySeparator(normalizedDirectory)
                ? normalizedDirectory
                : normalizedDirectory + Path.DirectorySeparatorChar;
            return string.Equals(
                    fullPath,
                    fullDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(
                    directoryPrefix,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string RemoveExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase) &&
            path.Length >= ExtendedPathPrefix.Length + 3 &&
            char.IsAsciiLetter(path[ExtendedPathPrefix.Length]) &&
            path[ExtendedPathPrefix.Length + 1] == ':' &&
            path[ExtendedPathPrefix.Length + 2] is '\\' or '/')
        {
            return path[ExtendedPathPrefix.Length..];
        }
        return path;
    }
}

internal enum LiveStackPersistedFrameDisposition
{
    Accepted,
    Rejected,
    Unreadable,
    Ignored,
}

internal sealed record LiveStackPersistedFrame
{
    public string Path { get; init; } = string.Empty;
    public LiveStackPersistedFrameDisposition Disposition { get; init; }
    public string? Reason { get; init; }
    public double? ExposureSeconds { get; init; }
    public long Length { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }
    public DateTimeOffset ProcessedAtUtc { get; init; }
    public string? FileIdentity { get; init; }
}

internal sealed record LiveStackPersistedSnrSample
{
    public int AcceptedFrames { get; init; }
    public double? CumulativeExposureSeconds { get; init; }
    public double Noise { get; init; }
    public double Background { get; init; }
    public double Signal { get; init; }
    public double[] ChannelNoise { get; init; } = [];
    public DateTimeOffset MeasuredAtUtc { get; init; }
}

internal sealed record LiveStackCalibrationEpoch
{
    public int StartsAtAcceptedFrame { get; init; }
    public string? BiasPath { get; init; }
    public string? DarkPath { get; init; }
    public string? FlatPath { get; init; }
    public double? DarkExposureSeconds { get; init; }
    public DateTimeOffset SelectedAtUtc { get; init; }
}

/// <summary>
/// App-owned state that the opaque native checkpoint does not retain.
/// One instance belongs to one filter group.
/// </summary>
internal sealed record LiveStackPersistedState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SessionId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string GroupTitle { get; init; } = string.Empty;
    public string? FilterName { get; init; }
    public string WatchFolder { get; init; } = string.Empty;
    public bool IncludesSubdirectories { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string StackOptionsJson { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public LiveStackCalibrationEpoch[] CalibrationHistory { get; init; } = [];
    /// <summary>
    /// Successful snapshot/final export paths, retained so files written into
    /// the watched tree remain excluded after a restart.
    /// </summary>
    public string[] ExportedPaths { get; init; } = [];
    /// <summary>
    /// Processing history in capture order. Filtering this array to accepted
    /// frames preserves the order represented by the native input ledger.
    /// </summary>
    public LiveStackPersistedFrame[] Frames { get; init; } = [];
    public LiveStackPersistedSnrSample[] SnrSamples { get; init; } = [];
}

/// <summary>
/// The authoritative native facts used to prove that a manifest describes
/// the context beside it. A native adapter supplies this after save and open.
/// </summary>
internal sealed record LiveStackNativeState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string CoreVersion { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Channels { get; init; }
    public int AcceptedFrames { get; init; }
    public int RejectedFrames { get; init; }
    public string InputMode { get; init; } = string.Empty;
    public string ConfigurationFingerprint { get; init; } = string.Empty;
    public string[] InputPaths { get; init; } = [];
    /// <summary>
    /// Authoritative immutable source metadata for the native reference.
    /// Null only for contexts written before this additive live-state field.
    /// </summary>
    public CalibrationFrameProbe? ReferenceFrame { get; init; }

    public bool DescribesSameCheckpoint(LiveStackNativeState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (SchemaVersion != other.SchemaVersion ||
            Width != other.Width ||
            Height != other.Height ||
            Channels != other.Channels ||
            AcceptedFrames != other.AcceptedFrames ||
            RejectedFrames != other.RejectedFrames ||
            !string.Equals(InputMode, other.InputMode, StringComparison.Ordinal) ||
            !string.Equals(
                ConfigurationFingerprint,
                other.ConfigurationFingerprint,
                StringComparison.Ordinal) ||
            // The field is additive. A context reopened by a newer core may
            // expose reference metadata that was absent from the old manifest;
            // when the manifest did capture it, it remains authoritative.
            (ReferenceFrame is not null &&
             !Equals(ReferenceFrame, other.ReferenceFrame)) ||
            InputPaths is null ||
            other.InputPaths is null ||
            InputPaths.Length != other.InputPaths.Length)
        {
            return false;
        }

        for (int index = 0; index < InputPaths.Length; index++)
        {
            if (!LiveStackPath.Equals(InputPaths[index], other.InputPaths[index]))
            {
                return false;
            }
        }
        return true;
    }
}
