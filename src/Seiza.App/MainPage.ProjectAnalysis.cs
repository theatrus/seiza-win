using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Services;

namespace Seiza.App;

public sealed partial class MainPage
{
    private bool _isAnalyzingProject;
    private CancellationTokenSource? _projectAnalysisCancellation;

    private async void ProjectAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnalyzingProject || _disposed) return;
        string[] inputs = _imagePaths.Where(ImageFileService.IsAstronomyImage).ToArray();
        if (inputs.Length == 0) return;
        _isAnalyzingProject = true;
        using var cancellation = new CancellationTokenSource();
        _projectAnalysisCancellation = cancellation;
        UpdateVisualState();

        var status = new TextBlock { Text = $"Analyzing {inputs.Length:N0} images…", TextWrapping = TextWrapping.Wrap };
        var elapsed = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progressBar = new ProgressBar { IsIndeterminate = true };
        var content = new StackPanel { Spacing = 12, MinWidth = 380, MaxWidth = 520 };
        content.Children.Add(status);
        content.Children.Add(progressBar);
        content.Children.Add(elapsed);
        content.Children.Add(new TextBlock
        {
            Text = "Analyzes the open collection: one panel or pointing of monochrome, non-Bayer FITS/XISF images.",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Seiza project analysis",
            Content = content,
            CloseButtonText = "Cancel"
        };
        bool running = true;
        void CancelAnalysis()
        {
            if (!running || cancellation.IsCancellationRequested) return;
            cancellation.Cancel();
            status.Text = "Cancelling… Waiting for the current image operation to stop safely.";
            dialog.CloseButtonText = "Cancelling…";
        }
        dialog.CloseButtonClick += (_, args) => { if (running) { args.Cancel = true; CancelAnalysis(); } };
        dialog.Closing += (_, args) => { if (running && !_disposed) { args.Cancel = true; CancelAnalysis(); } };
        var clock = Stopwatch.StartNew();
        var phaseClock = Stopwatch.StartNew();
        ProjectAnalysisProgress? currentProgress = null;
        void UpdateTiming()
        {
            elapsed.Text = $"Elapsed: {clock.Elapsed:hh\\:mm\\:ss}";
            if (!running || cancellation.IsCancellationRequested) return;
            var remaining = currentProgress?.EstimateRemaining(phaseClock.Elapsed);
            elapsed.Text += remaining is { } estimate
                ? $"\nEstimated time left in this phase: ~{Math.Max(1, (int)Math.Ceiling(estimate.TotalSeconds)):N0}s"
                : "\nEstimating time remaining…";
            if (currentProgress?.Phase == "frames") elapsed.Text += " · Depth calculation follows.";
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (_disposed) timer.Stop(); else UpdateTiming(); };
        try
        {
            var showing = dialog.ShowAsync();
            timer.Start();
            ProjectAnalysisReport? report = null;
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Seiza", "AnalysisReports");
                report = await ProjectAnalysisService.AnalyzeAsync(inputs, directory,
                    new Progress<ProjectAnalysisProgress>(update =>
                    {
                        if (_disposed || !running || cancellation.IsCancellationRequested) return;
                        if (currentProgress?.Phase != update.Phase) phaseClock.Restart();
                        currentProgress = update;
                        string phase = update.Phase switch
                        {
                            "frames" => "Phase 1 of 2 · Frame analysis",
                            "depth" => "Phase 2 of 2 · Depth calculation",
                            _ => update.Detail
                        };
                        status.Text = update.Percent is { } percent
                            ? $"{phase}: {update.Completed:N0} / {update.Total:N0} ({percent:F0}%)\n{update.Detail}"
                            : phase;
                        if (update.Path is not null) status.Text += "\n" + System.IO.Path.GetFileName(update.Path);
                        progressBar.IsIndeterminate = update.Percent is null;
                        progressBar.Value = update.Percent ?? 0;
                        UpdateTiming();
                    }), cancellation.Token);
                if (!_disposed)
                {
                    dialog.Title = report.MeasuredFrames == 0 ? "No images could be measured" : "Analysis complete";
                    status.Text = $"Measured {report.MeasuredFrames:N0} of {report.TotalFrames:N0} images.\n\n" +
                        "Open the report in your browser?\n\n" + report.HtmlPath;
                    dialog.PrimaryButtonText = "Open report";
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!_disposed)
                {
                    dialog.Title = "Analysis cancelled";
                    status.Text = "Analysis stopped. No report was saved; your source images are unchanged.";
                }
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    dialog.Title = "Analysis failed";
                    status.Text = DescribeException(exception);
                }
            }
            finally
            {
                running = false;
                timer.Stop();
                progressBar.IsIndeterminate = false;
                progressBar.Visibility = Visibility.Collapsed;
                elapsed.Text = $"Elapsed: {clock.Elapsed:hh\\:mm\\:ss}";
                dialog.CloseButtonText = "Close";
                if (_disposed) dialog.Hide();
            }

            if (await showing == ContentDialogResult.Primary && report is not null && !_disposed)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(report.HtmlPath) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    await new ContentDialog
                    {
                        XamlRoot = XamlRoot, Title = "Could not open the browser", CloseButtonText = "Close",
                        Content = $"{DescribeException(exception)}\n\nYour report is saved at:\n{report.HtmlPath}"
                    }.ShowAsync();
                }
            }
        }
        catch (Exception exception)
        {
            if (!_disposed) ViewModel.ErrorMessage = DescribeException(exception);
        }
        finally
        {
            timer.Stop();
            _projectAnalysisCancellation = null;
            _isAnalyzingProject = false;
            if (!_disposed) UpdateVisualState();
        }
    }
}

