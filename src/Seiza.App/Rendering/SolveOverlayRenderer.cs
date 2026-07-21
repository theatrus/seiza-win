using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Seiza.App.Models;
using Windows.UI;

namespace Seiza.App.Rendering;

internal sealed class SolveOverlayRenderer
{
    private const int GridSamples = 96;

    private static readonly double[] GridStepsDegrees =
    [
        1.0 / 3600, 2.0 / 3600, 5.0 / 3600, 10.0 / 3600, 15.0 / 3600,
        30.0 / 3600, 1.0 / 60, 2.0 / 60, 5.0 / 60, 10.0 / 60,
        15.0 / 60, 30.0 / 60, 1, 2, 5, 10, 15, 30, 45, 90,
    ];

    private static readonly Color NamedStarColor = ColorFromHex(0xFFD479);
    private static readonly Color DetectedStarColor = ColorFromHex(0xB7A6FF);
    private static readonly Color TransientColor = ColorFromHex(0xFF7BE0);
    private static readonly Color CometColor = ColorFromHex(0x7BFFD0);
    private static readonly Color AsteroidColor = ColorFromHex(0xFFB36B);
    private static readonly Color FieldStarColor = ColorFromHex(0xEEF7FF);
    private static readonly Color GridColor = ColorFromHex(0x7DDBE8, 175);
    private static readonly Color GridLabelColor = ColorFromHex(0xB9F3F7);
    private static readonly Color CenterColor = ColorFromHex(0xF2C66D);
    private static readonly Color LabelShadowColor = Color.FromArgb(220, 0, 0, 0);

    private readonly SolveResult _result;
    private readonly double _sourceWidth;
    private readonly double _sourceHeight;
    private readonly List<GridLine> _gridLines;

    public SolveOverlayRenderer(SolveResult result, int sourceWidth, int sourceHeight)
    {
        _result = result;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _gridLines = BuildCoordinateGrid();
    }

    public void Draw(
        CanvasDrawingSession drawingSession,
        OverlayOptions options,
        float scaleX,
        float scaleY,
        Vector2 offset)
    {
        float referenceScale = MathF.Max(0.001f, MathF.Min(MathF.Abs(scaleX), MathF.Abs(scaleY)));
        float stroke = Math.Clamp(referenceScale * 1.35f, 1.0f, 2.5f);

        if (options.ShowCoordinateGrid)
        {
            DrawGrid(drawingSession, scaleX, scaleY, offset, stroke);
        }

        if (options.ShowFieldStars)
        {
            DrawFieldStars(drawingSession, scaleX, scaleY, offset, stroke);
        }

        foreach (SolveObjectPoint item in _result.ObjectPositions)
        {
            if (!ShouldDraw(item, options))
            {
                continue;
            }

            DrawObject(drawingSession, item, options, scaleX, scaleY, offset, stroke);
        }

        if (options.ShowDetectedStars)
        {
            DrawDetectedStars(drawingSession, scaleX, scaleY, offset, stroke);
        }

        if (options.ShowFieldCenter)
        {
            DrawFieldCenter(drawingSession, scaleX, scaleY, offset, stroke);
        }
    }

    private void DrawGrid(
        CanvasDrawingSession drawingSession,
        float scaleX,
        float scaleY,
        Vector2 offset,
        float stroke)
    {
        foreach (GridLine line in _gridLines)
        {
            DrawPolyline(drawingSession, line.Points, GridColor, stroke, scaleX, scaleY, offset, false);
            if (line.Points.Count > 0)
            {
                DrawLabel(
                    drawingSession,
                    line.Label,
                    Transform(line.Points[0], scaleX, scaleY, offset) + new Vector2(4, 3),
                    GridLabelColor,
                    11);
            }
        }
    }

    private void DrawFieldStars(
        CanvasDrawingSession drawingSession,
        float scaleX,
        float scaleY,
        Vector2 offset,
        float stroke)
    {
        foreach (SolveCatalogStarPoint star in _result.CatalogStarPositions)
        {
            Vector2 center = Transform(star.X, star.Y, scaleX, scaleY, offset);
            float radius = Math.Clamp((float)((13.5 - star.Magnitude) * 0.4), 2.0f, 5.5f);
            drawingSession.DrawCircle(center, radius, FieldStarColor, stroke);
        }
    }

    private void DrawDetectedStars(
        CanvasDrawingSession drawingSession,
        float scaleX,
        float scaleY,
        Vector2 offset,
        float stroke)
    {
        foreach (SolveImagePoint star in _result.DetectedStarPositions)
        {
            Vector2 center = Transform(star.X, star.Y, scaleX, scaleY, offset);
            const float inner = 3;
            const float outer = 7;
            drawingSession.DrawLine(center.X - outer, center.Y, center.X - inner, center.Y, DetectedStarColor, stroke);
            drawingSession.DrawLine(center.X + inner, center.Y, center.X + outer, center.Y, DetectedStarColor, stroke);
            drawingSession.DrawLine(center.X, center.Y - outer, center.X, center.Y - inner, DetectedStarColor, stroke);
            drawingSession.DrawLine(center.X, center.Y + inner, center.X, center.Y + outer, DetectedStarColor, stroke);
        }
    }

    private void DrawFieldCenter(
        CanvasDrawingSession drawingSession,
        float scaleX,
        float scaleY,
        Vector2 offset,
        float stroke)
    {
        Vector2 center = Transform(_sourceWidth / 2, _sourceHeight / 2, scaleX, scaleY, offset);
        const float radius = 11;
        const float arm = 17;
        drawingSession.DrawCircle(center, radius, CenterColor, stroke);
        drawingSession.DrawLine(center.X - arm, center.Y, center.X - radius - 2, center.Y, CenterColor, stroke);
        drawingSession.DrawLine(center.X + radius + 2, center.Y, center.X + arm, center.Y, CenterColor, stroke);
        drawingSession.DrawLine(center.X, center.Y - arm, center.X, center.Y - radius - 2, CenterColor, stroke);
        drawingSession.DrawLine(center.X, center.Y + radius + 2, center.X, center.Y + arm, CenterColor, stroke);
    }

    private static bool ShouldDraw(SolveObjectPoint item, OverlayOptions options)
    {
        return item.Kind.ToLowerInvariant() switch
        {
            "star" or "double-star" or "identified-star" => options.ShowNamedStars,
            "transient" when item.NearCapture == false => options.ShowHistoricalTransients,
            "transient" => options.ShowTransients,
            "comet" or "asteroid" => options.ShowMinorBodies,
            _ => options.ShowDeepSky && !options.HiddenDeepSkyCatalogs.Contains(GetCatalog(item)),
        };
    }

    private static void DrawObject(
        CanvasDrawingSession drawingSession,
        SolveObjectPoint item,
        OverlayOptions options,
        float scaleX,
        float scaleY,
        Vector2 offset,
        float stroke)
    {
        Color color = GetObjectColor(item);
        Vector2 center = Transform(item.X, item.Y, scaleX, scaleY, offset);
        string kind = item.Kind.ToLowerInvariant();
        float labelOffset = 10;

        if (kind is "star" or "double-star" or "identified-star")
        {
            DrawDiamond(drawingSession, center, 6, color, stroke);
        }
        else if (kind is "comet" or "asteroid")
        {
            DrawDiamond(drawingSession, center, 7, color, stroke);
            DrawMotionVector(drawingSession, item, center, color, stroke);
        }
        else if (kind == "transient")
        {
            drawingSession.DrawCircle(center, 7, color, stroke);
            drawingSession.DrawLine(center.X - 10, center.Y, center.X + 10, center.Y, color, stroke);
            drawingSession.DrawLine(center.X, center.Y - 10, center.X, center.Y + 10, color, stroke);
        }
        else
        {
            bool drewOutline = options.ShowCatalogOutlines && item.Outlines.Length > 0;
            if (drewOutline)
            {
                foreach (SolveObjectOutline outline in item.Outlines)
                {
                    foreach (SolveObjectContour contour in outline.Contours)
                    {
                        var points = contour.Points
                            .Where(point => point.Length >= 2)
                            .Select(point => new Vector2((float)point[0], (float)point[1]))
                            .ToList();
                        DrawPolyline(
                            drawingSession,
                            points,
                            color,
                            stroke,
                            scaleX,
                            scaleY,
                            offset,
                            contour.Closed);
                    }
                }
            }
            else
            {
                float radiusX = Math.Clamp((float)item.SemiMajorPixels * MathF.Abs(scaleX), 6, 120);
                float radiusY = Math.Clamp((float)item.SemiMinorPixels * MathF.Abs(scaleY), 6, 120);
                DrawRotatedEllipse(
                    drawingSession,
                    center,
                    radiusX,
                    radiusY,
                    (float)(item.AngleDegrees ?? 0),
                    color,
                    stroke);
                labelOffset = radiusX + 5;
            }
        }

        if (options.ShowObjectLabels)
        {
            DrawLabel(
                drawingSession,
                item.DisplayName,
                center + new Vector2(labelOffset, -9),
                color,
                12);
        }
    }

    private static void DrawMotionVector(
        CanvasDrawingSession drawingSession,
        SolveObjectPoint item,
        Vector2 center,
        Color color,
        float stroke)
    {
        if (item.DirectionImageAngleDegrees is not double angle)
        {
            return;
        }

        double radians = angle * Math.PI / 180;
        float length = Math.Clamp((float)(item.MotionArcsecPerHour ?? 0) * 0.4f, 10, 28);
        Vector2 direction = new((float)Math.Cos(radians), (float)Math.Sin(radians));
        Vector2 end = center + (direction * length);
        drawingSession.DrawLine(center, end, color, stroke);
        Vector2 side = new(-direction.Y, direction.X);
        drawingSession.DrawLine(end, end - (direction * 5) + (side * 3), color, stroke);
        drawingSession.DrawLine(end, end - (direction * 5) - (side * 3), color, stroke);
    }

    private static void DrawDiamond(
        CanvasDrawingSession drawingSession,
        Vector2 center,
        float radius,
        Color color,
        float stroke)
    {
        Vector2 top = center + new Vector2(0, -radius);
        Vector2 right = center + new Vector2(radius, 0);
        Vector2 bottom = center + new Vector2(0, radius);
        Vector2 left = center + new Vector2(-radius, 0);
        drawingSession.DrawLine(top, right, color, stroke);
        drawingSession.DrawLine(right, bottom, color, stroke);
        drawingSession.DrawLine(bottom, left, color, stroke);
        drawingSession.DrawLine(left, top, color, stroke);
    }

    private static void DrawRotatedEllipse(
        CanvasDrawingSession drawingSession,
        Vector2 center,
        float radiusX,
        float radiusY,
        float angleDegrees,
        Color color,
        float stroke)
    {
        const int segments = 48;
        double angle = angleDegrees * Math.PI / 180;
        float cosAngle = (float)Math.Cos(angle);
        float sinAngle = (float)Math.Sin(angle);
        Vector2? previous = null;
        Vector2 first = default;
        for (int index = 0; index <= segments; index++)
        {
            double phase = index * Math.PI * 2 / segments;
            float x = radiusX * (float)Math.Cos(phase);
            float y = radiusY * (float)Math.Sin(phase);
            Vector2 point = center + new Vector2(
                (x * cosAngle) - (y * sinAngle),
                (x * sinAngle) + (y * cosAngle));
            if (index == 0)
            {
                first = point;
            }
            if (previous is Vector2 previousPoint)
            {
                drawingSession.DrawLine(previousPoint, point, color, stroke);
            }
            previous = point;
        }
        if (previous is Vector2 last)
        {
            drawingSession.DrawLine(last, first, color, stroke);
        }
    }

    private static void DrawPolyline(
        CanvasDrawingSession drawingSession,
        IReadOnlyList<Vector2> points,
        Color color,
        float stroke,
        float scaleX,
        float scaleY,
        Vector2 offset,
        bool closed)
    {
        if (points.Count < 2)
        {
            return;
        }

        Vector2 first = Transform(points[0], scaleX, scaleY, offset);
        Vector2 previous = first;
        for (int index = 1; index < points.Count; index++)
        {
            Vector2 current = Transform(points[index], scaleX, scaleY, offset);
            drawingSession.DrawLine(previous, current, color, stroke);
            previous = current;
        }
        if (closed)
        {
            drawingSession.DrawLine(previous, first, color, stroke);
        }
    }

    private static void DrawLabel(
        CanvasDrawingSession drawingSession,
        string label,
        Vector2 position,
        Color color,
        float fontSize)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        using CanvasTextFormat format = new()
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
        };
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

    private List<GridLine> BuildCoordinateGrid()
    {
        if (!TryGetWorldBounds(out WorldBounds bounds))
        {
            return [];
        }

        double raRange = bounds.MaximumRa - bounds.MinimumRa;
        double decRange = bounds.MaximumDec - bounds.MinimumDec;
        double raStep = ChooseGridStep(raRange);
        double decStep = ChooseGridStep(decRange);
        var lines = new List<GridLine>();

        double firstRa = Math.Ceiling(bounds.MinimumRa / raStep) * raStep;
        for (double ra = firstRa; ra <= bounds.MaximumRa + (raStep * 0.25); ra += raStep)
        {
            var points = new List<Vector2>();
            for (int index = 0; index <= GridSamples; index++)
            {
                double dec = bounds.MinimumDec + (decRange * index / GridSamples);
                if (TryWorldToPixel(NormalizeRa(ra), dec, out Vector2 point) && IsNearImage(point))
                {
                    points.Add(point);
                }
            }
            if (points.Count >= 2)
            {
                lines.Add(new GridLine(points, FormatRa(ra)));
            }
        }

        double firstDec = Math.Ceiling(bounds.MinimumDec / decStep) * decStep;
        for (double dec = firstDec; dec <= bounds.MaximumDec + (decStep * 0.25); dec += decStep)
        {
            var points = new List<Vector2>();
            for (int index = 0; index <= GridSamples; index++)
            {
                double ra = bounds.MinimumRa + (raRange * index / GridSamples);
                if (TryWorldToPixel(NormalizeRa(ra), dec, out Vector2 point) && IsNearImage(point))
                {
                    points.Add(point);
                }
            }
            if (points.Count >= 2)
            {
                lines.Add(new GridLine(points, FormatDec(dec)));
            }
        }

        return lines;
    }

    private bool TryGetWorldBounds(out WorldBounds bounds)
    {
        var ras = new List<double>();
        var decs = new List<double>();
        for (int row = 0; row <= 8; row++)
        {
            for (int column = 0; column <= 8; column++)
            {
                double x = _sourceWidth * column / 8;
                double y = _sourceHeight * row / 8;
                if (TryPixelToWorld(x, y, out double ra, out double dec))
                {
                    ras.Add(UnwrapRa(ra, _result.CenterRaDegrees));
                    decs.Add(dec);
                }
            }
        }

        if (ras.Count == 0)
        {
            bounds = default;
            return false;
        }

        bounds = new WorldBounds(ras.Min(), ras.Max(), decs.Min(), decs.Max());
        return true;
    }

    private bool TryPixelToWorld(double x, double y, out double ra, out double dec)
    {
        WcsResult wcs = _result.Wcs;
        if (wcs.Crval.Length < 2 || wcs.Crpix.Length < 2 || wcs.Cd.Length < 2 ||
            wcs.Cd[0].Length < 2 || wcs.Cd[1].Length < 2)
        {
            ra = 0;
            dec = 0;
            return false;
        }

        double dx = x + 1 - wcs.Crpix[0];
        double dy = y + 1 - wcs.Crpix[1];
        double xi = ((wcs.Cd[0][0] * dx) + (wcs.Cd[0][1] * dy)) * Math.PI / 180;
        double eta = ((wcs.Cd[1][0] * dx) + (wcs.Cd[1][1] * dy)) * Math.PI / 180;
        double ra0 = wcs.Crval[0] * Math.PI / 180;
        double dec0 = wcs.Crval[1] * Math.PI / 180;
        double denominator = Math.Cos(dec0) - (eta * Math.Sin(dec0));
        double raRadians = ra0 + Math.Atan2(xi, denominator);
        double decRadians = Math.Atan2(
            Math.Sin(dec0) + (eta * Math.Cos(dec0)),
            Math.Sqrt((denominator * denominator) + (xi * xi)));
        ra = NormalizeRa(raRadians * 180 / Math.PI);
        dec = decRadians * 180 / Math.PI;
        return double.IsFinite(ra) && double.IsFinite(dec);
    }

    private bool TryWorldToPixel(double ra, double dec, out Vector2 point)
    {
        WcsResult wcs = _result.Wcs;
        if (wcs.Crval.Length < 2 || wcs.Crpix.Length < 2 || wcs.Cd.Length < 2 ||
            wcs.Cd[0].Length < 2 || wcs.Cd[1].Length < 2)
        {
            point = default;
            return false;
        }

        double raRadians = ra * Math.PI / 180;
        double decRadians = dec * Math.PI / 180;
        double ra0 = wcs.Crval[0] * Math.PI / 180;
        double dec0 = wcs.Crval[1] * Math.PI / 180;
        double deltaRa = Math.IEEERemainder(raRadians - ra0, Math.PI * 2);
        double cosc = (Math.Sin(dec0) * Math.Sin(decRadians)) +
            (Math.Cos(dec0) * Math.Cos(decRadians) * Math.Cos(deltaRa));
        if (cosc <= 0)
        {
            point = default;
            return false;
        }

        double xi = Math.Cos(decRadians) * Math.Sin(deltaRa) / cosc;
        double eta = ((Math.Cos(dec0) * Math.Sin(decRadians)) -
            (Math.Sin(dec0) * Math.Cos(decRadians) * Math.Cos(deltaRa))) / cosc;
        xi *= 180 / Math.PI;
        eta *= 180 / Math.PI;

        double determinant = (wcs.Cd[0][0] * wcs.Cd[1][1]) -
            (wcs.Cd[0][1] * wcs.Cd[1][0]);
        if (Math.Abs(determinant) < 1e-16)
        {
            point = default;
            return false;
        }

        double dx = ((wcs.Cd[1][1] * xi) - (wcs.Cd[0][1] * eta)) / determinant;
        double dy = ((-wcs.Cd[1][0] * xi) + (wcs.Cd[0][0] * eta)) / determinant;
        double x = dx + wcs.Crpix[0] - 1;
        double y = dy + wcs.Crpix[1] - 1;
        point = new Vector2((float)x, (float)y);
        return float.IsFinite(point.X) && float.IsFinite(point.Y);
    }

    private bool IsNearImage(Vector2 point) =>
        point.X >= -(_sourceWidth * 0.1) &&
        point.X <= _sourceWidth * 1.1 &&
        point.Y >= -(_sourceHeight * 0.1) &&
        point.Y <= _sourceHeight * 1.1;

    private static double ChooseGridStep(double range)
    {
        double target = Math.Max(range / 5, GridStepsDegrees[0]);
        return GridStepsDegrees.FirstOrDefault(step => step >= target, GridStepsDegrees[^1]);
    }

    private static string FormatRa(double degrees)
    {
        double totalHours = NormalizeRa(degrees) / 15;
        int hours = (int)Math.Floor(totalHours);
        int minutes = (int)Math.Floor((totalHours - hours) * 60);
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}h {minutes:00}m");
    }

    private static string FormatDec(double degrees)
    {
        string sign = degrees < 0 ? "\u2212" : "+";
        double absolute = Math.Abs(degrees);
        int wholeDegrees = (int)Math.Floor(absolute);
        int minutes = (int)Math.Floor((absolute - wholeDegrees) * 60);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{wholeDegrees:00}\u00B0 {minutes:00}\u2032");
    }

    private static double NormalizeRa(double value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }

    private static double UnwrapRa(double value, double reference)
    {
        double difference = Math.IEEERemainder(value - reference, 360);
        return reference + difference;
    }

    private static Vector2 Transform(
        double x,
        double y,
        float scaleX,
        float scaleY,
        Vector2 offset) =>
        new(((float)x * scaleX) + offset.X, ((float)y * scaleY) + offset.Y);

    private static Vector2 Transform(
        Vector2 point,
        float scaleX,
        float scaleY,
        Vector2 offset) =>
        new((point.X * scaleX) + offset.X, (point.Y * scaleY) + offset.Y);

    private static DeepSkyCatalog GetCatalog(SolveObjectPoint item)
    {
        string source = $"{item.CatalogSource} {item.Source} {item.Name}".ToUpperInvariant();
        if (source.Contains("MESSIER", StringComparison.Ordinal) || StartsWithCatalog(item.Name, "M"))
        {
            return DeepSkyCatalog.Messier;
        }
        if (source.Contains("NGC", StringComparison.Ordinal) || StartsWithCatalog(item.Name, "NGC"))
        {
            return DeepSkyCatalog.Ngc;
        }
        if (source.Contains("SHARPLESS", StringComparison.Ordinal) ||
            source.Contains("VDB", StringComparison.Ordinal) ||
            StartsWithCatalog(item.Name, "SH2"))
        {
            return DeepSkyCatalog.SharplessVdb;
        }
        if (source.Contains("LBN", StringComparison.Ordinal))
        {
            return DeepSkyCatalog.Lbn;
        }
        if (source.Contains("CED", StringComparison.Ordinal))
        {
            return DeepSkyCatalog.Cederblad;
        }
        if (source.Contains("BARNARD", StringComparison.Ordinal) ||
            source.Contains("LDN", StringComparison.Ordinal))
        {
            return DeepSkyCatalog.DarkNebulae;
        }
        if (source.Contains("SNR", StringComparison.Ordinal) ||
            source.Contains("GREEN", StringComparison.Ordinal))
        {
            return DeepSkyCatalog.SupernovaRemnants;
        }
        if (source.Contains("UGC", StringComparison.Ordinal))
        {
            return DeepSkyCatalog.Ugc;
        }
        if (source.Contains("PGC", StringComparison.Ordinal))
        {
            return DeepSkyCatalog.Pgc;
        }
        if (source.Contains("IC", StringComparison.Ordinal) || StartsWithCatalog(item.Name, "IC"))
        {
            return DeepSkyCatalog.Ic;
        }
        return DeepSkyCatalog.Other;
    }

    private static bool StartsWithCatalog(string name, string prefix) =>
        name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
        (prefix.Length > 1 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static Color GetObjectColor(SolveObjectPoint item)
    {
        return item.Kind.ToLowerInvariant() switch
        {
            "star" or "double-star" or "identified-star" => NamedStarColor,
            "transient" => TransientColor,
            "comet" => CometColor,
            "asteroid" => AsteroidColor,
            _ => GetCatalog(item) switch
            {
                DeepSkyCatalog.Messier => ColorFromHex(0xF2CA72),
                DeepSkyCatalog.Ngc => ColorFromHex(0x55CFFF),
                DeepSkyCatalog.Ic => ColorFromHex(0x72DFB9),
                DeepSkyCatalog.SharplessVdb => ColorFromHex(0xEE9A78),
                DeepSkyCatalog.Lbn => ColorFromHex(0xA2D96F),
                DeepSkyCatalog.Cederblad => ColorFromHex(0x70D7D0),
                DeepSkyCatalog.DarkNebulae => ColorFromHex(0xB4A3F0),
                DeepSkyCatalog.SupernovaRemnants => ColorFromHex(0xF18782),
                DeepSkyCatalog.Ugc => ColorFromHex(0x79AFF5),
                DeepSkyCatalog.Pgc => ColorFromHex(0xA1AED8),
                _ => ColorFromHex(0xC1D1D3),
            },
        };
    }

    private static Color ColorFromHex(uint rgb, byte alpha = 255) => Color.FromArgb(
        alpha,
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));

    private sealed record GridLine(IReadOnlyList<Vector2> Points, string Label);

    private readonly record struct WorldBounds(
        double MinimumRa,
        double MaximumRa,
        double MinimumDec,
        double MaximumDec);
}
