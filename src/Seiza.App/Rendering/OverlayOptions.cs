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

    public bool ShowFieldStars { get; set; }

    public bool ShowFieldCenter { get; set; } = true;

    public HashSet<DeepSkyCatalog> HiddenDeepSkyCatalogs { get; } = [];

    public bool HasVisibleOverlays =>
        ShowDeepSky ||
        ShowNamedStars ||
        ShowTransients ||
        ShowHistoricalTransients ||
        ShowMinorBodies ||
        ShowCoordinateGrid ||
        ShowDetectedStars ||
        ShowFieldStars ||
        ShowFieldCenter;

    public void HideAll()
    {
        ShowDeepSky = false;
        ShowNamedStars = false;
        ShowTransients = false;
        ShowHistoricalTransients = false;
        ShowMinorBodies = false;
        ShowCoordinateGrid = false;
        ShowDetectedStars = false;
        ShowFieldStars = false;
        ShowFieldCenter = false;
    }
}
