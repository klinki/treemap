using System.Globalization;

namespace Treemap;

/// <summary>
/// Immutable float rectangle. Half-open on the right/bottom edges so that
/// adjacent cells tile without double-claiming a pixel along shared borders.
/// </summary>
public readonly struct RectF : IEquatable<RectF>
{
    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;

    public RectF(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static RectF Empty => default;

    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float Area => Width * Height;

    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public float ShortestSide => MathF.Min(Width, Height);

    public PointF Center => new(X + (Width * 0.5f), Y + (Height * 0.5f));

    public bool Contains(float px, float py)
        => px >= X && px < Right && py >= Y && py < Bottom;

    public bool Intersects(in RectF other)
        => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    public RectF Inset(float dx, float dy)
        => new(X + dx, Y + dy, MathF.Max(0f, Width - (2f * dx)), MathF.Max(0f, Height - (2f * dy)));

    public RectF InsetUniform(float d) => Inset(d, d);

    /// <summary>Axis-aligned intersection, or <see cref="Empty"/> when disjoint.</summary>
    public RectF Intersect(in RectF other)
    {
        float l = MathF.Max(X, other.X);
        float t = MathF.Max(Y, other.Y);
        float r = MathF.Min(Right, other.Right);
        float b = MathF.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? Empty : new RectF(l, t, r - l, b - t);
    }

    /// <summary>Component-wise linear interpolation, used by the animation tween.</summary>
    public static RectF Lerp(in RectF a, in RectF b, float t)
        => new(a.X + ((b.X - a.X) * t),
               a.Y + ((b.Y - a.Y) * t),
               a.Width + ((b.Width - a.Width) * t),
               a.Height + ((b.Height - a.Height) * t));

    public bool Equals(RectF other)
        => X.Equals(other.X) && Y.Equals(other.Y)
        && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is RectF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"({X:0.##}, {Y:0.##}) {Width:0.##}x{Height:0.##}");
}

public readonly struct PointF(float x, float y) : IEquatable<PointF>
{
    public readonly float X = x;
    public readonly float Y = y;

    public bool Equals(PointF other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is PointF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:0.##}, {Y:0.##})");
}
