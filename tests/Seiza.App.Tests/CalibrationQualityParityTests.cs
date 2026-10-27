using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationQualityParityTests
{
    [Theory]
    [InlineData("different-gains")]
    [InlineData("dark-leak")]
    public void PublishedNativeScreeningMatchesCapturedPsfGuardPeers(string name)
    {
        Assert.True(CalibrationCoreCapabilities.Current.SupportsBiasDarkQuality);
        using var fixture = CalibrationParityFixtures.Read("psf-guard.json");
        Assert.Equal("79f5e31d53b8371e8fcd0b22a9b69692412cd8b4", fixture.RootElement.GetProperty("psfGuardRevision").GetString());
        Assert.Equal("0.8.1", fixture.RootElement.GetProperty("calibrationVersion").GetString());
        var testCase = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        // Explicit fixture peers only. Production peer selection belongs in the shared planner.
        foreach (var peers in testCase.GetProperty("frames").EnumerateArray().GroupBy(frame =>
            (Kind: frame.GetProperty("kind").GetString(), Gain: frame.GetProperty("gain").GetInt32())))
        {
            JsonElement[] frames = peers.ToArray();
            DarkQualityMeasurement[] measurements = frames.Select(frame => frame.GetProperty("measurement")
                .Deserialize(CalibrationQualityJsonContext.Default.DarkQualityMeasurement)!).ToArray();
            var report = peers.Key.Kind == "bias" ? CalibrationQualityService.ScreenBiasFrames(measurements)
                : CalibrationQualityService.ScreenDarkFrames(measurements);
            Assert.Equal(frames.Select(frame => frame.GetProperty("grade").GetString()), report.Frames.Select(frame => frame.Grade));
            for (int i = 0; i < frames.Length; i++)
            {
                string? code = frames[i].GetProperty("gradeCode").GetString();
                if (code is null) Assert.Empty(report.Frames[i].Findings);
                else Assert.Contains(report.Frames[i].Findings, finding => finding.Code == code);
                var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(measurements[i],
                    CalibrationQualityJsonContext.Default.DarkQualityMeasurement), CalibrationQualityJsonContext.Default.DarkQualityMeasurement);
                Assert.Equal(measurements[i], restored);
            }
        }
    }

    [Fact]
    public void ExistingFlatScreeningMatchesTheSameCapturedReference()
    {
        using var fixture = CalibrationParityFixtures.Read("psf-guard.json");
        var flat = fixture.RootElement.GetProperty("flatQuality");
        var measurements = flat.GetProperty("measurements").EnumerateArray().Select(item =>
            item.Deserialize(FlatQualityJsonContext.Default.FlatQualityMeasurement)!).ToArray();
        var report = FlatQualityService.ScreenFrames(measurements);
        Assert.Equal(flat.GetProperty("grades").EnumerateArray().Select(item => item.GetString()), report.Frames.Select(frame => frame.Grade));
        JsonElement[] codes = flat.GetProperty("gradeCodes").EnumerateArray().ToArray();
        for (int i = 0; i < codes.Length; i++)
            if (codes[i].GetString() is { } code) Assert.Contains(report.Frames[i].Findings, finding => finding.Code == code);
    }

    [Fact]
    public void MeasurementRetainsPhysicalAduAndCanBeRescreenedAfterRemovingItsFile()
    {
        string root = Directory.CreateTempSubdirectory("SeizaQualityParity-").FullName;
        string path = Path.Combine(root, "bias.fits");
        try
        {
            CalibrationParityFixtures.WriteFrame(path, 64, 64, (x, y) => checked((ushort)(500 + (y * 64 + x) % 7 - 3)), "BIAS");
            var measurement = CalibrationQualityService.MeasureFrame(new(path));
            Assert.Equal(64, measurement.Width);
            Assert.Equal(64, measurement.Height);
            Assert.InRange(measurement.Level, 499, 501);
            Assert.True(measurement.Noise > 0);
            Assert.True(measurement.AlgorithmVersion > 0);
            File.Delete(path);
            Assert.All(CalibrationQualityService.ScreenBiasFrames([measurement, measurement]).Frames, frame => Assert.Equal("ok", frame.Grade));
            Assert.Throws<SeizaCoreException>(() => CalibrationQualityService.MeasureFrame(new(path)));
            Assert.Empty(CalibrationQualityService.ScreenDarkFrames([]).Frames);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void DefaultsStayInTheCoreAndInvalidSigmaIsRejected()
    {
        string root = Directory.CreateTempSubdirectory("SeizaQualitySigma-").FullName;
        string path = Path.Combine(root, "bias.fits");
        try
        {
            string json = JsonSerializer.Serialize(new DarkQualityMeasureRequest(path), CalibrationQualityJsonContext.Default.DarkQualityMeasureRequest);
            Assert.DoesNotContain("hotSigma", json);
            CalibrationParityFixtures.WriteFrame(path, 8, 8, (_, _) => 500, "BIAS");
            Assert.Throws<SeizaCoreException>(() => CalibrationQualityService.MeasureFrame(new(path, 0)));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void StoredMeasurementsRetainUnknownNativeFieldsAndWideHotCounts()
    {
        const string json = """
            {"algorithmVersion":42,"width":64,"height":64,"level":500,"sigma":3,"noise":2,
             "rowBanding":1,"columnBanding":2,"hotPixels":4294967297,"hotThreshold":520,"futureEvidence":{"basis":"physical_adu"}}
            """;
        var value = JsonSerializer.Deserialize(json, CalibrationQualityJsonContext.Default.DarkQualityMeasurement)!;
        Assert.Equal(4294967297UL, value.HotPixels);
        string restored = JsonSerializer.Serialize(value, CalibrationQualityJsonContext.Default.DarkQualityMeasurement);
        using var original = JsonDocument.Parse(json);
        using var roundtrip = JsonDocument.Parse(restored);
        Assert.True(JsonElement.DeepEquals(original.RootElement, roundtrip.RootElement));
    }
}
