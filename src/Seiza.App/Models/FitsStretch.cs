using System.Text;
using System.Text.Json;

namespace Seiza.App.Models;

internal enum FitsStretchType
{
    AutoMtf,
    PercentileAsinh,
    Linear,
    Asinh,
    Mtf,
    Ghs,
    Identity,
}

internal static class FitsStretchTypeExtensions
{
    public static string Title(this FitsStretchType type) => type switch
    {
        FitsStretchType.AutoMtf => "Auto MTF",
        FitsStretchType.PercentileAsinh => "Percentile Asinh",
        FitsStretchType.Linear => "Linear",
        FitsStretchType.Asinh => "Asinh",
        FitsStretchType.Mtf => "Midtones Transfer",
        FitsStretchType.Ghs => "Generalized Hyperbolic",
        FitsStretchType.Identity => "No Stretch",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string Help(this FitsStretchType type) => type switch
    {
        FitsStretchType.AutoMtf =>
            "Choose an MTF curve from the image median and median absolute deviation.",
        FitsStretchType.PercentileAsinh =>
            "Choose black and white points from image percentiles, then apply an asinh curve.",
        FitsStretchType.Linear =>
            "Map explicit black and white points linearly to the display range.",
        FitsStretchType.Asinh =>
            "Apply an asinh curve between explicit black and white points.",
        FitsStretchType.Mtf =>
            "Apply explicit shadows, midtone, and highlights parameters.",
        FitsStretchType.Ghs =>
            "Apply a manual Generalized Hyperbolic Stretch with protection boundaries.",
        FitsStretchType.Identity =>
            "Clamp normalized astronomy samples to the display range without a stretch curve.",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string JsonName(this FitsStretchType type) => type switch
    {
        FitsStretchType.AutoMtf => "auto-mtf",
        FitsStretchType.PercentileAsinh => "percentile-asinh",
        FitsStretchType.Linear => "linear",
        FitsStretchType.Asinh => "asinh",
        FitsStretchType.Mtf => "mtf",
        FitsStretchType.Ghs => "ghs",
        FitsStretchType.Identity => "identity",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

internal enum FitsStretchColorStrategy
{
    Linked,
    Unlinked,
    LuminancePreserving,
}

internal static class FitsStretchColorStrategyExtensions
{
    public static string Title(this FitsStretchColorStrategy strategy) => strategy switch
    {
        FitsStretchColorStrategy.Linked => "Linked Channels",
        FitsStretchColorStrategy.Unlinked => "Per Channel",
        FitsStretchColorStrategy.LuminancePreserving => "Preserve Luminance Color",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    public static string Help(this FitsStretchColorStrategy strategy) => strategy switch
    {
        FitsStretchColorStrategy.Linked =>
            "Analyze all channels together and apply one shared curve.",
        FitsStretchColorStrategy.Unlinked =>
            "Analyze and stretch each color channel independently.",
        FitsStretchColorStrategy.LuminancePreserving =>
            "Stretch Rec. 709 luminance while retaining RGB chromaticity.",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    public static string JsonName(this FitsStretchColorStrategy strategy) => strategy switch
    {
        FitsStretchColorStrategy.Linked => "linked",
        FitsStretchColorStrategy.Unlinked => "unlinked",
        FitsStretchColorStrategy.LuminancePreserving => "luminance-preserving",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };
}

internal sealed class FitsDeconvolutionConfiguration : IEquatable<FitsDeconvolutionConfiguration>
{
    public double PsfFwhmPixels { get; set; } = 3.0;
    public int Iterations { get; set; } = 4;
    public double Amount { get; set; } = 0.35;
    public double NoiseFraction { get; set; } = 0.001;
    public double MaxCorrection { get; set; } = 2.0;

    public FitsDeconvolutionConfiguration Clone() =>
        (FitsDeconvolutionConfiguration)MemberwiseClone();

    public string? ValidationMessage
    {
        get
        {
            if (!double.IsFinite(PsfFwhmPixels) ||
                !double.IsFinite(Amount) ||
                !double.IsFinite(NoiseFraction) ||
                !double.IsFinite(MaxCorrection))
            {
                return "Deconvolution parameters must be finite numbers.";
            }
            if (PsfFwhmPixels is < 0.25 or > 100)
            {
                return "PSF FWHM must be between 0.25 and 100 pixels.";
            }
            if (Iterations is < 1 or > 50)
            {
                return "Iterations must be between 1 and 50.";
            }
            if (Amount is < 0 or > 1)
            {
                return "Amount must be between 0 and 1.";
            }
            if (NoiseFraction is < 0 or > 0.25)
            {
                return "Noise damping must be between 0 and 0.25.";
            }
            return MaxCorrection is < 1 or > 100
                ? "Correction limit must be between 1 and 100."
                : null;
        }
    }

    public bool Equals(FitsDeconvolutionConfiguration? other) => other is not null &&
        PsfFwhmPixels == other.PsfFwhmPixels &&
        Iterations == other.Iterations &&
        Amount == other.Amount &&
        NoiseFraction == other.NoiseFraction &&
        MaxCorrection == other.MaxCorrection;

    public override bool Equals(object? obj) => Equals(obj as FitsDeconvolutionConfiguration);

    public override int GetHashCode() => HashCode.Combine(
        PsfFwhmPixels,
        Iterations,
        Amount,
        NoiseFraction,
        MaxCorrection);
}

internal sealed class FitsStretchConfiguration : IEquatable<FitsStretchConfiguration>
{
    public FitsStretchType Type { get; set; } = FitsStretchType.AutoMtf;
    public FitsStretchColorStrategy ColorStrategy { get; set; } = FitsStretchColorStrategy.Unlinked;
    public int MaxAnalysisSamples { get; set; } = 200_000;

    public double TargetMedian { get; set; } = 0.2;
    public double ShadowsClip { get; set; } = -2.8;

    public double BlackPercentile { get; set; } = 0.01;
    public double WhitePercentile { get; set; } = 0.995;
    public double Strength { get; set; } = 10.0;

    public double Black { get; set; }
    public double White { get; set; } = 1.0;

    public double Shadows { get; set; }
    public double Midtone { get; set; } = 0.25;
    public double Highlights { get; set; } = 1.0;

    public double StretchFactor { get; set; } = 1.0;
    public double LocalIntensity { get; set; }
    public double SymmetryPoint { get; set; }
    public double ProtectShadows { get; set; }
    public double ProtectHighlights { get; set; } = 1.0;

    public static FitsStretchConfiguration CreateIdentity() => new()
    {
        Type = FitsStretchType.Identity,
    };

    public FitsStretchConfiguration Clone() => (FitsStretchConfiguration)MemberwiseClone();

    public string? ValidationMessage
    {
        get
        {
            double[] finiteValues =
            [
                TargetMedian, ShadowsClip, BlackPercentile, WhitePercentile,
                Strength, Black, White, Shadows, Midtone, Highlights,
                StretchFactor, LocalIntensity, SymmetryPoint, ProtectShadows,
                ProtectHighlights,
            ];
            if (finiteValues.Any(value => !double.IsFinite(value)))
            {
                return "Stretch parameters must be finite numbers.";
            }
            if (MaxAnalysisSamples <= 0)
            {
                return "Analysis samples must be greater than zero.";
            }

            return Type switch
            {
                FitsStretchType.AutoMtf when TargetMedian is <= 0 or >= 1 =>
                    "Target median must be between 0 and 1.",
                FitsStretchType.AutoMtf when ShadowsClip > 0 =>
                    "Shadows clipping must be zero or negative.",
                FitsStretchType.PercentileAsinh when
                    BlackPercentile is < 0 or > 1 ||
                    WhitePercentile is < 0 or > 1 ||
                    WhitePercentile <= BlackPercentile =>
                    "White percentile must be greater than black percentile.",
                FitsStretchType.PercentileAsinh when Strength <= 0 =>
                    "Asinh strength must be greater than zero.",
                FitsStretchType.Linear when White <= Black =>
                    "White point must be greater than black point.",
                FitsStretchType.Asinh when White <= Black =>
                    "White point must be greater than black point.",
                FitsStretchType.Asinh when Strength <= 0 =>
                    "Asinh strength must be greater than zero.",
                FitsStretchType.Mtf when Highlights <= Shadows =>
                    "Highlights must be greater than shadows.",
                FitsStretchType.Mtf when Midtone is <= 0 or >= 1 =>
                    "Midtone must be between 0 and 1.",
                FitsStretchType.Ghs when White <= Black =>
                    "White point must be greater than black point.",
                FitsStretchType.Ghs when
                    StretchFactor is < 0 or > 20 ||
                    LocalIntensity is < -5 or > 15 ||
                    SymmetryPoint is < 0 or > 1 ||
                    ProtectShadows < 0 || ProtectShadows > SymmetryPoint ||
                    ProtectHighlights < SymmetryPoint || ProtectHighlights > 1 =>
                    "GHS requires 0 ≤ shadow protection ≤ symmetry ≤ highlight protection ≤ 1.",
                _ => null,
            };
        }
    }

    public bool Equals(FitsStretchConfiguration? other) => other is not null &&
        Type == other.Type &&
        ColorStrategy == other.ColorStrategy &&
        MaxAnalysisSamples == other.MaxAnalysisSamples &&
        TargetMedian == other.TargetMedian &&
        ShadowsClip == other.ShadowsClip &&
        BlackPercentile == other.BlackPercentile &&
        WhitePercentile == other.WhitePercentile &&
        Strength == other.Strength &&
        Black == other.Black &&
        White == other.White &&
        Shadows == other.Shadows &&
        Midtone == other.Midtone &&
        Highlights == other.Highlights &&
        StretchFactor == other.StretchFactor &&
        LocalIntensity == other.LocalIntensity &&
        SymmetryPoint == other.SymmetryPoint &&
        ProtectShadows == other.ProtectShadows &&
        ProtectHighlights == other.ProtectHighlights;

    public override bool Equals(object? obj) => Equals(obj as FitsStretchConfiguration);

    public override int GetHashCode() => HashCode.Combine(Type, ColorStrategy, MaxAnalysisSamples);
}

internal sealed class FitsStretchStack : IEquatable<FitsStretchStack>
{
    public FitsStretchStack(IEnumerable<FitsStretchConfiguration> stages)
    {
        Stages = stages.Select(stage => stage.Clone()).ToList();
        if (Stages.Count == 0)
        {
            throw new ArgumentException("A stretch stack must contain at least one stage.", nameof(stages));
        }
    }

    public IReadOnlyList<FitsStretchConfiguration> Stages { get; }

    public static FitsStretchStack Default { get; } = new([new FitsStretchConfiguration()]);

    public FitsStretchStack Clone() => new(Stages);

    public string? ValidationMessage =>
        Stages.Select(stage => stage.ValidationMessage).FirstOrDefault(message => message is not null);

    public bool Equals(FitsStretchStack? other) => other is not null &&
        Stages.SequenceEqual(other.Stages);

    public override bool Equals(object? obj) => Equals(obj as FitsStretchStack);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (FitsStretchConfiguration stage in Stages)
        {
            hash.Add(stage);
        }
        return hash.ToHashCode();
    }
}

internal sealed class FitsImageProcessingConfiguration
{
    public FitsImageProcessingConfiguration(
        FitsStretchStack stretchStack,
        bool extractsBackground,
        FitsDeconvolutionConfiguration? deconvolution = null,
        bool interactivePreview = false)
    {
        StretchStack = stretchStack.Clone();
        ExtractsBackground = extractsBackground;
        Deconvolution = deconvolution?.Clone();
        InteractivePreview = interactivePreview;
    }

    public FitsStretchStack StretchStack { get; }
    public bool ExtractsBackground { get; }
    public FitsDeconvolutionConfiguration? Deconvolution { get; }
    public bool InteractivePreview { get; }

    public static FitsImageProcessingConfiguration Default { get; } = new(
        FitsStretchStack.Default,
        false);

    public string ToJson()
    {
        string? validationMessage = StretchStack.ValidationMessage ??
            Deconvolution?.ValidationMessage;
        if (validationMessage is not null)
        {
            throw new InvalidOperationException(validationMessage);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("stretch");
            writer.WriteStartArray();
            foreach (FitsStretchConfiguration stage in StretchStack.Stages)
            {
                WriteStage(writer, stage);
            }
            writer.WriteEndArray();

            if (ExtractsBackground)
            {
                writer.WritePropertyName("background");
                writer.WriteStartObject();
                writer.WriteString("mode", "subtract");
                writer.WriteEndObject();
            }
            if (Deconvolution is not null)
            {
                writer.WritePropertyName("deconvolution");
                writer.WriteStartObject();
                writer.WriteNumber("psf_fwhm_pixels", Deconvolution.PsfFwhmPixels);
                writer.WriteNumber("iterations", Deconvolution.Iterations);
                writer.WriteNumber("amount", Deconvolution.Amount);
                writer.WriteNumber("noise_fraction", Deconvolution.NoiseFraction);
                writer.WriteNumber("max_correction", Deconvolution.MaxCorrection);
                writer.WriteEndObject();
            }
            if (InteractivePreview)
            {
                writer.WriteBoolean("interactive_preview", true);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStage(Utf8JsonWriter writer, FitsStretchConfiguration stage)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("model");
        writer.WriteStartObject();
        writer.WriteString("type", stage.Type.JsonName());
        switch (stage.Type)
        {
            case FitsStretchType.AutoMtf:
                writer.WriteNumber("target_median", stage.TargetMedian);
                writer.WriteNumber("shadows_clip", stage.ShadowsClip);
                break;
            case FitsStretchType.PercentileAsinh:
                writer.WriteNumber("black_percentile", stage.BlackPercentile);
                writer.WriteNumber("white_percentile", stage.WhitePercentile);
                writer.WriteNumber("strength", stage.Strength);
                break;
            case FitsStretchType.Linear:
                WriteBlackAndWhite(writer, stage);
                break;
            case FitsStretchType.Asinh:
                WriteBlackAndWhite(writer, stage);
                writer.WriteNumber("strength", stage.Strength);
                break;
            case FitsStretchType.Mtf:
                writer.WriteNumber("shadows", stage.Shadows);
                writer.WriteNumber("midtone", stage.Midtone);
                writer.WriteNumber("highlights", stage.Highlights);
                break;
            case FitsStretchType.Ghs:
                writer.WriteNumber("stretch_factor", stage.StretchFactor);
                writer.WriteNumber("local_intensity", stage.LocalIntensity);
                writer.WriteNumber("symmetry_point", stage.SymmetryPoint);
                writer.WriteNumber("protect_shadows", stage.ProtectShadows);
                writer.WriteNumber("protect_highlights", stage.ProtectHighlights);
                WriteBlackAndWhite(writer, stage);
                break;
            case FitsStretchType.Identity:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }
        writer.WriteEndObject();
        writer.WriteString("color_strategy", stage.ColorStrategy.JsonName());
        writer.WriteNumber("max_analysis_samples", stage.MaxAnalysisSamples);
        writer.WriteEndObject();
    }

    private static void WriteBlackAndWhite(Utf8JsonWriter writer, FitsStretchConfiguration stage)
    {
        writer.WriteNumber("black", stage.Black);
        writer.WriteNumber("white", stage.White);
    }
}

internal sealed class FitsStretchHistory
{
    private readonly Stack<FitsStretchStack> _undo = [];
    private readonly Stack<FitsStretchStack> _redo = [];

    public FitsStretchStack Current { get; private set; } = FitsStretchStack.Default.Clone();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Reset()
    {
        Current = FitsStretchStack.Default.Clone();
        _undo.Clear();
        _redo.Clear();
    }

    public bool Replace(FitsStretchStack stack)
    {
        if (Current.Equals(stack))
        {
            return false;
        }
        _undo.Push(Current.Clone());
        Current = stack.Clone();
        _redo.Clear();
        return true;
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out FitsStretchStack? previous))
        {
            return false;
        }
        _redo.Push(Current.Clone());
        Current = previous;
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out FitsStretchStack? next))
        {
            return false;
        }
        _undo.Push(Current.Clone());
        Current = next;
        return true;
    }
}
