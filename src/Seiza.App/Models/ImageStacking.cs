using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Seiza.App.Services;

namespace Seiza.App.Models;

internal enum StackNormalizationMode
{
    None,
    Global,
    Local,
    LocalBackground,
}

internal enum StackRegistrationModel { Similarity, Affine, Quadratic }
internal enum StackWeightingMode { Equal, InverseNoiseVariance }
internal enum StackInterpolation { Bilinear, Lanczos3 }
internal enum StackDemosaic { Vng, Mhc, Bilinear }
internal enum StackCfaIntegration { Demosaic, BayerDrizzle }
internal enum StackCanvasMode { Reference, Expanded }

internal enum StackRejectionMode
{
    None,
    DeltaSigma,
}

internal sealed class ImageStackOptions
{
    public const int MaximumGpuMemoryMib = 1_048_576;
    private double? _minimumOverlap;
    public bool UseGpu { get; set; } = true;
    public int? GpuMemoryMib { get; set; }
    // Diagnostic only; normal app requests leave tracing off.
    internal bool TraceGpu { get; set; }
    // Host-side planning policy, not part of the native StackOptions JSON.
    public StackCanvasMode CanvasMode { get; set; }
    public int MaximumCanvasMegasamples { get; set; } = 256;
    public StackNormalizationMode Normalization { get; set; } = StackNormalizationMode.Global;
    public int LocalTileSize { get; set; } = 256;
    public StackRejectionMode Rejection { get; set; } = StackRejectionMode.DeltaSigma;
    public double SigmaLow { get; set; } = 3.0;
    public double SigmaHigh { get; set; } = 3.0;
    public int RejectionWarmup { get; set; } = 5;
    public double MaximumRegistrationRms { get; set; } = 2.0;
    public double MaximumDriftPixels { get; set; } = 256.0;
    public double MaximumDriftFraction { get; set; } = 0.15;
    public double MinimumOverlap
    {
        get => _minimumOverlap ?? (CanvasMode == StackCanvasMode.Expanded ? 0.05 : 0.60);
        set => _minimumOverlap = value;
    }
    public StackRegistrationModel RegistrationModel { get; set; } = StackRegistrationModel.Similarity;
    public StackWeightingMode Weighting { get; set; } = StackWeightingMode.Equal;
    public double MinimumWeight { get; set; } = 0.05;
    public double MaximumWeight { get; set; } = 20;
    public StackInterpolation Interpolation { get; set; } = StackInterpolation.Bilinear;
    public StackDemosaic Demosaic { get; set; } = StackDemosaic.Vng;
    public StackCfaIntegration CfaIntegration { get; set; } = StackCfaIntegration.Demosaic;
    public bool SuppressHotPixels { get; set; }
    public bool LearnDefects { get; set; }
    public JsonElement? LearnedDefects { get; set; }
    public double CosmeticLowSigma { get; set; } = 16;
    public double CosmeticHighSigma { get; set; } = 16;

    // All option values are scalars. Keep snapshots complete as new choices are added.
    public ImageStackOptions Copy() => (ImageStackOptions)MemberwiseClone();

    /// <summary>
    /// Integrate every accepted frame again after stacking, with
    /// leave-one-out rejection, to remove trails that live rejection kept.
    /// This is an app-side step: it is deliberately left out of
    /// <see cref="ToJson"/>, so it never changes the native options or the
    /// checkpoint compatibility that compares them.
    /// </summary>
    public bool RemoveTransients { get; set; } = true;

    /// <summary>The low sigma for transient removal; zero selects the native default.</summary>
    public float TransientLowSigma => Rejection == StackRejectionMode.DeltaSigma
        ? (float)SigmaLow
        : 0f;

    /// <summary>The high sigma for transient removal; zero selects the native default.</summary>
    public float TransientHighSigma => Rejection == StackRejectionMode.DeltaSigma
        ? (float)SigmaHigh
        : 0f;

    public string? ValidationMessage
    {
        get
        {
            if (!Enum.IsDefined(CanvasMode) || !Enum.IsDefined(Normalization) || !Enum.IsDefined(Rejection) ||
                !Enum.IsDefined(RegistrationModel) || !Enum.IsDefined(Weighting) ||
                !Enum.IsDefined(Interpolation) || !Enum.IsDefined(Demosaic) ||
                !Enum.IsDefined(CfaIntegration))
            {
                return "Choose a supported stacking option.";
            }
            if (CanvasMode == StackCanvasMode.Expanded && MaximumCanvasMegasamples is < 1 or > 1024)
            {
                return "Choose a canvas limit between 1 and 1024 million samples.";
            }
            if (CanvasMode == StackCanvasMode.Expanded && CfaIntegration == StackCfaIntegration.BayerDrizzle)
            {
                return "Expanded mosaic canvases do not support Bayer drizzle. Choose demosaiced integration.";
            }
            if (CanvasMode == StackCanvasMode.Expanded && Normalization == StackNormalizationMode.Local)
            {
                return "Expanded mosaic canvases do not support Local normalization. Choose Local background, Global, or None.";
            }
            if (GpuMemoryMib is < 1 or > MaximumGpuMemoryMib)
            {
                return $"Choose a GPU memory budget between 1 and {MaximumGpuMemoryMib} MiB, or automatic.";
            }
            if (Normalization is StackNormalizationMode.Local or StackNormalizationMode.LocalBackground &&
                LocalTileSize < 16)
            {
                return "Local normalization tiles must be at least 16 pixels wide.";
            }
            if (Rejection == StackRejectionMode.DeltaSigma &&
                (!IsPositiveNativeFloat(SigmaLow) || !IsPositiveNativeFloat(SigmaHigh)))
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
            if (Weighting == StackWeightingMode.InverseNoiseVariance &&
                (!IsPositiveNativeFloat(MinimumWeight) || MinimumWeight > 1 ||
                 !IsPositiveNativeFloat(MaximumWeight) || MaximumWeight < 1))
            {
                return "Frame weights must be finite with 0 < minimum ≤ 1 ≤ maximum.";
            }
            if (SuppressHotPixels &&
                (!IsPositiveNativeFloat(CosmeticLowSigma) || !IsPositiveNativeFloat(CosmeticHighSigma)))
            {
                return "Hot/dead pixel thresholds must be positive finite numbers.";
            }
            return null;
        }
    }

    public string ToJson()
    {
        if (ValidationMessage is string message)
        {
            throw new ArgumentException(message);
        }
        var payload = new StackOptionsPayload(
            new StackRegistrationPayload(MaximumDriftPixels, MaximumDriftFraction,
                RegistrationModel switch
                {
                    StackRegistrationModel.Affine => "affine",
                    StackRegistrationModel.Quadratic => "quadratic",
                    _ => null,
                }),
            new StackNormalizationPayload(
                Normalization switch
                {
                    StackNormalizationMode.None => "none",
                    StackNormalizationMode.Local => "local",
                    StackNormalizationMode.LocalBackground => "local-background",
                    _ => "global",
                },
                Normalization is StackNormalizationMode.Local or StackNormalizationMode.LocalBackground
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
            new StackAcceptancePayload(MaximumRegistrationRms, MinimumOverlap),
            SuppressHotPixels ? new StackCosmeticPayload(CosmeticLowSigma, CosmeticHighSigma) : null,
            Weighting == StackWeightingMode.InverseNoiseVariance
                ? new StackWeightingPayload("inverse-noise-variance", MinimumWeight, MaximumWeight) : null,
            CfaIntegration == StackCfaIntegration.BayerDrizzle ? "bayer_drizzle" : null,
            Interpolation == StackInterpolation.Lanczos3 ? "lanczos3" : null,
            Demosaic switch
            {
                StackDemosaic.Mhc => "mhc",
                StackDemosaic.Bilinear => "bilinear",
                _ => null,
            }, LearnedDefects, UseGpu && !TraceGpu && GpuMemoryMib is null ? null : GpuPayload());
        return JsonSerializer.Serialize(
            payload,
            SeizaJsonSerializerContext.Default.StackOptionsPayload);
    }

    internal static bool SavedUsesGpu(string json)
    {
        var options = JsonNode.Parse(json)!.AsObject();
        return options["gpu"]?["enabled"]?.GetValue<bool>() ?? true;
    }

    internal bool MatchesSavedGpuPolicy(string json)
    {
        var gpu = JsonNode.Parse(json)!["gpu"];
        return (gpu?["enabled"]?.GetValue<bool>() ?? true) == UseGpu &&
            (gpu?["trace"]?.GetValue<bool>() ?? false) == TraceGpu &&
            gpu?["memory_bytes"]?.GetValue<ulong>() == GpuPayload().MemoryBytes;
    }

    private StackGpuPayload GpuPayload() => new(UseGpu, TraceGpu,
        GpuMemoryMib is int mib ? checked((ulong)mib * 1_048_576) : null);

    internal string ToGpuJson()
    {
        if (GpuMemoryMib is < 1 or > MaximumGpuMemoryMib)
            throw new ArgumentOutOfRangeException(nameof(GpuMemoryMib));
        return JsonSerializer.Serialize(GpuPayload(), SeizaJsonSerializerContext.Default.StackGpuPayload);
    }

    internal string ApplyGpuToSavedOptions(string json)
    {
        var saved = JsonNode.Parse(json)!.AsObject();
        saved["gpu"] = JsonNode.Parse(ToGpuJson());
        return saved.ToJsonString();
    }

    private static void ValidateSavedGpu(JsonNode? node)
    {
        if (node is null) return;
        var gpu = node.AsObject();
        foreach (string key in new[] { "enabled", "trace", "defects", "debayer", "detection", "resampling", "accumulator", "integration" })
            if (gpu.ContainsKey(key)) _ = gpu[key]?.GetValue<bool>() ?? throw new JsonException("Invalid GPU flag.");
        if (gpu["memory_bytes"] is JsonNode memory && memory.GetValue<ulong>() == 0)
            throw new JsonException("GPU memory budget must be positive.");
        if (gpu.ContainsKey("concurrency") && (gpu["concurrency"]?.GetValue<uint>() ?? 0) == 0)
            throw new JsonException("GPU concurrency must be positive.");
    }

    internal static bool SameProcessingOptions(string left, string right)
    {
        try
        {
            JsonObject a = JsonNode.Parse(left)!.AsObject(), b = JsonNode.Parse(right)!.AsObject();
            // Validate the saved execution-policy shape before a checkpoint is
            // opened, even though it does not change processing compatibility.
            ValidateSavedGpu(a["gpu"]);
            ValidateSavedGpu(b["gpu"]);
            a.Remove("gpu"); b.Remove("gpu");
            return JsonNode.DeepEquals(a, b);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        { return false; }
    }

    // The native contract stores these thresholds as f32, not C# doubles.
    private static bool IsPositiveNativeFloat(double value) =>
        double.IsFinite(value) && float.IsFinite((float)value) && (float)value > 0;
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

internal static class ImageStackFrameLabel
{
    public static string DisplayName(string path, string? browserRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.IsNullOrWhiteSpace(browserRoot)) return Path.GetFileName(path);
        string relative = Path.GetRelativePath(browserRoot, path);
        bool outsideRoot = Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        return outsideRoot ? Path.GetFullPath(path) : relative;
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

    public static ImageFilenameFilter FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string displayName = string.Join(
            ' ',
            name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string token = string.Concat(displayName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));
        string? id = token switch
        {
            "l" or "lum" or "luminance" => "luminance",
            "r" or "red" => "red",
            "g" or "green" => "green",
            "b" or "blue" => "blue",
            "ha" or "halpha" or "hydrogenalpha" => "hydrogen-alpha",
            "oiii" or "o3" or "oxygeniii" => "oxygen-iii",
            "sii" or "s2" or "sulfurii" or "sulphurii" => "sulfur-ii",
            "hb" or "hbeta" or "hydrogenbeta" => "hydrogen-beta",
            _ => null,
        };
        return id is null ? Named(displayName) : Known(id);
    }

    private static ImageFilenameFilter Named(string name) =>
        new(
            $"named:{string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant()}",
            name,
            name);

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
    public string? FieldTitle { get; init; }
    public string Title => FieldTitle is null ? Filter?.Title ?? (Id == "all" ? "All frames" : "Other")
        : Filter is null ? FieldTitle : $"{FieldTitle} · {Filter.Title}";
    public string FilenameSuffix => FieldTitle is null ? Filter?.FilenameSuffix ?? "Other"
        : Filter is null ? FieldTitle : $"{FieldTitle}-{Filter.FilenameSuffix}";
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
    ImageStackCalibration Calibration)
{
    public LibraryPreparedPlan? LibraryPlan { get; init; }
    /// <summary>
    /// Optional geometry-only image shared by channel stacks. It is never
    /// included implicitly or used to select calibration/normalization.
    /// </summary>
    public string? AlignmentReferencePath { get; init; }

    /// <summary>Native-ranked channel lights to try, best first (at most five).</summary>
    public IReadOnlyList<string> ChannelReferenceCandidates { get; init; } = [];

    public string[] InputsStartingWith(string referencePath) => new[] { referencePath }
        .Concat(Inputs.Where(path => !LiveStackPath.Equals(path, referencePath)))
        .ToArray();
}

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
            .Select(job => LiveStackPath.NormalizeForComparison(job.Request.OutputPath))
            .ToArray();
        string[] existingOutputs = outputs.Where(File.Exists).ToArray();
        if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length ||
            existingOutputs.Select((output, index) =>
                existingOutputs.Skip(index + 1).Any(other =>
                    StackSourceIdentity.RefersToSameFile(output, other))).Any(equivalent => equivalent))
        {
            throw new ArgumentException(
                "Each filter stack must use a different output file.",
                nameof(jobs));
        }

        var sources = new HashSet<string>(
            GetProtectedSourcePaths(jobs),
            StringComparer.OrdinalIgnoreCase);
        if (outputs.Any(output => sources.Contains(output) ||
            (File.Exists(output) && sources.Any(source =>
                StackSourceIdentity.RefersToSameFile(output, source)))))
        {
            throw new ArgumentException(
                "Choose output files that are not input or calibration files in this batch, including alignment reference images.",
                nameof(jobs));
        }
    }

    internal static string[] GetProtectedSourcePaths(IReadOnlyList<ImageStackJob> jobs) =>
        jobs.SelectMany(job => SourcePaths(job.Request))
            .Select(LiveStackPath.NormalizeForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateRequest(ImageStackRequest request)
    {
        if (request.Options.CanvasMode == StackCanvasMode.Expanded && request.AlignmentReferencePath is not null)
        {
            throw new ArgumentException("Expanded mosaics cannot use a shared fixed alignment image.", nameof(request));
        }
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
        if (request.AlignmentReferencePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.AlignmentReferencePath);
            string alignmentPath = Path.GetFullPath(request.AlignmentReferencePath);
            if (new[] { request.Calibration.BiasPath, request.Calibration.DarkPath, request.Calibration.FlatPath }
                .OfType<string>().Any(path => LiveStackPath.Equals(path, alignmentPath)))
            {
                throw new ArgumentException("The alignment reference image must not be a calibration master.", nameof(request));
            }
        }
        if (request.ChannelReferenceCandidates.Count > 5 ||
            request.ChannelReferenceCandidates.Any(candidate =>
                !request.Inputs.Any(input => LiveStackPath.Equals(input, candidate))) ||
            request.ChannelReferenceCandidates.Select(LiveStackPath.NormalizeForComparison)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.ChannelReferenceCandidates.Count ||
            (request.ChannelReferenceCandidates.Count > 0 && request.AlignmentReferencePath is null))
        {
            throw new ArgumentException("Choose up to five distinct channel reference candidates from this stack's light frames.", nameof(request));
        }
    }

    private static IEnumerable<string> SourcePaths(ImageStackRequest request) =>
        request.Inputs.Concat(request.LibraryPlan?.Review.Sources.Select(s => s.Path) ?? [])
        .Concat(request.LibraryPlan?.OriginalPaths ?? []).Concat(new[]
        {
            request.Calibration.BiasPath,
            request.Calibration.DarkPath,
            request.Calibration.FlatPath,
            request.AlignmentReferencePath,
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
    RemovingTransients,
    Writing,
}

/// <summary>
/// One stacking progress report. <c>PhaseFraction</c> is progress through the
/// current phase when it is not measured in stacked frames, such as transient
/// removal; null uses the frame counts.
/// </summary>
internal sealed record ImageStackProgress(
    ImageStackProgressPhase Phase,
    string Message,
    int CompletedFrames,
    int TotalFrames,
    int AcceptedFrames,
    int RejectedFrames,
    double? PhaseFraction = null)
{
    public double FractionCompleted => PhaseFraction is double fraction
        ? double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0
        : TotalFrames <= 0
            ? 0
            : Math.Clamp((double)CompletedFrames / TotalFrames, 0, 1);
}

/// <summary>
/// One native transient-removal progress report: the pass (0 while
/// estimating, 1 while integrating), the zero-based frame about to be read,
/// and the number of accepted frames.
/// </summary>
internal readonly record struct ImageStackReintegrationProgress(
    int Pass,
    int Index,
    int Count);

internal static class ImageStackTransientRemoval
{
    public const int PassCount = 3;

    public static string Message(ImageStackReintegrationProgress progress)
    {
        int pass = Math.Clamp(progress.Pass, 0, PassCount - 1) + 1;
        if (progress.Count <= 0)
        {
            return $"Removing transients: pass {pass} of {PassCount}";
        }
        int frame = Math.Clamp(progress.Index, 0, progress.Count - 1) + 1;
        return $"Removing transients: pass {pass} of {PassCount}, " +
            $"frame {frame} of {progress.Count}";
    }

    public static double Fraction(ImageStackReintegrationProgress progress)
    {
        if (progress.Count <= 0)
        {
            return 0;
        }
        int pass = Math.Clamp(progress.Pass, 0, PassCount - 1);
        int index = Math.Clamp(progress.Index, 0, progress.Count);
        return Math.Clamp(
            ((double)pass * progress.Count + index) / ((double)PassCount * progress.Count),
            0,
            1);
    }

    public static string NotRemovedNote(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Transients were not removed; the stack was saved without that step."
            : $"Transients were not removed; the stack was saved without that step. {reason.Trim()}";
}

/// <summary>
/// One written stack. <c>TransientRemovalNote</c> says why the optional
/// transient-removal step did not run, or is null when it ran or was off.
/// </summary>
internal sealed record ImageStackResult(
    string OutputPath,
    int AcceptedFrames,
    int RejectedFrames,
    IReadOnlyList<ImageStackDisposition> Dispositions,
    StackSnrAnalysis SnrAnalysis,
    string? SnrWarning,
    string? TransientRemovalNote = null,
    StackAlignmentReferenceSelection? ReferenceSelection = null,
    string? CanvasNote = null);

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
    [property: JsonPropertyName("acceptance")] StackAcceptancePayload Acceptance,
    [property: JsonPropertyName("cosmetic"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StackCosmeticPayload? Cosmetic,
    [property: JsonPropertyName("weighting"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StackWeightingPayload? Weighting,
    [property: JsonPropertyName("cfa_integration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CfaIntegration,
    [property: JsonPropertyName("interpolation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Interpolation,
    [property: JsonPropertyName("demosaic"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Demosaic,
    [property: JsonPropertyName("defects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Defects,
    [property: JsonPropertyName("gpu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StackGpuPayload? Gpu);

internal sealed record StackGpuPayload(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("trace"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Trace = false,
    [property: JsonPropertyName("memory_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? MemoryBytes = null);

internal sealed record StackCosmeticPayload(
    [property: JsonPropertyName("low_sigma")] double LowSigma,
    [property: JsonPropertyName("high_sigma")] double HighSigma);

internal sealed record StackWeightingPayload(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("minimum_weight")] double MinimumWeight,
    [property: JsonPropertyName("maximum_weight")] double MaximumWeight);

internal sealed record StackRegistrationPayload(
    [property: JsonPropertyName("maximum_drift_pixels")] double MaximumDriftPixels,
    [property: JsonPropertyName("maximum_drift_fraction")] double MaximumDriftFraction,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model);

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
