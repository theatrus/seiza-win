namespace Seiza.App.Models;

public sealed class CatalogComponentStatus
{
    public bool Available { get; init; }

    public string? Path { get; init; }
}

public sealed class CatalogStatus
{
    public required string Directory { get; init; }

    public bool ReadyForSolving { get; init; }

    public bool ReadyForOverlays { get; init; }

    public required CatalogComponentStatus StarCatalog { get; init; }

    public required CatalogComponentStatus BlindIndex { get; init; }

    public required CatalogComponentStatus Objects { get; init; }

    public required CatalogComponentStatus Transients { get; init; }

    public required CatalogComponentStatus MinorBodies { get; init; }
}
