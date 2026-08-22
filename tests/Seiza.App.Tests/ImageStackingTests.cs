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
}
