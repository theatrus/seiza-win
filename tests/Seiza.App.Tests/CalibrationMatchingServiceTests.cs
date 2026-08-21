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
        Assert.Equal(1, defaults.RotationDeg);
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
