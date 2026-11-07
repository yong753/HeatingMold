using System;
using System.Collections.Generic;

namespace HeatingMold.Core.Geometry;

/// <summary>작업평면에 투영된 정확 CAD 경계 곡선에서 얻은 교차점.</summary>
public readonly record struct RegionBoundaryHit(Vec2 Point, bool AtTrimEndpoint);

/// <summary>광선 질의 결과 상태. 모호/실패는 소비자가 숨기지 않고 판정에 반영해야 한다.</summary>
public enum RegionRayStatus { Hit, Miss, Ambiguous, Failure }

/// <summary>정확 경계 광선 질의 결과. Detail은 실패/모호 사유(사용자 표시용).</summary>
public readonly record struct RegionRayResult(RegionRayStatus Status,
    IReadOnlyList<RegionBoundaryHit> Hits, string Detail);

/// <summary>점에서 정확 경계까지의 최단 거리 질의 결과.</summary>
public readonly record struct RegionBoundaryProbe(RegionRayStatus Status, double Distance, string Detail);

/// <summary>
/// 작업평면에 투영된 원본 CAD 경계 곡선에 대한 질의. 표시용 폴리라인/현 근사를 대신 사용하지 않는다.
/// 모든 실패(오래된 참조, 기하 오류, 접선/겹침)는 명시적으로 보고한다.
/// </summary>
public interface IExactRegionBoundary
{
    /// <summary>origin에서 direction 방향(정규화 불필요)으로의 광선 교차. 히트는 진행 방향 순서다.</summary>
    RegionRayResult CastRay(Vec2 origin, Vec2 direction);

    /// <summary>점에서 경계까지의 최단 거리. maxDistance 밖이면 Miss.</summary>
    RegionBoundaryProbe ProbeDistance(Vec2 point, double maxDistance);
}

/// <summary>
/// 정확 CAD 경계 기준 적용영역 포함 판정.
/// 구간의 모든 경계 교차 파라미터 사이 open interval을 판정하므로 표본 간격에 의존하지 않는다.
/// 경계 위 점은 수치 허용오차(OnBoundaryTolerance)로만 인정하며, 모호한 해는 실패로 처리한다.
/// </summary>
public sealed class ExactRegionContainment
{
    /// <summary>곡선 위 판정의 수치 허용오차(mm). 설계/스냅 허용오차가 아니다.</summary>
    public const double OnBoundaryTolerance = 1e-6;

    private const double DirectionEpsilon = 1e-9;

    // 광선이 경계 꼭짓점을 지나면 even-odd가 모호해진다. 방향을 바꿔 재시도하고,
    // 모든 방향이 꼭짓점/접선에 걸리면 명시적 실패로 끝낸다(허용오차 확대 금지).
    private static readonly Vec2[] RayDirections =
    {
        new(1.0, 0.0),
        new(0.3826834323650898, 0.9238795325112867),
        new(-0.7071067811865476, 0.7071067811865476),
        new(0.9238795325112867, -0.3826834323650898),
        new(-0.3826834323650898, -0.9238795325112867),
    };

    private readonly IExactRegionBoundary _boundary;

    public ExactRegionContainment(IExactRegionBoundary boundary)
    {
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
    }

    /// <summary>구간들이 정확 경계 안(또는 경계 위)에 남는지 검사한다.</summary>
    public bool ValidateSegments(IReadOnlyList<(Vec2 A, Vec2 B)> segments,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        out string failure)
    {
        failure = string.Empty;
        ArgumentNullException.ThrowIfNull(segments);
        exclusions ??= Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>();

        foreach ((Vec2 a, Vec2 b) in segments)
        {
            double length = a.DistanceTo(b);
            if (!double.IsFinite(length)) { failure = "구간 좌표가 유한하지 않습니다."; return false; }
            if (length <= DirectionEpsilon) continue;

            if (!Classify(a, out Relation relationA, out failure)) return false;
            if (relationA == Relation.Outside) { failure = OutsideFailure(a); return false; }
            if (!Classify(b, out Relation relationB, out failure)) return false;
            if (relationB == Relation.Outside) { failure = OutsideFailure(b); return false; }

            if (TryExclusionViolation(a, b, exclusions, out Vec2 excludedPoint))
            {
                failure = OutsideFailure(excludedPoint);
                return false;
            }

            Vec2 direction = b - a;
            RegionRayResult ray = _boundary.CastRay(a, direction);
            if (ray.Status == RegionRayStatus.Failure) { failure = ray.Detail; return false; }
            if (ray.Status == RegionRayStatus.Ambiguous)
            {
                // 경계 곡선과 같은 직선 위에 놓인 구간(마진 0의 직선 변)만 경계 위 구간으로
                // 인정한다. 끝점이 경계 위가 아니면 접선/겹침은 명시적 실패다.
                if (relationA == Relation.OnBoundary && relationB == Relation.OnBoundary)
                {
                    if (SegmentOnBoundary(a, b, out failure)) continue;
                    if (failure.Length == 0)
                        failure = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "수정된 구간이 경계 곡선과 접하거나 겹칩니다 — ({0:F3}, {1:F3})", a.X, a.Y);
                    return false;
                }
                failure = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "수정된 구간이 경계 곡선과 접하거나 겹칩니다 — ({0:F3}, {1:F3})", a.X, a.Y);
                return false;
            }
            if (ray.Status == RegionRayStatus.Miss) continue; // 끝점 판정만으로 내부 확정

            double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
            List<double> parameters = new();
            foreach (RegionBoundaryHit hit in ray.Hits)
            {
                double t = ((hit.Point.X - a.X) * direction.X + (hit.Point.Y - a.Y) * direction.Y) /
                           lengthSquared;
                if (t <= 1e-12 || t >= 1.0 - 1e-12) continue;
                parameters.Add(t);
            }
            parameters.Sort();

            // 교차 사이 open interval의 even-odd 상태는 일정하다. 중점 하나로 구간 전체를 판정한다.
            // 구간은 [0, p0], [p0, p1], ..., [pn, 1]로 교차 수 + 1개다.
            for (int i = 0; i <= parameters.Count; i++)
            {
                double start = i == 0 ? 0.0 : parameters[i - 1];
                double end = i == parameters.Count ? 1.0 : parameters[i];
                if (end - start <= 1e-12) continue;
                Vec2 sample = a + direction * (start + (end - start) * 0.5);
                if (!Classify(sample, out Relation relation, out failure)) return false;
                if (relation == Relation.Outside) { failure = OutsideFailure(sample); return false; }
                // 경계 위(수치 허용오차 이내)는 통과시킨다. 폭이 허용오차보다 좁은 홈은
                // 기존 계약과 동일하게 기하학적으로 구분하지 않는다.
            }
        }
        return true;
    }

    private enum Relation { Inside, OnBoundary, Outside }

    private bool Classify(Vec2 point, out Relation relation, out string failure)
    {
        relation = Relation.Outside;
        failure = string.Empty;

        RegionBoundaryProbe probe = _boundary.ProbeDistance(point, OnBoundaryTolerance);
        if (probe.Status == RegionRayStatus.Failure) { failure = probe.Detail; return false; }
        if (probe.Status == RegionRayStatus.Ambiguous) { failure = AmbiguousFailure(point); return false; }
        if (probe.Status == RegionRayStatus.Hit) { relation = Relation.OnBoundary; return true; }

        foreach (Vec2 direction in RayDirections)
        {
            RegionRayResult ray = _boundary.CastRay(point, direction);
            if (ray.Status == RegionRayStatus.Failure) { failure = ray.Detail; return false; }
            if (ray.Status == RegionRayStatus.Ambiguous) continue; // 방향만 모호: 다른 방향으로 재시도
            bool retry = false;
            IReadOnlyList<RegionBoundaryHit> hits = ray.Hits ?? Array.Empty<RegionBoundaryHit>();
            for (int i = 0; i < hits.Count && !retry; i++)
            {
                if (hits[i].AtTrimEndpoint) { retry = true; break; }
                for (int j = i + 1; j < hits.Count; j++)
                    if (hits[i].Point.DistanceTo(hits[j].Point) <= OnBoundaryTolerance)
                    { retry = true; break; }
            }
            if (retry) continue;
            // 꼭짓점을 지나지 않는 광선의 교차 수 홀짝이 even-odd 포함 여부다.
            relation = hits.Count % 2 == 1 ? Relation.Inside : Relation.Outside;
            return true;
        }
        failure = AmbiguousFailure(point);
        return false;
    }

    /// <summary>구간 전체가 경계 위에 있는지 오차 제어 표본으로 확인한다(겹침 판정 전용).</summary>
    private bool SegmentOnBoundary(Vec2 a, Vec2 b, out string failure)
    {
        failure = string.Empty;
        double length = a.DistanceTo(b);
        int steps = (int)Math.Clamp(Math.Ceiling(length), 2.0, 64.0);
        Vec2 direction = b - a;
        for (int i = 0; i <= steps; i++)
        {
            Vec2 point = a + direction * ((double)i / steps);
            RegionBoundaryProbe probe = _boundary.ProbeDistance(point, OnBoundaryTolerance);
            if (probe.Status == RegionRayStatus.Failure) { failure = probe.Detail; return false; }
            if (probe.Status != RegionRayStatus.Hit) return false;
        }
        return true;
    }

    private static string OutsideFailure(Vec2 p) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "수정된 구간이 적용영역을 벗어납니다 — ({0:F3}, {1:F3})", p.X, p.Y);

    private static string AmbiguousFailure(Vec2 p) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "수정된 구간의 적용영역 경계 판정이 모호합니다 — ({0:F3}, {1:F3})", p.X, p.Y);

    /// <summary>제외 사각형의 내부(경계 제외)를 지나는 구간을 찾는다.</summary>
    private static bool TryExclusionViolation(Vec2 a, Vec2 b,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        out Vec2 violation)
    {
        violation = default;
        if (exclusions.Count == 0) return false;
        foreach (var exclusion in exclusions)
        {
            double minX = exclusion.MinX, minY = exclusion.MinY;
            double maxX = exclusion.MaxX, maxY = exclusion.MaxY;
            if (!double.IsFinite(minX) || !double.IsFinite(minY) ||
                !double.IsFinite(maxX) || !double.IsFinite(maxY)) continue;
            if (minX > maxX) (minX, maxX) = (maxX, minX);
            if (minY > maxY) (minY, maxY) = (maxY, minY);

            // Liang-Barsky를 열린 사각형으로 적용한다. 경계에 정확히 놓인 구간은 허용한다.
            double t0 = 0.0, t1 = 1.0;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (!Clip(-dx, a.X - minX, ref t0, ref t1) ||
                !Clip(dx, maxX - a.X, ref t0, ref t1) ||
                !Clip(-dy, a.Y - minY, ref t0, ref t1) ||
                !Clip(dy, maxY - a.Y, ref t0, ref t1))
                continue;
            // 경계에 정확히 놓인 구간은 허용하고, 엄격한 내부를 지나는 구간만 위반으로 본다.
            if (!(t1 > t0)) continue;
            Vec2 middle = new(a.X + dx * ((t0 + t1) * 0.5), a.Y + dy * ((t0 + t1) * 0.5));
            if (middle.X > minX && middle.X < maxX && middle.Y > minY && middle.Y < maxY)
            {
                violation = middle;
                return true;
            }
        }
        return false;
    }

    private static bool Clip(double denominator, double numerator, ref double t0, ref double t1)
    {
        if (Math.Abs(denominator) < double.Epsilon) return numerator >= 0.0;
        double t = numerator / denominator;
        if (denominator > 0.0) { if (t < t1) t1 = t; }
        else { if (t > t0) t0 = t; }
        return t0 < t1;
    }
}
