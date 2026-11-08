using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Seiza.App.Models;
using Seiza.App.Services;

namespace Seiza.App;

internal sealed class FlatInspectionControl : StackPanel
{
    private readonly NumberBox _scale = new() { Header = "Saturation level (source units)", Value = 65535 };
    private readonly CheckBox _optics = new() { Content = "Estimate dust distance" };
    private readonly NumberBox _focal = new() { Header = "Focal ratio (f/)", Value = 5 };
    private readonly NumberBox _pixel = new() { Header = "Pixel size after binning (µm)", Value = 3.76 };
    private readonly NumberBox _obstruction = new() { Header = "Obstruction diameter (% of aperture)", Value = 0 };
    private readonly Button _inspect = new() { Content = "Inspect Flat" };
    private readonly TextBlock _status = Text("Measure the linear source; display processing does not affect these readings.");
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly StackPanel _options = new() { Spacing = 8 };
    private readonly ContentControl _optionsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private string? _path;
    private int _generation;
    private int _sourceGeneration;
    public event EventHandler? AvailabilityChanged;
    public bool IsAvailable => Visibility == Visibility.Visible;
    public FlatInspectionControl()
    {
        Visibility = Visibility.Collapsed; Spacing = 8;
        Children.Add(new TextBlock { Text = "Flat inspection", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        Children.Add(_status);
        var options = _options;
        options.Children.Add(_scale); options.Children.Add(Text("Use 65535 for 16-bit ADU or 1 for normalized samples.")); options.Children.Add(_optics);
        var geometry = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        geometry.Children.Add(_focal); geometry.Children.Add(_pixel); geometry.Children.Add(_obstruction);
        geometry.Children.Add(Text("Use the binned pixel size. Dust distances are estimates."));
        options.Children.Add(geometry);
        _optionsHost.Content = options;
        _optics.Click += (_, _) => geometry.Visibility = _optics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(new Expander { Header = "Measurement options", Content = _optionsHost, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch });
        Children.Add(_inspect); Children.Add(_results);
        foreach (var number in new[] { _scale, _focal, _pixel, _obstruction }) number.ValueChanged += (_, _) => MarkChanged();
        _optics.Click += (_, _) => MarkChanged();
        _inspect.Click += async (_, _) => await InspectAsync();
    }
    private void MarkChanged()
    {
        _results.Children.Clear();
        _status.Text = "Settings changed. Inspect again to update the measurements.";
    }
    public async void SetSource(string? path)
    {
        if (_path == path) return;
        _path = path; ++_generation; int generation = ++_sourceGeneration;
        Visibility = Visibility.Collapsed; AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        _results.Children.Clear(); _inspect.IsEnabled = true; _optionsHost.IsEnabled = true;
        _status.Text = "Measure the linear source; display processing does not affect these readings.";
        if (path is null || !ImageFileTypes.IsAstronomyImage(path)) return;
        try
        {
            CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(path);
            if (generation != _sourceGeneration || path != _path) return;
            Visibility = probe.Role == CalibrationFrameRoles.Flat ? Visibility.Visible : Visibility.Collapsed;
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception) { /* Image loading reports unreadable sources separately. */ }
    }
    public async Task InspectAsync()
    {
        if (_path is not { } path || !IsAvailable) return;
        int generation = ++_generation;
        _inspect.IsEnabled = false; _optionsHost.IsEnabled = false; _status.Text = "Measuring the linear flat…";
        try
        {
            FlatInspection result = await FlatInspectionService.InspectAsync(path, new FlatQualityOptions
            {
                Enabled = true, FullScale = _scale.Value, UseOptics = _optics.IsChecked == true,
                FocalRatio = _focal.Value, PixelSizeUm = _pixel.Value, Obstruction = _obstruction.Value / 100,
            });
            if (generation != _generation || path != _path) return;
            Show(result);
        }
        catch (Exception e) { if (generation == _generation) _status.Text = e.Message; }
        finally { if (generation == _generation) { _inspect.IsEnabled = true; _optionsHost.IsEnabled = true; } }
    }
    private void Show(FlatInspection result)
    {
        _results.Children.Clear();
        FlatQualityMeasurement m = result.Measurement;
        _status.Text = result.Verdict is { } verdict ? "Raw exposure grade: " + verdict.Grade
            : "Processed/master flat: measurements only; no raw-exposure grade.";
        foreach (string line in new[] { $"Median level: {m.Level:N2} source units", $"Saturation level: {m.FullScale:N2} source units",
            "Channel medians: " + string.Join(", ", m.ChannelLevels.Select(v => v.ToString("N2", System.Globalization.CultureInfo.CurrentCulture))),
            $"Clipped fraction: {m.ClippedFraction:P3}", $"Response noise: {m.Noise:P2}", $"Detection depth: {m.DetectionDepth:P2}",
            $"Compact dust features: {m.FeatureCount}", $"Vignetting regions: {m.Vignetting.Length}" }) _results.Children.Add(Text(line));
        foreach (FlatFinding finding in result.Verdict?.Findings ?? []) _results.Children.Add(Text(finding.Description));
        _results.Children.Add(Text("Single-frame measurements do not establish set stability or which lights the flat matches."));
        var map = new Grid { ColumnSpacing = 1, RowSpacing = 1 };
        FlatIllumination illumination = m.Illumination;
        for (int x = 0; x < illumination.Columns; x++) map.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int y = 0; y < illumination.Rows; y++) map.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < illumination.Response.Length; i++)
        {
            double value = illumination.Response[i];
            byte gray = (byte)Math.Clamp((int)Math.Round(value * 160), 20, 245);
            var cell = new Border { Height = 12, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, gray, gray, gray)) };
            int x = i % illumination.Columns, y = i / illumination.Columns;
            AutomationProperties.SetName(cell, $"Illumination row {y + 1}, column {x + 1}: {value:G4}");
            ToolTipService.SetToolTip(cell, $"({x + 1}, {y + 1}): {value:G4}");
            Grid.SetColumn(cell, x); Grid.SetRow(cell, y); map.Children.Add(cell);
        }
        _results.Children.Add(Text("Coarse illumination response (normalized)")); _results.Children.Add(map);
        var features = new StackPanel { Spacing = 6 };
        foreach (FlatFeature f in m.Features.Take(50)) features.Children.Add(Text($"{f.Shape}: ({f.X:0.#}, {f.Y:0.#}), radius {f.Radius:0.#} px, depth {f.Depth:P2}" +
            (f.DistanceMm is double distance ? $", about {distance:0.##} mm from sensor" : "")));
        if (m.Features.Length > 50) features.Children.Add(Text($"Showing the first 50 of {m.Features.Length} recorded dust features."));
        foreach (FlatVignetting v in m.Vignetting) features.Children.Add(Text($"Vignetting {v.Edge}: ({v.X:0.#}, {v.Y:0.#}), area {v.Area:P2}, depth {v.Depth:P2}"));
        _results.Children.Add(new Expander { Header = "Dust geometry and vignetting", Content = features,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
