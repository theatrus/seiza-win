using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Seiza.App.Models;
using Seiza.App.Rendering;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Seiza.App;

public sealed partial class ImageInspector : UserControl
{
    private readonly List<InspectorEntry> _allHeaders = [];

    public event EventHandler? SolveRequested;

    public event EventHandler? ExportWcsRequested;

    public event EventHandler? StarAnalysisRequested;

    public ObservableCollection<InspectorEntry> ImageDetails { get; } = [];

    public ObservableCollection<InspectorEntry> SolveDetails { get; } = [];

    public ObservableCollection<InspectorEntry> StarAnalysisDetails { get; } = [];

    public ObservableCollection<StarTiltCellViewModel> TiltCells { get; } = [];

    public ObservableCollection<InspectorEntry> VisibleHeaders { get; } = [];

    public ImageInspector()
    {
        InitializeComponent();
    }

    public void ClearMetadata()
    {
        ImageDetails.Clear();
        SidebarAnalyzeStarsButton.IsEnabled = false;
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
        bool supportsStarAnalysis = SupportsAstronomyProcessing(metadata);
        SidebarAnalyzeStarsButton.IsEnabled = supportsStarAnalysis;
        if (!supportsStarAnalysis && StarAnalysisDetails.Count == 0)
        {
            StarAnalysisStateText.Text =
                "Star measurement is available for FITS and XISF source images.";
        }

        ImageDetails.Clear();
        ImageDetails.Add(new("Dimensions", $"{metadata.Width:N0} × {metadata.Height:N0}"));
        ImageDetails.Add(new("Format", metadata.Format));
        ImageDetails.Add(new("Encoding", FormatColorKind(metadata.ColorKind)));
        if (SupportsAstronomyProcessing(metadata))
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
                processing.BackgroundConfiguration?.Summary ?? "Original"));
            if (metadata.BackgroundProcessing is { } background)
            {
                ImageDetails.Add(new("Fitted model", background.ModelTitle));
                ImageDetails.Add(new(
                    "Background samples",
                    $"{background.Diagnostics.AcceptedSamples:N0} of " +
                    $"{background.Diagnostics.CandidateSamples:N0}"));
            }
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
        SidebarSolveButton.Content = "Solve";
        SidebarSolveButton.Visibility = Visibility.Visible;
        ExportWcsButton.Visibility = Visibility.Collapsed;
        CatalogSettingsButton.Visibility = Visibility.Collapsed;
    }

    public void ResetStarAnalysis()
    {
        StarAnalysisDetails.Clear();
        TiltCells.Clear();
        StarAnalysisStateText.Text = "Not analyzed";
        StarAnalysisProgressRing.IsActive = false;
        StarAnalysisProgressRing.Visibility = Visibility.Collapsed;
        TiltCellsRepeater.Visibility = Visibility.Collapsed;
        StarAnalysisGuidanceText.Visibility = Visibility.Collapsed;
        SidebarAnalyzeStarsButton.Content = "Analyze stars";
        SidebarAnalyzeStarsButton.Visibility = Visibility.Visible;
    }

    public void BeginStarAnalysis()
    {
        StarAnalysisDetails.Clear();
        TiltCells.Clear();
        StarAnalysisStateText.Text = "Measuring source stars…";
        StarAnalysisProgressRing.Visibility = Visibility.Visible;
        StarAnalysisProgressRing.IsActive = true;
        TiltCellsRepeater.Visibility = Visibility.Collapsed;
        StarAnalysisGuidanceText.Visibility = Visibility.Collapsed;
        SidebarAnalyzeStarsButton.Visibility = Visibility.Collapsed;
    }

    public void ShowStarAnalysisFailure(string message)
    {
        StarAnalysisDetails.Clear();
        TiltCells.Clear();
        StarAnalysisStateText.Text = message;
        StarAnalysisProgressRing.IsActive = false;
        StarAnalysisProgressRing.Visibility = Visibility.Collapsed;
        TiltCellsRepeater.Visibility = Visibility.Collapsed;
        StarAnalysisGuidanceText.Visibility = Visibility.Collapsed;
        SidebarAnalyzeStarsButton.Content = "Try again";
        SidebarAnalyzeStarsButton.Visibility = Visibility.Visible;
    }

    public void ShowStarAnalysisResult(StarAnalysisResult result)
    {
        StarAnalysisDetails.Clear();
        TiltCells.Clear();
        StarAnalysisProgressRing.IsActive = false;
        StarAnalysisProgressRing.Visibility = Visibility.Collapsed;
        SidebarAnalyzeStarsButton.Content = "Analyze again";
        SidebarAnalyzeStarsButton.Visibility = Visibility.Visible;
        StarAnalysisGuidanceText.Visibility = Visibility.Visible;

        int count = result.Stars.Length;
        StarAnalysisStateText.Text = count == 0
            ? "No measurable stars were found."
            : $"Measured {count:N0} stars in the linear source image.";
        StarAnalysisDetails.Add(new("Stars", count.ToString("N0", CultureInfo.CurrentCulture)));
        if (count > 0)
        {
            StarAnalysisDetails.Add(new("Average HFR", $"{result.AverageHfr:N2} px"));
            StarAnalysisDetails.Add(new("Average FWHM", $"{result.AverageFwhm:N2} px"));
        }
        StarAnalysisDetails.Add(new("Background", result.BackgroundMean.ToString("N2", CultureInfo.CurrentCulture)));
        StarAnalysisDetails.Add(new("Noise σ", result.NoiseSigma.ToString("N2", CultureInfo.CurrentCulture)));

        if (result.Tilt.TiltPercent is double tilt)
        {
            StarAnalysisDetails.Add(new("Corner tilt", $"{tilt:N1}%"));
        }
        else
        {
            StarAnalysisDetails.Add(new("Corner tilt", "Needs stars in all four corners"));
        }
        if (result.Tilt.CurvaturePercent is double curvature)
        {
            StarAnalysisDetails.Add(new("Field curvature", $"{curvature:+0.0;-0.0;0.0}%"));
        }
        else
        {
            StarAnalysisDetails.Add(new("Field curvature", "Needs four corners and center"));
        }

        TiltCellOverlayMeasurement[] cellMeasurements = result.Cells
            .Select(cell => new TiltCellOverlayMeasurement(
                cell.Row,
                cell.Col,
                cell.StarCount,
                cell.MedianHfr))
            .ToArray();
        TiltCellOverlayMeasurement[] cornerMeasurements = cellMeasurements
            .Where(cell => cell.Row != 1 && cell.Column != 1)
            .ToArray();
        bool hasReliableCorners = cornerMeasurements.Length == 4 &&
            cornerMeasurements.All(StarAnalysisOverlayGeometry.IsReliableCell);
        TiltCellOverlayMeasurement centerMeasurement = cellMeasurements.Single(
            cell => cell.Row == 1 && cell.Column == 1);
        bool hasReliableCenter = StarAnalysisOverlayGeometry.IsReliableCell(centerMeasurement);
        bool hasLowConfidenceVerdict =
            (result.Tilt.TiltPercent is not null && !hasReliableCorners) ||
            (result.Tilt.CurvaturePercent is not null &&
                (!hasReliableCorners || !hasReliableCenter));
        if (hasLowConfidenceVerdict)
        {
            StarAnalysisDetails.Add(new(
                "Confidence",
                "Low — fewer than 3 stars in a required corner or center"));
        }
        else if (result.Tilt.TiltPercent is not null ||
            result.Tilt.CurvaturePercent is not null)
        {
            StarAnalysisDetails.Add(new(
                "Confidence",
                "At least 3 stars in every required region"));
        }

        if (hasReliableCorners &&
            StarAnalysisOverlayGeometry.HasMeaningfulReliableSpread(cornerMeasurements))
        {
            if (result.Tilt.BestCorner is { } best)
            {
                StarAnalysisDetails.Add(new("Sharpest corner", FormatCorner(best)));
            }
            if (result.Tilt.WorstCorner is { } worst)
            {
                StarAnalysisDetails.Add(new("Softest corner", FormatCorner(worst)));
            }
        }

        double? sharpest = StarAnalysisOverlayGeometry.FindSharpestReliableHfr(
            cellMeasurements);
        foreach (StarAnalysisCell cell in result.Cells
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Col))
        {
            var measurement = new TiltCellOverlayMeasurement(
                cell.Row,
                cell.Col,
                cell.StarCount,
                cell.MedianHfr);
            bool lowSample = !StarAnalysisOverlayGeometry.IsReliableCell(measurement);
            TiltCellVisualKind visualKind = StarAnalysisOverlayGeometry.ClassifyCell(
                measurement,
                sharpest);
            Color color = visualKind switch
            {
                TiltCellVisualKind.Good => Color.FromArgb(54, 36, 176, 91),
                TiltCellVisualKind.Warning => Color.FromArgb(62, 236, 169, 45),
                TiltCellVisualKind.Poor => Color.FromArgb(62, 224, 72, 72),
                _ => Color.FromArgb(42, 128, 128, 128),
            };
            string detail = $"{cell.StarCount:N0} star{(cell.StarCount == 1 ? string.Empty : "s")}";
            if (cell.MedianEccentricity is double eccentricity)
            {
                detail += $" · e {eccentricity:N2}";
            }
            if (lowSample && cell.StarCount > 0)
            {
                detail += " · low sample";
            }
            TiltCells.Add(new StarTiltCellViewModel(
                FormatCell(cell.Row, cell.Col),
                cell.MedianHfr is double median ? $"HFR {median:N2} px" : "No stars",
                detail,
                new SolidColorBrush(color)));
        }
        TiltCellsRepeater.Visibility = TiltCells.Count == 9
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void BeginSolve()
    {
        SolveDetails.Clear();
        SolveStateText.Text = "Solving…";
        SolveProgressRing.Visibility = Visibility.Visible;
        SolveProgressRing.IsActive = true;
        SidebarSolveButton.Visibility = Visibility.Collapsed;
        ExportWcsButton.Visibility = Visibility.Collapsed;
        CatalogSettingsButton.Visibility = Visibility.Collapsed;
    }

    public void ShowSolveFailure(string message, bool needsCatalogSetup)
    {
        SolveDetails.Clear();
        SolveStateText.Text = message;
        SolveProgressRing.IsActive = false;
        SolveProgressRing.Visibility = Visibility.Collapsed;
        SidebarSolveButton.Content = "Try again";
        SidebarSolveButton.Visibility = Visibility.Visible;
        ExportWcsButton.Visibility = Visibility.Collapsed;
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
        SidebarSolveButton.Visibility = Visibility.Collapsed;
        ExportWcsButton.Visibility = Visibility.Visible;
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
            "Plate-solve detections",
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
            ? "No source headers"
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

    private void SidebarSolve_Click(object sender, RoutedEventArgs e) =>
        SolveRequested?.Invoke(this, EventArgs.Empty);

    private void ExportWcs_Click(object sender, RoutedEventArgs e) =>
        ExportWcsRequested?.Invoke(this, EventArgs.Empty);

    private void SidebarAnalyzeStars_Click(object sender, RoutedEventArgs e) =>
        StarAnalysisRequested?.Invoke(this, EventArgs.Empty);

    private static string FormatCell(int row, int col) => (row, col) switch
    {
        (0, 0) => "Top left",
        (0, 1) => "Top",
        (0, 2) => "Top right",
        (1, 0) => "Left",
        (1, 1) => "Center",
        (1, 2) => "Right",
        (2, 0) => "Bottom left",
        (2, 1) => "Bottom",
        (2, 2) => "Bottom right",
        _ => $"{row + 1}, {col + 1}",
    };

    private static string FormatCorner(StarAnalysisCornerPosition corner) => corner switch
    {
        StarAnalysisCornerPosition.TopLeft => "Top left",
        StarAnalysisCornerPosition.TopRight => "Top right",
        StarAnalysisCornerPosition.BottomLeft => "Bottom left",
        StarAnalysisCornerPosition.BottomRight => "Bottom right",
        _ => corner.ToString(),
    };

    private static bool SupportsColorStretch(ImageMetadata metadata) =>
        metadata.ColorKind is "planar-rgb" or "bayer";

    private static bool SupportsAstronomyProcessing(ImageMetadata metadata) =>
        string.Equals(metadata.Format, "FITS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(metadata.Format, "XISF", StringComparison.OrdinalIgnoreCase);

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

public sealed class StarTiltCellViewModel
{
    public StarTiltCellViewModel(string label, string hfr, string detail, Brush background)
    {
        Label = label;
        Hfr = hfr;
        Detail = detail;
        Background = background;
    }

    public string Label { get; }

    public string Hfr { get; }

    public string Detail { get; }

    public Brush Background { get; }
}
