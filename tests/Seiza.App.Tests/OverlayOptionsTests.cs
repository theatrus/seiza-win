using Seiza.App.Rendering;
using Xunit;

namespace Seiza.App.Tests;

public sealed class OverlayOptionsTests
{
    [Fact]
    public void ParallelogramTiltIsVisibleOnlyWhenPerimeterIsAvailable()
    {
        var options = new OverlayOptions();
        options.HideAll();
        options.ShowParallelogramTilt = true;

        Assert.False(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: false,
            hasTriangleTilt: false));
        Assert.True(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: true,
            hasTriangleTilt: false));
    }

    [Fact]
    public void TriangleTiltIsVisibleOnlyWhenNativeDiagramIsAvailable()
    {
        var options = new OverlayOptions();
        options.HideAll();
        options.ShowTriangleTilt = true;

        Assert.False(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: false,
            hasTriangleTilt: false));
        Assert.True(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: false,
            hasTriangleTilt: true));
    }

    [Fact]
    public void OtherStarAnalysisLayersRemainVisibleWithoutTiltPerimeter()
    {
        var options = new OverlayOptions();
        options.HideAll();
        options.ShowMeasuredStars = true;

        Assert.True(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: false,
            hasTriangleTilt: false));

        options.ShowMeasuredStars = false;
        options.ShowSensorTilt = true;

        Assert.True(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: false,
            hasTriangleTilt: false));
    }

    [Fact]
    public void SnapshotPreservesParallelogramTiltAndOwnsCatalogSet()
    {
        var options = new OverlayOptions();
        options.HideAll();
        options.ShowParallelogramTilt = true;
        options.ShowTriangleTilt = true;
        options.HiddenDeepSkyCatalogs.Add(DeepSkyCatalog.Messier);

        OverlayOptions snapshot = options.Snapshot();
        options.ShowParallelogramTilt = false;
        options.ShowTriangleTilt = false;
        options.HiddenDeepSkyCatalogs.Add(DeepSkyCatalog.Ngc);

        Assert.True(snapshot.ShowParallelogramTilt);
        Assert.True(snapshot.ShowTriangleTilt);
        Assert.Contains(DeepSkyCatalog.Messier, snapshot.HiddenDeepSkyCatalogs);
        Assert.DoesNotContain(DeepSkyCatalog.Ngc, snapshot.HiddenDeepSkyCatalogs);
    }

    [Fact]
    public void HideAllClearsParallelogramTiltAndEveryVisibilityAggregate()
    {
        var options = new OverlayOptions
        {
            ShowParallelogramTilt = true,
            ShowTriangleTilt = true,
            ShowSensorTilt = true,
            ShowMeasuredStars = true,
        };

        options.HideAll();

        Assert.False(options.ShowParallelogramTilt);
        Assert.False(options.ShowTriangleTilt);
        Assert.False(options.HasVisibleStarAnalysisOverlays(
            hasTiltPerimeter: true,
            hasTriangleTilt: true));
        Assert.False(options.HasVisibleSolveOverlays);
        Assert.False(options.HasVisibleOverlays);
    }
}
