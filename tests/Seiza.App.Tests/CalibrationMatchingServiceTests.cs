using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationMatchingServiceTests
{
    [Fact]
    public void NativeDefaultsAreExposedWithoutReimplementingThem()
    {
        CalibrationMatchTolerances defaults =
            CalibrationMatchingService.GetDefaultTolerances();

        Assert.Equal(0.05, defaults.ExposureSeconds);
        Assert.Equal(0.001, defaults.ExposureFraction);
        Assert.Equal(3, defaults.DarkTemperatureC);
        Assert.Equal(2, defaults.RotationDeg);
        Assert.Equal(86_400UL, defaults.FlatSessionSeconds);
    }

    [Fact]
    public void NativeSensorMatchRequiresPositiveIdentityAndMatchingReadout()
    {
        CalibrationFrameSignature light = Signature();

        Assert.True(CalibrationMatchingService.SensorMatches(light, light with { }));
        Assert.False(CalibrationMatchingService.SensorMatches(
            light,
            light with { ReadoutMode = 2 }));
        Assert.False(CalibrationMatchingService.SensorMatches(
            new CalibrationFrameSignature(),
            new CalibrationFrameSignature()));
    }

    [Fact]
    public void NativeOpticsAndDarkMatchesUseSeizaDefaults()
    {
        CalibrationFrameSignature light = Signature();

        Assert.True(CalibrationMatchingService.OpticsMatch(light, light with { }));
        Assert.True(CalibrationMatchingService.OpticsMatch(
            light,
            light with { RotationDeg = 121.23 }));
        Assert.False(CalibrationMatchingService.OpticsMatch(
            light,
            light with { RotationDeg = 122.5 }));
        Assert.False(CalibrationMatchingService.OpticsMatch(
            light,
            light with { Filter = "OIII" }));
        Assert.True(CalibrationMatchingService.DarkMatches(light, light with { }));
        Assert.False(CalibrationMatchingService.DarkMatches(
            light,
            light with { ExposureSeconds = 330 }));
        Assert.False(CalibrationMatchingService.DarkMatches(
            light,
            light with { CameraTempC = -5 }));
    }

    [Fact]
    public void NativeMismatchDescriptionsNameReadingsAndTolerance()
    {
        CalibrationFrameSignature light = Signature();

        string sensor = CalibrationMatchingService.DescribeSensorMismatch(
            light,
            light with { Gain = 200 });
        Assert.Contains("gain", sensor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100", sensor, StringComparison.Ordinal);
        Assert.Contains("200", sensor, StringComparison.Ordinal);

        string optics = CalibrationMatchingService.DescribeOpticsMismatch(
            light,
            light with { RotationDeg = 122.5 });
        Assert.Contains("rotation", optics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.50", optics, StringComparison.Ordinal);
        Assert.Contains("tolerance 2.00", optics, StringComparison.OrdinalIgnoreCase);
    }

    private static CalibrationFrameSignature Signature() => new()
    {
        Camera = "ASI2600MM",
        Telescope = "Askar107PHQ",
        Width = 6248,
        Height = 4176,
        Channels = 1,
        BinningX = 1,
        BinningY = 1,
        Gain = 100,
        Offset = 50,
        ReadoutMode = 1,
        Filter = "H-alpha",
        FocalLengthMm = 749,
        RotationDeg = 120,
        ExposureSeconds = 300,
        CameraTempC = -10,
    };

}
