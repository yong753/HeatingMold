using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// 분할로 만들어진 독립 영역의 정확 포함 판정.
/// 이 영역의 경계는 원본 CAD edge 구간과 새 artificial 절단선이 섞인 확정 작업평면 외곽이므로,
/// 포함 판정은 그 외곽 폴리곤을 기준으로 한다. 원본 CAD edge identity는 인출점 binding에
/// 그대로 보존되며 이 질의가 다른 edge로 재바인딩하지 않는다.
/// </summary>
internal sealed class SplitRegionContainmentBoundary : IExactRegionBoundary
{
    private const double ParameterEpsilon = 1e-9;
    private const double PointEpsilon = 1e-6;

    private readonly IReadOnlyList<Vec2> _ring;

    internal SplitRegionContainmentBoundary(IReadOnlyList<Vec2> ring)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        if (_ring.Count < 3) throw new ArgumentException("영역 외곽은 세 점 이상이어야 합니다", nameof(ring));
    }

    public RegionRayResult CastRay(Vec2 origin, Vec2 direction)
    {
        if (!IsFinite(origin) || !IsFinite(direction))
            return new RegionRayResult(RegionRayStatus.Failure, Array.Empty<RegionBoundaryHit>(),
                "영역 경계 질의 좌표가 유한하지 않습니다.");

        double directionLengthSquared = direction.X * direction.X + direction.Y * direction.Y;
        if (!(directionLengthSquared > 0.0))
            return new RegionRayResult(RegionRayStatus.Failure, Array.Empty<RegionBoundaryHit>(),
                "영역 경계 광선 방향이 퇴화했습니다.");

        var hits = new List<(double Along, Vec2 Point)>();
        for (int i = 0; i < _ring.Count; i++)
        {
            Vec2 a = _ring[i];
            Vec2 b = _ring[(i + 1) % _ring.Count];
            Vec2 edge = b - a;
            double edgeLengthSquared = edge.X * edge.X + edge.Y * edge.Y;
            if (!(edgeLengthSquared > 1e-18)) continue;

            double denominator = Cross(direction, edge);
            double lengthScale = Math.Sqrt(directionLengthSquared * edgeLengthSquared);
            if (Math.Abs(denominator) <= 1e-12 * lengthScale)
            {
                // 광선이 이 변과 평행하다. 같은 직선 위에 있으면 접선/겹침으로 명시 보고한다.
                if (Math.Abs(Cross(a - origin, direction)) <= 1e-9 * Math.Sqrt(directionLengthSquared))
                    return new RegionRayResult(RegionRayStatus.Ambiguous, Array.Empty<RegionBoundaryHit>(),
                        "영역 경계와 접하거나 겹칩니다.");
                continue;
            }

            double along = Cross(a - origin, edge) / denominator;
            double u = Cross(a - origin, direction) / denominator;
            if (along < -PointEpsilon) continue;
            if (u < -ParameterEpsilon || u > 1.0 + ParameterEpsilon) continue;

            Vec2 point = new(origin.X + direction.X * along, origin.Y + direction.Y * along);
            if (hits.Exists(hit => hit.Point.DistanceTo(point) <= PointEpsilon)) continue;
            hits.Add((along, point));
        }

        if (hits.Count == 0)
            return new RegionRayResult(RegionRayStatus.Miss, Array.Empty<RegionBoundaryHit>(), string.Empty);
        hits.Sort((left, right) => left.Along.CompareTo(right.Along));
        var result = new List<RegionBoundaryHit>(hits.Count);
        foreach ((double _, Vec2 point) in hits)
            result.Add(new RegionBoundaryHit(point, false));
        return new RegionRayResult(RegionRayStatus.Hit, result, string.Empty);
    }

    public RegionBoundaryProbe ProbeDistance(Vec2 point, double maxDistance)
    {
        if (!IsFinite(point) || !double.IsFinite(maxDistance) || maxDistance < 0.0)
            return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0, "영역 경계 거리 질의 입력이 유효하지 않습니다.");

        double nearest = double.PositiveInfinity;
        for (int i = 0; i < _ring.Count; i++)
        {
            double distance = DistanceToSegment(_ring[i], _ring[(i + 1) % _ring.Count], point);
            if (distance < nearest) nearest = distance;
        }
        if (!double.IsFinite(nearest))
            return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0, "영역 경계 거리가 유한하지 않습니다.");
        return nearest <= maxDistance
            ? new RegionBoundaryProbe(RegionRayStatus.Hit, nearest, string.Empty)
            : new RegionBoundaryProbe(RegionRayStatus.Miss, 0.0, string.Empty);
    }

    private static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 point)
    {
        Vec2 side = b - a;
        double lengthSquared = side.X * side.X + side.Y * side.Y;
        if (!(lengthSquared > 1e-18)) return point.DistanceTo(a);
        double u = ((point.X - a.X) * side.X + (point.Y - a.Y) * side.Y) / lengthSquared;
        u = Math.Clamp(u, 0.0, 1.0);
        return new Vec2(a.X + side.X * u, a.Y + side.Y * u).DistanceTo(point);
    }

    private static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    private static bool IsFinite(Vec2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
