using System;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>작업평면 2D 경계 판정에 공통으로 쓰는 순수 산술. 허용치·바인딩 정책은 호출부가 소유한다.</summary>
internal static class PlanarMath2D
{
    public static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 point)
    {
        Vec2 side = b - a;
        double lengthSquared = Dot(side, side);
        if (lengthSquared <= 1e-18) return point.DistanceTo(a);
        double u = Math.Clamp(Dot(point - a, side) / lengthSquared, 0, 1);
        return Lerp(a, b, u).DistanceTo(point);
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public static bool IsFinite(Vec2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
