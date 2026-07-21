using CommunityToolkit.Mvvm.ComponentModel;
using Seiza.App.Services;

namespace Seiza.App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    public string CoreVersionText { get; } = $"Seiza Rust core {SeizaCore.Version}";

    [ObservableProperty]
    public partial string FileName { get; set; } = "No image open";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Open an image or a folder to begin.";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasImage { get; set; }

    [ObservableProperty]
    public partial bool CanNavigatePrevious { get; set; }

    [ObservableProperty]
    public partial bool CanNavigateNext { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSolving { get; set; }

    [ObservableProperty]
    public partial bool HasSolution { get; set; }

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    public partial string? SolveErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool NeedsCatalogSetup { get; set; }

    [ObservableProperty]
    public partial string SolveTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolveCoordinatesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolveQualityText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolveOverlayText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolveAvailabilityText { get; set; } = string.Empty;

    public void BeginLoading(string path)
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusText = $"Opening {Path.GetFileName(path)}…";
    }

    public void CompleteLoading(string path, Models.ImageMetadata metadata)
    {
        IsLoading = false;
        HasImage = true;
        FileName = Path.GetFileName(path);

        string color = metadata.ColorKind switch
        {
            "planar-rgb" => "RGB",
            "bayer" => "Bayer",
            "mono" => "monochrome",
            _ => metadata.ColorKind,
        };
        StatusText = $"{metadata.Width:N0} × {metadata.Height:N0} · {metadata.Format} · {color}";
    }

    public void FailLoading(string message)
    {
        IsLoading = false;
        ErrorMessage = message;
        StatusText = HasImage ? StatusText : message;
    }

    public void ResetSolve()
    {
        IsSolving = false;
        HasSolution = false;
        SolveErrorMessage = null;
        NeedsCatalogSetup = false;
        SolveTitleText = string.Empty;
        SolveCoordinatesText = string.Empty;
        SolveQualityText = string.Empty;
        SolveOverlayText = string.Empty;
        SolveAvailabilityText = string.Empty;
    }

    public void BeginSolving()
    {
        IsSolving = true;
        HasSolution = false;
        SolveErrorMessage = null;
        NeedsCatalogSetup = false;
        SolveTitleText = "Solving plate...";
        SolveCoordinatesText = string.Empty;
        SolveQualityText = string.Empty;
        SolveOverlayText = string.Empty;
        SolveAvailabilityText = string.Empty;
    }

    public void CompleteSolve(Models.SolveResult result)
    {
        IsSolving = false;
        HasSolution = true;
        SolveErrorMessage = null;
        NeedsCatalogSetup = false;
        SolveTitleText = $"Solved in {result.ElapsedMilliseconds / 1000.0:0.00}s";
        SolveCoordinatesText =
            $"RA {result.CenterRaDegrees:N5} deg, Dec {result.CenterDecDegrees:N5} deg  -  " +
            $"{result.ScaleArcsecPerPixel:N2} arcsec/px";
        SolveQualityText =
            $"{result.MatchedStars:N0} matched / {result.DetectedStars:N0} detected  -  " +
            $"RMS {result.RmsArcsec:N2} arcsec";

        IReadOnlyDictionary<string, int>? counts = result.OverlayCounts;
        if (counts is not null)
        {
            counts.TryGetValue("deep_sky", out int deepSky);
            counts.TryGetValue("named_stars", out int namedStars);
            counts.TryGetValue("transients", out int transients);
            counts.TryGetValue("minor_bodies", out int minorBodies);
            SolveOverlayText =
                $"{deepSky:N0} deep-sky  -  {namedStars:N0} named stars  -  " +
                $"{transients:N0} transients  -  {minorBodies:N0} solar-system";
        }

        IReadOnlyDictionary<string, bool>? availability = result.OverlayAvailability;
        if (availability is not null)
        {
            var unavailable = new List<string>();
            if (!IsAvailable(availability, "deep_sky") ||
                !IsAvailable(availability, "named_stars"))
            {
                unavailable.Add("object catalog");
            }
            if (!IsAvailable(availability, "transients"))
            {
                unavailable.Add("transients");
            }
            if (!IsAvailable(availability, "minor_bodies"))
            {
                unavailable.Add("solar-system bodies");
            }
            SolveAvailabilityText = unavailable.Count == 0
                ? string.Empty
                : $"Optional overlays unavailable: {string.Join(", ", unavailable)}";
        }
    }

    public void FailSolve(string message, bool needsCatalogSetup)
    {
        IsSolving = false;
        HasSolution = false;
        SolveErrorMessage = message;
        NeedsCatalogSetup = needsCatalogSetup;
        SolveTitleText = string.Empty;
    }

    private static bool IsAvailable(
        IReadOnlyDictionary<string, bool> availability,
        string layer) =>
        availability.TryGetValue(layer, out bool value) && value;
}
