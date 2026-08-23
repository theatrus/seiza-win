using System.Numerics;
using Microsoft.Graphics.Canvas;

namespace Seiza.App.Rendering;

/// <summary>
/// Composes independently available overlay producers. The same scene is drawn
/// into the interactive viewport and full-resolution export targets.
/// </summary>
internal sealed class OverlayScene
{
    public SolveOverlayRenderer? SolveOverlay { get; set; }

    public StarAnalysisOverlayRenderer? StarAnalysisOverlay { get; set; }

    public bool HasSolveOverlay => SolveOverlay is not null;

    public bool HasStarAnalysisOverlay => StarAnalysisOverlay is not null;

    public bool HasAnyOverlay => HasSolveOverlay || HasStarAnalysisOverlay;

    public bool HasVisibleOverlays(OverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (SolveOverlay is not null && options.HasVisibleSolveOverlays) ||
            (StarAnalysisOverlay is not null && options.HasVisibleStarAnalysisOverlays);
    }

    public OverlayScene Snapshot() => new()
    {
        SolveOverlay = SolveOverlay,
        StarAnalysisOverlay = StarAnalysisOverlay,
    };

    public void Clear()
    {
        SolveOverlay = null;
        StarAnalysisOverlay = null;
    }

    public void Draw(
        CanvasDrawingSession drawingSession,
        OverlayOptions options,
        float scaleX,
        float scaleY,
        Vector2 offset)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(options);

        var transform = new ImageSpaceTransform(scaleX, scaleY, offset);
        // The translucent tilt field is a background diagnostic. Draw it
        // before plate-solve annotations so it cannot wash out their labels,
        // then put measured-star markers above both layers.
        StarAnalysisOverlay?.DrawSensorTilt(drawingSession, options, transform);
        SolveOverlay?.Draw(drawingSession, options, scaleX, scaleY, offset);
        StarAnalysisOverlay?.DrawMeasuredStars(drawingSession, options, transform);
    }
}
