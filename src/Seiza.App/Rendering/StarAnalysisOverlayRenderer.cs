using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Seiza.App.Models;
using Windows.Foundation;
using Windows.UI;

namespace Seiza.App.Rendering;

internal sealed class StarAnalysisOverlayRenderer
{
    private const float ScreenStrokeWidth = 1.35f;
    private const float EmphasisStrokeWidth = 2.5f;

    private static readonly Color MeasuredStarColor = ColorFromHex(0xFFD479);
    private static readonly Color TiltPerimeterColor = ColorFromHex(0xFFD479);
    private static readonly Color TriangleTiltColor = ColorFromHex(0x62D9FF);
    private static readonly Color GridColor = ColorFromHex(0xD5E0E5, 205);
    private static readonly Color NeutralFillColor = ColorFromHex(0x82909A, 28);
    private static readonly Color NeutralLabelColor = ColorFromHex(0xD5E0E5);
    private static readonly Color LowSampleLabelColor = ColorFromHex(0xFFD479);
    private static readonly Color GoodFillColor = ColorFromHex(0x54D17A, 42);
    private static readonly Color GoodLabelColor = ColorFromHex(0x73EA94);
    private static readonly Color WarningFillColor = ColorFromHex(0xF1B84B, 46);
    private static readonly Color WarningLabelColor = ColorFromHex(0xFFD479);
    private static readonly Color PoorFillColor = ColorFromHex(0xE96767, 50);
    private static readonly Color PoorLabelColor = ColorFromHex(0xFF8B8B);
    private static readonly Color OrientationColor = ColorFromHex(0xEEF7FF, 220);
    private static readonly Color LabelShadowColor = Color.FromArgb(225, 0, 0, 0);

    private readonly StarAnalysisResult _result;
    private readonly double _sourceWidth;
    private readonly double _sourceHeight;
    private readonly StarAnalysisStar[] _selectedStars;
    private readonly StarAnalysisCell?[,] _cells = new StarAnalysisCell?[3, 3];
    private readonly TiltCellOverlayMeasurement[] _cellMeasurements;
    private readonly double? _sharpestReliableHfr;
    private readonly bool _hasMeaningfulSpread;
    private readonly (int Row, int Column)? _sharpestCell;
    private readonly (int Row, int Column)? _softestCell;
    private readonly TiltPerimeterDiagram? _tiltPerimeterDiagram;
    private readonly TriangleTiltDiagram? _triangleTiltDiagram;

    internal bool HasTiltPerimeter => _tiltPerimeterDiagram is not null;

    internal bool HasTriangleTilt => _triangleTiltDiagram is not null;

    public StarAnalysisOverlayRenderer(
        StarAnalysisResult result,
        int sourceWidth,
        int sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        _result = result;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;

        StarAnalysisStar[] resultStars = result.Stars ?? [];
        var measurements = new StarOverlayMeasurement[resultStars.Length];
        for (int index = 0; index < resultStars.Length; index++)
        {
            StarAnalysisStar star = resultStars[index];
            measurements[index] = new(index, star.X, star.Y, star.Hfr);
        }
        _selectedStars = StarAnalysisOverlayGeometry
            .SelectStarIndices(measurements)
            .Select(index => resultStars[index])
            .ToArray();

        foreach (StarAnalysisCell cell in result.Cells ?? [])
        {
            if (cell.Row is >= 0 and < 3 && cell.Col is >= 0 and < 3)
            {
                // Prefer the entry backed by the larger sample if malformed input
                // contains a duplicate cell.
                StarAnalysisCell? current = _cells[cell.Row, cell.Col];
                if (current is null || cell.StarCount > current.StarCount)
                {
                    _cells[cell.Row, cell.Col] = cell;
                }
            }
        }

        var cellMeasurements = new List<TiltCellOverlayMeasurement>(9);
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                StarAnalysisCell? cell = _cells[row, column];
                cellMeasurements.Add(new(
                    row,
                    column,
                    cell?.StarCount ?? 0,
                    cell?.MedianHfr));
            }
        }
        _cellMeasurements = [.. cellMeasurements];
        StarAnalysisOverlayGeometry.TryCreateTiltPerimeter(
            _cellMeasurements,
            _sourceWidth,
            _sourceHeight,
            out _tiltPerimeterDiagram);
        StarAnalysisOverlayGeometry.TryCreateTriangleTilt(
            result.TriangleTilt,
            _sourceWidth,
            _sourceHeight,
            out _triangleTiltDiagram);
        _sharpestReliableHfr = StarAnalysisOverlayGeometry.FindSharpestReliableHfr(
            _cellMeasurements);
        _hasMeaningfulSpread = StarAnalysisOverlayGeometry.HasMeaningfulReliableSpread(
            _cellMeasurements);
        if (_hasMeaningfulSpread)
        {
            TiltCellOverlayMeasurement[] reliable = _cellMeasurements
                .Where(StarAnalysisOverlayGeometry.IsReliableCell)
                .ToArray();
            TiltCellOverlayMeasurement sharpest = reliable.MinBy(cell => cell.MedianHfr!.Value);
            TiltCellOverlayMeasurement softest = reliable.MaxBy(cell => cell.MedianHfr!.Value);
            _sharpestCell = (sharpest.Row, sharpest.Column);
            _softestCell = (softest.Row, softest.Column);
        }
    }

    internal void DrawTriangleTilt(
        CanvasDrawingSession drawingSession,
        OverlayOptions options,
        ImageSpaceTransform transform)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ShowTriangleTilt || _triangleTiltDiagram is not { } diagram)
        {
            return;
        }

        Vector2 center = transform.ToTarget(diagram.Center);
        Vector2[] vertices = diagram.Vertices
            .Select(vertex => transform.ToTarget(vertex.Point))
            .ToArray();
        if (!IsFinite(center) || vertices.Any(vertex => !IsFinite(vertex)))
        {
            return;
        }

        for (int index = 0; index < vertices.Length; index++)
        {
            Vector2 start = vertices[index];
            Vector2 end = vertices[(index + 1) % vertices.Length];
            drawingSession.DrawLine(start, end, TriangleTiltColor, EmphasisStrokeWidth);
            drawingSession.DrawLine(center, start, TriangleTiltColor, ScreenStrokeWidth);
            drawingSession.FillCircle(start, 2.75f, TriangleTiltColor);
        }

        float sourceFontSize = Math.Clamp(
            (float)(Math.Min(_sourceWidth, _sourceHeight) / 55),
            20,
            72);
        float targetFontSize = MathF.Max(
            sourceFontSize * transform.AverageAbsoluteScale,
            0.1f);
        if (targetFontSize < 8)
        {
            return;
        }

        using CanvasTextFormat valueFormat = new()
        {
            FontFamily = "Segoe UI",
            FontSize = targetFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        TargetRectangle targetImageBounds = transform.ToTarget(
            new SourceRectangle(0, 0, _sourceWidth, _sourceHeight));
        for (int index = 0; index < vertices.Length; index++)
        {
            Vector2 outward = vertices[index] - center;
            if (outward.LengthSquared() > 0)
            {
                outward = Vector2.Normalize(outward);
            }
            Vector2 labelCenter = vertices[index] +
                (outward * MathF.Max(targetFontSize * 0.85f, 5));
            float labelWidth = targetFontSize * 7.2f;
            float labelHeight = targetFontSize * 1.45f;
            labelCenter = ClampLabelCenter(
                labelCenter,
                targetImageBounds,
                labelWidth,
                labelHeight);
            TriangleTiltVertex vertex = diagram.Vertices[index];
            string label = string.Create(
                CultureInfo.InvariantCulture,
                $"S{vertex.Sector}  HFR {vertex.MedianHfr:0.00}");
            DrawCenteredLabel(
                drawingSession,
                label,
                labelCenter,
                labelWidth,
                labelHeight,
                TriangleTiltColor,
                valueFormat);
        }

        string centerLabel = FormatTriangleTiltCenterLabel(diagram);
        int centerLabelLines = centerLabel.Count(character => character == '\n') + 1;
        DrawCenteredLabel(
            drawingSession,
            centerLabel,
            center,
            targetFontSize * 10.5f,
            targetFontSize * (1.25f + (centerLabelLines * 0.85f)),
            TriangleTiltColor,
            valueFormat);
    }

    internal void DrawTiltPerimeter(
        CanvasDrawingSession drawingSession,
        OverlayOptions options,
        ImageSpaceTransform transform)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ShowParallelogramTilt || _tiltPerimeterDiagram is not { } diagram)
        {
            return;
        }

        Vector2 center = transform.ToTarget(diagram.Center);
        Vector2[] vertices = diagram.Vertices
            .Select(vertex => transform.ToTarget(vertex.Point))
            .ToArray();
        if (!IsFinite(center) || vertices.Any(vertex => !IsFinite(vertex)))
        {
            return;
        }

        Color color = TiltPerimeterColor;
        for (int index = 0; index < vertices.Length; index++)
        {
            Vector2 start = vertices[index];
            Vector2 end = vertices[(index + 1) % vertices.Length];
            drawingSession.DrawLine(start, end, color, EmphasisStrokeWidth);
            drawingSession.FillCircle(start, 2.75f, color);
        }
        drawingSession.DrawLine(vertices[0], vertices[2], color, ScreenStrokeWidth);
        drawingSession.DrawLine(vertices[1], vertices[3], color, ScreenStrokeWidth);

        float sourceFontSize = Math.Clamp(
            (float)(Math.Min(_sourceWidth, _sourceHeight) / 55),
            20,
            72);
        float targetFontSize = MathF.Max(
            sourceFontSize * transform.AverageAbsoluteScale,
            0.1f);
        if (targetFontSize < 8)
        {
            return;
        }

        using CanvasTextFormat valueFormat = new()
        {
            FontFamily = "Segoe UI",
            FontSize = targetFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        TargetRectangle targetImageBounds = transform.ToTarget(
            new SourceRectangle(0, 0, _sourceWidth, _sourceHeight));
        for (int index = 0; index < vertices.Length; index++)
        {
            Vector2 outward = vertices[index] - center;
            if (outward.LengthSquared() > 0)
            {
                outward = Vector2.Normalize(outward);
            }
            Vector2 labelCenter = vertices[index] +
                (outward * MathF.Max(targetFontSize * 0.85f, 5));
            float labelWidth = targetFontSize * 5.6f;
            float labelHeight = targetFontSize * 1.45f;
            labelCenter = ClampLabelCenter(
                labelCenter,
                targetImageBounds,
                labelWidth,
                labelHeight);
            string label = "HFR " + diagram.Vertices[index].MedianHfr.ToString(
                "0.00",
                CultureInfo.InvariantCulture);
            DrawCenteredLabel(
                drawingSession,
                label,
                labelCenter,
                labelWidth,
                labelHeight,
                color,
                valueFormat);
        }

        string centerLabel = FormatTiltPerimeterCenterLabel(diagram);
        DrawCenteredLabel(
            drawingSession,
            centerLabel,
            center,
            targetFontSize * 9.5f,
            targetFontSize * (centerLabel.Contains('\n') ? 3.0f : 1.7f),
            color,
            valueFormat);
    }

    internal void DrawMeasuredStars(
        CanvasDrawingSession drawingSession,
        OverlayOptions options,
        ImageSpaceTransform transform)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ShowMeasuredStars)
        {
            return;
        }

        float targetFontSize = MathF.Max(9 * transform.AverageAbsoluteScale, 0.1f);
        using CanvasTextFormat labelFormat = new()
        {
            FontFamily = "Segoe UI",
            FontSize = targetFontSize,
        };
        int labelsDrawn = 0;
        foreach (StarAnalysisStar star in _selectedStars)
        {
            double sourceRadius = Math.Max(2.5 * star.Hfr, 5);
            Vector2 center = transform.ToTarget(star.X, star.Y);
            Vector2 radii = transform.SourceRadiusToTarget(sourceRadius);
            if (!IsFinite(center) || !IsFinite(radii) || radii.X <= 0 || radii.Y <= 0)
            {
                continue;
            }

            drawingSession.DrawEllipse(
                center,
                radii.X,
                radii.Y,
                MeasuredStarColor,
                ScreenStrokeWidth);
            if (sourceRadius < 8)
            {
                drawingSession.FillCircle(center, 1.35f, MeasuredStarColor);
            }

            if (labelsDrawn >= StarAnalysisOverlayGeometry.MaximumStarLabels ||
                !StarAnalysisOverlayGeometry.ShouldDrawStarLabel(radii, targetFontSize))
            {
                continue;
            }

            string label = star.Hfr.ToString("0.00", CultureInfo.InvariantCulture);
            Vector2 position = center + new Vector2(
                radii.X + MathF.Max(3 * transform.AverageAbsoluteScale, 2),
                -(targetFontSize * 0.55f));
            DrawLabel(drawingSession, label, position, MeasuredStarColor, labelFormat);
            labelsDrawn++;
        }
    }

    internal void DrawSensorTilt(
        CanvasDrawingSession drawingSession,
        OverlayOptions options,
        ImageSpaceTransform transform)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ShowSensorTilt)
        {
            return;
        }

        float sourceCellFontSize = Math.Clamp(
            (float)(Math.Min(_sourceWidth / 3, _sourceHeight / 3) / 18),
            15,
            72);
        float targetFontSize = MathF.Max(
            sourceCellFontSize * transform.AverageAbsoluteScale,
            0.1f);
        using CanvasTextFormat cellTextFormat = new()
        {
            FontFamily = "Segoe UI",
            FontSize = targetFontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                StarAnalysisCell? cell = _cells[row, column];
                TiltCellOverlayMeasurement measurement = _cellMeasurements[(row * 3) + column];
                TiltCellVisualKind visualKind = StarAnalysisOverlayGeometry.ClassifyCell(
                    measurement,
                    _sharpestReliableHfr);
                SourceRectangle sourceBounds = StarAnalysisOverlayGeometry.GetCellBounds(
                    row,
                    column,
                    _sourceWidth,
                    _sourceHeight);
                TargetRectangle bounds = transform.ToTarget(sourceBounds);
                Color fillColor = GetFillColor(visualKind);
                Color labelColor = GetLabelColor(visualKind, measurement.StarCount);
                bool drawsOrientation = cell is not null &&
                    StarAnalysisOverlayGeometry.ShouldDrawOrientation(
                        _result.MajorAxisOrientationsNormalized,
                        cell.StarCount,
                        cell.MeanTheta,
                        cell.ThetaCoherence);

                drawingSession.FillRectangle(
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    fillColor);

                bool canDrawLabel = targetFontSize >= 8 &&
                    bounds.Width >= targetFontSize * 5 &&
                    bounds.Height >= targetFontSize * 2.5f;
                if (canDrawLabel)
                {
                    DrawCellLabel(
                        drawingSession,
                        FormatCellLabel(cell),
                        bounds,
                        labelColor,
                        cellTextFormat,
                        drawsOrientation);
                }

                if (drawsOrientation)
                {
                    DrawOrientation(drawingSession, cell!, sourceBounds, transform);
                }
            }
        }

        DrawGridLines(drawingSession, transform);
        if (_hasMeaningfulSpread)
        {
            DrawCellEmphasis(drawingSession, transform, _sharpestCell, GoodLabelColor);
            DrawCellEmphasis(drawingSession, transform, _softestCell, PoorLabelColor);
        }
    }

    private void DrawGridLines(
        CanvasDrawingSession drawingSession,
        ImageSpaceTransform transform)
    {
        for (int line = 0; line <= 3; line++)
        {
            double x = _sourceWidth * line / 3;
            Vector2 verticalStart = transform.ToTarget(x, 0);
            Vector2 verticalEnd = transform.ToTarget(x, _sourceHeight);
            drawingSession.DrawLine(verticalStart, verticalEnd, GridColor, ScreenStrokeWidth);

            double y = _sourceHeight * line / 3;
            Vector2 horizontalStart = transform.ToTarget(0, y);
            Vector2 horizontalEnd = transform.ToTarget(_sourceWidth, y);
            drawingSession.DrawLine(horizontalStart, horizontalEnd, GridColor, ScreenStrokeWidth);
        }
    }

    private void DrawCellEmphasis(
        CanvasDrawingSession drawingSession,
        ImageSpaceTransform transform,
        (int Row, int Column)? cell,
        Color color)
    {
        if (cell is not { } position)
        {
            return;
        }

        TargetRectangle bounds = transform.ToTarget(
            StarAnalysisOverlayGeometry.GetCellBounds(
                position.Row,
                position.Column,
                _sourceWidth,
                _sourceHeight));
        drawingSession.DrawRectangle(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            color,
            EmphasisStrokeWidth);
    }

    private static void DrawOrientation(
        CanvasDrawingSession drawingSession,
        StarAnalysisCell cell,
        SourceRectangle bounds,
        ImageSpaceTransform transform)
    {
        double theta = cell.MeanTheta!.Value;
        double coherence = Math.Clamp(cell.ThetaCoherence, 0, 1);
        double halfLength = Math.Min(bounds.Width, bounds.Height) *
            (0.09 + (0.07 * coherence));
        double centerX = bounds.X + (bounds.Width / 2);
        double centerY = bounds.Y + (bounds.Height * 0.78);
        double dx = Math.Cos(theta) * halfLength;
        double dy = Math.Sin(theta) * halfLength;
        Vector2 start = transform.ToTarget(centerX - dx, centerY - dy);
        Vector2 end = transform.ToTarget(centerX + dx, centerY + dy);
        float strength = (float)((coherence -
            StarAnalysisOverlayGeometry.MinimumOrientationCoherence) /
            (1 - StarAnalysisOverlayGeometry.MinimumOrientationCoherence));
        float stroke = 1.5f + (Math.Clamp(strength, 0, 1) * 2.5f);
        drawingSession.DrawLine(start, end, OrientationColor, stroke);
    }

    private static void DrawCellLabel(
        CanvasDrawingSession drawingSession,
        string label,
        TargetRectangle bounds,
        Color color,
        CanvasTextFormat format,
        bool reservesOrientationSpace)
    {
        float height = reservesOrientationSpace ? bounds.Height * 0.7f : bounds.Height;
        var textBounds = new Rect(bounds.X, bounds.Y, bounds.Width, height);
        ReadOnlySpan<Vector2> shadowOffsets =
        [
            new(-1, -1), new(0, -1), new(1, -1),
            new(-1, 0), new(1, 0),
            new(-1, 1), new(0, 1), new(1, 1),
        ];
        foreach (Vector2 shadowOffset in shadowOffsets)
        {
            var shadowBounds = new Rect(
                textBounds.X + shadowOffset.X,
                textBounds.Y + shadowOffset.Y,
                textBounds.Width,
                textBounds.Height);
            drawingSession.DrawText(label, shadowBounds, LabelShadowColor, format);
        }
        drawingSession.DrawText(label, textBounds, color, format);
    }

    private static void DrawLabel(
        CanvasDrawingSession drawingSession,
        string label,
        Vector2 position,
        Color color,
        CanvasTextFormat format)
    {
        ReadOnlySpan<Vector2> shadowOffsets =
        [
            new(-1, -1), new(0, -1), new(1, -1),
            new(-1, 0), new(1, 0),
            new(-1, 1), new(0, 1), new(1, 1),
        ];
        foreach (Vector2 shadowOffset in shadowOffsets)
        {
            drawingSession.DrawText(label, position + shadowOffset, LabelShadowColor, format);
        }
        drawingSession.DrawText(label, position, color, format);
    }

    private static void DrawCenteredLabel(
        CanvasDrawingSession drawingSession,
        string label,
        Vector2 center,
        float width,
        float height,
        Color color,
        CanvasTextFormat format)
    {
        var bounds = new Rect(
            center.X - (width / 2),
            center.Y - (height / 2),
            width,
            height);
        ReadOnlySpan<Vector2> shadowOffsets =
        [
            new(-1, -1), new(0, -1), new(1, -1),
            new(-1, 0), new(1, 0),
            new(-1, 1), new(0, 1), new(1, 1),
        ];
        foreach (Vector2 shadowOffset in shadowOffsets)
        {
            var shadowBounds = new Rect(
                bounds.X + shadowOffset.X,
                bounds.Y + shadowOffset.Y,
                bounds.Width,
                bounds.Height);
            drawingSession.DrawText(label, shadowBounds, LabelShadowColor, format);
        }
        drawingSession.DrawText(label, bounds, color, format);
    }

    private static Vector2 ClampLabelCenter(
        Vector2 center,
        TargetRectangle bounds,
        float width,
        float height)
    {
        float halfWidth = MathF.Min(width / 2, bounds.Width / 2);
        float halfHeight = MathF.Min(height / 2, bounds.Height / 2);
        return new(
            Math.Clamp(center.X, bounds.Left + halfWidth, bounds.Right - halfWidth),
            Math.Clamp(center.Y, bounds.Top + halfHeight, bounds.Bottom - halfHeight));
    }

    private string FormatTiltPerimeterCenterLabel(TiltPerimeterDiagram diagram)
    {
        var lines = new List<string>(2);
        if (diagram.CenterMeasurement?.MedianHfr is double centerHfr)
        {
            lines.Add("CENTER HFR " + centerHfr.ToString(
                "0.00",
                CultureInfo.InvariantCulture));
        }
        if (_result.Tilt.TiltPercent is double tiltPercent)
        {
            lines.Add("CORNER TILT " + tiltPercent.ToString(
                "0.0",
                CultureInfo.InvariantCulture) + "%");
        }
        return lines.Count > 0 ? string.Join('\n', lines) : "HFR TILT";
    }

    private static string FormatTriangleTiltCenterLabel(TriangleTiltDiagram diagram)
    {
        var lines = new List<string>(3);
        if (diagram.CenterHfr is double centerHfr)
        {
            lines.Add("CENTER HFR " + centerHfr.ToString(
                "0.00",
                CultureInfo.InvariantCulture));
        }
        lines.Add("MEDIAN HFR " + diagram.OverallMedianHfr.ToString(
            "0.00",
            CultureInfo.InvariantCulture));
        lines.Add("SECTOR TILT " + diagram.TiltPercent.ToString(
            "0.0",
            CultureInfo.InvariantCulture) + "%");
        return string.Join('\n', lines);
    }

    private static string FormatCellLabel(StarAnalysisCell? cell)
    {
        if (cell is null || cell.StarCount <= 0)
        {
            return "No measured stars";
        }

        string count = string.Create(
            CultureInfo.InvariantCulture,
            $"{cell.StarCount:N0} {(cell.StarCount == 1 ? "star" : "stars")}");
        string sample = cell.StarCount < StarAnalysisOverlayGeometry.MinimumReliableCellStars
            ? " · low sample"
            : string.Empty;
        return cell.MedianHfr is double hfr && double.IsFinite(hfr)
            ? string.Create(CultureInfo.InvariantCulture, $"HFR {hfr:0.00}\n{count}{sample}")
            : $"HFR unavailable\n{count}{sample}";
    }

    private static Color GetFillColor(TiltCellVisualKind visualKind) => visualKind switch
    {
        TiltCellVisualKind.Good => GoodFillColor,
        TiltCellVisualKind.Warning => WarningFillColor,
        TiltCellVisualKind.Poor => PoorFillColor,
        _ => NeutralFillColor,
    };

    private static Color GetLabelColor(TiltCellVisualKind visualKind, int starCount) =>
        starCount is > 0 and < StarAnalysisOverlayGeometry.MinimumReliableCellStars
            ? LowSampleLabelColor
            : visualKind switch
            {
                TiltCellVisualKind.Good => GoodLabelColor,
                TiltCellVisualKind.Warning => WarningLabelColor,
                TiltCellVisualKind.Poor => PoorLabelColor,
                _ => NeutralLabelColor,
            };

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static Color ColorFromHex(uint rgb, byte alpha = 255) => Color.FromArgb(
        alpha,
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));
}
