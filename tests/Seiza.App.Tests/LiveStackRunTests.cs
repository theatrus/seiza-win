using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackRunTests
{
    [Fact]
    public void HeaderFilterWinsAndKnownAliasesShareAnIdentity()
    {
        LiveStackFilterIdentity header = LiveStackFilterIdentity.FromProbe(
            Probe("target_R_001.fits", "  Luminance  "));
        LiveStackFilterIdentity alias = LiveStackFilterIdentity.FromProbe(
            Probe("target_002.fits", "L"));

        Assert.Equal(LiveStackFilterSource.Header, header.Source);
        Assert.Equal("Luminance", header.DisplayName);
        Assert.True(header.Matches(alias));
    }

    [Fact]
    public void FilenameIsUsedOnlyWhenHeaderFilterIsMissing()
    {
        LiveStackFilterIdentity fallback = LiveStackFilterIdentity.FromProbe(
            Probe("M101_Ha_001.fits", null));
        LiveStackFilterIdentity explicitHeader = LiveStackFilterIdentity.FromProbe(
            Probe("M101_Ha_002.fits", "Clear"));

        Assert.Equal(LiveStackFilterSource.Filename, fallback.Source);
        Assert.Equal("H-alpha", fallback.DisplayName);
        Assert.Equal(LiveStackFilterSource.Header, explicitHeader.Source);
        Assert.Equal("Clear", explicitHeader.DisplayName);
        Assert.False(fallback.Matches(explicitHeader));
    }

    [Fact]
    public void CalibrationIdentityRejectsChangedKnownFields()
    {
        var reference = new CalibrationFrameSignature
        {
            Camera = "ASI 2600MM",
            Width = 6248,
            Height = 4176,
            Channels = 1,
            BinningX = 1,
            BinningY = 1,
            Gain = 100,
            Offset = 50,
            ReadoutMode = 1,
            BayerPattern = "RGGB",
        };

        bool matches = LiveStackCalibrationIdentity.Matches(
            reference,
            reference with { Gain = 101 },
            out string? reason);

        Assert.False(matches);
        Assert.Contains("gain", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibrationIdentityRequiresCandidateValuesOnlyWhenReferenceKnowsThem()
    {
        var sparseReference = new CalibrationFrameSignature { Width = 1024 };
        var richCandidate = new CalibrationFrameSignature
        {
            Camera = "New camera",
            Width = 1024,
            Gain = 120,
        };

        Assert.True(LiveStackCalibrationIdentity.Matches(
            sparseReference,
            richCandidate,
            out string? reason));
        Assert.Null(reason);
        Assert.False(LiveStackCalibrationIdentity.Matches(
            richCandidate,
            sparseReference,
            out reason));
        Assert.Contains("camera", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(12, false)]
    public void SnrMeasurementsUsePowerOfTwoDepths(int frames, bool expected) =>
        Assert.Equal(expected, LiveStackRunMath.IsSnrCheckpointDepth(frames));

    [Fact]
    public void FinalSnrMeasurementIncludesANonPowerOfTwoDepthWithoutDuplicatingIt()
    {
        Assert.False(LiveStackRunMath.IsSnrMeasurementDue(5, [1, 2, 4]));
        Assert.True(LiveStackRunMath.IsSnrMeasurementDue(
            5,
            [1, 2, 4],
            includeCurrentDepth: true));
        Assert.False(LiveStackRunMath.IsSnrMeasurementDue(
            5,
            [1, 2, 4, 5],
            includeCurrentDepth: true));
    }

    [Fact]
    public void SnrPlotRecomputesEveryPointWithTheDeepestSignal()
    {
        LiveStackPersistedSnrSample[] samples =
        [
            Sample(frames: 1, noise: 4, signal: 8),
            Sample(frames: 2, noise: 2, signal: 12),
            Sample(frames: 4, noise: 1, signal: 20),
        ];

        IReadOnlyList<StackSnrPlotPoint> plot = LiveStackRunMath.CreateSnrPlot(samples);

        Assert.Equal([5d, 10d, 20d], plot.Select(point => point.Snr));
    }

    [Fact]
    public void ExposureIsWithheldWhenAnyAcceptedFrameIsUnknown()
    {
        LiveStackPersistedFrame[] frames =
        [
            Frame("one.fits", 60),
            Frame("two.fits", null),
            Frame("flat.fits", 10) with
            {
                Disposition = LiveStackPersistedFrameDisposition.Ignored,
            },
        ];

        Assert.Null(LiveStackRunMath.CumulativeExposure(frames));
        Assert.Equal(
            120,
            LiveStackRunMath.CumulativeExposure(
                [Frame("one.fits", 60), Frame("two.fits", 60)]));
    }

    [Fact]
    public void NewRunsResumeByDefaultAndCheckpointAgeNeverGoesNegative()
    {
        var configuration = new LiveStackRunConfiguration();
        DateTimeOffset saved = new(2026, 8, 19, 12, 0, 1, TimeSpan.Zero);
        var snapshot = new LiveStackRunSnapshot
        {
            ObservedAtUtc = saved.AddSeconds(-1),
            LastCheckpointAtUtc = saved,
        };

        Assert.True(configuration.ResumeExisting);
        Assert.Equal(TimeSpan.Zero, snapshot.CheckpointAge);
    }

    [Fact]
    public void DefaultPreviewUsesPhysicalLinearRobustPercentileDomainAndAutoMtf()
    {
        using JsonDocument document = JsonDocument.Parse(
            LiveStackRunConfiguration.DefaultPreviewProcessingJson);
        JsonElement root = document.RootElement;
        JsonElement sampleDomain = root.GetProperty("sample_domain");
        JsonElement normalization = sampleDomain.GetProperty("normalization");
        JsonElement stretch = root.GetProperty("stretch")[0];

        Assert.Equal("physical-linear", sampleDomain.GetProperty("type").GetString());
        Assert.Equal("robust-percentile", normalization.GetProperty("type").GetString());
        Assert.Equal(0.001, normalization.GetProperty("black_percentile").GetDouble());
        Assert.Equal(0.999, normalization.GetProperty("white_percentile").GetDouble());
        Assert.Equal(200_000, normalization.GetProperty("max_analysis_samples").GetInt32());
        JsonElement model = stretch.GetProperty("model");
        Assert.Equal("auto-mtf", model.GetProperty("type").GetString());
        Assert.Equal(0.2, model.GetProperty("target_median").GetDouble());
        Assert.Equal(-2.8, model.GetProperty("shadows_clip").GetDouble());
        Assert.Equal("unlinked", stretch.GetProperty("color_strategy").GetString());
        Assert.Equal(200_000, stretch.GetProperty("max_analysis_samples").GetInt32());
    }

    [Fact]
    public void ExtendedWindowsLedgerPathsMatchPersistedOrdinaryPaths()
    {
        string ordinary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "light-001.fits"));
        string extended = @"\\?\" + ordinary;
        LiveStackNativeState expected = NativeState(ordinary);
        LiveStackNativeState actual = NativeState(extended);

        Assert.True(LiveStackPath.Equals(ordinary, extended));
        Assert.True(expected.DescribesSameCheckpoint(actual));
    }

    [Fact]
    public void DirectoryContainmentHandlesExtendedPathsWithoutMatchingSiblingPrefixes()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "seiza-live"));
        string child = Path.Combine(root, "group", "context.bin");
        string extendedChild = @"\\?\" + child;
        string sibling = Path.Combine(root + "-old", "context.bin");

        Assert.True(LiveStackPath.IsWithinDirectory(root, root));
        Assert.True(LiveStackPath.IsWithinDirectory(extendedChild, root));
        Assert.False(LiveStackPath.IsWithinDirectory(sibling, root));
    }

    [Fact]
    public void NativeReferenceFrameStateDeserializesAsAuthoritativeMetadata()
    {
        string json = """
            {
              "schemaVersion": 1,
              "coreVersion": "0.18.0",
              "width": 1024,
              "height": 768,
              "channels": 1,
              "acceptedFrames": 2,
              "rejectedFrames": 0,
              "inputMode": "calibrate-and-prepare",
              "configurationFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "inputPaths": ["light-001.fits", "light-002.fits"],
              "referenceFrame": {
                "role": "light",
                "isMaster": false,
                "signature": {"camera":"ASI2600MM","width":1024,"height":768,"channels":1,"filter":"L"},
                "calibrationState": {"biasSubtracted":false,"darkSubtracted":false,"flatNormalized":false}
              }
            }
            """;

        LiveStackNativeState state = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.LiveStackNativeState)!;

        Assert.NotNull(state.ReferenceFrame);
        Assert.Equal(CalibrationFrameRoles.Light, state.ReferenceFrame.Role);
        Assert.Equal("ASI2600MM", state.ReferenceFrame.Signature.Camera);
        Assert.Equal("L", state.ReferenceFrame.Signature.Filter);
    }

    [Fact]
    public void AdditiveNativeReferenceMetadataDoesNotInvalidateAnOlderManifest()
    {
        LiveStackNativeState oldManifestState = NativeState("light-001.fits");
        LiveStackNativeState reopenedByNewCore = oldManifestState with
        {
            ReferenceFrame = Probe("light-001.fits", "L"),
        };

        Assert.True(oldManifestState.DescribesSameCheckpoint(reopenedByNewCore));
        Assert.False(reopenedByNewCore.DescribesSameCheckpoint(oldManifestState));
    }

    [Fact]
    public void AChangeDuringCheckpointPublicationKeepsTheSessionDirty()
    {
        Assert.False(LiveStackRunMath.CheckpointRemainsDirty(7, 7));
        Assert.True(LiveStackRunMath.CheckpointRemainsDirty(8, 7));
    }

    [Fact]
    public void ActiveMastersRequireAnUnprocessedNonMasterLight()
    {
        var calibration = new ImageStackCalibration { BiasPath = "master-bias.fits" };
        CalibrationFrameProbe raw = Probe("raw-light.fits", "L");

        Assert.True(LiveStackCalibrationSelection.HasAnyMasters(calibration));
        Assert.True(CalibrationLightEligibility.IsEligible(raw));
        Assert.False(CalibrationLightEligibility.IsEligible(raw with { IsMaster = true }));
        Assert.False(CalibrationLightEligibility.IsEligible(raw with
        {
            CalibrationState = new CalibrationFrameState { DarkSubtracted = true },
        }));
    }

    [Fact]
    public void CalibrationSelectionNormalizesPathsAndComparesDarkExposureOverrides()
    {
        string root = Path.Combine(Path.GetTempPath(), "seiza-calibration-selection");
        string dark = Path.Combine(root, "master-dark.fits");
        var first = new ImageStackCalibration
        {
            DarkPath = dark,
            OverridesDarkExposure = true,
            DarkExposureSeconds = 600,
        };
        var equivalent = new ImageStackCalibration
        {
            DarkPath = Path.Combine(root, ".", "master-dark.fits"),
            OverridesDarkExposure = true,
            DarkExposureSeconds = 600,
        };

        Assert.True(LiveStackCalibrationSelection.AreEquivalent(first, equivalent));
        Assert.False(LiveStackCalibrationSelection.AreEquivalent(
            first,
            new ImageStackCalibration
            {
                DarkPath = dark,
                OverridesDarkExposure = true,
                DarkExposureSeconds = 300,
            }));
        Assert.False(LiveStackCalibrationSelection.AreEquivalent(
            first,
            new ImageStackCalibration()));
    }

    [Fact]
    public void AttentionPresentationOmitsBlankItemsAndKeepsRecentActionableMessages()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LiveStackAttention[] attention =
        [
            new("old", null, now),
            new(" ", @"C:\captures\blank.fits", now),
            new("new", @"C:\captures\new.fits", now),
        ];

        string[] messages = LiveStackAttentionPresentation.RecentMessages(
            attention,
            maximumItems: 1);

        Assert.Equal(["new.fits — new"], messages);
        Assert.Empty(LiveStackAttentionPresentation.RecentMessages(
            [new LiveStackAttention(string.Empty, null, now)]));
    }

    private static CalibrationFrameProbe Probe(string path, string? filter) => new()
    {
        Path = path,
        Role = CalibrationFrameRoles.Light,
        Signature = new CalibrationFrameSignature { Filter = filter },
    };

    private static LiveStackPersistedSnrSample Sample(
        int frames,
        double noise,
        double signal) => new()
        {
            AcceptedFrames = frames,
            Noise = noise,
            Signal = signal,
            ChannelNoise = [noise],
        };

    private static LiveStackPersistedFrame Frame(string path, double? exposure) => new()
    {
        Path = path,
        Disposition = LiveStackPersistedFrameDisposition.Accepted,
        ExposureSeconds = exposure,
    };

    private static LiveStackNativeState NativeState(string path) => new()
    {
        CoreVersion = "0.17.0",
        Width = 1024,
        Height = 768,
        Channels = 1,
        AcceptedFrames = 1,
        InputMode = "calibrate-and-prepare",
        ConfigurationFingerprint = new string('a', 64),
        InputPaths = [path],
    };
}
