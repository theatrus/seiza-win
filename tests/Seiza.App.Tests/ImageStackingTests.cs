using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageStackingTests
{
    [Fact]
    public void GroupsSplitDetectedFiltersAndKeepUnknownFramesTogether()
    {
        string[] paths =
        [
            @"C:\lights\M101_Ha_001.fits",
            @"C:\lights\M101_Ha_002.fits",
            @"C:\lights\M101_OIII_001.xisf",
            @"C:\lights\M101_OIII_002.xisf",
            @"C:\lights\M101_001.fits",
            @"C:\lights\M101_002.fits",
        ];

        IReadOnlyList<ImageStackGroup> groups = ImageStackGrouping.Groups(paths, splitByFilter: true);

        Assert.Collection(
            groups,
            group =>
            {
                Assert.Equal("hydrogen-alpha", group.Id);
                Assert.Equal(2, group.Inputs.Count);
            },
            group =>
            {
                Assert.Equal("oxygen-iii", group.Id);
                Assert.Equal(2, group.Inputs.Count);
            },
            group =>
            {
                Assert.Equal("other", group.Id);
                Assert.Equal(2, group.Inputs.Count);
            });
    }

    [Fact]
    public void OptionsSerializeThePublishedRustContract()
    {
        var options = new ImageStackOptions
        {
            Normalization = StackNormalizationMode.Local,
            LocalTileSize = 128,
            Rejection = StackRejectionMode.DeltaSigma,
            SigmaLow = 2.5,
            SigmaHigh = 3.5,
            RejectionWarmup = 7,
            MaximumRegistrationRms = 1.25,
            MaximumDriftPixels = 512,
            MaximumDriftFraction = 0.2,
            MinimumOverlap = 0.75,
        };

        using JsonDocument document = JsonDocument.Parse(options.ToJson());
        JsonElement root = document.RootElement;

        Assert.Equal("local", root.GetProperty("normalization").GetProperty("mode").GetString());
        Assert.Equal(128, root.GetProperty("normalization").GetProperty("options").GetProperty("tile_size").GetInt32());
        Assert.Equal("delta-sigma", root.GetProperty("rejection").GetProperty("mode").GetString());
        Assert.Equal(7, root.GetProperty("rejection").GetProperty("options").GetProperty("warmup_samples").GetInt32());
        Assert.Equal(512, root.GetProperty("registration").GetProperty("maximum_drift_pixels").GetDouble());
        Assert.Equal(0.75, root.GetProperty("acceptance").GetProperty("minimum_overlap_fraction").GetDouble());
    }

    [Fact]
    public void DefaultOptionsPreserveTheLegacyCheckpointJsonExactly()
    {
        const string expected = "{\"registration\":{\"maximum_drift_pixels\":256,\"maximum_drift_fraction\":0.15},\"normalization\":{\"mode\":\"global\",\"options\":null},\"rejection\":{\"mode\":\"delta-sigma\",\"options\":{\"low_sigma\":3,\"high_sigma\":3,\"warmup_samples\":5,\"minimum_sigma\":1E-06}},\"acceptance\":{\"maximum_registration_rms_pixels\":2,\"minimum_overlap_fraction\":0.6}}";

        Assert.Null(new ImageStackOptions().ValidationMessage);
        Assert.Equal(expected, new ImageStackOptions().ToJson());
    }

    [Theory]
    [InlineData("normalization", 0, "none")]
    [InlineData("normalization", 1, "global")]
    [InlineData("normalization", 2, "local")]
    [InlineData("normalization", 3, "local-background")]
    [InlineData("rejection", 0, "none")]
    [InlineData("rejection", 1, "delta-sigma")]
    [InlineData("registration", 0, null)]
    [InlineData("registration", 1, "affine")]
    [InlineData("registration", 2, "quadratic")]
    [InlineData("weighting", 0, null)]
    [InlineData("weighting", 1, "inverse-noise-variance")]
    [InlineData("interpolation", 0, null)]
    [InlineData("interpolation", 1, "lanczos3")]
    [InlineData("demosaic", 0, null)]
    [InlineData("demosaic", 1, "mhc")]
    [InlineData("demosaic", 2, "bilinear")]
    [InlineData("cfa_integration", 0, null)]
    [InlineData("cfa_integration", 1, "bayer_drizzle")]
    public void EveryOptionEnumUsesThePublishedWireSpellingOrDefaultOmission(
        string field,
        int value,
        string? expected)
    {
        var options = new ImageStackOptions();
        SetEnum(options, field, value);
        using JsonDocument document = JsonDocument.Parse(options.ToJson());
        JsonElement root = document.RootElement;
        JsonElement owner = field == "registration" ? root.GetProperty(field) : root;
        string property = field == "registration" ? "model" : field;

        if (expected is null)
        {
            Assert.False(owner.TryGetProperty(property, out _));
        }
        else
        {
            JsonElement serialized = owner.GetProperty(property);
            Assert.Equal(expected, serialized.ValueKind == JsonValueKind.Object
                ? serialized.GetProperty("mode").GetString()
                : serialized.GetString());
        }

        if (field == "normalization")
        {
            JsonElement local = root.GetProperty(field).GetProperty("options");
            if (value is 2 or 3)
            {
                Assert.Equal(256, local.GetProperty("tile_size").GetInt32());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, local.ValueKind);
            }
        }
    }

    [Fact]
    public void AdvancedOptionsSerializeTogetherWithoutDroppingTheirParameters()
    {
        var options = new ImageStackOptions
        {
            RegistrationModel = StackRegistrationModel.Quadratic,
            Normalization = StackNormalizationMode.LocalBackground,
            LocalTileSize = 64,
            Weighting = StackWeightingMode.InverseNoiseVariance,
            MinimumWeight = 0.125,
            MaximumWeight = 8,
            Interpolation = StackInterpolation.Lanczos3,
            Demosaic = StackDemosaic.Mhc,
            CfaIntegration = StackCfaIntegration.BayerDrizzle,
            SuppressHotPixels = true,
            CosmeticLowSigma = 12,
            CosmeticHighSigma = 14,
        };
        using JsonDocument document = JsonDocument.Parse(options.ToJson());
        JsonElement root = document.RootElement;

        Assert.Equal("quadratic", root.GetProperty("registration").GetProperty("model").GetString());
        Assert.Equal("local-background", root.GetProperty("normalization").GetProperty("mode").GetString());
        Assert.Equal(64, root.GetProperty("normalization").GetProperty("options").GetProperty("tile_size").GetInt32());
        Assert.Equal("inverse-noise-variance", root.GetProperty("weighting").GetProperty("mode").GetString());
        Assert.Equal(0.125, root.GetProperty("weighting").GetProperty("minimum_weight").GetDouble());
        Assert.Equal(8, root.GetProperty("weighting").GetProperty("maximum_weight").GetDouble());
        Assert.Equal("lanczos3", root.GetProperty("interpolation").GetString());
        Assert.Equal("mhc", root.GetProperty("demosaic").GetString());
        Assert.Equal("bayer_drizzle", root.GetProperty("cfa_integration").GetString());
        Assert.Equal(12, root.GetProperty("cosmetic").GetProperty("low_sigma").GetDouble());
        Assert.Equal(14, root.GetProperty("cosmetic").GetProperty("high_sigma").GetDouble());
    }

    [Fact]
    public void InactiveNumericControlsDoNotValidateOrLeakIntoNativeJson()
    {
        var options = new ImageStackOptions
        {
            Normalization = StackNormalizationMode.None,
            LocalTileSize = -1,
            Rejection = StackRejectionMode.None,
            SigmaLow = double.NaN,
            SigmaHigh = double.PositiveInfinity,
            RejectionWarmup = -1,
            MinimumWeight = double.NaN,
            MaximumWeight = double.PositiveInfinity,
            CosmeticLowSigma = double.NaN,
            CosmeticHighSigma = double.NegativeInfinity,
        };

        Assert.Null(options.ValidationMessage);
        using JsonDocument document = JsonDocument.Parse(options.ToJson());
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("normalization").GetProperty("options").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rejection").GetProperty("options").ValueKind);
        Assert.False(root.TryGetProperty("weighting", out _));
        Assert.False(root.TryGetProperty("cosmetic", out _));
    }

    [Theory]
    [InlineData("normalization")]
    [InlineData("rejection")]
    [InlineData("registration")]
    [InlineData("weighting")]
    [InlineData("interpolation")]
    [InlineData("demosaic")]
    [InlineData("cfa_integration")]
    public void InvalidEnumsAreRejectedInsteadOfSilentlyUsingDefaults(string field)
    {
        var options = new ImageStackOptions();
        SetEnum(options, field, int.MaxValue);

        Assert.Equal("Choose a supported stacking option.", options.ValidationMessage);
        Assert.Throws<ArgumentException>(() => options.ToJson());
    }

    [Theory]
    [InlineData(2, 15, false)]
    [InlineData(3, 15, false)]
    [InlineData(2, 16, true)]
    [InlineData(3, 16, true)]
    public void BothLocalNormalizationModesEnforceTheTileMinimum(int mode, int tileSize, bool valid)
    {
        var options = new ImageStackOptions
        {
            Normalization = (StackNormalizationMode)mode,
            LocalTileSize = tileSize,
        };

        Assert.Equal(valid, options.ValidationMessage is null);
        if (!valid)
        {
            Assert.Throws<ArgumentException>(() => options.ToJson());
        }
    }

    [Theory]
    [InlineData("minimum", 0)]
    [InlineData("minimum", -0.1)]
    [InlineData("minimum", 1.001)]
    [InlineData("minimum", double.NaN)]
    [InlineData("minimum", double.PositiveInfinity)]
    [InlineData("minimum", double.Epsilon)]
    [InlineData("maximum", 0.999)]
    [InlineData("maximum", double.NaN)]
    [InlineData("maximum", double.PositiveInfinity)]
    [InlineData("maximum", double.MaxValue)]
    [InlineData("maximum", 1e40)]
    public void NoiseWeightingRejectsInvalidClamps(string bound, double value)
    {
        var options = new ImageStackOptions { Weighting = StackWeightingMode.InverseNoiseVariance };
        if (bound == "minimum")
        {
            options.MinimumWeight = value;
        }
        else
        {
            options.MaximumWeight = value;
        }

        Assert.Equal("Frame weights must be finite with 0 < minimum ≤ 1 ≤ maximum.", options.ValidationMessage);
        Assert.Throws<ArgumentException>(() => options.ToJson());
    }

    [Fact]
    public void NoiseWeightingAllowsUnityAndFiniteNativeFloatMaximum()
    {
        var options = new ImageStackOptions
        {
            Weighting = StackWeightingMode.InverseNoiseVariance,
            MinimumWeight = 1,
            MaximumWeight = 1,
        };

        Assert.Null(options.ValidationMessage);
        options.MaximumWeight = float.MaxValue;
        Assert.Null(options.ValidationMessage);
        Assert.NotEmpty(options.ToJson());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    [InlineData(1e40)]
    public void CosmeticCorrectionRejectsInvalidLowAndHighThresholds(double value)
    {
        var low = new ImageStackOptions { SuppressHotPixels = true, CosmeticLowSigma = value };
        var high = new ImageStackOptions { SuppressHotPixels = true, CosmeticHighSigma = value };

        Assert.Equal("Hot/dead pixel thresholds must be positive finite numbers.", low.ValidationMessage);
        Assert.Equal(low.ValidationMessage, high.ValidationMessage);
        Assert.Throws<ArgumentException>(() => low.ToJson());
        Assert.Throws<ArgumentException>(() => high.ToJson());
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1e40)]
    public void RejectionThresholdsMustRemainPositiveAndFiniteAsNativeFloats(double value)
    {
        var low = new ImageStackOptions { SigmaLow = value };
        var high = new ImageStackOptions { SigmaHigh = value };

        Assert.Equal("Sigma thresholds must be positive numbers.", low.ValidationMessage);
        Assert.Equal(low.ValidationMessage, high.ValidationMessage);
        Assert.Throws<ArgumentException>(() => low.ToJson());
        Assert.Throws<ArgumentException>(() => high.ToJson());
    }

    private static void SetEnum(ImageStackOptions options, string field, int value)
    {
        switch (field)
        {
            case "normalization": options.Normalization = (StackNormalizationMode)value; break;
            case "rejection": options.Rejection = (StackRejectionMode)value; break;
            case "registration": options.RegistrationModel = (StackRegistrationModel)value; break;
            case "weighting": options.Weighting = (StackWeightingMode)value; break;
            case "interpolation": options.Interpolation = (StackInterpolation)value; break;
            case "demosaic": options.Demosaic = (StackDemosaic)value; break;
            case "cfa_integration": options.CfaIntegration = (StackCfaIntegration)value; break;
            default: throw new ArgumentException("Unknown option field.", nameof(field));
        }
    }

    [Fact]
    public void CalibrationRejectsAFrameReusedAsAMaster()
    {
        string input = Path.GetFullPath(@"C:\lights\M101_Ha_001.fits");
        var calibration = new ImageStackCalibration { DarkPath = input };

        string? message = calibration.ValidationMessage([input, @"C:\lights\M101_Ha_002.fits"]);

        Assert.Equal("Each light frame and calibration master must be a different file.", message);
    }

    [Fact]
    public void CalibrationCopyIsIndependentAndPreservesSettings()
    {
        var original = new ImageStackCalibration
        {
            BiasPath = @"C:\calibration\bias.fits",
            DarkPath = @"C:\calibration\dark.fits",
            FlatPath = @"C:\calibration\flat.fits",
            OverridesDarkExposure = true,
            DarkExposureSeconds = 120,
        };

        ImageStackCalibration copy = original.Copy();
        copy.DarkPath = null;

        Assert.NotSame(original, copy);
        Assert.Equal(@"C:\calibration\dark.fits", original.DarkPath);
        Assert.Equal(original.BiasPath, copy.BiasPath);
        Assert.Equal(original.FlatPath, copy.FlatPath);
        Assert.Equal(original.OverridesDarkExposure, copy.OverridesDarkExposure);
        Assert.Equal(original.DarkExposureSeconds, copy.DarkExposureSeconds);
    }

    [Fact]
    public void BatchCancellationReportsCompletedOutputs()
    {
        string output = @"C:\stacks\M101-Ha.fits";

        var exception = new ImageStackBatchCanceledException([output], CancellationToken.None);

        Assert.Equal([output], exception.CompletedOutputPaths);
        Assert.Contains("Already saved: M101-Ha.fits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchFailureReportsCompletedOutputsAndPreservesCause()
    {
        var cause = new InvalidOperationException("OIII registration failed.");
        string output = @"C:\stacks\M101-Ha.fits";

        var exception = new ImageStackBatchFailureException(cause, [output]);

        Assert.Same(cause, exception.InnerException);
        Assert.Equal([output], exception.CompletedOutputPaths);
        Assert.Contains("Already saved: M101-Ha.fits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitOutputNamesStayUniqueWhenAFilterIsNamedOther()
    {
        ImageStackGroup[] groups =
        [
            new(
                "named:other",
                new ImageFilenameFilter("named:other", "Other", "Other"),
                [@"C:\lights\other-a.fits", @"C:\lights\other-b.fits"]),
            new(
                "other",
                null,
                [@"C:\lights\unknown-a.fits", @"C:\lights\unknown-b.fits"]),
        ];

        IReadOnlyDictionary<string, string> outputs = ImageStackOutputNaming.SplitOutputPaths(
            @"C:\stacks",
            "stacked",
            groups);

        Assert.Equal("stacked-Other.fits", Path.GetFileName(outputs["named:other"]));
        Assert.Equal("stacked-Other-2.fits", Path.GetFileName(outputs["other"]));
    }

    [Fact]
    public void BatchValidationRejectsDuplicateOutputPaths()
    {
        ImageStackJob[] jobs =
        [
            Job("ha", [@"C:\lights\ha-1.fits", @"C:\lights\ha-2.fits"], @"C:\stacks\same.fits"),
            Job("oiii", [@"C:\lights\o3-1.fits", @"C:\lights\o3-2.fits"], @"C:\stacks\same.fits"),
        ];

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ImageStackValidation.ValidateBatch(jobs));

        Assert.Contains("different output file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchValidationProtectsInputsFromEveryGroup()
    {
        string laterInput = @"C:\lights\o3-1.fits";
        ImageStackJob[] jobs =
        [
            Job("ha", [@"C:\lights\ha-1.fits", @"C:\lights\ha-2.fits"], laterInput),
            Job("oiii", [laterInput, @"C:\lights\o3-2.fits"], @"C:\stacks\oiii.fits"),
        ];

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ImageStackValidation.ValidateBatch(jobs));

        Assert.Contains("not input or calibration files", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicOutputFailurePreservesExistingDestination()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string destination = Path.Combine(directory, "stacked.fits");
            File.WriteAllText(destination, "existing");

            Assert.Throws<InvalidOperationException>(() => AtomicOutputFile.Write(
                destination,
                staging =>
                {
                    File.WriteAllText(staging, "partial");
                    throw new InvalidOperationException("write failed");
                },
                CancellationToken.None));

            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, ".seiza-stack-*.fits"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AtomicOutputCancellationPreservesExistingDestination()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string destination = Path.Combine(directory, "stacked.fits");
            File.WriteAllText(destination, "existing");
            using var cancellation = new CancellationTokenSource();

            Assert.Throws<OperationCanceledException>(() => AtomicOutputFile.Write(
                destination,
                staging =>
                {
                    File.WriteAllText(staging, "complete but unpublished");
                    cancellation.Cancel();
                },
                cancellation.Token));

            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, ".seiza-stack-*.fits"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AtomicOutputSuccessReplacesExistingDestination()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string destination = Path.Combine(directory, "stacked.fits");
            File.WriteAllText(destination, "existing");

            AtomicOutputFile.Write(
                destination,
                staging => File.WriteAllText(staging, "complete"),
                CancellationToken.None);

            Assert.Equal("complete", File.ReadAllText(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, ".seiza-stack-*.fits"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ImageStackJob Job(
        string id,
        IReadOnlyList<string> inputs,
        string output) => new(
            new ImageStackGroup(id, null, inputs),
            new ImageStackRequest(
                inputs,
                output,
                new ImageStackOptions(),
                new ImageStackCalibration()));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void TransientRemovalStaysOutOfTheNativeOptions()
    {
        var enabled = new ImageStackOptions { RemoveTransients = true };
        var disabled = new ImageStackOptions { RemoveTransients = false };

        string json = enabled.ToJson();

        Assert.Equal(json, disabled.ToJson());
        Assert.DoesNotContain("transient", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reintegrat", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(new ImageStackOptions().RemoveTransients);
    }

    [Fact]
    public void TransientRemovalUsesDeltaSigmaThresholdsOrTheNativeDefault()
    {
        var deltaSigma = new ImageStackOptions
        {
            Rejection = StackRejectionMode.DeltaSigma,
            SigmaLow = 2.5,
            SigmaHigh = 4,
        };
        var none = new ImageStackOptions
        {
            Rejection = StackRejectionMode.None,
            SigmaLow = 2.5,
            SigmaHigh = 4,
        };

        Assert.Equal(2.5f, deltaSigma.TransientLowSigma);
        Assert.Equal(4f, deltaSigma.TransientHighSigma);
        Assert.Equal(0f, none.TransientLowSigma);
        Assert.Equal(0f, none.TransientHighSigma);
    }

    [Theory]
    [InlineData(0, 0, 40, "Removing transients: pass 1 of 3, frame 1 of 40", 0.0)]
    [InlineData(0, 20, 40, "Removing transients: pass 1 of 3, frame 21 of 40", 1.0 / 6)]
    [InlineData(1, 2, 40, "Removing transients: pass 2 of 3, frame 3 of 40", 0.35)]
    [InlineData(1, 39, 40, "Removing transients: pass 2 of 3, frame 40 of 40", 79.0 / 120)]
    [InlineData(2, 0, 40, "Removing transients: pass 3 of 3, frame 1 of 40", 2.0 / 3)]
    [InlineData(2, 39, 40, "Removing transients: pass 3 of 3, frame 40 of 40", 119.0 / 120)]
    [InlineData(0, 0, 0, "Removing transients: pass 1 of 3", 0.0)]
    public void TransientRemovalProgressDescribesPassAndFrame(
        int pass,
        int index,
        int count,
        string message,
        double fraction)
    {
        var progress = new ImageStackReintegrationProgress(pass, index, count);

        Assert.Equal(message, ImageStackTransientRemoval.Message(progress));
        Assert.Equal(fraction, ImageStackTransientRemoval.Fraction(progress), 6);
    }

    [Fact]
    public void PhaseFractionOverridesFrameCountsInProgress()
    {
        var stacking = new ImageStackProgress(
            ImageStackProgressPhase.Stacking, "frame", 5, 10, 5, 0);
        ImageStackProgress removing = stacking with
        {
            Phase = ImageStackProgressPhase.RemovingTransients,
            CompletedFrames = 10,
            PhaseFraction = 0.25,
        };

        Assert.Equal(0.5, stacking.FractionCompleted, 6);
        Assert.Equal(0.25, removing.FractionCompleted, 6);
        Assert.Equal(0.0, (removing with { PhaseFraction = double.NaN }).FractionCompleted);
    }

    [Fact]
    public void TransientRemovalNoteKeepsTheNativeReason()
    {
        string note = ImageStackTransientRemoval.NotRemovedNote(
            " the checkpoint was saved by an older Seiza ");

        Assert.StartsWith("Transients were not removed", note, StringComparison.Ordinal);
        Assert.EndsWith("the checkpoint was saved by an older Seiza", note, StringComparison.Ordinal);
    }
}
