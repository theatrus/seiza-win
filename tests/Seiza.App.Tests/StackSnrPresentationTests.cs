using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackSnrPresentationTests
{
    [Fact]
    public void AnalyzerUsesDeepestSignalAndReportsEfficiencyAgainstSquareRoot()
    {
        StackSnrAnalysis analysis = StackSnrAnalyzer.Analyze(
        [
            new StackSnrMeasurement(1, 8, 0.1, 2),
            new StackSnrMeasurement(4, 5, 0.1, 8),
        ]);

        Assert.Equal([1d, 1.6d], analysis.Points.Select(point => point.Snr));
        Assert.Equal(1.6, analysis.NoiseImprovement, 10);
        Assert.Equal(2, analysis.IdealImprovement, 10);
        Assert.Equal(0.8, analysis.Efficiency, 10);
    }

    [Fact]
    public void AnalyzerRejectsInvalidMeasurementsAndKeepsTheLastDuplicateDepth()
    {
        StackSnrAnalysis analysis = StackSnrAnalyzer.Analyze(
        [
            new StackSnrMeasurement(0, 1, 0, 1),
            new StackSnrMeasurement(2, double.NaN, 0, 1),
            new StackSnrMeasurement(4, 8, 0, 4),
            new StackSnrMeasurement(4, 4, 0, 8),
        ]);

        StackSnrPlotPoint point = Assert.Single(analysis.Points);
        Assert.Equal(2, point.Snr);
    }

    [Fact]
    public void PlotUsesLogAxesAndIncludesTheSquareRootIdeal()
    {
        StackSnrPlotPoint[] points =
        [
            new(1, 10, 20, 300),
            new(4, 18, 11, 1200),
            new(16, 35, 5.7, 4800),
        ];

        StackSnrPlotGeometry geometry = StackSnrPlotLayout.Create(points, 400, 200);

        Assert.Equal(3, geometry.Measured.Count);
        Assert.Equal(3, geometry.Ideal.Count);
        Assert.Equal(1u, geometry.MinimumFrames);
        Assert.Equal(16u, geometry.MaximumFrames);
        Assert.Equal(geometry.Measured[0].X, geometry.Ideal[0].X, 8);
        Assert.True(geometry.Ideal[^1].Y < geometry.Measured[^1].Y);
    }

    [Fact]
    public void PlotDropsInvalidPointsAndKeepsTheNewestPointAtEachDepth()
    {
        StackSnrPlotPoint[] points =
        [
            new(2, 12, 10, 600),
            new(2, 14, 9, 600),
            new(4, double.NaN, 7, 1200),
            new(8, 0, 6, 2400),
        ];

        StackSnrPlotGeometry geometry = StackSnrPlotLayout.Create(points, 400, 200);

        Assert.Single(geometry.Measured);
        Assert.Equal(2u, geometry.MinimumFrames);
        Assert.Equal(2u, geometry.MaximumFrames);
        Assert.Equal(14, geometry.MinimumSnr);
    }

    [Fact]
    public void PlotIsEmptyUntilItHasUsableSpaceAndData()
    {
        Assert.Empty(StackSnrPlotLayout.Create([], 400, 200).Measured);
        Assert.Empty(StackSnrPlotLayout.Create([new(1, 1, 1, 1)], 10, 10).Measured);
    }

    [Fact]
    public void MeasurementPolicyDoesNotRescanAStalledAcceptedDepth()
    {
        IReadOnlySet<int> scheduled = new HashSet<int> { 1, 2, 4, 5 };
        var attempted = new HashSet<int>();

        Assert.True(StackSnrMeasurementPolicy.TryBegin(2, scheduled, attempted, false));
        Assert.False(StackSnrMeasurementPolicy.TryBegin(2, scheduled, attempted, false));
        Assert.True(StackSnrMeasurementPolicy.TryBegin(2, scheduled, attempted, true));
        Assert.False(StackSnrMeasurementPolicy.TryBegin(3, scheduled, attempted, false));
    }
}
