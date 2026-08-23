namespace Seiza.App.Rendering;

internal enum DeepSkyCatalog
{
    Messier,
    Ngc,
    Ic,
    SharplessVdb,
    Lbn,
    Cederblad,
    DarkNebulae,
    SupernovaRemnants,
    Ugc,
    Pgc,
    Other,
}

internal sealed class OverlayOptions
{
    public bool ShowDeepSky { get; set; } = true;

    public bool ShowNamedStars { get; set; } = true;

    public bool ShowTransients { get; set; } = true;

    public bool ShowHistoricalTransients { get; set; }

    public bool ShowMinorBodies { get; set; } = true;

    public bool ShowCoordinateGrid { get; set; } = true;

    public bool ShowCatalogOutlines { get; set; } = true;

    public bool ShowObjectLabels { get; set; } = true;

    public bool ShowDetectedStars { get; set; }

    public bool ShowMeasuredStars { get; set; }

    public bool ShowSensorTilt { get; set; }

    public bool ShowFieldStars { get; set; }

    public bool ShowFieldCenter { get; set; } = true;

    public HashSet<DeepSkyCatalog> HiddenDeepSkyCatalogs { get; } = [];

    public bool HasVisibleSolveOverlays =>
        ShowDeepSky ||
        ShowNamedStars ||
        ShowTransients ||
        ShowHistoricalTransients ||
        ShowMinorBodies ||
        ShowCoordinateGrid ||
        ShowDetectedStars ||
        ShowFieldStars ||
        ShowFieldCenter;

    public bool HasVisibleStarAnalysisOverlays =>
        ShowMeasuredStars || ShowSensorTilt;

    public bool HasVisibleOverlays =>
        HasVisibleSolveOverlays || HasVisibleStarAnalysisOverlays;

    public OverlayOptions Snapshot()
    {
        var snapshot = new OverlayOptions
        {
            ShowDeepSky = ShowDeepSky,
            ShowNamedStars = ShowNamedStars,
            ShowTransients = ShowTransients,
            ShowHistoricalTransients = ShowHistoricalTransients,
            ShowMinorBodies = ShowMinorBodies,
            ShowCoordinateGrid = ShowCoordinateGrid,
            ShowCatalogOutlines = ShowCatalogOutlines,
            ShowObjectLabels = ShowObjectLabels,
            ShowDetectedStars = ShowDetectedStars,
            ShowMeasuredStars = ShowMeasuredStars,
            ShowSensorTilt = ShowSensorTilt,
            ShowFieldStars = ShowFieldStars,
            ShowFieldCenter = ShowFieldCenter,
        };
        snapshot.HiddenDeepSkyCatalogs.UnionWith(HiddenDeepSkyCatalogs);
        return snapshot;
    }

    public void HideAll()
    {
        ShowDeepSky = false;
        ShowNamedStars = false;
        ShowTransients = false;
        ShowHistoricalTransients = false;
        ShowMinorBodies = false;
        ShowCoordinateGrid = false;
        ShowDetectedStars = false;
        ShowMeasuredStars = false;
        ShowSensorTilt = false;
        ShowFieldStars = false;
        ShowFieldCenter = false;
    }
}
