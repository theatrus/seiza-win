using System.Numerics;
using Seiza.App.Models;

namespace Seiza.App.Rendering;

internal readonly record struct StarOverlayMeasurement(
    int SourceIndex,
    double X,
    double Y,
    double Hfr);

internal readonly record struct TiltCellOverlayMeasurement(
    int Row,
    int Column,
    int StarCount,
    double? MedianHfr);

internal readonly record struct TiltPerimeterVertex(
    int Row,
    int Column,
    int StarCount,
    double MedianHfr,
    Vector2 Point);

internal sealed record TiltPerimeterDiagram(
    Vector2 Center,
    IReadOnlyList<TiltPerimeterVertex> Vertices,
    TiltCellOverlayMeasurement? CenterMeasurement,
    double ReferenceCornerHfr);

internal readonly record struct TriangleTiltVertex(
    int Sector,
    double AxisAngleDegrees,
    int StarCount,
    double MedianHfr,
    Vector2 Point);

internal sealed record TriangleTiltDiagram(
    Vector2 Center,
    IReadOnlyList<TriangleTiltVertex> Vertices,
    int CenterStarCount,
    double? CenterHfr,
    double ReferenceWorstHfr,
    double OverallMedianHfr,
    double TiltPercent);

internal enum TiltCellVisualKind
{
    Neutral,
    Good,
    Warning,
    Poor,
}

internal static class StarAnalysisOverlayGeometry
{
    public const int MaximumStarMarkers = 1000;
    public const int MaximumStarLabels = 100;
    public const int MinimumReliableCellStars = 3;
    public const double MeaningfulCellSpreadFraction = 0.03;
    public const double MinimumOrientationCoherence = 0.25;
    public const double TiltPerimeterMaximumAxisExtentFraction = 0.4;
    public const double TriangleTiltMaximumRadiusFraction = 0.4;

    private static readonly (int Row, int Column, Vector2 Direction)[] TiltPerimeterOrder =
    [
        (0, 0, new(-1, -1)),
        (0, 2, new(1, -1)),
        (2, 2, new(1, 1)),
        (2, 0, new(-1, 1)),
    ];

    public static IReadOnlyList<int> SelectStarIndices(
        IReadOnlyList<StarOverlayMeasurement> stars,
        int maximumCount = MaximumStarMarkers) =>
        stars
            .Where(IsUsableStar)
            .OrderBy(star => star.Hfr)
            .ThenBy(star => star.SourceIndex)
            .Take(Math.Max(maximumCount, 0))
            .Select(star => star.SourceIndex)
            .ToArray();

    public static SourceRectangle GetCellBounds(
        int row,
        int column,
        double sourceWidth,
        double sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        double left = sourceWidth * column / 3;
        double top = sourceHeight * row / 3;
        double right = sourceWidth * (column + 1) / 3;
        double bottom = sourceHeight * (row + 1) / 3;
        return new(left, top, right - left, bottom - top);
    }

    public static double? FindSharpestReliableHfr(
        IEnumerable<TiltCellOverlayMeasurement> cells)
    {
        double minimum = cells
            .Where(IsReliableCell)
            .Select(cell => cell.MedianHfr!.Value)
            .DefaultIfEmpty(double.NaN)
            .Min();
        return double.IsFinite(minimum) ? minimum : null;
    }

    public static TiltCellVisualKind ClassifyCell(
        TiltCellOverlayMeasurement cell,
        double? sharpestReliableHfr)
    {
        if (!IsReliableCell(cell) ||
            sharpestReliableHfr is not double sharpest ||
            !double.IsFinite(sharpest) ||
            sharpest <= 0)
        {
            return TiltCellVisualKind.Neutral;
        }

        double softness = (cell.MedianHfr!.Value - sharpest) / sharpest;
        if (softness < 0.10)
        {
            return TiltCellVisualKind.Good;
        }
        if (softness < 0.25)
        {
            return TiltCellVisualKind.Warning;
        }
        return TiltCellVisualKind.Poor;
    }

    public static bool HasMeaningfulReliableSpread(
        IEnumerable<TiltCellOverlayMeasurement> cells)
    {
        double[] values = cells
            .Where(IsReliableCell)
            .Select(cell => cell.MedianHfr!.Value)
            .ToArray();
        if (values.Length < 2)
        {
            return false;
        }

        double minimum = values.Min();
        double maximum = values.Max();
        return minimum > 0 &&
            double.IsFinite(minimum) &&
            double.IsFinite(maximum) &&
            ((maximum - minimum) / minimum) >= MeaningfulCellSpreadFraction;
    }

    public static bool ShouldDrawStarLabel(
        Vector2 targetRadii,
        float targetFontSize) =>
        MathF.Min(targetRadii.X, targetRadii.Y) >= 8 && targetFontSize >= 8;

    public static bool ShouldDrawOrientation(
        bool majorAxisOrientationsNormalized,
        int starCount,
        double? meanTheta,
        double coherence) =>
        majorAxisOrientationsNormalized &&
        starCount >= MinimumReliableCellStars &&
        meanTheta is double theta &&
        double.IsFinite(theta) &&
        double.IsFinite(coherence) &&
        coherence > MinimumOrientationCoherence;

    /// <summary>
    /// Builds the four-corner perimeter used by the parallelogram tilt diagram.
    /// Each corner moves a vertex away from the image center in proportion to
    /// its HFR; equal HFR values therefore form a square and a softer corner
    /// pushes its vertex farther out. The softest corner establishes the
    /// 40%-of-short-axis reference radius.
    /// </summary>
    public static bool TryCreateTiltPerimeter(
        IEnumerable<TiltCellOverlayMeasurement> cells,
        double sourceWidth,
        double sourceHeight,
        out TiltPerimeterDiagram? diagram)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        var byPosition = new Dictionary<(int Row, int Column), TiltCellOverlayMeasurement>();
        foreach (TiltCellOverlayMeasurement cell in cells)
        {
            if (cell.Row is < 0 or > 2 || cell.Column is < 0 or > 2)
            {
                continue;
            }

            var position = (cell.Row, cell.Column);
            if (!byPosition.TryGetValue(position, out TiltCellOverlayMeasurement current) ||
                cell.StarCount > current.StarCount)
            {
                byPosition[position] = cell;
            }
        }

        var cornerMeasurements = new TiltCellOverlayMeasurement[TiltPerimeterOrder.Length];
        for (int index = 0; index < TiltPerimeterOrder.Length; index++)
        {
            (int row, int column, _) = TiltPerimeterOrder[index];
            if (!byPosition.TryGetValue((row, column), out TiltCellOverlayMeasurement cell) ||
                !IsReliableCell(cell))
            {
                diagram = null;
                return false;
            }
            cornerMeasurements[index] = cell;
        }

        double referenceCornerHfr = cornerMeasurements
            .Max(cell => cell.MedianHfr!.Value);
        double maximumAxisExtent = Math.Min(sourceWidth, sourceHeight) *
            TiltPerimeterMaximumAxisExtentFraction;
        var center = new Vector2((float)(sourceWidth / 2), (float)(sourceHeight / 2));
        var vertices = new TiltPerimeterVertex[TiltPerimeterOrder.Length];
        for (int index = 0; index < TiltPerimeterOrder.Length; index++)
        {
            TiltCellOverlayMeasurement cell = cornerMeasurements[index];
            Vector2 direction = TiltPerimeterOrder[index].Direction;
            double hfr = cell.MedianHfr!.Value;
            double normalizedRadius = hfr / referenceCornerHfr;
            Vector2 point = center +
                (direction * (float)(normalizedRadius * maximumAxisExtent));
            vertices[index] = new(
                cell.Row,
                cell.Column,
                cell.StarCount,
                hfr,
                point);
        }

        TiltCellOverlayMeasurement? centerMeasurement =
            byPosition.TryGetValue((1, 1), out TiltCellOverlayMeasurement centerCell) &&
            IsReliableCell(centerCell)
                ? centerCell
                : null;
        diagram = new(
            center,
            vertices,
            centerMeasurement,
            referenceCornerHfr);
        return true;
    }

    /// <summary>
    /// Builds a three-sector tilt diagram exclusively from the native triangle
    /// summary. Axis angles use image coordinates: zero points up and values
    /// increase clockwise. The native worst-sector median establishes the
    /// 40%-of-short-axis reference radius.
    /// </summary>
    public static bool TryCreateTriangleTilt(
        StarAnalysisTriangleTilt? triangleTilt,
        double sourceWidth,
        double sourceHeight,
        out TriangleTiltDiagram? diagram)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        if (triangleTilt is not { Ready: true } triangle ||
            triangle.MinimumStarsPerRegion <= 0 ||
            triangle.Sectors is not { Length: 3 } ||
            triangle.WorstSector is not int worstSectorId ||
            worstSectorId is < 1 or > 3 ||
            triangle.OverallMedianHfr is not double overallMedianHfr ||
            !double.IsFinite(overallMedianHfr) ||
            overallMedianHfr <= 0 ||
            triangle.TiltPercent is not double tiltPercent ||
            !double.IsFinite(tiltPercent) ||
            tiltPercent < 0)
        {
            diagram = null;
            return false;
        }

        StarAnalysisTriangleSector[] sectors = triangle.Sectors;
        var orderedSectors = new StarAnalysisTriangleSector[3];
        for (int index = 0; index < sectors.Length; index++)
        {
            StarAnalysisTriangleSector? sector = sectors[index];
            int expectedSector = index + 1;
            if (sector is null ||
                sector.Sector != expectedSector ||
                sector.StarCount < triangle.MinimumStarsPerRegion ||
                sector.MedianHfr is not double medianHfr ||
                !double.IsFinite(medianHfr) ||
                medianHfr <= 0 ||
                !double.IsFinite(sector.AxisAngleDegrees) ||
                sector.AxisAngleDegrees is < 0 or >= 360)
            {
                diagram = null;
                return false;
            }
            orderedSectors[index] = sector;
        }

        StarAnalysisTriangleSector worstSector = orderedSectors[worstSectorId - 1];
        double referenceWorstHfr = worstSector.MedianHfr!.Value;
        double maximumMedianHfr = orderedSectors.Max(sector => sector.MedianHfr!.Value);
        if (Math.Abs(referenceWorstHfr - maximumMedianHfr) >
            1e-9 * Math.Max(1, maximumMedianHfr))
        {
            diagram = null;
            return false;
        }

        var center = new Vector2((float)(sourceWidth / 2), (float)(sourceHeight / 2));
        double maximumRadius = Math.Min(sourceWidth, sourceHeight) *
            TriangleTiltMaximumRadiusFraction;
        var vertices = new TriangleTiltVertex[orderedSectors.Length];
        for (int index = 0; index < orderedSectors.Length; index++)
        {
            StarAnalysisTriangleSector sector = orderedSectors[index];
            double medianHfr = sector.MedianHfr!.Value;
            double radians = sector.AxisAngleDegrees * Math.PI / 180;
            var direction = new Vector2(
                (float)Math.Sin(radians),
                (float)-Math.Cos(radians));
            Vector2 point = center +
                (direction * (float)(maximumRadius * medianHfr / referenceWorstHfr));
            vertices[index] = new(
                sector.Sector,
                sector.AxisAngleDegrees,
                sector.StarCount,
                medianHfr,
                point);
        }

        double? centerHfr = triangle.Center is { } centerMeasurement &&
            centerMeasurement.StarCount >= triangle.MinimumStarsPerRegion &&
            centerMeasurement.MedianHfr is double value &&
            double.IsFinite(value) &&
            value > 0
                ? value
                : null;
        diagram = new(
            center,
            vertices,
            triangle.Center?.StarCount ?? 0,
            centerHfr,
            referenceWorstHfr,
            overallMedianHfr,
            tiltPercent);
        return true;
    }

    private static bool IsUsableStar(StarOverlayMeasurement star) =>
        star.SourceIndex >= 0 &&
        double.IsFinite(star.X) &&
        double.IsFinite(star.Y) &&
        double.IsFinite(star.Hfr) &&
        star.Hfr > 0;

    public static bool IsReliableCell(TiltCellOverlayMeasurement cell) =>
        cell.StarCount >= MinimumReliableCellStars &&
        cell.MedianHfr is double hfr &&
        double.IsFinite(hfr) &&
        hfr > 0;
}
