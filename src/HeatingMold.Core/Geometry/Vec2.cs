namespace HeatingMold.Core.Geometry;

/// <summary>2D 점/벡터 (UV 또는 로컬 프레임 좌표).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Vec2 other) => (this - other).Length;

    /// <summary>원점 기준 회전 (rad).</summary>
    public Vec2 Rotate(double angleRad)
    {
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        return new(X * c - Y * s, X * s + Y * c);
    }

    public override string ToString() => $"({X:0.####}, {Y:0.####})";
}
