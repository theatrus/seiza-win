using System.Text.Json;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxCatalogStatusPresentationTests
{
    [Fact]
    public void ReadyCataloguesRetainCoverageWarningAndSetupDestination()
    {
        var result = Format(new { available = true, starCount = 123, maxMagnitude = 17 }, new { available = true }, 18);

        Assert.Equal("C:/catalogues", result.Directory);
        Assert.Contains("123 stellar distances", result.Description);
        Assert.Contains("object distances ready", result.Description);
        Assert.Contains("Selected Gaia depth exceeds offline coverage", result.Description);
    }

    [Theory]
    [InlineData("SEIZA_STAR_DISTANCES", "stars")]
    [InlineData("SEIZA_OBJECT_DISTANCES", "objects")]
    public void MissingPinnedPathShowsResolutionErrorAndDoesNotSuggestDownloadRepair(string variable, string component)
    {
        var unavailable = new
        {
            available = false,
            path = "P:/missing/distances.bin",
            resolutionError = $"{variable} is set but no distance file was found there",
            overrideVariable = variable,
        };
        var result = component == "stars" ? Format(unavailable, Missing) : Format(Missing, unavailable);

        Assert.Contains("P:/missing/distances.bin", result.Description);
        Assert.Contains("no distance file was found there", result.Description);
        Assert.Contains($"Check or clear {variable}", result.Description);
        Assert.Contains("does not change this override", result.Description);
        Assert.DoesNotContain("Download Offline Distances", result.Description);
    }

    [Fact]
    public void CorruptPinnedFileAlsoRequiresCorrectingItsOverride()
    {
        var result = Format(Missing, new
        {
            available = false,
            path = "P:/pinned/object-distances.bin",
            error = "Distance file was built for another object catalogue",
            overrideVariable = "SEIZA_OBJECT_DISTANCES",
        });

        Assert.Contains("P:/pinned/object-distances.bin", result.Description);
        Assert.Contains("built for another object catalogue", result.Description);
        Assert.Contains("Check or clear SEIZA_OBJECT_DISTANCES", result.Description);
        Assert.DoesNotContain("Download Offline Distances", result.Description);
    }

    [Fact]
    public void MissingOrMismatchedCounterpartExplainsWhyMatchingFilesAreRequired()
    {
        var result = Format(Missing, new
        {
            available = false,
            path = "C:/catalogues/object-distances.bin",
            error = "Object catalogue C:/catalogues/objects.bin could not be read",
        });

        Assert.Contains("objects.bin could not be read", result.Description);
        Assert.Contains("Download Offline Distances to install matching catalogue files", result.Description);
        Assert.DoesNotContain("object distances ready", result.Description);
        Assert.DoesNotContain("corrupt", result.Description);
    }

    [Fact]
    public void OrdinaryMissingFilesAreNotMisidentifiedAsPinnedOrCorrupt()
    {
        string description = Format(Missing, Missing).Description;

        Assert.Contains("stellar distances not installed", description);
        Assert.Contains("object distances not installed", description);
        Assert.DoesNotContain("override", description);
        Assert.DoesNotContain("corrupt", description);
    }

    private static object Missing => new { available = false, path = "C:/catalogues/missing.bin" };

    private static (string Directory, string Description) Format(object stars, object objects, double magnitude = 17)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            directory = "C:/catalogues",
            stars,
            objects,
        }));
        return ParallaxCatalogStatusPresentation.Format(document.RootElement, magnitude);
    }
}
