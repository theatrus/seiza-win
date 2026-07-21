using System.Text.Json.Serialization;

namespace Seiza.App.Models;

public sealed class SolveResult
{
    public double CenterRaDegrees { get; init; }

    public double CenterDecDegrees { get; init; }

    public double ScaleArcsecPerPixel { get; init; }

    public int MatchedStars { get; init; }

    public double RmsArcsec { get; init; }

    public int DetectedStars { get; init; }

    public int ElapsedMilliseconds { get; init; }

    public SolveImagePoint[] DetectedStarPositions { get; init; } = [];

    public SolveCatalogStarPoint[] CatalogStarPositions { get; init; } = [];

    public SolveObjectPoint[] ObjectPositions { get; init; } = [];

    public string? ObjectCatalogError { get; init; }

    public string? CaptureTime { get; init; }

    public IReadOnlyDictionary<string, bool>? OverlayAvailability { get; init; }

    public IReadOnlyDictionary<string, string>? OverlayUnavailableReasons { get; init; }

    public IReadOnlyDictionary<string, int>? OverlayCounts { get; init; }

    public required WcsResult Wcs { get; init; }
}

public sealed class SolveImagePoint
{
    public double X { get; init; }

    public double Y { get; init; }
}

public sealed class SolveCatalogStarPoint
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Magnitude { get; init; }
}

public sealed class SolveObjectPoint
{
    public string? StableId { get; init; }

    public required string Name { get; init; }

    public string CommonName { get; init; } = string.Empty;

    public required string Kind { get; init; }

    public required string Source { get; init; }

    public string? CatalogSource { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double SemiMajorPixels { get; init; }

    public double SemiMinorPixels { get; init; }

    public double? AngleDegrees { get; init; }

    public double? Prominence { get; init; }

    public double? RaDegrees { get; init; }

    public double? DecDegrees { get; init; }

    public string? Discovered { get; init; }

    public bool? NearCapture { get; init; }

    public double? DistanceAu { get; init; }

    public double? MotionArcsecPerHour { get; init; }

    public double? DirectionPositionAngleDegrees { get; init; }

    public double? DirectionImageAngleDegrees { get; init; }

    public SolveObjectOutline[] Outlines { get; init; } = [];

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(CommonName) ||
        string.Equals(CommonName, Name, StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Name} · {CommonName}";
}

public sealed class SolveObjectOutline
{
    public required string GeometryId { get; init; }

    public required string SourceRecordId { get; init; }

    public required string Role { get; init; }

    public required string Quality { get; init; }

    public string? Level { get; init; }

    public SolveObjectContour[] Contours { get; init; } = [];
}

public sealed class SolveObjectContour
{
    public bool Closed { get; init; }

    public double[][] Points { get; init; } = [];
}

public sealed class WcsResult
{
    public double[] Crval { get; init; } = [];

    public double[] Crpix { get; init; } = [];

    public double[][] Cd { get; init; } = [];

    public SipResult? Sip { get; init; }
}

public sealed class SipResult
{
    public int Order { get; init; }

    public double[] A { get; init; } = [];

    public double[] B { get; init; } = [];

    public double[] Ap { get; init; } = [];

    public double[] Bp { get; init; } = [];
}
