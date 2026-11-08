using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class FlatInspectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaFlatInspection-").FullName;
    private string Flat(string name, string kind = "FLAT", bool processed = false)
    {
        string path = Path.Combine(_root, name + ".fits");
        CalibrationParityFixtures.WriteFrame(path, 256, 256, (x, y) => (ushort)(
            (Math.Pow(x - 128, 2) + Math.Pow(y - 128, 2) < 1000 ? 24000 : 30000) + (x + y) % 7 - 3),
            kind, normalizedFlat: processed);
        return path;
    }
    [Fact]
    public async Task InspectionMatchesNativeScreenAndKeepsLinearSourceAndDustGeometry()
    {
        string flat = Flat("raw"); byte[] before = File.ReadAllBytes(flat);
        var options = new FlatQualityOptions { Enabled = true, UseOptics = true, FocalRatio = 5, PixelSizeUm = 3.76 };
        FlatInspection result = await FlatInspectionService.InspectAsync(flat, options);
        FlatQualityReport screen = FlatQualityService.ScreenFrames([result.Measurement]);
        Assert.Equal(screen.Frames[0].Grade, result.Verdict!.Grade);
        Assert.Equal(screen.Frames[0].Findings, result.Verdict.Findings);
        Assert.Contains(result.Measurement.Features, f => f.Radius > 0 && f.DistanceMm > 0);
        Assert.Equal(result.Measurement.Illumination.Columns * result.Measurement.Illumination.Rows, result.Measurement.Illumination.Response.Length);
        FlatInspection clipped = await FlatInspectionService.InspectAsync(flat, options with { FullScale = 20000 });
        Assert.True(clipped.Measurement.ClippedFraction > result.Measurement.ClippedFraction);
        Assert.Equal(before, File.ReadAllBytes(flat));
    }
    [Theory]
    [InlineData("MASTER FLAT", false)]
    [InlineData("FLAT", true)]
    public async Task ProcessedAndMasterFlatsHaveMeasurementsWithoutRawExposureGrade(string kind, bool processed)
    {
        FlatInspection result = await FlatInspectionService.InspectAsync(Flat("processed", kind, processed), new());
        Assert.Null(result.Verdict); Assert.True(result.Measurement.Level > 0);
    }
    [Fact]
    public async Task InvalidInputsAndGeometryAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FlatInspectionService.InspectAsync(Flat("light", "LIGHT"), new()));
        await Assert.ThrowsAsync<ArgumentException>(() => FlatInspectionService.InspectAsync(Flat("flat"), new() { FullScale = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => FlatInspectionService.InspectAsync(Flat("flat"), new() { UseOptics = true, FocalRatio = -1 }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlatInspectionService.InspectAsync(Flat("cancel"), new(), new(true)));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
