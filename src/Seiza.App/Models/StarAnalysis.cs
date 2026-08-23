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
