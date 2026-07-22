using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Seiza.App;

public sealed partial class ImageInspector : UserControl
{
    private readonly List<InspectorEntry> _allHeaders = [];

    public ObservableCollection<InspectorEntry> ImageDetails { get; } = [];

    public ObservableCollection<InspectorEntry> SolveDetails { get; } = [];

    public ObservableCollection<InspectorEntry> VisibleHeaders { get; } = [];

    public ImageInspector()
    {
        InitializeComponent();
    }

    public void ClearMetadata()
    {
        ImageDetails.Clear();
        _allHeaders.Clear();
        VisibleHeaders.Clear();
        HeaderSearchBox.Text = string.Empty;
        ClearHistograms();
        UpdateHeaderState();
    }

    internal void ShowMetadata(
        ImageMetadata metadata,
        FitsImageProcessingConfiguration processing)
    {
        ImageDetails.Clear();
        ImageDetails.Add(new("Dimensions", $"{metadata.Width:N0} × {metadata.Height:N0}"));
        ImageDetails.Add(new("Format", metadata.Format));
        ImageDetails.Add(new("Encoding", FormatColorKind(metadata.ColorKind)));
        if (string.Equals(metadata.Format, "FITS", StringComparison.OrdinalIgnoreCase))
        {
            FitsStretchConfiguration current = processing.StretchStack.Stages[^1];
            string stretch = processing.StretchStack.Stages.Count == 1
                ? current.Type.Title()
                : $"{processing.StretchStack.Stages.Count} stages · {current.Type.Title()}";
            ImageDetails.Add(new("Stretch", stretch));
            if (SupportsColorStretch(metadata))
            {
                ImageDetails.Add(new("Color", current.ColorStrategy.Title()));
            }
            ImageDetails.Add(new(
                "Background",
                processing.ExtractsBackground ? "Gradient removed" : "Original"));
            if (processing.Deconvolution is { } deconvolution)
            {
                ImageDetails.Add(new("Deconvolution", "Light Richardson–Lucy"));
                ImageDetails.Add(new(
                    "PSF FWHM",
                    $"{deconvolution.PsfFwhmPixels:N2} px"));
                ImageDetails.Add(new(
                    "Restoration",
                    $"{deconvolution.Iterations} iterations · {deconvolution.Amount:P0} amount"));
            }
            else
            {
                ImageDetails.Add(new("Deconvolution", "Off"));
            }
        }
        ImageDetails.Add(new("Minimum", metadata.Statistics.Minimum.ToString("N0", CultureInfo.CurrentCulture)));
        ImageDetails.Add(new("Maximum", metadata.Statistics.Maximum.ToString("N0", CultureInfo.CurrentCulture)));
        ImageDetails.Add(new("Mean", metadata.Statistics.Mean.ToString("N2", CultureInfo.CurrentCulture)));
        ImageDetails.Add(new("Median", metadata.Statistics.Median.ToString("N0", CultureInfo.CurrentCulture)));
        ImageDetails.Add(new("MAD", metadata.Statistics.Mad.ToString("N2", CultureInfo.CurrentCulture)));

        ShowHistograms(metadata);

        _allHeaders.Clear();
        _allHeaders.AddRange(metadata.Headers
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new InspectorEntry(pair.Key, FormatHeaderValue(pair.Value))));
        ApplyHeaderFilter();
    }

    private void ShowHistograms(ImageMetadata metadata)
    {
        bool isMonochrome = metadata.ColorKind.StartsWith("mono", StringComparison.OrdinalIgnoreCase);
        bool hasInput = metadata.InputHistogram is { IsValid: true };
        bool hasDisplay = metadata.DisplayHistogram is { IsValid: true };
        HistogramsSection.Visibility = hasInput || hasDisplay
            ? Visibility.Visible
            : Visibility.Collapsed;

        InputHistogramSection.Visibility = hasInput ? Visibility.Visible : Visibility.Collapsed;
        if (metadata.InputHistogram is { IsValid: true } input)
        {
            InputHistogramPlot.ShowHistogram(input, isMonochrome);
            InputHistogramLowerLabel.Text = FormatHistogramLevel(input.LowerBound);
            InputHistogramUpperLabel.Text = FormatHistogramLevel(input.UpperBound);
        }
        else
        {
            InputHistogramPlot.ClearHistogram();
        }

        DisplayHistogramSection.Visibility = hasDisplay ? Visibility.Visible : Visibility.Collapsed;
        if (metadata.DisplayHistogram is { IsValid: true } display)
        {
            DisplayHistogramPlot.ShowHistogram(display, isMonochrome);
            DisplayHistogramLowerLabel.Text = FormatHistogramLevel(display.LowerBound);
            DisplayHistogramUpperLabel.Text = FormatHistogramLevel(display.UpperBound);
        }
        else
        {
            DisplayHistogramPlot.ClearHistogram();
        }
    }

    private void ClearHistograms()
    {
        HistogramsSection.Visibility = Visibility.Collapsed;
        InputHistogramPlot.ClearHistogram();
        DisplayHistogramPlot.ClearHistogram();
    }

    private static string FormatHistogramLevel(double value) =>
        Math.Abs(value - Math.Round(value)) < double.Epsilon
            ? Math.Round(value).ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("N2", CultureInfo.CurrentCulture);

    public void ResetSolve()
    {
        SolveDetails.Clear();
        SolveStateText.Text = "Not solved";
        SolveProgressRing.IsActive = false;
        SolveProgressRing.Visibility = Visibility.Collapsed;
        CatalogSettingsButton.Visibility = Visibility.Collapsed;
    }

    public void BeginSolve()
    {
        SolveDetails.Clear();
        SolveStateText.Text = "Solving…";
        SolveProgressRing.Visibility = Visibility.Visible;
        SolveProgressRing.IsActive = true;
        CatalogSettingsButton.Visibility = Visibility.Collapsed;
    }

    public void ShowSolveFailure(string message, bool needsCatalogSetup)
    {
        SolveDetails.Clear();
        SolveStateText.Text = message;
        SolveProgressRing.IsActive = false;
        SolveProgressRing.Visibility = Visibility.Collapsed;
        CatalogSettingsButton.Visibility = needsCatalogSetup
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void ShowSolveResult(SolveResult result)
    {
        SolveDetails.Clear();
        SolveStateText.Text = $"Solved in {result.ElapsedMilliseconds / 1000.0:0.00}s";
        SolveProgressRing.IsActive = false;
        SolveProgressRing.Visibility = Visibility.Collapsed;
        CatalogSettingsButton.Visibility = Visibility.Collapsed;

        SolveDetails.Add(new("RA", $"{result.CenterRaDegrees:N5}°"));
        SolveDetails.Add(new("Dec", $"{result.CenterDecDegrees:N5}°"));
        SolveDetails.Add(new("Scale", $"{result.ScaleArcsecPerPixel:N3}″/px"));
        SolveDetails.Add(new("Matches", result.MatchedStars.ToString("N0", CultureInfo.CurrentCulture)));
        SolveDetails.Add(new("Detected", result.DetectedStars.ToString("N0", CultureInfo.CurrentCulture)));
        SolveDetails.Add(new("RMS", $"{result.RmsArcsec:N2}″"));
        if (!string.IsNullOrWhiteSpace(result.CaptureTime))
        {
            SolveDetails.Add(new("Acquired", result.CaptureTime));
        }

        if (result.OverlayCounts is { } counts)
        {
            SolveDetails.Add(new("Deep sky", GetCount(counts, "deep_sky")));
            SolveDetails.Add(new("Named stars", GetCount(counts, "named_stars")));
            SolveDetails.Add(new("Transients", GetCount(counts, "transients")));
            SolveDetails.Add(new("Solar system", GetCount(counts, "minor_bodies")));
        }
        else
        {
            SolveDetails.Add(new("Sky objects", result.ObjectPositions.Length.ToString("N0", CultureInfo.CurrentCulture)));
        }

        SolveDetails.Add(new(
            "Detected diagnostics",
            result.DetectedStarPositions.Length.ToString("N0", CultureInfo.CurrentCulture)));
        SolveDetails.Add(new(
            "Catalog diagnostics",
            result.CatalogStarPositions.Length.ToString("N0", CultureInfo.CurrentCulture)));

        if (!string.IsNullOrWhiteSpace(result.ObjectCatalogError))
        {
            SolveDetails.Add(new("Object overlay", result.ObjectCatalogError));
        }
        if (result.OverlayUnavailableReasons is { } reasons)
        {
            foreach ((string key, string reason) in reasons.OrderBy(pair => pair.Key))
            {
                SolveDetails.Add(new($"{OverlayLayerName(key)} unavailable", reason));
            }
        }
    }

    private void HeaderSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyHeaderFilter();

    private void ApplyHeaderFilter()
    {
        string query = HeaderSearchBox.Text.Trim();
        IEnumerable<InspectorEntry> filtered = string.IsNullOrWhiteSpace(query)
            ? _allHeaders
            : _allHeaders.Where(entry =>
                entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Value.Contains(query, StringComparison.OrdinalIgnoreCase));

        VisibleHeaders.Clear();
        foreach (InspectorEntry entry in filtered)
        {
            VisibleHeaders.Add(entry);
        }
        UpdateHeaderState();
    }

    private void UpdateHeaderState()
    {
        HeadersRepeater.Visibility = VisibleHeaders.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HeadersEmptyText.Text = _allHeaders.Count == 0
            ? "No FITS headers"
            : "No headers match this search";
        HeadersEmptyText.Visibility = VisibleHeaders.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CopyHeadersButton.IsEnabled = VisibleHeaders.Count > 0;
    }

    private void CopyHeaders_Click(object sender, RoutedEventArgs e)
    {
        if (VisibleHeaders.Count == 0)
        {
            return;
        }

        string text = string.Join(
            Environment.NewLine,
            VisibleHeaders.Select(entry => $"{entry.Label} = {entry.Value}"));
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void CatalogSettings_Click(object sender, RoutedEventArgs e) =>
        App.ShowCatalogSettings();

    private static bool SupportsColorStretch(ImageMetadata metadata) =>
        metadata.ColorKind is "planar-rgb" or "bayer";

    private static string FormatColorKind(string colorKind) => colorKind switch
    {
        "planar-rgb" => "Planar RGB",
        "bayer" => "Bayer / OSC",
        "mono" => "Monochrome",
        _ => colorKind,
    };

    private static string FormatHeaderValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "T",
        JsonValueKind.False => "F",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText(),
    };

    private static string GetCount(IReadOnlyDictionary<string, int> counts, string key) =>
        (counts.TryGetValue(key, out int count) ? count : 0)
        .ToString("N0", CultureInfo.CurrentCulture);

    private static string OverlayLayerName(string key) => key switch
    {
        "deep_sky" => "Deep sky",
        "named_stars" => "Named stars",
        "transients" => "Transients",
        "historical_transients" => "Older transients",
        "minor_bodies" => "Solar system",
        "field_stars" => "Field stars",
        _ => key.Replace('_', ' '),
    };
}
