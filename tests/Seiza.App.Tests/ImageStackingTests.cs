using System.Text.Json;
using Seiza.App.Models;
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
}
