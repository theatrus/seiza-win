using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationFramesTests
{
    [Fact]
    public void MasterBuildRequestUsesTheCamelCaseCabiContract()
    {
        var request = new CalibrationMasterBuildRequest
        {
            Kind = "flat",
            Inputs = [@"C:\cal\flat-1.fits", @"C:\cal\flat-2.fits"],
            Output = @"C:\masters\flat.fits",
            Bias = @"C:\masters\bias.fits",
            Dark = @"C:\masters\dark-flat.fits",
            DefectSuppression = new(),
        };

        string json = JsonSerializer.Serialize(
            request,
            SeizaJsonSerializerContext.Default.CalibrationMasterBuildRequest);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement root = document.RootElement;
        Assert.Equal("flat", root.GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("inputs").GetArrayLength());
        Assert.Equal(3, root.GetProperty("rejection").GetProperty("lowSigma").GetDouble());
        Assert.Equal(
            16,
            root.GetProperty("defectSuppression").GetProperty("highSigma").GetDouble());
        Assert.False(root.TryGetProperty("Kind", out _));
    }

    [Fact]
    public void ProbeDeserializesHeaderSignatureAndCalibrationState()
    {
        const string Json = """
            {
              "schemaVersion": 1,
              "path": "C:\\lights\\M101_L_001.fits",
              "format": "FITS",
              "role": "light",
              "rawImageType": "LIGHT",
              "isMaster": false,
              "signature": {
                "camera": "ASI2600MM",
                "width": 6248,
                "height": 4176,
                "filter": "L",
                "exposureSeconds": 300.0
              },
              "calibrationState": {
                "biasSubtracted": false,
                "darkSubtracted": false,
                "flatNormalized": false
              }
            }
            """;

        CalibrationFrameProbe? probe = JsonSerializer.Deserialize(
            Json,
            SeizaJsonSerializerContext.Default.CalibrationFrameProbe);

        Assert.NotNull(probe);
        Assert.Equal(CalibrationFrameRoles.Light, probe.Role);
        Assert.Equal("L", probe.Signature.Filter);
        Assert.Equal(300, probe.Signature.ExposureSeconds);
        Assert.False(probe.CalibrationState.DarkSubtracted);
    }

    [Fact]
    public void PlanRequestSerializesMultiTargetDependenciesForTheCabi()
    {
        var reference = new CalibrationPlanRecord(
            @"C:\lights\L-300.fits",
            CalibrationFrameRoles.Light,
            new CalibrationFrameSignature { ExposureSeconds = 300 });
        var request = new CalibrationPlanRequest(
            CalibrationFrameRoles.Dark,
            reference,
            [],
            2,
            new CalibrationPlanTolerances())
        {
            References =
            [
                reference,
                new CalibrationPlanRecord(
                    @"C:\lights\L-120.fits",
                    CalibrationFrameRoles.Light,
                    new CalibrationFrameSignature { ExposureSeconds = 120 }),
            ],
            Dependencies = new CalibrationPlanDependencies { BiasAvailable = true },
        };

        string json = JsonSerializer.Serialize(
            request,
            SeizaJsonSerializerContext.Default.CalibrationPlanRequest);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("references").GetArrayLength());
        Assert.True(root.GetProperty("dependencies").GetProperty("biasAvailable").GetBoolean());
        Assert.False(root.TryGetProperty("References", out _));
    }

    [Fact]
    public void LightEligibilityRejectsMastersAndEveryPreprocessedState()
    {
        CalibrationFrameProbe raw = new()
        {
            Path = @"C:\lights\raw.fits",
            Role = CalibrationFrameRoles.Light,
        };
        CalibrationFrameProbe[] ineligible =
        [
            raw with { IsMaster = true },
            raw with
            {
                CalibrationState = new CalibrationFrameState { BiasSubtracted = true },
            },
            raw with
            {
                CalibrationState = new CalibrationFrameState { DarkSubtracted = true },
            },
            raw with
            {
                CalibrationState = new CalibrationFrameState { FlatNormalized = true },
            },
            raw with { Role = CalibrationFrameRoles.Dark },
        ];

        Assert.True(CalibrationLightEligibility.IsEligible(raw));
        Assert.All(ineligible, probe =>
        {
            Assert.False(CalibrationLightEligibility.IsEligible(probe));
            Assert.NotNull(CalibrationLightEligibility.GetIneligibilityReason(probe));
            Assert.Throws<ArgumentException>(
                () => CalibrationLightEligibility.Validate(probe, nameof(probe)));
        });
    }
}
