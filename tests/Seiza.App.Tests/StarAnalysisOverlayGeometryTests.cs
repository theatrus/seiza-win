using System.Numerics;
using Seiza.App.Rendering;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StarAnalysisOverlayGeometryTests
{
    [Fact]
    public void ImageTransformKeepsGeometryRegisteredAcrossZoomAndPan()
    {
        var transform = new ImageSpaceTransform(2.5f, 1.5f, new(11, -7));

        Vector2 point = transform.ToTarget(20, 30);
        Vector2 radii = transform.SourceRadiusToTarget(4);

        Assert.Equal(new Vector2(61, 38), point);
        Assert.Equal(new Vector2(10, 6), radii);
        Assert.Equal(2, transform.AverageAbsoluteScale);
    }

    [Fact]
    public void TransformNormalizesBoundsForAFlippedAxis()
    {
        var transform = new ImageSpaceTransform(-2, 3, new(100, 5));

        TargetRectangle result = transform.ToTarget(new SourceRectangle(10, 20, 30, 40));

        Assert.Equal(20, result.X);
        Assert.Equal(65, result.Y);
        Assert.Equal(60, result.Width);
        Assert.Equal(120, result.Height);
    }

    [Fact]
    public void SelectStarsTakesSmallestFinitePositiveHfrValues()
    {
        var stars = new[]
        {
            new StarOverlayMeasurement(0, 1, 2, 4),
            new StarOverlayMeasurement(1, 1, 2, double.NaN),
            new StarOverlayMeasurement(2, 1, 2, 1.5),
            new StarOverlayMeasurement(3, 1, 2, 2),
            new StarOverlayMeasurement(4, double.PositiveInfinity, 2, 1),
            new StarOverlayMeasurement(5, 1, 2, 0),
        };

        IReadOnlyList<int> selected = StarAnalysisOverlayGeometry.SelectStarIndices(stars, 2);

        Assert.Equal([2, 3], selected);
    }

    [Fact]
    public void SelectStarsCapsMarkersAtOneThousandSmallestHfrValues()
    {
        StarOverlayMeasurement[] stars = Enumerable.Range(0, 1_200)
            .Select(index => new StarOverlayMeasurement(index, index, index, 1_200 - index))
            .ToArray();

        IReadOnlyList<int> selected = StarAnalysisOverlayGeometry.SelectStarIndices(stars);

        Assert.Equal(StarAnalysisOverlayGeometry.MaximumStarMarkers, selected.Count);
        Assert.Equal(1_199, selected[0]);
        Assert.Equal(200, selected[^1]);
    }

    [Fact]
    public void CellBoundsMeetExactlyAtSourceThirds()
    {
        SourceRectangle topLeft = StarAnalysisOverlayGeometry.GetCellBounds(0, 0, 100, 80);
        SourceRectangle center = StarAnalysisOverlayGeometry.GetCellBounds(1, 1, 100, 80);
        SourceRectangle bottomRight = StarAnalysisOverlayGeometry.GetCellBounds(2, 2, 100, 80);

        Assert.Equal(100d / 3, topLeft.Right, 12);
        Assert.Equal(topLeft.Right, center.Left, 12);
        Assert.Equal(200d / 3, center.Right, 12);
        Assert.Equal(center.Right, bottomRight.Left, 12);
        Assert.Equal(100, bottomRight.Right, 12);
        Assert.Equal(80, bottomRight.Bottom, 12);
    }

    [Theory]
    [InlineData(10.0, (int)TiltCellVisualKind.Good)]
    [InlineData(10.99, (int)TiltCellVisualKind.Good)]
    [InlineData(11.0, (int)TiltCellVisualKind.Warning)]
    [InlineData(12.49, (int)TiltCellVisualKind.Warning)]
    [InlineData(12.5, (int)TiltCellVisualKind.Poor)]
    public void CellColorUsesSoftnessRelativeToSharpest(
        double medianHfr,
        int expected)
    {
        var cell = new TiltCellOverlayMeasurement(0, 0, 5, medianHfr);

        TiltCellVisualKind result = StarAnalysisOverlayGeometry.ClassifyCell(cell, 10);

        Assert.Equal((TiltCellVisualKind)expected, result);
    }

    [Fact]
    public void CellWithTooFewStarsStaysNeutral()
    {
        var cell = new TiltCellOverlayMeasurement(0, 0, 2, 50);

        TiltCellVisualKind result = StarAnalysisOverlayGeometry.ClassifyCell(cell, 10);

        Assert.Equal(TiltCellVisualKind.Neutral, result);
    }

    [Theory]
    [InlineData(10.29, false)]
    [InlineData(10.30, true)]
    public void BestWorstEmphasisRequiresThreePercentSpread(double softHfr, bool expected)
    {
        var cells = new[]
        {
            new TiltCellOverlayMeasurement(0, 0, 4, 10),
            new TiltCellOverlayMeasurement(0, 1, 4, softHfr),
        };

        bool result = StarAnalysisOverlayGeometry.HasMeaningfulReliableSpread(cells);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(false, 3, 1.0, 0.9, false)]
    [InlineData(true, 2, 1.0, 0.9, false)]
    [InlineData(true, 3, null, 0.9, false)]
    [InlineData(true, 3, 1.0, 0.25, false)]
    [InlineData(true, 3, 1.0, 0.251, true)]
    public void OrientationRequiresReliableSampleNormalizedMajorAxisAndCoherence(
        bool normalized,
        int starCount,
        double? theta,
        double coherence,
        bool expected)
    {
        bool result = StarAnalysisOverlayGeometry.ShouldDrawOrientation(
            normalized,
            starCount,
            theta,
            coherence);

        Assert.Equal(expected, result);
    }
}
