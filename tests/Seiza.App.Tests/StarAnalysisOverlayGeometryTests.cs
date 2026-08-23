using System.Numerics;
using Seiza.App.Models;
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

    [Fact]
    public void FourEqualCornerHfrValuesCreateSquareInPerimeterOrder()
    {
        TiltCellOverlayMeasurement[] cells = CreateTiltCells((_, _) => 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            cells,
            1_000,
            500,
            out TiltPerimeterDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Equal(new Vector2(500, 250), diagram.Center);
        Assert.Equal(
            [
                new Vector2(300, 50),
                new Vector2(700, 50),
                new Vector2(700, 450),
                new Vector2(300, 450),
            ],
            diagram.Vertices.Select(vertex => vertex.Point));
        Assert.Equal((0, 0), (diagram.Vertices[0].Row, diagram.Vertices[0].Column));
        Assert.Equal((0, 2), (diagram.Vertices[1].Row, diagram.Vertices[1].Column));
        Assert.Equal((2, 2), (diagram.Vertices[2].Row, diagram.Vertices[2].Column));
        Assert.Equal((2, 0), (diagram.Vertices[3].Row, diagram.Vertices[3].Column));
        Assert.Equal(2, diagram.CenterMeasurement?.MedianHfr);
        Assert.Equal(2, diagram.ReferenceCornerHfr);
    }

    [Fact]
    public void SofterCornerPushesItsVertexFartherFromCenter()
    {
        TiltCellOverlayMeasurement[] cells = CreateTiltCells(
            (row, column) => row == 0 && column == 2 ? 4 : 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            cells,
            1_000,
            500,
            out TiltPerimeterDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Equal(4, diagram.ReferenceCornerHfr);
        Assert.Equal(new Vector2(700, 50), diagram.Vertices[1].Point);
        Assert.Equal(new Vector2(400, 150), diagram.Vertices[0].Point);
    }

    [Fact]
    public void NonCornerCellsDoNotAffectTiltPerimeter()
    {
        TiltCellOverlayMeasurement[] cells = CreateTiltCells(
            (row, column) => row != 1 && column != 1 ? 2 : null,
            (row, column) => row != 1 && column != 1 ? 4 : 0);

        bool created = StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            cells,
            1_000,
            500,
            out TiltPerimeterDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Equal(2, diagram.ReferenceCornerHfr);
        Assert.Equal(4, diagram.Vertices.Count);
    }

    [Fact]
    public void TiltPerimeterRequiresAllFourReliableCornerMeasurements()
    {
        TiltCellOverlayMeasurement[] cells = CreateTiltCells(
            (row, column) => row == 2 && column == 0 ? null : 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            cells,
            1_000,
            500,
            out TiltPerimeterDiagram? diagram);

        Assert.False(created);
        Assert.Null(diagram);
    }

    [Fact]
    public void TiltPerimeterSuppressesLowSampleCorner()
    {
        TiltCellOverlayMeasurement[] cells = CreateTiltCells(
            (_, _) => 2,
            (row, column) => row == 0 && column == 2 ? 2 : 4);

        bool created = StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            cells,
            1_000,
            500,
            out TiltPerimeterDiagram? diagram);

        Assert.False(created);
        Assert.Null(diagram);
    }

    [Fact]
    public void TiltPerimeterKeepsDiagramWhenCenterMeasurementIsUnavailable()
    {
        TiltCellOverlayMeasurement[] cells = CreateTiltCells(
            (row, column) => row == 1 && column == 1 ? null : 2,
            (row, column) => row == 1 && column == 1 ? 0 : 4);

        bool created = StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            cells,
            1_000,
            500,
            out TiltPerimeterDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Null(diagram.CenterMeasurement);
    }

    [Fact]
    public void TriangleTiltUsesNativeClockwiseAxes()
    {
        StarAnalysisTriangleTilt triangle = CreateTriangleTilt(
            angleDegrees: 0,
            medianHfr: _ => 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            triangle,
            1_000,
            500,
            out TriangleTiltDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Equal(new Vector2(500, 250), diagram.Center);
        Assert.Equal([1, 2, 3], diagram.Vertices.Select(vertex => vertex.Sector));
        AssertVectorNear(new(500, 50), diagram.Vertices[0].Point);
        AssertVectorNear(new(673.2051f, 350), diagram.Vertices[1].Point);
        AssertVectorNear(new(326.7949f, 350), diagram.Vertices[2].Point);
    }

    [Fact]
    public void TriangleTiltScalesEachNativeMedianAgainstWorstSector()
    {
        StarAnalysisTriangleTilt triangle = CreateTriangleTilt(
            angleDegrees: 0,
            medianHfr: sector => sector == 2 ? 4 : 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            triangle,
            1_000,
            500,
            out TriangleTiltDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Equal(4, diagram.ReferenceWorstHfr);
        AssertVectorNear(new(500, 150), diagram.Vertices[0].Point);
        AssertVectorNear(new(673.2051f, 350), diagram.Vertices[1].Point);
    }

    [Fact]
    public void TriangleTiltHonorsRotatedNativeAxis()
    {
        StarAnalysisTriangleTilt triangle = CreateTriangleTilt(
            angleDegrees: 90,
            medianHfr: _ => 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            triangle,
            1_000,
            500,
            out TriangleTiltDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        AssertVectorNear(new(700, 250), diagram.Vertices[0].Point);
    }

    [Fact]
    public void TriangleTiltRequiresNativeReadyVerdict()
    {
        StarAnalysisTriangleTilt triangle = CreateTriangleTilt(
            ready: false,
            medianHfr: _ => 2);

        bool created = StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            triangle,
            1_000,
            500,
            out TriangleTiltDiagram? diagram);

        Assert.False(created);
        Assert.Null(diagram);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TriangleTiltRejectsIncompleteReadySector(bool lowCount)
    {
        StarAnalysisTriangleTilt triangle = CreateTriangleTilt(
            ready: true,
            medianHfr: sector => !lowCount && sector == 1 ? null : 2,
            starCount: sector => lowCount && sector == 1 ? 2 : 3);

        bool created = StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            triangle,
            1_000,
            500,
            out TriangleTiltDiagram? diagram);

        Assert.False(created);
        Assert.Null(diagram);
    }

    [Fact]
    public void TriangleTiltDoesNotGateOnCenterAndHidesLowSampleCenterHfr()
    {
        StarAnalysisTriangleTilt triangle = CreateTriangleTilt(
            medianHfr: _ => 2,
            centerStarCount: 1,
            centerHfr: 1.5);

        bool created = StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            triangle,
            1_000,
            500,
            out TriangleTiltDiagram? diagram);

        Assert.True(created);
        Assert.NotNull(diagram);
        Assert.Equal(1, diagram.CenterStarCount);
        Assert.Null(diagram.CenterHfr);
    }

    private static TiltCellOverlayMeasurement[] CreateTiltCells(
        Func<int, int, double?> hfr,
        Func<int, int, int>? starCount = null) =>
        Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 3)
                .Select(column => new TiltCellOverlayMeasurement(
                    row,
                    column,
                    starCount?.Invoke(row, column) ?? 4,
                    hfr(row, column))))
            .ToArray();

    private static StarAnalysisTriangleTilt CreateTriangleTilt(
        double angleDegrees = 0,
        bool ready = true,
        Func<int, double?>? medianHfr = null,
        Func<int, int>? starCount = null,
        int centerStarCount = 3,
        double? centerHfr = 2)
    {
        StarAnalysisTriangleSector[] sectors = Enumerable.Range(1, 3)
            .Select(sector => new StarAnalysisTriangleSector
            {
                Sector = sector,
                AxisAngleDegrees = NormalizeDegrees(angleDegrees + ((sector - 1) * 120)),
                StarCount = starCount?.Invoke(sector) ?? 3,
                MedianHfr = medianHfr is null ? 2 : medianHfr(sector),
            })
            .ToArray();
        StarAnalysisTriangleSector[] measured = sectors
            .Where(sector => sector.MedianHfr.HasValue)
            .ToArray();
        int? bestSector = ready && measured.Length > 0
            ? measured.MinBy(sector => sector.MedianHfr!.Value)!.Sector
            : null;
        int? worstSector = ready && measured.Length > 0
            ? measured.MaxBy(sector => sector.MedianHfr!.Value)!.Sector
            : null;
        double overallMedianHfr = measured.Length > 0
            ? measured.Average(sector => sector.MedianHfr!.Value)
            : 2;
        double? tiltPercent = ready && bestSector.HasValue && worstSector.HasValue
            ? 100 *
                (sectors[worstSector.Value - 1].MedianHfr!.Value -
                    sectors[bestSector.Value - 1].MedianHfr!.Value) /
                overallMedianHfr
            : null;
        return new StarAnalysisTriangleTilt
        {
            AngleDegrees = angleDegrees,
            InnerRadiusPixels = 100,
            OuterRadiusPixels = 200,
            MinimumStarsPerRegion = 3,
            Ready = ready,
            Center = new StarAnalysisTriangleCenter
            {
                StarCount = centerStarCount,
                MedianHfr = centerHfr,
            },
            Sectors = sectors,
            OverallMedianHfr = overallMedianHfr,
            TiltPercent = tiltPercent,
            BestSector = bestSector,
            WorstSector = worstSector,
        };
    }

    private static double NormalizeDegrees(double value)
    {
        double normalized = value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static void AssertVectorNear(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0, 0.001);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, 0.001);
    }
}
