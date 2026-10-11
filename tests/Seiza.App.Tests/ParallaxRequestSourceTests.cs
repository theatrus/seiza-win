using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxRequestSourceTests
{
    private const string Snapshot = "private/source.png";
    private const string Starless = "private/imported-starless.tiff";
    private const string Stars = "private/imported-stars.tiff";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreviewAndExportNativeRequestsPreferPrivateStarsOverStarlessSource(bool preview)
    {
        ParallaxRequest request = Create(preview, automaticSeparation: false);

        // These are the serialized inputs passed to native PrepareAsync by both
        // Prepare Preview and an export-first workflow, not only a path selector.
        using JsonDocument json = JsonDocument.Parse(request.ToJson());
        Assert.Equal(Stars, json.RootElement.GetProperty("image").GetString());
        Assert.Equal(Starless, json.RootElement.GetProperty("starless").GetString());
        Assert.Equal(Stars, json.RootElement.GetProperty("stars").GetString());
        Assert.Equal(preview ? "960x540" : "1920x1080", request.Video.Size);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutomaticPreviewAndExportRequestsUseSnapshotAndIgnoreOldManualLayers(bool preview)
    {
        ParallaxRequest request = Create(preview, automaticSeparation: true);

        using JsonDocument json = JsonDocument.Parse(request.ToJson());
        Assert.Equal(Snapshot, json.RootElement.GetProperty("image").GetString());
        Assert.False(json.RootElement.TryGetProperty("starless", out _));
        Assert.False(json.RootElement.TryGetProperty("stars", out _));
    }

    [Theory]
    [InlineData(false, Stars)]
    [InlineData(true, Snapshot)]
    public async Task GenerateTourBlindSolveUsesTheSameRequestSourceAsPreparation(bool automaticSeparation, string expected)
    {
        ParallaxRequest request = Create(preview: false, automaticSeparation);
        WcsResult solution = Wcs();
        var calls = new List<string>();

        await ParallaxRequestSource.EnsureWcsAsync(request, path =>
        {
            calls.Add(path);
            return Task.FromResult(solution);
        }, CancellationToken.None);

        Assert.Equal([expected], calls);
        Assert.Same(solution, request.Inputs.Wcs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservedWcsBypassesBlindSolveForBothSeparationModes(bool automaticSeparation)
    {
        WcsResult solution = Wcs();
        ParallaxRequest request = Create(preview: false, automaticSeparation, solution);

        await ParallaxRequestSource.EnsureWcsAsync(request,
            _ => throw new InvalidOperationException("An existing WCS must bypass the solver."), CancellationToken.None);

        Assert.Same(solution, request.Inputs.Wcs);
    }

    [Fact]
    public async Task TourBeforeImportingManualLayersStillSolvesTheSnapshot()
    {
        ParallaxRequest request = ParallaxRequestSource.Create(new(), preview: false,
            Snapshot, automaticSeparation: false, starless: null, stars: null, new());
        string? solved = null;

        await ParallaxRequestSource.EnsureWcsAsync(request, path =>
        {
            solved = path;
            return Task.FromResult(Wcs());
        }, CancellationToken.None);

        Assert.Equal(Snapshot, solved);
    }

    [Fact]
    public async Task CancelledTourDoesNotPublishABlindSolveResult()
    {
        ParallaxRequest request = Create(preview: false, automaticSeparation: false);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ParallaxRequestSource.EnsureWcsAsync(request, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Wcs());
        }, cancellation.Token));

        Assert.Null(request.Inputs.Wcs);
    }

    private static ParallaxRequest Create(bool preview, bool automaticSeparation, WcsResult? wcs = null) =>
        ParallaxRequestSource.Create(new(), preview, Snapshot, automaticSeparation, Starless, Stars,
            new() { Wcs = wcs, CatalogDirectory = "catalogs" });

    private static WcsResult Wcs() => new()
    {
        Crval = [315.13, 67.77], Crpix = [100, 50], Cd = [[-0.001, 0], [0, 0.001]],
    };
}
