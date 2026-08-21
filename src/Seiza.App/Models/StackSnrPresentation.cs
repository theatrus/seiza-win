namespace Seiza.App.Models;

internal sealed record StackSnrPlotPoint(
    uint Frames,
    double Snr,
    double Noise,
    double ExposureSeconds);

internal sealed record StackSnrMeasurement(
    uint Frames,
    double Noise,
    double Background,
    double Signal,
    double ExposureSeconds = 0);

internal sealed record StackSnrAnalysis(
    IReadOnlyList<StackSnrPlotPoint> Points,
    double NoiseImprovement,
    double IdealImprovement,
    double Efficiency)
{
    public static StackSnrAnalysis Empty { get; } = new([], 0, 0, 0);
}

internal static class StackSnrAnalyzer
{
    public static StackSnrAnalysis Analyze(IEnumerable<StackSnrMeasurement> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StackSnrMeasurement[] samples = source
            .Where(sample =>
                sample.Frames > 0 &&
                double.IsFinite(sample.Noise) &&
                sample.Noise > 0 &&
                double.IsFinite(sample.Signal))
            .OrderBy(sample => sample.Frames)
            .GroupBy(sample => sample.Frames)
            .Select(group => group.Last())
            .ToArray();
        if (samples.Length == 0)
        {
            return StackSnrAnalysis.Empty;
        }

        double commonSignal = samples[^1].Signal;
        if (!double.IsFinite(commonSignal) || commonSignal <= 0)
        {
            return StackSnrAnalysis.Empty;
        }
        StackSnrPlotPoint[] points = samples.Select(sample => new StackSnrPlotPoint(
            sample.Frames,
            commonSignal / sample.Noise,
            sample.Noise,
            sample.ExposureSeconds)).ToArray();
        double noiseImprovement = samples[0].Noise / samples[^1].Noise;
        double idealImprovement = Math.Sqrt((double)samples[^1].Frames / samples[0].Frames);
        double efficiency = idealImprovement > 0 ? noiseImprovement / idealImprovement : 0;
        return new StackSnrAnalysis(points, noiseImprovement, idealImprovement, efficiency);
    }
}

internal static class StackSnrMeasurementPolicy
{
    public static bool TryBegin(
        int acceptedFrames,
        IReadOnlySet<int> scheduledDepths,
        ISet<int> attemptedDepths,
        bool includeCurrentDepth)
    {
        ArgumentNullException.ThrowIfNull(scheduledDepths);
        ArgumentNullException.ThrowIfNull(attemptedDepths);
        if (acceptedFrames <= 0 ||
            (!includeCurrentDepth && !scheduledDepths.Contains(acceptedFrames)))
        {
            return false;
        }

        // A scheduled depth is scanned once even if subsequent input frames
        // are rejected and the accepted count does not advance. The one final
        // call may retry a depth that was unavailable earlier.
        return includeCurrentDepth || attemptedDepths.Add(acceptedFrames);
    }
}

internal readonly record struct StackSnrChartPoint(double X, double Y);

internal sealed record StackSnrPlotGeometry(
    IReadOnlyList<StackSnrChartPoint> Measured,
    IReadOnlyList<StackSnrChartPoint> Ideal,
    uint MinimumFrames,
    uint MaximumFrames,
    double MinimumSnr,
    double MaximumSnr)
{
    public static StackSnrPlotGeometry Empty { get; } = new(
        [],
        [],
        0,
        0,
        0,
        0);
}

internal static class StackSnrPlotLayout
{
    public static StackSnrPlotGeometry Create(
        IEnumerable<StackSnrPlotPoint> source,
        double width,
        double height,
        double horizontalPadding = 12,
        double verticalPadding = 10)
    {
        StackSnrPlotPoint[] points = source
            .Where(point =>
                point.Frames > 0 &&
                double.IsFinite(point.Snr) &&
                point.Snr > 0)
            .OrderBy(point => point.Frames)
            .GroupBy(point => point.Frames)
            .Select(group => group.Last())
            .ToArray();
        if (points.Length == 0 ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= horizontalPadding * 2 ||
            height <= verticalPadding * 2)
        {
            return StackSnrPlotGeometry.Empty;
        }

        StackSnrPlotPoint first = points[0];
        var idealValues = points.Select(point =>
            first.Snr * Math.Sqrt((double)point.Frames / first.Frames)).ToArray();
        double minimumSnr = points.Select(point => point.Snr)
            .Concat(idealValues)
            .Min();
        double maximumSnr = points.Select(point => point.Snr)
            .Concat(idealValues)
            .Max();
        uint minimumFrames = points[0].Frames;
        uint maximumFrames = points[^1].Frames;

        double minimumLogFrames = Math.Log(minimumFrames);
        double maximumLogFrames = Math.Log(maximumFrames);
        double minimumLogSnr = Math.Log(minimumSnr);
        double maximumLogSnr = Math.Log(maximumSnr);
        double frameSpan = Math.Max(maximumLogFrames - minimumLogFrames, 1.0e-9);
        double snrSpan = Math.Max(maximumLogSnr - minimumLogSnr, 1.0e-9);
        double plotWidth = width - horizontalPadding * 2;
        double plotHeight = height - verticalPadding * 2;

        StackSnrChartPoint Map(uint frames, double snr) => new(
            horizontalPadding +
                (Math.Log(frames) - minimumLogFrames) / frameSpan * plotWidth,
            verticalPadding + plotHeight -
                (Math.Log(snr) - minimumLogSnr) / snrSpan * plotHeight);

        return new StackSnrPlotGeometry(
            points.Select(point => Map(point.Frames, point.Snr)).ToArray(),
            points.Zip(idealValues, (point, ideal) => Map(point.Frames, ideal)).ToArray(),
            minimumFrames,
            maximumFrames,
            minimumSnr,
            maximumSnr);
    }
}
