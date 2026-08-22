namespace Seiza.App.Models;

internal enum LiveStackRunState
{
    Created,
    Restoring,
    WaitingForLight,
    Watching,
    Processing,
    Checkpointing,
    Pausing,
    Paused,
    SavingSnapshot,
    Finishing,
    Completed,
    NeedsAttention,
    Faulted,
    Disposed,
}

internal enum LiveStackFilterSource
{
    Header,
    Filename,
    Unspecified,
}

/// <summary>
/// A comparison-safe filter identity. Known aliases such as L/Luminance and
/// Ha/H-alpha intentionally resolve to the same key.
/// </summary>
internal sealed record LiveStackFilterIdentity(
    string Key,
    string DisplayName,
    LiveStackFilterSource Source)
{
    public static LiveStackFilterIdentity FromProbe(CalibrationFrameProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        string? headerFilter = NullIfWhiteSpace(probe.Signature.Filter);
        if (headerFilter is not null)
        {
            return FromName(headerFilter, LiveStackFilterSource.Header);
        }

        ImageFilenameFilter? filenameFilter = ImageFilenameFilter.Detect(probe.Path);
        return filenameFilter is null
            ? Unspecified()
            : new LiveStackFilterIdentity(
                filenameFilter.Id,
                filenameFilter.Title,
                LiveStackFilterSource.Filename);
    }

    public static LiveStackFilterIdentity FromStoredName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Unspecified()
            : FromName(name, LiveStackFilterSource.Header);

    public bool Matches(LiveStackFilterIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Key, other.Key, StringComparison.Ordinal);
    }

    private static LiveStackFilterIdentity FromName(
        string name,
        LiveStackFilterSource source)
    {
        ImageFilenameFilter filter = ImageFilenameFilter.FromName(name);
        return new LiveStackFilterIdentity(filter.Id, filter.Title, source);
    }

    private static LiveStackFilterIdentity Unspecified() =>
        new("unfiltered", "Unfiltered", LiveStackFilterSource.Unspecified);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Exact calibration-defining identity checks for lights entering one live
/// accumulator. Unknown reference fields are permissive; a known reference
/// field requires the candidate to supply the same value.
/// </summary>
internal static class LiveStackCalibrationIdentity
{
    public static bool Matches(
        CalibrationFrameSignature reference,
        CalibrationFrameSignature candidate,
        out string? mismatchReason)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);

        mismatchReason = FirstMismatch(
            StringMismatch("camera", reference.Camera, candidate.Camera),
            ValueMismatch("width", reference.Width, candidate.Width),
            ValueMismatch("height", reference.Height, candidate.Height),
            ValueMismatch("channel count", reference.Channels, candidate.Channels),
            ValueMismatch("X binning", reference.BinningX, candidate.BinningX),
            ValueMismatch("Y binning", reference.BinningY, candidate.BinningY),
            ValueMismatch("gain", reference.Gain, candidate.Gain),
            ValueMismatch("offset", reference.Offset, candidate.Offset),
            ValueMismatch("readout mode", reference.ReadoutMode, candidate.ReadoutMode),
            StringMismatch(
                "Bayer pattern",
                reference.BayerPattern,
                candidate.BayerPattern));
        return mismatchReason is null;
    }

    private static string? ValueMismatch<T>(
        string name,
        T? reference,
        T? candidate) where T : struct, IEquatable<T>
    {
        if (reference is null)
        {
            return null;
        }
        return candidate is null
            ? $"The candidate does not report {name}."
            : !reference.Value.Equals(candidate.Value)
                ? $"The candidate {name} does not match the reference."
                : null;
    }

    private static string? StringMismatch(
        string name,
        string? reference,
        string? candidate)
    {
        string? expected = Normalize(reference);
        if (expected is null)
        {
            return null;
        }
        string? actual = Normalize(candidate);
        return actual is null
            ? $"The candidate does not report {name}."
            : !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
                ? $"The candidate {name} does not match the reference."
                : null;
    }

    private static string? FirstMismatch(params string?[] reasons) =>
        reasons.FirstOrDefault(reason => reason is not null);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}

internal sealed record LiveStackRunConfiguration
{
    public const string DefaultPreviewProcessingJson =
        "{\"sample_domain\":{\"type\":\"physical-linear\",\"normalization\":{" +
        "\"type\":\"robust-percentile\",\"black_percentile\":0.001," +
        "\"white_percentile\":0.999," +
        "\"max_analysis_samples\":200000}},\"stretch\":[{\"model\":{" +
        "\"type\":\"auto-mtf\",\"target_median\":0.2," +
        "\"shadows_clip\":-2.8},\"color_strategy\":\"unlinked\"," +
        "\"max_analysis_samples\":200000}]}";

    public string WatchFolder { get; init; } = string.Empty;
    public string SessionRootDirectory { get; init; } = string.Empty;
    public string GroupId { get; init; } = "live";
    public string GroupTitle { get; init; } = "Live stack";
    public bool IncludeSubdirectories { get; init; }
    public bool ResumeExisting { get; init; } = true;
    public bool ApplyCalibrationOnResume { get; init; }
    public string? InitialReferencePath { get; init; }
    public string? OutputPath { get; init; }
    public ImageStackOptions Options { get; init; } = new();
    public ImageStackCalibration Calibration { get; init; } = new();
    public string PreviewProcessingJson { get; init; } = DefaultPreviewProcessingJson;
    public uint PreviewMaxDimension { get; init; } = 1600;
    public TimeSpan PreviewInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(2);
    public int CheckpointAcceptedFrameInterval { get; init; } = 5;
    public int MaximumReadAttempts { get; init; } = 4;

    /// <summary>
    /// Files the monitor must never treat as captured lights. The optional
    /// initial reference is deliberately omitted: a restored session may not
    /// contain it, in which case it remains a legitimate new capture.
    /// </summary>
    public string[] MonitorExcludedPaths() => new[]
    {
        OutputPath,
        Calibration.BiasPath,
        Calibration.DarkPath,
        Calibration.FlatPath,
    }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(path!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WatchFolder))
        {
            throw new ArgumentException("A watched folder is required.", nameof(WatchFolder));
        }
        if (string.IsNullOrWhiteSpace(SessionRootDirectory))
        {
            throw new ArgumentException(
                "A live-stack session directory is required.",
                nameof(SessionRootDirectory));
        }
        if (string.IsNullOrWhiteSpace(GroupId) || string.IsNullOrWhiteSpace(GroupTitle))
        {
            throw new ArgumentException("The live-stack group needs an id and title.");
        }
        ArgumentNullException.ThrowIfNull(Options);
        ArgumentNullException.ThrowIfNull(Calibration);
        if (Options.ValidationMessage is string optionsError)
        {
            throw new ArgumentException(optionsError, nameof(Options));
        }
        if (Calibration.ValidationMessage([]) is string calibrationError)
        {
            throw new ArgumentException(calibrationError, nameof(Calibration));
        }
        if (string.IsNullOrWhiteSpace(PreviewProcessingJson))
        {
            throw new ArgumentException(
                "A preview processing configuration is required.",
                nameof(PreviewProcessingJson));
        }
        if (PreviewMaxDimension is 0 or > 8192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreviewMaxDimension),
                "The preview bound must be between 1 and 8192 pixels.");
        }
        if (PreviewInterval < TimeSpan.Zero || CheckpointInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PreviewInterval),
                "Preview and checkpoint intervals must be non-negative and positive, respectively.");
        }
        if (CheckpointAcceptedFrameInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CheckpointAcceptedFrameInterval));
        }
        if (MaximumReadAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReadAttempts),
                "Read attempts must be between 1 and 20.");
        }
    }
}

internal sealed record LiveStackAttention(
    string Message,
    string? Path,
    DateTimeOffset OccurredAtUtc);

internal static class LiveStackAttentionPresentation
{
    public static string[] RecentMessages(
        IEnumerable<LiveStackAttention> attention,
        int maximumItems = 8)
    {
        ArgumentNullException.ThrowIfNull(attention);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);

        return attention
            .Where(static item => !string.IsNullOrWhiteSpace(item.Message))
            .TakeLast(maximumItems)
            .Select(item => string.IsNullOrWhiteSpace(item.Path)
                ? item.Message
                : $"{Path.GetFileName(item.Path)} — {item.Message}")
            .ToArray();
    }
}

internal sealed record LiveStackRunSnapshot
{
    public LiveStackRunState State { get; init; } = LiveStackRunState.Created;
    public string StatusMessage { get; init; } = "Ready to watch a folder.";
    public string? CurrentPath { get; init; }
    public LiveStackFilterIdentity? LockedFilter { get; init; }
    public CalibrationFrameSignature? ReferenceSignature { get; init; }
    public int AcceptedFrames { get; init; }
    public int RejectedFrames { get; init; }
    public int IgnoredFrames { get; init; }
    public int UnreadableFrames { get; init; }
    public string FolderMonitorStatus { get; init; } = "Stopped";
    public string? FolderMonitorMessage { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? LastCheckpointAtUtc { get; init; }
    public long? LastCheckpointGeneration { get; init; }
    public IReadOnlyList<LiveStackCalibrationEpoch> CalibrationHistory { get; init; } = [];
    public IReadOnlyList<LiveStackPersistedFrame> Frames { get; init; } = [];
    public IReadOnlyList<LiveStackPersistedSnrSample> SnrSamples { get; init; } = [];
    public IReadOnlyList<StackSnrPlotPoint> SnrPlot { get; init; } = [];
    public RenderedImageData? Preview { get; init; }
    public IReadOnlyList<LiveStackAttention> Attention { get; init; } = [];
    /// <summary>
    /// Native finalization consumed the in-memory session, but the durable
    /// pre-finalization checkpoint remains available. The window must close
    /// and reopen the run before any stack operation can continue.
    /// </summary>
    public bool RequiresReopenToResume { get; init; }

    public bool HasStack => AcceptedFrames > 0;

    public double? CumulativeExposureSeconds =>
        LiveStackRunMath.CumulativeExposure(Frames);

    public bool IsRunning => State is
        LiveStackRunState.Restoring or
        LiveStackRunState.WaitingForLight or
        LiveStackRunState.Watching or
        LiveStackRunState.Processing or
        LiveStackRunState.Checkpointing;

    public TimeSpan? CheckpointAge => LastCheckpointAtUtc is DateTimeOffset saved
        ? ObservedAtUtc - saved < TimeSpan.Zero
            ? TimeSpan.Zero
            : ObservedAtUtc - saved
        : null;
}

internal sealed class LiveStackRunChangedEventArgs(LiveStackRunSnapshot snapshot) : EventArgs
{
    public LiveStackRunSnapshot Snapshot { get; } = snapshot;
}

internal sealed record LiveStackExportResult(
    string OutputPath,
    int AcceptedFrames,
    int RejectedFrames);

internal static class LiveStackRunMath
{
    public static bool CheckpointRemainsDirty(
        long currentRevision,
        long capturedRevision) => currentRevision != capturedRevision;

    public static bool IsSnrCheckpointDepth(int acceptedFrames) =>
        acceptedFrames > 0 && (acceptedFrames & (acceptedFrames - 1)) == 0;

    public static bool IsSnrMeasurementDue(
        int acceptedFrames,
        IEnumerable<int> measuredDepths,
        bool includeCurrentDepth = false)
    {
        ArgumentNullException.ThrowIfNull(measuredDepths);
        return acceptedFrames > 0 &&
            (includeCurrentDepth || IsSnrCheckpointDepth(acceptedFrames)) &&
            !measuredDepths.Contains(acceptedFrames);
    }

    public static IReadOnlyList<StackSnrPlotPoint> CreateSnrPlot(
        IEnumerable<LiveStackPersistedSnrSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return StackSnrAnalyzer.Analyze(samples
            .Where(IsUsable)
            .Select(sample => new StackSnrMeasurement(
                checked((uint)sample.AcceptedFrames),
                sample.Noise,
                sample.Background,
                sample.Signal,
                sample.CumulativeExposureSeconds ?? 0)))
            .Points;
    }

    public static double? CumulativeExposure(
        IEnumerable<LiveStackPersistedFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        LiveStackPersistedFrame[] accepted = frames
            .Where(frame => frame.Disposition == LiveStackPersistedFrameDisposition.Accepted)
            .ToArray();
        return accepted.Length > 0 && accepted.All(frame =>
                frame.ExposureSeconds is double exposure &&
                double.IsFinite(exposure) &&
                exposure > 0)
            ? accepted.Sum(frame => frame.ExposureSeconds!.Value)
            : null;
    }

    private static bool IsUsable(LiveStackPersistedSnrSample sample) =>
        sample.AcceptedFrames > 0 &&
        double.IsFinite(sample.Noise) &&
        sample.Noise > 0 &&
        double.IsFinite(sample.Signal) &&
        sample.ChannelNoise is not null;
}

internal static class LiveStackCalibrationSelection
{
    public static bool HasAnyMasters(ImageStackCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return !string.IsNullOrWhiteSpace(calibration.BiasPath) ||
            !string.IsNullOrWhiteSpace(calibration.DarkPath) ||
            !string.IsNullOrWhiteSpace(calibration.FlatPath);
    }

    public static bool AreEquivalent(ImageStackCalibration left, ImageStackCalibration right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return PathsEqual(left.BiasPath, right.BiasPath) &&
            PathsEqual(left.DarkPath, right.DarkPath) &&
            PathsEqual(left.FlatPath, right.FlatPath) &&
            (left.DarkPath is null || right.DarkPath is null ||
             (left.OverridesDarkExposure == right.OverridesDarkExposure &&
              (!left.OverridesDarkExposure ||
               left.DarkExposureSeconds.Equals(right.DarkExposureSeconds))));
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        }
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
