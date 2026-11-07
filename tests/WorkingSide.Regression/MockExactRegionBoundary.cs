using HeatingMold.Core.Geometry;

/// <summary>합성 폴리곤 영역을 정확 경계 질의로 제공하는 회귀 테스트용 어댑터.</summary>
internal sealed class MockExactRegionBoundary : IExactRegionBoundary
{
    private readonly List<(Vec2 A, Vec2 B)> _segments = new();

    public MockExactRegionBoundary(params Vec2[][] rings)
    {
        foreach (Vec2[] ring in rings)
            for (int i = 0; i < ring.Length; i++)
                _segments.Add((ring[i], ring[(i + 1) % ring.Length]));
    }

    public RegionRayResult CastRay(Vec2 origin, Vec2 direction)
    {
        double dx = direction.X, dy = direction.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) ||
            !double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
            return new RegionRayResult(RegionRayStatus.Failure, Array.Empty<RegionBoundaryHit>(),
                "유한하지 않은 질의");
        var hits = new List<RegionBoundaryHit>();
        foreach ((Vec2 a, Vec2 b) in _segments)
        {
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double ox = a.X - origin.X, oy = a.Y - origin.Y;
            double denominator = dx * ey - dy * ex;
            if (Math.Abs(denominator) <= 1e-12)
            {
                if (Math.Abs(dx * oy - dy * ox) > 1e-9) continue;
                if (ex * dx + ey * dy < 0) continue;
                return new RegionRayResult(RegionRayStatus.Ambiguous,
                    Array.Empty<RegionBoundaryHit>(), "경계와 겹칩니다");
            }
            double t = (ox * ey - oy * ex) / denominator;
            double u = (ox * dy - oy * dx) / denominator;
            if (t <= 0 || u < 0 || u > 1) continue;
            hits.Add(new RegionBoundaryHit(new Vec2(origin.X + dx * t, origin.Y + dy * t),
                u <= 1e-9 || u >= 1 - 1e-9));
        }
        return hits.Count == 0
            ? new RegionRayResult(RegionRayStatus.Miss, Array.Empty<RegionBoundaryHit>(), string.Empty)
            : new RegionRayResult(RegionRayStatus.Hit, hits, string.Empty);
    }

    public RegionBoundaryProbe ProbeDistance(Vec2 point, double maxDistance)
    {
        if (_segments.Count == 0)
            return new RegionBoundaryProbe(RegionRayStatus.Failure, 0, "경계 정보가 없습니다");
        double nearest = double.PositiveInfinity;
        foreach ((Vec2 a, Vec2 b) in _segments)
        {
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double lengthSquared = ex * ex + ey * ey;
            double u = lengthSquared <= 0 ? 0 :
                ((point.X - a.X) * ex + (point.Y - a.Y) * ey) / lengthSquared;
            u = Math.Clamp(u, 0, 1);
            nearest = Math.Min(nearest, new Vec2(a.X + ex * u, a.Y + ey * u).DistanceTo(point));
        }
        return nearest <= maxDistance
            ? new RegionBoundaryProbe(RegionRayStatus.Hit, nearest, string.Empty)
            : new RegionBoundaryProbe(RegionRayStatus.Miss, 0, string.Empty);
    }
}
