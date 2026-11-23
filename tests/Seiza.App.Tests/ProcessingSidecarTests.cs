using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ProcessingSidecarTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "seiza-sidecar-tests-" + Guid.NewGuid().ToString("N"));

    public ProcessingSidecarTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ReopenRestoresAllStepsWithoutChangingSourceOrSavingPreviewState()
    {
        string image = Path.Combine(_directory, "nebula.xisf");
        await File.WriteAllTextAsync(image, "source image bytes");
        var recipe = new FitsImageProcessingConfiguration(
            new FitsStretchStack([
                new FitsStretchConfiguration { Type = FitsStretchType.AutoMtf, TargetMedian = 0.3, ColorStrategy = FitsStretchColorStrategy.Linked },
                new FitsStretchConfiguration { Type = FitsStretchType.AutoMtf, TargetMedian = 0.4 },
            ]),
            new FitsBackgroundConfiguration { Strength = 0.65 },
            new FitsDeconvolutionConfiguration { Amount = 0.4 },
            interactivePreview: true,
            ai: new AiProcessing { Model = "noise-v1-d33", High = 0.4, Low = 0.7, Backend = "directml", DeviceId = 1, Tile = 512, Precision = "f16", Scale = 8 })
        { PreserveDisplaySamples = true };

        await ProcessingSidecarService.SaveAsync(image, recipe);
        Assert.Equal(recipe.Clone(false), await ProcessingSidecarService.ReadAsync(image));
        Assert.Equal("source image bytes", await File.ReadAllTextAsync(image));
        string json = await File.ReadAllTextAsync(image + ".seiza.json");
        Assert.Contains("org.seiza.image-processing", json, StringComparison.Ordinal);
        Assert.DoesNotContain("interactive_preview", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, json, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_directory, "*.json"));
    }

    [Fact]
    public async Task MissingSidecarDoesNotCreateOneAndDifferentImageExtensionsStaySeparate()
    {
        string fits = Path.Combine(_directory, "nebula.fits");
        string xisf = Path.Combine(_directory, "nebula.xisf");
        Assert.Null(await ProcessingSidecarService.ReadAsync(fits));
        Assert.Empty(Directory.GetFiles(_directory));
        await ProcessingSidecarService.SaveAsync(fits, FitsImageProcessingConfiguration.Default);
        Assert.Null(await ProcessingSidecarService.ReadAsync(xisf));
        Assert.NotEqual(ProcessingSidecarService.PathFor(fits), ProcessingSidecarService.PathFor(xisf));
    }

    [Fact]
    public async Task DisablingAiAndUndoingArePersistedAsCompleteReplacementRecipes()
    {
        string image = Path.Combine(_directory, "nebula.fits");
        var on = new FitsImageProcessingConfiguration(FitsStretchStack.Default, null, ai: new AiProcessing { High = 0.4 });
        await ProcessingSidecarService.SaveAsync(image, on);
        await ProcessingSidecarService.SaveAsync(image, FitsImageProcessingConfiguration.Default);
        Assert.Null((await ProcessingSidecarService.ReadAsync(image))!.Ai);
        await ProcessingSidecarService.SaveAsync(image, on);
        Assert.Equal(on, await ProcessingSidecarService.ReadAsync(image));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schema\":\"org.seiza.image-processing\",\"version\":999,\"processing\":{}}")]
    [InlineData("{\"stretch\":[]}")]
    public async Task InvalidOrFutureRecipeIsReportedAndLeftIntact(string json)
    {
        string image = Path.Combine(_directory, "nebula.fits");
        string sidecar = ProcessingSidecarService.PathFor(image);
        await File.WriteAllTextAsync(sidecar, json);
        await Assert.ThrowsAnyAsync<Exception>(() => ProcessingSidecarService.ReadAsync(image));
        Assert.Equal(json, await File.ReadAllTextAsync(sidecar));
    }

    [Fact]
    public async Task CanceledOrInvalidSavePreservesLastGoodRecipe()
    {
        string image = Path.Combine(_directory, "nebula.fits");
        await ProcessingSidecarService.SaveAsync(image, FitsImageProcessingConfiguration.Default);
        string original = await File.ReadAllTextAsync(ProcessingSidecarService.PathFor(image));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessingSidecarService.SaveAsync(
            image, FitsImageProcessingConfiguration.Default, canceled.Token));
        var invalid = new FitsImageProcessingConfiguration(FitsStretchStack.Default, null, ai: new AiProcessing { High = 2 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessingSidecarService.SaveAsync(image, invalid));
        Assert.Equal(original, await File.ReadAllTextAsync(ProcessingSidecarService.PathFor(image)));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailedReplacementKeepsExistingFileAndCleansStagingFile()
    {
        string image = Path.Combine(_directory, "nebula.fits");
        string sidecar = ProcessingSidecarService.PathFor(image);
        await ProcessingSidecarService.SaveAsync(image, FitsImageProcessingConfiguration.Default);
        File.SetAttributes(sidecar, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ProcessingSidecarService.SaveAsync(image, FitsImageProcessingConfiguration.Default));
            Assert.Equal(FitsImageProcessingConfiguration.Default, await ProcessingSidecarService.ReadAsync(image));
            Assert.Single(Directory.GetFiles(_directory));
        }
        finally { File.SetAttributes(sidecar, FileAttributes.Normal); }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
