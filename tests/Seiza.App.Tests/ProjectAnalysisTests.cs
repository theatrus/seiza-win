using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ProjectAnalysisTests
{
    [Fact]
    public async Task EmptyCollectionIsRejectedBeforeNativeWork()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectAnalysisService.AnalyzeAsync([], Path.GetTempPath()));
    }

    [Fact]
    public async Task StarFieldsProduceReportsWithoutChangingInputsOrOverwritingEarlierReports()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"seiza-analysis-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "stars.fits");
            StackingOptionsNativeTests.WriteStarField(input, 0, bayer: false, side: 512);
            byte[] original = File.ReadAllBytes(input);
            var first = await ProjectAnalysisService.AnalyzeAsync([input], directory);
            var second = await ProjectAnalysisService.AnalyzeAsync([input], directory);
            Assert.Equal(1, first.MeasuredFrames);
            Assert.Equal(1, first.TotalFrames);
            Assert.NotEqual(first.HtmlPath, second.HtmlPath);
            Assert.True(File.Exists(first.HtmlPath));
            Assert.Contains("stars.fits", await File.ReadAllTextAsync(second.HtmlPath));
            Assert.Equal(original, File.ReadAllBytes(input));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FailedFramesStillProduceAReadableDiagnosticReport()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"seiza-analysis-test-{Guid.NewGuid():N}");
        try
        {
            var stages = new List<ProjectAnalysisProgress>();
            var report = await ProjectAnalysisService.AnalyzeAsync(
                [Path.Combine(directory, "missing frame.fits")], directory,
                new InlineProgress<ProjectAnalysisProgress>(stages.Add));
            Assert.Equal(0, report.MeasuredFrames);
            Assert.Equal(1, report.TotalFrames);
            string html = await File.ReadAllTextAsync(report.HtmlPath);
            Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("missing frame.fits", html);
            Assert.Contains(stages, p => p.Phase == "frames" && p.Completed == 1 && p.Total == 1);
            Assert.Equal("report", stages[^1].Phase);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("frames", 0)]
    [InlineData("frames", 1)]
    [InlineData("depth", 1)]
    [InlineData("report", 0)]
    public async Task CancellingNativeWorkStopsWithoutPublishingReport(string phase, int completed)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"seiza-analysis-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "stars.fits");
            StackingOptionsNativeTests.WriteStarField(input, 0, bayer: false, side: 512);
            byte[] original = File.ReadAllBytes(input);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<ProjectAnalysisProgress>(p =>
            {
                if (p.Phase == phase && p.Completed == completed) cancellation.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProjectAnalysisService.AnalyzeAsync([input], directory, progress, cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(original, File.ReadAllBytes(input));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EtaWaitsForSamplesAndUsesOnlyTheCurrentPhase()
    {
        var progress = new ProjectAnalysisProgress("frames", 0, 10, "Reading", null);
        Assert.Null(progress.EstimateRemaining(TimeSpan.FromSeconds(10)));
        progress = progress with { Completed = 2 };
        Assert.Equal(20, progress.Percent);
        Assert.Equal(TimeSpan.FromSeconds(80), progress.EstimateRemaining(TimeSpan.FromSeconds(20)));
        Assert.Null((progress with { Completed = 10 }).EstimateRemaining(TimeSpan.FromSeconds(100)));
        Assert.Null((progress with { Phase = "depth", Completed = 0 }).EstimateRemaining(TimeSpan.Zero));
    }
}
