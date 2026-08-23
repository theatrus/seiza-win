using System.Text.Json.Serialization;

namespace Seiza.App.Models;

public sealed class StarAnalysisResult
{
    public required int SchemaVersion { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>
    /// True only when theta values describe the fitted PSF's major axis in [0, pi).
    /// Older native cores omit this capability, which safely disables direction overlays.
    /// </summary>
    public bool MajorAxisOrientationsNormalized { get; init; }

    public required double AverageHfr { get; init; }

    public required double AverageFwhm { get; init; }

    public required double NoiseSigma { get; init; }

    public required double BackgroundMean { get; init; }

    public required StarAnalysisStar[] Stars { get; init; }

    public required StarAnalysisCell[] Cells { get; init; }

    public required StarAnalysisTilt Tilt { get; init; }

    /// <summary>
    /// Optional three-sector radial tilt analysis. Native cores omit this
    /// field unless the request includes a triangle angle.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StarAnalysisTriangleTilt? TriangleTilt { get; init; }

    [JsonIgnore]
    public bool HasPsfMeasurements => Stars.Any(star =>
        star.Eccentricity.HasValue && star.Theta.HasValue);

    public void Validate() => StarAnalysisValidator.Validate(this);
}

public sealed class StarAnalysisStar
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Hfr { get; init; }

    public required double Fwhm { get; init; }

    public required double Brightness { get; init; }

    public required double Background { get; init; }

    public required double Snr { get; init; }

    public required double Flux { get; init; }

    public required int PixelCount { get; init; }

    public required bool Saturated { get; init; }

    public double? Eccentricity { get; init; }

    public double? Theta { get; init; }

    public double? RSquared { get; init; }
}

public sealed class StarAnalysisCell
{
    public required int Row { get; init; }

    public required int Col { get; init; }

    public required int StarCount { get; init; }

    [JsonRequired]
    public double? MedianHfr { get; init; }

    [JsonRequired]
    public double? MedianEccentricity { get; init; }

    [JsonRequired]
    public double? MeanTheta { get; init; }

    public required double ThetaCoherence { get; init; }
}

public sealed class StarAnalysisTilt
{
    [JsonRequired]
    public double? CenterHfr { get; init; }

    public required StarAnalysisCorner[] Corners { get; init; }

    [JsonRequired]
    public double? MeanHfr { get; init; }

    [JsonRequired]
    public double? TiltPercent { get; init; }

    [JsonRequired]
    public double? CurvaturePercent { get; init; }

    [JsonRequired]
    public StarAnalysisCornerPosition? WorstCorner { get; init; }

    [JsonRequired]
    public StarAnalysisCornerPosition? BestCorner { get; init; }
}

public sealed class StarAnalysisCorner
{
    public required StarAnalysisCornerPosition Corner { get; init; }

    [JsonRequired]
    public double? Hfr { get; init; }
}

public sealed class StarAnalysisTriangleTilt
{
    public required double AngleDegrees { get; init; }

    public required double InnerRadiusPixels { get; init; }

    public required double OuterRadiusPixels { get; init; }

    public required int MinimumStarsPerRegion { get; init; }

    public required bool Ready { get; init; }

    public required StarAnalysisTriangleCenter Center { get; init; }

    public required StarAnalysisTriangleSector[] Sectors { get; init; }

    [JsonRequired]
    public double? OverallMedianHfr { get; init; }

    [JsonRequired]
    public double? TiltPercent { get; init; }

    [JsonRequired]
    public int? BestSector { get; init; }

    [JsonRequired]
    public int? WorstSector { get; init; }
}

public sealed class StarAnalysisTriangleCenter
{
    public required int StarCount { get; init; }

    [JsonRequired]
    public double? MedianHfr { get; init; }
}

public sealed class StarAnalysisTriangleSector
{
    public required int Sector { get; init; }

    public required double AxisAngleDegrees { get; init; }

    public required int StarCount { get; init; }

    [JsonRequired]
    public double? MedianHfr { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<StarAnalysisCornerPosition>))]
public enum StarAnalysisCornerPosition
{
    [JsonStringEnumMemberName("top-left")]
    TopLeft,

    [JsonStringEnumMemberName("top-right")]
    TopRight,

    [JsonStringEnumMemberName("bottom-left")]
    BottomLeft,

    [JsonStringEnumMemberName("bottom-right")]
    BottomRight,
}

internal static class StarAnalysisValidator
{
    private const int SupportedSchemaVersion = 1;

    internal static void Validate(StarAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SchemaVersion != SupportedSchemaVersion)
        {
            throw Invalid($"unsupported schema version {result.SchemaVersion}");
        }

        if (result.Width <= 0 || result.Height <= 0)
        {
            throw Invalid("image dimensions must be positive");
        }

        RequireFiniteNonnegative(result.AverageHfr, "average HFR");
        RequireFiniteNonnegative(result.AverageFwhm, "average FWHM");
        RequireFiniteNonnegative(result.NoiseSigma, "noise sigma");
        RequireFiniteNonnegative(result.BackgroundMean, "background mean");

        if (result.Stars is null)
        {
            throw Invalid("stars are missing");
        }

        if (result.Cells is null)
        {
            throw Invalid("cells are missing");
        }

        if (result.Tilt is null)
        {
            throw Invalid("tilt summary is missing");
        }

        for (int index = 0; index < result.Stars.Length; index++)
        {
            ValidateStar(result.Stars[index], index, result);
        }

        if (result.Stars.Length > 0 && result.AverageHfr <= 0)
        {
            throw Invalid("average HFR must be positive when stars were detected");
        }

        ValidateCells(result.Cells, result.Stars.Length);
        ValidateTilt(result.Tilt, result.Cells);
        if (result.TriangleTilt is not null)
        {
            ValidateTriangleTilt(result.TriangleTilt, result);
        }
    }

    internal static void ValidateTriangleRequest(
        StarAnalysisResult result,
        double? requestedAngleDegrees)
    {
        bool requested = requestedAngleDegrees.HasValue;
        bool returned = result.TriangleTilt is not null;
        if (requested && !returned)
        {
            throw Invalid("triangle tilt is missing from a request that enabled it");
        }

        if (!requested && returned)
        {
            throw Invalid("triangle tilt was returned without being requested");
        }

        if (requested)
        {
            double expectedAngle = NormalizeDegrees(requestedAngleDegrees!.Value);
            RequireApproximately(
                result.TriangleTilt!.AngleDegrees,
                expectedAngle,
                "triangle angle does not match the requested angle");
        }
    }

    private static void ValidateStar(
        StarAnalysisStar? star,
        int index,
        StarAnalysisResult result)
    {
        if (star is null)
        {
            throw Invalid($"star {index} is null");
        }

        RequireFiniteRange(star.X, 0, result.Width, $"star {index} X", maximumInclusive: false);
        RequireFiniteRange(star.Y, 0, result.Height, $"star {index} Y", maximumInclusive: false);
        RequireFinitePositive(star.Hfr, $"star {index} HFR");
        RequireFiniteNonnegative(star.Fwhm, $"star {index} FWHM");
        RequireFiniteNonnegative(star.Brightness, $"star {index} brightness");
        RequireFiniteNonnegative(star.Background, $"star {index} background");
        RequireFiniteNonnegative(star.Snr, $"star {index} SNR");
        RequireFiniteNonnegative(star.Flux, $"star {index} flux");
        if (star.PixelCount <= 0)
        {
            throw Invalid($"star {index} pixel count must be positive");
        }

        bool hasEccentricity = star.Eccentricity.HasValue;
        if (hasEccentricity != star.Theta.HasValue ||
            hasEccentricity != star.RSquared.HasValue)
        {
            throw Invalid($"star {index} has an incomplete PSF measurement");
        }

        if (!hasEccentricity)
        {
            return;
        }

        RequireFiniteRange(
            star.Eccentricity!.Value,
            0,
            1,
            $"star {index} eccentricity",
            maximumInclusive: true);
        RequireFinite(star.Theta!.Value, $"star {index} theta");
        if (result.MajorAxisOrientationsNormalized)
        {
            RequireFiniteRange(
                star.Theta.Value,
                0,
                Math.PI,
                $"star {index} theta",
                maximumInclusive: false);
        }

        RequireFinite(star.RSquared!.Value, $"star {index} R-squared");
        if (star.RSquared.Value > 1)
        {
            throw Invalid($"star {index} R-squared exceeds 1");
        }
    }

    private static void ValidateCells(StarAnalysisCell[] cells, int starCount)
    {
        if (cells.Length != 9)
        {
            throw Invalid("the tilt grid must contain exactly nine cells");
        }

        var positions = new HashSet<(int Row, int Col)>();
        long countedStars = 0;
        for (int index = 0; index < cells.Length; index++)
        {
            StarAnalysisCell? cell = cells[index];
            if (cell is null)
            {
                throw Invalid($"cell {index} is null");
            }

            if (cell.Row is < 0 or > 2 || cell.Col is < 0 or > 2 ||
                !positions.Add((cell.Row, cell.Col)))
            {
                throw Invalid("the tilt grid must contain each 3x3 position exactly once");
            }

            if (cell.StarCount < 0)
            {
                throw Invalid($"cell {cell.Row},{cell.Col} has a negative star count");
            }

            countedStars += cell.StarCount;
            ValidateOptionalNonnegative(cell.MedianHfr, $"cell {cell.Row},{cell.Col} median HFR");
            ValidateOptionalRange(
                cell.MedianEccentricity,
                0,
                1,
                $"cell {cell.Row},{cell.Col} median eccentricity");
            ValidateOptionalRange(
                cell.MeanTheta,
                0,
                Math.PI,
                $"cell {cell.Row},{cell.Col} mean theta",
                maximumInclusive: false);
            RequireFiniteRange(
                cell.ThetaCoherence,
                0,
                1,
                $"cell {cell.Row},{cell.Col} theta coherence",
                maximumInclusive: true);

            if (cell.StarCount == 0 &&
                (cell.MedianHfr.HasValue || cell.MedianEccentricity.HasValue ||
                 cell.MeanTheta.HasValue || cell.ThetaCoherence != 0))
            {
                throw Invalid($"empty cell {cell.Row},{cell.Col} contains measurements");
            }

            if (cell.StarCount > 0 && !cell.MedianHfr.HasValue)
            {
                throw Invalid($"non-empty cell {cell.Row},{cell.Col} is missing median HFR");
            }

            if (!cell.MeanTheta.HasValue && cell.ThetaCoherence != 0)
            {
                throw Invalid($"cell {cell.Row},{cell.Col} has coherence without a direction");
            }
        }

        if (countedStars != starCount)
        {
            throw Invalid("cell star counts do not match the detected-star count");
        }
    }

    private static void ValidateTilt(StarAnalysisTilt tilt, StarAnalysisCell[] cells)
    {
        ValidateOptionalNonnegative(tilt.CenterHfr, "center HFR");
        ValidateOptionalNonnegative(tilt.MeanHfr, "mean HFR");
        ValidateOptionalNonnegative(tilt.TiltPercent, "tilt percent");
        ValidateOptionalFinite(tilt.CurvaturePercent, "curvature percent");

        if (tilt.Corners is null || tilt.Corners.Length != 4)
        {
            throw Invalid("the tilt summary must contain exactly four corners");
        }

        var corners = new HashSet<StarAnalysisCornerPosition>();
        foreach (StarAnalysisCorner? corner in tilt.Corners)
        {
            if (corner is null || !Enum.IsDefined(corner.Corner) || !corners.Add(corner.Corner))
            {
                throw Invalid("the tilt summary must contain each corner exactly once");
            }

            ValidateOptionalNonnegative(corner.Hfr, $"{corner.Corner} HFR");
            (int row, int col) = CornerCell(corner.Corner);
            double? cellHfr = cells.Single(cell => cell.Row == row && cell.Col == col).MedianHfr;
            if (corner.Hfr != cellHfr)
            {
                throw Invalid($"{corner.Corner} HFR does not match its grid cell");
            }
        }

        double? centerCellHfr = cells.Single(cell => cell.Row == 1 && cell.Col == 1).MedianHfr;
        if (tilt.CenterHfr != centerCellHfr)
        {
            throw Invalid("center HFR does not match the center grid cell");
        }

        bool allCornersMeasured = tilt.Corners.All(corner => corner.Hfr.HasValue);
        bool hasTiltVerdict = tilt.TiltPercent.HasValue;
        if (hasTiltVerdict != allCornersMeasured ||
            hasTiltVerdict != tilt.WorstCorner.HasValue ||
            hasTiltVerdict != tilt.BestCorner.HasValue)
        {
            throw Invalid("tilt verdict and best/worst corners are inconsistent");
        }

        if (tilt.WorstCorner.HasValue &&
            (!Enum.IsDefined(tilt.WorstCorner.Value) ||
             !Enum.IsDefined(tilt.BestCorner!.Value)))
        {
            throw Invalid("tilt verdict contains an unknown corner");
        }

        bool hasAnyCell = cells.Any(cell => cell.MedianHfr.HasValue);
        if (tilt.MeanHfr.HasValue != hasAnyCell)
        {
            throw Invalid("mean HFR availability does not match the grid");
        }

        bool canMeasureCurvature = allCornersMeasured && tilt.CenterHfr is > 0;
        if (tilt.CurvaturePercent.HasValue != canMeasureCurvature)
        {
            throw Invalid("curvature verdict availability does not match the grid");
        }
    }

    private static (int Row, int Col) CornerCell(StarAnalysisCornerPosition corner) => corner switch
    {
        StarAnalysisCornerPosition.TopLeft => (0, 0),
        StarAnalysisCornerPosition.TopRight => (0, 2),
        StarAnalysisCornerPosition.BottomLeft => (2, 0),
        StarAnalysisCornerPosition.BottomRight => (2, 2),
        _ => throw Invalid("unknown corner"),
    };

    private static void ValidateTriangleTilt(
        StarAnalysisTriangleTilt triangle,
        StarAnalysisResult result)
    {
        RequireNormalizedDegrees(triangle.AngleDegrees, "triangle angle");
        RequireFinitePositive(triangle.InnerRadiusPixels, "triangle inner radius");
        RequireFinitePositive(triangle.OuterRadiusPixels, "triangle outer radius");

        double expectedInnerRadius = 0.25 * Math.Sqrt(
            Math.Pow(result.Width / 2.0, 2) +
            Math.Pow(result.Height / 2.0, 2));
        double expectedOuterRadius = 0.5 * Math.Min(result.Width, result.Height);
        RequireApproximately(
            triangle.InnerRadiusPixels,
            expectedInnerRadius,
            "triangle inner radius does not match the image dimensions");
        RequireApproximately(
            triangle.OuterRadiusPixels,
            expectedOuterRadius,
            "triangle outer radius does not match the image dimensions");

        if (triangle.MinimumStarsPerRegion != 3)
        {
            throw Invalid("triangle minimum stars per region must be 3");
        }

        if (triangle.Center is null)
        {
            throw Invalid("triangle center statistics are missing");
        }

        ValidateTriangleRegion(
            triangle.Center.StarCount,
            triangle.Center.MedianHfr,
            "triangle center");

        if (triangle.Sectors is null || triangle.Sectors.Length != 3)
        {
            throw Invalid("triangle tilt must contain exactly three sectors");
        }

        long regionStarCount = triangle.Center.StarCount;
        for (int index = 0; index < triangle.Sectors.Length; index++)
        {
            StarAnalysisTriangleSector? sector = triangle.Sectors[index];
            int expectedSector = index + 1;
            if (sector is null || sector.Sector != expectedSector)
            {
                throw Invalid("triangle sectors must be ordered 1, 2, 3");
            }

            RequireNormalizedDegrees(
                sector.AxisAngleDegrees,
                $"triangle sector {sector.Sector} axis angle");
            double expectedAxis = NormalizeDegrees(
                triangle.AngleDegrees + (sector.Sector - 1) * 120.0);
            RequireApproximately(
                sector.AxisAngleDegrees,
                expectedAxis,
                $"triangle sector {sector.Sector} axis angle is inconsistent");
            ValidateTriangleRegion(
                sector.StarCount,
                sector.MedianHfr,
                $"triangle sector {sector.Sector}");
            regionStarCount += sector.StarCount;
        }

        if (regionStarCount > result.Stars.Length)
        {
            throw Invalid("triangle region star counts exceed the detected-star count");
        }

        long annularStarCount = triangle.Sectors.Sum(
            sector => (long)sector.StarCount);
        bool hasAnnulus = triangle.InnerRadiusPixels < triangle.OuterRadiusPixels;
        if (!hasAnnulus && annularStarCount != 0)
        {
            throw Invalid("triangle sectors contain stars without a usable annulus");
        }

        ValidateOptionalPositive(triangle.OverallMedianHfr, "triangle overall median HFR");
        if (triangle.OverallMedianHfr.HasValue != (annularStarCount > 0))
        {
            throw Invalid("triangle overall median HFR availability is inconsistent");
        }

        bool expectedReady =
            hasAnnulus &&
            triangle.Sectors.All(
                sector => sector.StarCount >= triangle.MinimumStarsPerRegion);
        if (triangle.Ready != expectedReady)
        {
            throw Invalid("triangle readiness is inconsistent with its region samples");
        }

        bool hasCompleteVerdict =
            triangle.TiltPercent.HasValue &&
            triangle.BestSector.HasValue &&
            triangle.WorstSector.HasValue;
        bool hasAnyVerdict =
            triangle.TiltPercent.HasValue ||
            triangle.BestSector.HasValue ||
            triangle.WorstSector.HasValue;
        if (hasAnyVerdict != hasCompleteVerdict || hasCompleteVerdict != triangle.Ready)
        {
            throw Invalid("triangle tilt verdict and readiness are inconsistent");
        }

        if (!triangle.Ready)
        {
            return;
        }

        RequireFiniteNonnegative(triangle.TiltPercent!.Value, "triangle tilt percent");
        int expectedBestSector = triangle.Sectors
            .OrderBy(sector => sector.MedianHfr!.Value)
            .ThenBy(sector => sector.Sector)
            .First()
            .Sector;
        int expectedWorstSector = triangle.Sectors
            .OrderByDescending(sector => sector.MedianHfr!.Value)
            .ThenBy(sector => sector.Sector)
            .First()
            .Sector;
        if (triangle.BestSector != expectedBestSector ||
            triangle.WorstSector != expectedWorstSector)
        {
            throw Invalid("triangle best/worst sectors are inconsistent with their medians");
        }

        double bestHfr = triangle.Sectors[expectedBestSector - 1].MedianHfr!.Value;
        double worstHfr = triangle.Sectors[expectedWorstSector - 1].MedianHfr!.Value;
        double expectedTiltPercent =
            100.0 * (worstHfr - bestHfr) / triangle.OverallMedianHfr!.Value;
        RequireApproximately(
            triangle.TiltPercent.Value,
            expectedTiltPercent,
            "triangle tilt percent is inconsistent with its medians");
    }

    private static void ValidateTriangleRegion(
        int starCount,
        double? medianHfr,
        string name)
    {
        if (starCount < 0)
        {
            throw Invalid($"{name} has a negative star count");
        }

        ValidateOptionalPositive(medianHfr, $"{name} median HFR");
        if (medianHfr.HasValue != (starCount > 0))
        {
            throw Invalid($"{name} median HFR availability does not match its star count");
        }
    }

    private static void ValidateOptionalPositive(double? value, string name)
    {
        if (value.HasValue)
        {
            RequireFinitePositive(value.Value, name);
        }
    }

    private static void RequireNormalizedDegrees(double value, string name)
    {
        RequireFiniteRange(value, 0, 360, name, maximumInclusive: false);
    }

    private static double NormalizeDegrees(double value)
    {
        double normalized = value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static void RequireApproximately(double actual, double expected, string detail)
    {
        double tolerance = 1e-9 * Math.Max(1, Math.Max(Math.Abs(actual), Math.Abs(expected)));
        if (Math.Abs(actual - expected) > tolerance)
        {
            throw Invalid(detail);
        }
    }

    private static void ValidateOptionalNonnegative(double? value, string name)
    {
        if (value.HasValue)
        {
            RequireFiniteNonnegative(value.Value, name);
        }
    }

    private static void ValidateOptionalFinite(double? value, string name)
    {
        if (value.HasValue)
        {
            RequireFinite(value.Value, name);
        }
    }

    private static void ValidateOptionalRange(
        double? value,
        double minimum,
        double maximum,
        string name,
        bool maximumInclusive = true)
    {
        if (value.HasValue)
        {
            RequireFiniteRange(value.Value, minimum, maximum, name, maximumInclusive);
        }
    }

    private static void RequireFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw Invalid($"{name} must be finite and positive");
        }
    }

    private static void RequireFiniteNonnegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw Invalid($"{name} must be finite and nonnegative");
        }
    }

    private static void RequireFiniteRange(
        double value,
        double minimum,
        double maximum,
        string name,
        bool maximumInclusive)
    {
        bool aboveMaximum = maximumInclusive ? value > maximum : value >= maximum;
        if (!double.IsFinite(value) || value < minimum || aboveMaximum)
        {
            string upperBound = maximumInclusive ? "]" : ")";
            throw Invalid($"{name} must be in [{minimum}, {maximum}{upperBound}");
        }
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw Invalid($"{name} must be finite");
        }
    }

    private static InvalidDataException Invalid(string detail) =>
        new($"The Seiza core returned invalid star analysis data: {detail}.");
}
