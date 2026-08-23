using System.Numerics;

namespace Seiza.App.Rendering;

/// <summary>
/// Maps source-image coordinates into a drawing target. Keeping this transform
/// explicit prevents viewport and export renderers from accidentally mixing
/// screen-space and image-space geometry.
/// </summary>
internal readonly record struct ImageSpaceTransform(
    float ScaleX,
    float ScaleY,
    Vector2 Offset)
{
    public float AverageAbsoluteScale =>
        (MathF.Abs(ScaleX) + MathF.Abs(ScaleY)) / 2;

    public Vector2 ToTarget(double sourceX, double sourceY) => new(
        ((float)sourceX * ScaleX) + Offset.X,
        ((float)sourceY * ScaleY) + Offset.Y);

    public Vector2 ToTarget(Vector2 sourcePoint) => new(
        (sourcePoint.X * ScaleX) + Offset.X,
        (sourcePoint.Y * ScaleY) + Offset.Y);

    public Vector2 SourceRadiusToTarget(double sourceRadius) => new(
        (float)(Math.Abs(sourceRadius) * MathF.Abs(ScaleX)),
        (float)(Math.Abs(sourceRadius) * MathF.Abs(ScaleY)));

    public TargetRectangle ToTarget(SourceRectangle source)
    {
        Vector2 first = ToTarget(source.Left, source.Top);
        Vector2 second = ToTarget(source.Right, source.Bottom);
        float left = MathF.Min(first.X, second.X);
        float top = MathF.Min(first.Y, second.Y);
        return new(
            left,
            top,
            MathF.Max(first.X, second.X) - left,
            MathF.Max(first.Y, second.Y) - top);
    }
}

internal readonly record struct SourceRectangle(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;
}

internal readonly record struct TargetRectangle(
    float X,
    float Y,
    float Width,
    float Height)
{
    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public Vector2 Center => new(X + (Width / 2), Y + (Height / 2));
}
