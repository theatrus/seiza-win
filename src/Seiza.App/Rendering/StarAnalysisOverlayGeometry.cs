using System.Numerics;

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
