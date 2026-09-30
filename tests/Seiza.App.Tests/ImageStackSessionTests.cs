using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageStackSessionTests
{
    [Fact]
    public void LivePreviewMetadataAcceptsFloatingPointStatistics()
    {
        const string json = """
            {
              "width": 6248,
              "height": 4176,
              "planes": 1,
              "format": "Live stack",
              "colorKind": "mono",
              "statistics": {
                "minimum": 83.25,
                "maximum": 65512.75,
                "mean": 1024.5,
                "median": 948.125,
                "mad": 37.875,
                "sampleCount": 262144,
                "scale": null,
                "normalized": null
              },
              "headers": {},
              "liveStack": {
                "schemaVersion": 1,
                "acceptedFrames": 1,
                "rejectedFrames": 0,
                "inputMode": "calibrate-and-prepare"
              }
            }
            """;

        ImageMetadata metadata = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.ImageMetadata)!;

        Assert.Equal("Live stack", metadata.Format);
        Assert.Equal(83.25, metadata.Statistics.Minimum);
        Assert.Equal(65512.75, metadata.Statistics.Maximum);
        Assert.Equal(948.125, metadata.Statistics.Median);
    }

    [Fact]
    public void RelativeSnrUsesOneCommonSignalAcrossDepths()
    {
        var sample = new ImageStackSnrSample(
            Frames: 8,
            Noise: 2,
            Background: 100,
            Signal: 10,
            Snr: 5,
            ChannelNoise: [2]);

        Assert.Equal(10, sample.RelativeSnr(commonSignal: 20));
        Assert.NotEqual(sample.Snr, sample.RelativeSnr(commonSignal: 20));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void RelativeSnrRejectsInvalidCommonSignal(double commonSignal)
    {
        var sample = new ImageStackSnrSample(1, 2, 0, 0, 0, [2]);

        Assert.Equal(0, sample.RelativeSnr(commonSignal));
    }

    [Fact]
    public void PipelineResponsePreservesPerFrameFailuresAndTallies()
    {
        const string json = """
            {
              "frames": [
                { "source": "a.fits", "accepted": true, "reason": null },
                { "source": "b.fits", "accepted": false, "reason": "unreadable" }
              ],
              "integrated": 1,
              "rejected": 0,
              "failed": 1
            }
            """;

        ImageStackPipelineResult result = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.ImageStackPipelineResult)!;

        Assert.Equal(1, result.Integrated);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(1, result.Failed);
        Assert.Collection(
            result.Frames,
            frame => Assert.True(frame.Accepted),
            frame =>
            {
                Assert.False(frame.Accepted);
                Assert.Equal("unreadable", frame.Reason);
            });
    }

    [Fact]
    public void NativeStatePreservesFingerprintAndOrderedLedger()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "coreVersion": "0.17.0",
              "configurationFingerprint": "0123456789abcdef",
              "width": 6248,
              "height": 4176,
              "channels": 1,
              "acceptedFrames": 3,
              "rejectedFrames": 1,
              "inputMode": "calibrate-and-prepare",
              "inputPaths": ["reference.fits", "second.fits", "third.fits"]
            }
            """;

        LiveStackNativeState state = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.LiveStackNativeState)!;

        Assert.Equal("0.17.0", state.CoreVersion);
        Assert.Equal("0123456789abcdef", state.ConfigurationFingerprint);
        Assert.Equal(
            ["reference.fits", "second.fits", "third.fits"],
            state.InputPaths);
        Assert.Equal(3, state.AcceptedFrames);
        Assert.Equal(1, state.RejectedFrames);
    }

    [Fact]
    public void NativeStateReportsWhyReintegrationIsUnavailable()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "coreVersion": "0.18.18",
              "configurationFingerprint": "0123456789abcdef",
              "width": 6248,
              "height": 4176,
              "channels": 1,
              "acceptedFrames": 3,
              "rejectedFrames": 0,
              "inputMode": "calibrate-and-prepare",
              "inputPaths": ["reference.fits", "second.fits", "third.fits"],
              "reintegrationUnavailable": "the checkpoint was saved by an older Seiza"
            }
            """;

        LiveStackNativeState state = JsonSerializer.Deserialize(
            json,
            SeizaJsonSerializerContext.Default.LiveStackNativeState)!;

        Assert.Equal(
            "the checkpoint was saved by an older Seiza",
            state.ReintegrationUnavailable);
    }

    [Fact]
    public void NativeStateTreatsMissingOrNullReintegrationReasonAsReplayable()
    {
        const string withNull = """
            {"schemaVersion": 1, "acceptedFrames": 2, "reintegrationUnavailable": null}
            """;
        const string without = """
            {"schemaVersion": 1, "acceptedFrames": 2}
            """;

        LiveStackNativeState explicitNull = JsonSerializer.Deserialize(
            withNull,
            SeizaJsonSerializerContext.Default.LiveStackNativeState)!;
        LiveStackNativeState missing = JsonSerializer.Deserialize(
            without,
            SeizaJsonSerializerContext.Default.LiveStackNativeState)!;

        Assert.Null(explicitNull.ReintegrationUnavailable);
        Assert.Null(missing.ReintegrationUnavailable);
    }

    [Fact]
    public void ReintegrationReasonDoesNotChangeCheckpointIdentity()
    {
        var replayable = new LiveStackNativeState
        {
            CoreVersion = "0.18.18",
            Width = 100,
            Height = 80,
            Channels = 1,
            AcceptedFrames = 4,
            InputMode = "calibrate-and-prepare",
            ConfigurationFingerprint = new string('a', 64),
            InputPaths = ["a.fits", "b.fits", "c.fits", "d.fits"],
        };
        LiveStackNativeState unavailable = replayable with
        {
            ReintegrationUnavailable = "frames were supplied as pixels",
        };

        Assert.True(replayable.DescribesSameCheckpoint(unavailable));
        Assert.True(unavailable.DescribesSameCheckpoint(replayable));
    }
}
