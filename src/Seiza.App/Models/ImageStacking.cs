using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Seiza.App.Models;

internal enum StackNormalizationMode
{
    None,
    Global,
    Local,
}

internal enum StackRejectionMode
{
    None,
    DeltaSigma,
}

internal sealed class ImageStackOptions
{
    public StackNormalizationMode Normalization { get; set; } = StackNormalizationMode.Global;
    public int LocalTileSize { get; set; } = 256;
    public StackRejectionMode Rejection { get; set; } = StackRejectionMode.DeltaSigma;
    public double SigmaLow { get; set; } = 3.0;
    public double SigmaHigh { get; set; } = 3.0;
    public int RejectionWarmup { get; set; } = 5;
    public double MaximumRegistrationRms { get; set; } = 2.0;
    public double MaximumDriftPixels { get; set; } = 256.0;
    public double MaximumDriftFraction { get; set; } = 0.15;
    public double MinimumOverlap { get; set; } = 0.60;

    public string? ValidationMessage
    {
        get
        {
            if (Normalization == StackNormalizationMode.Local && LocalTileSize < 16)
            {
                return "Local normalization tiles must be at least 16 pixels wide.";
            }
            if (Rejection == StackRejectionMode.DeltaSigma &&
                (!double.IsFinite(SigmaLow) || SigmaLow <= 0 ||
                 !double.IsFinite(SigmaHigh) || SigmaHigh <= 0))
            {
                return "Sigma thresholds must be positive numbers.";
            }
            if (Rejection == StackRejectionMode.DeltaSigma && RejectionWarmup < 2)
            {
                return "Rejection warmup must include at least two frames.";
            }
            if (!double.IsFinite(MaximumRegistrationRms) || MaximumRegistrationRms <= 0)
            {
                return "Maximum registration RMS must be positive.";
            }
            if (!double.IsFinite(MaximumDriftPixels) || MaximumDriftPixels <= 0)
            {
                return "Maximum drift must be positive.";
            }
            if (!double.IsFinite(MaximumDriftFraction) ||
                MaximumDriftFraction is < 0 or > 1)
            {
                return "Maximum drift fraction must be between 0 and 1.";
            }
            if (!double.IsFinite(MinimumOverlap) || MinimumOverlap is < 0 or > 1)
            {
                return "Minimum overlap must be between 0 and 1.";
            }
            return null;
        }
    }

    public string ToJson()
    {
        var payload = new StackOptionsPayload(
            new StackRegistrationPayload(MaximumDriftPixels, MaximumDriftFraction),
            new StackNormalizationPayload(
                Normalization switch
                {
                    StackNormalizationMode.None => "none",
                    StackNormalizationMode.Local => "local",
                    _ => "global",
                },
                Normalization == StackNormalizationMode.Local
                    ? new StackLocalNormalizationPayload(LocalTileSize)
                    : null),
            new StackRejectionPayload(
                Rejection == StackRejectionMode.DeltaSigma ? "delta-sigma" : "none",
                Rejection == StackRejectionMode.DeltaSigma
                    ? new StackDeltaSigmaPayload(
                        SigmaLow,
                        SigmaHigh,
                        RejectionWarmup,
                        1.0e-6)
                    : null),
            new StackAcceptancePayload(MaximumRegistrationRms, MinimumOverlap));
        return JsonSerializer.Serialize(
            payload,
            SeizaJsonSerializerContext.Default.StackOptionsPayload);
    }
}

internal sealed class ImageStackCalibration
{
    public string? BiasPath { get; set; }
    public string? DarkPath { get; set; }
    public string? FlatPath { get; set; }
    public bool OverridesDarkExposure { get; set; }
    public double DarkExposureSeconds { get; set; } = 300.0;

    public ImageStackCalibration Copy() => new()
    {
        BiasPath = BiasPath,
        DarkPath = DarkPath,
        FlatPath = FlatPath,
        OverridesDarkExposure = OverridesDarkExposure,
        DarkExposureSeconds = DarkExposureSeconds,
    };

    public string? ValidationMessage(IReadOnlyList<string> inputs)
    {
        if (DarkPath is null && OverridesDarkExposure)
        {
            return "Choose a master dark before overriding its exposure.";
        }
        if (OverridesDarkExposure &&
            (!double.IsFinite(DarkExposureSeconds) || DarkExposureSeconds <= 0))
        {
            return "The master-dark exposure must be positive.";
        }

        string[] paths = inputs
            .Concat(new[] { BiasPath, DarkPath, FlatPath }.OfType<string>())
            .Select(Path.GetFullPath)
            .ToArray();
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length
            ? null
            : "Each light frame and calibration master must be a different file.";
    }
}

internal sealed record ImageFilenameFilter(string Id, string Title, string FilenameSuffix)
{
    private static readonly Regex TokenSeparator = new(
        @"[^\p{L}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ImageFilenameFilter? Detect(string path)
    {
        string filename = Path.GetFileNameWithoutExtension(path)
            .Replace("α", "alpha", StringComparison.Ordinal)
            .Replace("β", "beta", StringComparison.Ordinal);
        string[] originalTokens = TokenSeparator.Split(filename)
            .Where(token => token.Length > 0)
            .ToArray();
        string[] tokens = originalTokens.Select(token => token.ToLowerInvariant()).ToArray();

        for (int index = 0; index + 1 < tokens.Length; index++)
        {
            (string left, string right) = (tokens[index], tokens[index + 1]);
            if ((left is "h" or "hydrogen") && right == "alpha")
            {
                return Known("hydrogen-alpha");
            }
            if ((left is "o" or "oxygen") && right == "iii")
            {
                return Known("oxygen-iii");
            }
            if ((left is "s" or "sulfur" or "sulphur") && right == "ii")
            {
                return Known("sulfur-ii");
            }
            if ((left is "h" or "hydrogen") && right == "beta")
            {
                return Known("hydrogen-beta");
            }
        }

        foreach (string token in tokens)
        {
            string? id = token switch
            {
                "ha" or "halpha" or "hydrogenalpha" => "hydrogen-alpha",
                "oiii" or "o3" or "oxygeniii" => "oxygen-iii",
                "s" or "sii" or "s2" or "sulfurii" or "sulphurii" => "sulfur-ii",
                "hb" or "hbeta" or "hydrogenbeta" => "hydrogen-beta",
                "l" or "lum" or "luminance" => "luminance",
                "r" or "red" => "red",
                "g" or "green" => "green",
                "b" or "blue" => "blue",
                _ => null,
            };
            if (id is not null)
            {
                return Known(id);
            }
        }

        int marker = Array.FindIndex(tokens, token => token == "filter");
        if (marker >= 0 && marker + 1 < originalTokens.Length)
        {
            return Named(originalTokens[marker + 1]);
        }
        for (int index = originalTokens.Length - 1; index > 0; index--)
        {
            string token = originalTokens[index];
            if (token.All(char.IsLetter) &&
                (token.Length == 1 ||
                 (string.Equals(token, token.ToUpperInvariant(), StringComparison.Ordinal) &&
                  !string.Equals(token, token.ToLowerInvariant(), StringComparison.Ordinal))))
            {
                return Named(token);
            }
        }
        return null;
    }

    private static ImageFilenameFilter Named(string name) =>
        new($"named:{name.ToLowerInvariant()}", name, name);

    private static ImageFilenameFilter Known(string id) => id switch
    {
        "luminance" => new(id, "Luminance", "L"),
        "red" => new(id, "Red", "R"),
        "green" => new(id, "Green", "G"),
        "blue" => new(id, "Blue", "B"),
        "hydrogen-alpha" => new(id, "H-alpha", "Ha"),
        "oxygen-iii" => new(id, "OIII", "OIII"),
        "sulfur-ii" => new(id, "SII", "SII"),
        "hydrogen-beta" => new(id, "H-beta", "Hb"),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}

internal sealed record ImageStackGroup(
    string Id,
    ImageFilenameFilter? Filter,
    IReadOnlyList<string> Inputs)
{
    public string Title => Filter?.Title ?? (Id == "all" ? "All frames" : "Other");
    public string FilenameSuffix => Filter?.FilenameSuffix ?? "Other";
}

internal static class ImageStackGrouping
{
    public static bool HasMultipleDetectedFilters(IEnumerable<string> paths) =>
        paths.Select(ImageFilenameFilter.Detect)
            .Where(filter => filter is not null)
            .Select(filter => filter!.Id)
            .Distinct(StringComparer.Ordinal)
            .Skip(1)
            .Any();

    public static IReadOnlyList<ImageStackGroup> Groups(
        IReadOnlyList<string> paths,
        bool splitByFilter)
    {
        if (!splitByFilter || !HasMultipleDetectedFilters(paths))
        {
            return [new ImageStackGroup("all", null, paths)];
        }

        var order = new List<string>();
        var groups = new Dictionary<string, (ImageFilenameFilter? Filter, List<string> Inputs)>(
            StringComparer.Ordinal);
        foreach (string path in paths)
        {
            ImageFilenameFilter? filter = ImageFilenameFilter.Detect(path);
            string key = filter?.Id ?? "other";
            if (!groups.TryGetValue(key, out var group))
            {
                order.Add(key);
                group = (filter, []);
                groups.Add(key, group);
            }
            group.Inputs.Add(path);
        }
        return order.Select(key =>
        {
            var group = groups[key];
            return new ImageStackGroup(key, group.Filter, group.Inputs);
        }).ToArray();
    }
}

internal static class ImageStackOutputNaming
{
    public static string SafeBaseName(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value.Trim());
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(character => !invalid.Contains(character))).Trim();
    }

    public static IReadOnlyDictionary<string, string> SplitOutputPaths(
        string folderPath,
        string baseName,
        IReadOnlyList<ImageStackGroup> groups)
    {
        string safeBaseName = SafeBaseName(baseName);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ImageStackGroup group in groups)
        {
            string suffix = SafeBaseName(group.FilenameSuffix);
            string stem = $"{safeBaseName}-{suffix}";
            string output = Path.Combine(folderPath, $"{stem}.fits");
            for (int discriminator = 2; !usedPaths.Add(Path.GetFullPath(output)); discriminator++)
            {
                output = Path.Combine(folderPath, $"{stem}-{discriminator}.fits");
            }
            outputs.Add(group.Id, output);
        }
        return outputs;
    }
}

internal sealed record ImageStackRequest(
    IReadOnlyList<string> Inputs,
    string OutputPath,
    ImageStackOptions Options,
    ImageStackCalibration Calibration);

internal sealed record ImageStackJob(ImageStackGroup Group, ImageStackRequest Request);

internal static class ImageStackValidation
{
    public static void ValidateBatch(IReadOnlyList<ImageStackJob> jobs)
    {
        if (jobs.Count == 0)
        {
            throw new ArgumentException("Choose at least one stack group.", nameof(jobs));
        }

        foreach (ImageStackJob job in jobs)
        {
            ValidateRequest(job.Request);
        }

        string[] outputs = jobs
            .Select(job => Path.GetFullPath(job.Request.OutputPath))
            .ToArray();
        if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length)
        {
            throw new ArgumentException(
                "Each filter stack must use a different output file.",
                nameof(jobs));
        }

        var sources = new HashSet<string>(
            jobs.SelectMany(job => SourcePaths(job.Request)).Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        if (outputs.Any(sources.Contains))
        {
            throw new ArgumentException(
                "Choose output files that are not input or calibration files in this batch.",
                nameof(jobs));
        }
    }

    private static void ValidateRequest(ImageStackRequest request)
    {
        if (request.Inputs.Count < 2)
        {
            throw new ArgumentException("Choose at least two images to stack.", nameof(request));
        }
        string? validationMessage = request.Options.ValidationMessage
            ?? request.Calibration.ValidationMessage(request.Inputs);
        if (validationMessage is not null)
        {
            throw new ArgumentException(validationMessage, nameof(request));
        }
    }

    private static IEnumerable<string> SourcePaths(ImageStackRequest request) =>
        request.Inputs.Concat(new[]
        {
            request.Calibration.BiasPath,
            request.Calibration.DarkPath,
            request.Calibration.FlatPath,
        }.OfType<string>());
}

internal sealed record ImageStackDisposition(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("reason")] string? Reason);

internal enum ImageStackProgressPhase
{
    Preparing,
    Stacking,
    Writing,
}

internal sealed record ImageStackProgress(
    ImageStackProgressPhase Phase,
    string Message,
    int CompletedFrames,
    int TotalFrames,
    int AcceptedFrames,
    int RejectedFrames)
{
    public double FractionCompleted => TotalFrames <= 0
        ? 0
        : Math.Clamp((double)CompletedFrames / TotalFrames, 0, 1);
}

internal sealed record ImageStackResult(
    string OutputPath,
    int AcceptedFrames,
    int RejectedFrames,
    IReadOnlyList<ImageStackDisposition> Dispositions,
    StackSnrAnalysis SnrAnalysis,
    string? SnrWarning);

internal sealed record ImageStackBatchResult(IReadOnlyList<ImageStackResult> Results)
{
    public int AcceptedFrames => Results.Sum(result => result.AcceptedFrames);
    public int RejectedFrames => Results.Sum(result => result.RejectedFrames);
    public IReadOnlyList<string> OutputPaths => Results.Select(result => result.OutputPath).ToArray();
}

internal sealed class ImageStackBatchCanceledException : OperationCanceledException
{
    public ImageStackBatchCanceledException(
        IReadOnlyList<string> completedOutputPaths,
        CancellationToken cancellationToken)
        : base(MessageFor(completedOutputPaths), cancellationToken)
    {
        CompletedOutputPaths = completedOutputPaths;
    }

    public IReadOnlyList<string> CompletedOutputPaths { get; }

    private static string MessageFor(IReadOnlyList<string> paths) => paths.Count == 0
        ? "Stacking was cancelled. No output was written."
        : $"Stacking was cancelled. Already saved: {DisplayNames(paths)}.";

    private static string DisplayNames(IEnumerable<string> paths) =>
        string.Join(", ", paths.Select(Path.GetFileName));
}

internal sealed class ImageStackBatchFailureException : Exception
{
    public ImageStackBatchFailureException(
        Exception innerException,
        IReadOnlyList<string> completedOutputPaths)
        : base(
            $"{innerException.Message} Already saved: " +
            $"{string.Join(", ", completedOutputPaths.Select(Path.GetFileName))}.",
            innerException)
    {
        CompletedOutputPaths = completedOutputPaths;
    }

    public IReadOnlyList<string> CompletedOutputPaths { get; }
}

internal sealed record StackOptionsPayload(
    [property: JsonPropertyName("registration")] StackRegistrationPayload Registration,
    [property: JsonPropertyName("normalization")] StackNormalizationPayload Normalization,
    [property: JsonPropertyName("rejection")] StackRejectionPayload Rejection,
    [property: JsonPropertyName("acceptance")] StackAcceptancePayload Acceptance);

internal sealed record StackRegistrationPayload(
    [property: JsonPropertyName("maximum_drift_pixels")] double MaximumDriftPixels,
    [property: JsonPropertyName("maximum_drift_fraction")] double MaximumDriftFraction);

internal sealed record StackNormalizationPayload(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("options")] StackLocalNormalizationPayload? Options);

internal sealed record StackLocalNormalizationPayload(
    [property: JsonPropertyName("tile_size")] int TileSize);

internal sealed record StackRejectionPayload(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("options")] StackDeltaSigmaPayload? Options);

internal sealed record StackDeltaSigmaPayload(
    [property: JsonPropertyName("low_sigma")] double LowSigma,
    [property: JsonPropertyName("high_sigma")] double HighSigma,
    [property: JsonPropertyName("warmup_samples")] int WarmupSamples,
    [property: JsonPropertyName("minimum_sigma")] double MinimumSigma);

internal sealed record StackAcceptancePayload(
    [property: JsonPropertyName("maximum_registration_rms_pixels")] double MaximumRegistrationRmsPixels,
    [property: JsonPropertyName("minimum_overlap_fraction")] double MinimumOverlapFraction);
