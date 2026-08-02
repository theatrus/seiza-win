using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class FitsBackgroundConfigurationTests
{
    [Fact]
    public void AutomaticModelRoundTripsThroughClipboardJson()
    {
        var background = new FitsBackgroundConfiguration
        {
            Mode = FitsBackgroundCorrectionMode.Subtract,
            Strength = 0.65,
            ModelType = FitsBackgroundModelType.Automatic,
            AutomaticMaxDegree = 4,
            Ridge = 0.0003,
            RbfSmoothing = 0.08,
            MaxControlPoints = 256,
            AllowRadialBasisInAutomatic = true,
            MinimumImprovement = 0.2,
        };
        var processing = new FitsImageProcessingConfiguration(
            FitsStretchStack.Default,
            background);

        FitsImageProcessingConfiguration decoded =
            FitsImageProcessingConfiguration.FromClipboardJson(processing.ToClipboardJson());

        Assert.Equal(processing, decoded);
        using JsonDocument document = JsonDocument.Parse(decoded.ToJson());
        JsonElement encoded = document.RootElement
            .GetProperty("background")
            .GetProperty("config")
            .GetProperty("model");
        Assert.Equal("automatic", encoded.GetProperty("kind").GetString());
        Assert.True(encoded.GetProperty("allow_radial_basis").GetBoolean());
        Assert.Equal(0.2, encoded.GetProperty("minimum_improvement").GetDouble());
    }

    [Fact]
    public void RadialBasisDivideModelRoundTripsThroughClipboardJson()
    {
        var background = new FitsBackgroundConfiguration
        {
            Mode = FitsBackgroundCorrectionMode.Divide,
            Strength = 0.4,
            ModelType = FitsBackgroundModelType.RadialBasis,
            RbfSmoothing = 0.15,
            MaxControlPoints = 384,
        };
        var processing = new FitsImageProcessingConfiguration(
            FitsStretchStack.Default,
            background);

        FitsImageProcessingConfiguration decoded =
            FitsImageProcessingConfiguration.FromClipboardJson(processing.ToClipboardJson());

        Assert.Equal(processing, decoded);
        Assert.Equal(FitsBackgroundCorrectionMode.Divide, decoded.BackgroundConfiguration?.Mode);
        Assert.Equal(FitsBackgroundModelType.RadialBasis, decoded.BackgroundConfiguration?.ModelType);
    }

    [Fact]
    public void LegacyBackgroundJsonUsesTheHistoricalPolynomialModel()
    {
        const string json = """
            {
              "stretch": [
                {
                  "model": { "type": "auto-mtf", "target_median": 0.2, "shadows_clip": -2.8 },
                  "color_strategy": "unlinked",
                  "max_analysis_samples": 200000
                }
              ],
              "background": { "mode": "subtract" }
            }
            """;

        FitsImageProcessingConfiguration decoded =
            FitsImageProcessingConfiguration.FromClipboardJson(json);

        FitsBackgroundConfiguration background = Assert.IsType<FitsBackgroundConfiguration>(
            decoded.BackgroundConfiguration);
        Assert.Equal(FitsBackgroundModelType.Polynomial, background.ModelType);
        Assert.Equal(2, background.PolynomialDegree);
        Assert.Equal(1.0e-8, background.Ridge);
        Assert.Equal(1.0, background.Strength);
    }

    [Fact]
    public void InvalidBackgroundAmountIsRejected()
    {
        var processing = new FitsImageProcessingConfiguration(
            FitsStretchStack.Default,
            new FitsBackgroundConfiguration { Strength = 1.01 });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(processing.ToJson);

        Assert.Contains("between 0 and 1", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)FitsBackgroundModelType.Automatic)]
    [InlineData((int)FitsBackgroundModelType.Polynomial)]
    public void ModelsThatUseRidgeRejectNegativeValues(int modelValue)
    {
        var background = new FitsBackgroundConfiguration
        {
            ModelType = (FitsBackgroundModelType)modelValue,
            Ridge = -1,
        };

        Assert.Equal(
            "Background ridge must be a non-negative number.",
            background.ValidationMessage);
    }

    [Fact]
    public void RadialBasisIgnoresUnusedRidge()
    {
        var background = new FitsBackgroundConfiguration
        {
            ModelType = FitsBackgroundModelType.RadialBasis,
            Ridge = -1,
        };

        Assert.Null(background.ValidationMessage);
    }

    [Fact]
    public void HistoryTracksBackgroundOnlyChanges()
    {
        var history = new FitsImageProcessingHistory();
        var changed = new FitsImageProcessingConfiguration(
            FitsStretchStack.Default,
            new FitsBackgroundConfiguration
            {
                Mode = FitsBackgroundCorrectionMode.Divide,
                Strength = 0.5,
            });

        Assert.True(history.Replace(changed));
        Assert.Equal(changed, history.Current);
        Assert.True(history.Undo());
        Assert.Null(history.Current.BackgroundConfiguration);
        Assert.True(history.Redo());
        Assert.Equal(changed, history.Current);
    }
}
