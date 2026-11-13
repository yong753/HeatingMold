using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;
using static HeatingMold.App.PlanarMath2D;

namespace HeatingMold.App;

/// <summary>
/// 분할 영역 자식 하나의 정확 경계. 소속 판정은 근사 외곽 폴리곤까지의 거리가 아니라,
/// 부모의 원본 CAD trim을 이 자식의 유지 반평면과 제외영역으로 제한한 구간으로 결정한다.
/// 근사 폴리곤은 어느 쪽 절단선을 이 자식이 유지하는지, 그리고 절단선 표시 구간을
/// 정하는 데만 쓴다. 저장되는 binding에는 항상 <b>원본</b> trim 구간을 기록한다.
/// </summary>
internal sealed class SplitBoundarySnapshot
{
    private const double PointEpsilon = 1e-6;
    private const double ParameterEpsilon = 1e-9;

    // 반평면 판정은 정확 CAD 점과 절단선 위 점에만 적용한다. 근사 폴리곤의 격자 오차가
    // 아니라 원본 CAD 점 자체의 수치 오차만 허용한다.
    private const double HalfPlaneTolerance = 1e-6;

    // 제외 사각형은 경계 위 점을 제거하지 않는다. EffectiveRegionBoundary2D와 같은 기준.
    private const double ExclusionTolerance = 1e-7;

    internal readonly record struct CadInterval(
        ExitEdgeReference Original, ExitEdgeReference Query, double First, double Last);

    internal readonly record struct HalfPlane(Vec2 A, Vec2 Direction, bool KeepPositive);

    private readonly long _contextId;
    private readonly long _revision;
    private readonly IReadOnlyList<CadInterval> _cad;
    private readonly IReadOnlyList<HalfPlane> _planes;
    private readonly IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> _exclusions;

    private SplitBoundarySnapshot(long contextId, long revision, IReadOnlyList<CadInterval> cad,
        IReadOnlyList<HalfPlane> planes,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions)
    {
        _contextId = contextId;
        _revision = revision;
        _cad = cad;
        _planes = planes;
        _exclusions = exclusions;
    }

    /// <summary>이 자식이 유지하는 원본 CAD 곡선 구간(원본 parameter 기준).</summary>
    internal IReadOnlyList<CadInterval> CadIntervals => _cad;

    internal bool HasCadIntervals => _cad.Count > 0;

    /// <summary>
    /// 정확 경계점의 자식 소속 판정. 원본 CAD 점과 절단선 위 점에만 쓰며,
    /// 근사 폴리곤까지의 거리는 보지 않는다.
    /// </summary>
    internal bool IsInside(Vec2 point) => IsInsideGeometry(point, _planes, _exclusions);

    internal ExitBoundaryFailureReason ProjectCad(Vec2 point, double tolerance,
        List<ExitBoundaryPoint> candidates)
    {
        ExitBoundaryFailureReason deferred = ExitBoundaryFailureReason.NoIntersection;
        foreach (CadInterval interval in _cad)
        {
            ExitEdgeHit hit = Bridge.ProjectExitEdge(_contextId, _revision, interval.Query,
                point.X, point.Y, tolerance);
            if (hit == null) return ExitBoundaryFailureReason.GeometryFailure;
            if (hit.Status == ExitEdgeQueryStatus.Miss) continue;
            if (hit.Status != ExitEdgeQueryStatus.Hit)
            {
                deferred = CadExitBoundary2D.Reason(hit.Status);
                continue;
            }
            if (!TryDatum(hit, out Vec2 projected)) return ExitBoundaryFailureReason.GeometryFailure;
            // 부모 전체에서 가장 가까운 점 하나만 보고 버리지 않는다. 구간별 후보를 모두
            // 모아 이 자식의 반평면·제외영역 안에 있는 것만 남긴다.
            if (!IsInside(projected)) continue;
            AddCandidate(candidates, new ExitBoundaryPoint(projected, -1, hit.Parameter)
            {
                Binding = Convert(interval, hit)
            });
        }
        return deferred;
    }

    internal ExitBoundaryFailureReason AddRayHits(Vec2 origin, Vec2 direction, double tolerance,
        List<ExitBoundaryPoint> hits)
    {
        ExitBoundaryFailureReason deferred = ExitBoundaryFailureReason.NoIntersection;
        foreach (CadInterval interval in _cad)
        {
            ExitEdgeRayResult result = Bridge.IntersectExitEdgeRay(_contextId, _revision, interval.Query,
                origin.X, origin.Y, direction.X, direction.Y, tolerance);
            if (result == null) return ExitBoundaryFailureReason.GeometryFailure;
            if (result.Status == ExitEdgeQueryStatus.Miss) continue;
            if (result.Status != ExitEdgeQueryStatus.Hit)
            {
                deferred = CadExitBoundary2D.Reason(result.Status);
                continue;
            }
            foreach (ExitEdgeHit hit in result.Hits ?? Array.Empty<ExitEdgeHit>())
            {
                if (hit.Status != ExitEdgeQueryStatus.Hit) return CadExitBoundary2D.Reason(hit.Status);
                if (!TryDatum(hit, out Vec2 point)) return ExitBoundaryFailureReason.GeometryFailure;
                if (!IsInside(point)) continue;
                AddCandidate(hits, new ExitBoundaryPoint(point, -1, hit.Parameter)
                {
                    Binding = Convert(interval, hit)
                });
            }
        }
        return deferred;
    }

    /// <summary>
    /// 자동 인출점의 행 직선과 이 자식이 유지하는 원본 CAD 구간의 정확 교차.
    /// 마우스 최근접 투영과 달리 커서 허용치가 없고, 교차가 접선·겹침이거나 같은 2D 점의
    /// 다른 parameter가 나오면 명시적 실패로 돌려준다. Miss는 교차 없음으로 건너뛴다.
    /// </summary>
    internal ExitBoundaryFailureReason AddLineHits(Vec2 a, Vec2 b, List<ExitBoundaryPoint> hits)
    {
        if (!IsFinite(a) || !IsFinite(b) || a.DistanceTo(b) <= PointEpsilon)
            return ExitBoundaryFailureReason.InvalidInput;

        foreach (CadInterval interval in _cad)
        {
            ExitEdgeRayResult result = Bridge.IntersectExitEdgeLine(_contextId, _revision, interval.Query,
                a.X, a.Y, b.X, b.Y);
            if (result == null) return ExitBoundaryFailureReason.GeometryFailure;
            if (result.Status == ExitEdgeQueryStatus.Miss) continue;
            if (result.Status != ExitEdgeQueryStatus.Hit)
                return CadExitBoundary2D.Reason(result.Status);
            foreach (ExitEdgeHit hit in result.Hits ?? Array.Empty<ExitEdgeHit>())
            {
                if (hit.Status == ExitEdgeQueryStatus.Miss) continue;
                if (hit.Status != ExitEdgeQueryStatus.Hit)
                    return CadExitBoundary2D.Reason(hit.Status);
                if (!TryDatum(hit, out Vec2 point)) return ExitBoundaryFailureReason.GeometryFailure;
                if (!IsInside(point)) continue;
                AddLineCandidate(hits, new ExitBoundaryPoint(point, -1, hit.Parameter)
                {
                    Binding = Convert(interval, hit)
                });
            }
        }
        return ExitBoundaryFailureReason.NoIntersection;
    }

    private static void AddCandidate(List<ExitBoundaryPoint> candidates, ExitBoundaryPoint candidate)
    {
        if (candidates.Any(existing => existing.Point.DistanceTo(candidate.Point) <= PointEpsilon)) return;
        candidates.Add(candidate);
    }

    /// <summary>
    /// 정확 교차 후보는 같은 위치라도 원본 참조나 선택 parameter가 다르면 서로 다른 해로 남긴다.
    /// 호출부가 그 차이를 모호성으로 판정할 수 있어야 하기 때문이다.
    /// </summary>
    private static void AddLineCandidate(List<ExitBoundaryPoint> candidates, ExitBoundaryPoint candidate)
    {
        foreach (ExitBoundaryPoint existing in candidates)
        {
            if (existing.Point.DistanceTo(candidate.Point) > PointEpsilon) continue;
            if (ExitBoundaryIdentity.Same(existing, candidate)) return;
        }
        candidates.Add(candidate);
    }

    private ExitEdgeBinding Convert(CadInterval interval, ExitEdgeHit hit) =>
        new(interval.Original.Revision, _contextId, _revision, interval.Original.EdgeKey,
            interval.Original.FirstParameter, interval.Original.LastParameter, hit.Parameter,
            interval.Original.Reversed);

    private static bool TryDatum(ExitEdgeHit hit, out Vec2 point)
    {
        point = default;
        if (hit.DatumPoint is not { Length: >= 2 } || !double.IsFinite(hit.Parameter)) return false;
        point = new Vec2(hit.DatumPoint[0], hit.DatumPoint[1]);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private static bool IsInsideGeometry(Vec2 point,
        IReadOnlyList<HalfPlane> planes,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions)
    {
        foreach (HalfPlane plane in planes)
        {
            double side = Cross(plane.Direction, point - plane.A);
            if (plane.KeepPositive)
            {
                if (side < -HalfPlaneTolerance) return false;
            }
            else if (side > HalfPlaneTolerance) return false;
        }

        foreach ((double minX, double minY, double maxX, double maxY) in exclusions)
        {
            if (point.X > minX + ExclusionTolerance && point.X < maxX - ExclusionTolerance &&
                point.Y > minY + ExclusionTolerance && point.Y < maxY - ExclusionTolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 이 자식의 정확 경계를 구성한다. <paramref name="cutLines"/>는 이 영역이 상속한
    /// 전체 절단선이고, <paramref name="ring"/>은 분할이 만든 자식 외곽 폴리곤이다.
    /// </summary>
    internal static bool TryBuild(CadExitBoundary2D cad,
        IReadOnlyList<Vec2> ring,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        IReadOnlyList<(Vec2 A, Vec2 B)> cutLines,
        out SplitBoundarySnapshot snapshot, out string failure)
    {
        snapshot = null!;
        failure = string.Empty;
        if (cad == null)
        {
            failure = "분할 영역의 원본 CAD 경계가 없습니다.";
            return false;
        }
        if (ring == null || ring.Count < 3)
        {
            failure = "분할 영역 외곽선이 올바르지 않습니다.";
            return false;
        }

        var lines = new List<(Vec2 A, Vec2 B)>();
        foreach ((Vec2 a, Vec2 b) in cutLines ?? Array.Empty<(Vec2, Vec2)>())
        {
            if (!IsFinite(a) || !IsFinite(b) || a.DistanceTo(b) <= PointEpsilon) continue;
            if (lines.Any(line => SameLine(line, (a, b)))) continue;
            lines.Add((a, b));
        }
        if (lines.Count == 0)
        {
            failure = "분할 영역의 절단선을 찾지 못했습니다.";
            return false;
        }

        var planes = new List<HalfPlane>(lines.Count);
        foreach ((Vec2 a, Vec2 b) in lines)
        {
            if (!TryResolveHalfPlane(a, b, ring, out HalfPlane plane))
            {
                failure = "분할 영역 외곽을 절단선이 가로지릅니다.";
                return false;
            }
            planes.Add(plane);
        }

        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusionList =
            exclusions ?? Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>();
        var exclusionRects = exclusionList
            .Where(rect => rect.MaxX - rect.MinX > PointEpsilon && rect.MaxY - rect.MinY > PointEpsilon)
            .ToArray();

        // 절단선과 제외 사각형 변은 원본 CAD 곡선을 자식 구간으로 나누는 경계다.
        // 나눈 구간마다 중간점이 이 자식 안인지 확인해야 근사 폴리곤 없이 소속을 정할 수 있다.
        var dividers = new List<(Vec2 A, Vec2 B)>(lines);
        foreach ((double minX, double minY, double maxX, double maxY) in exclusionRects)
        {
            dividers.Add((new Vec2(minX, minY), new Vec2(minX, maxY)));
            dividers.Add((new Vec2(maxX, minY), new Vec2(maxX, maxY)));
            dividers.Add((new Vec2(minX, minY), new Vec2(maxX, minY)));
            dividers.Add((new Vec2(minX, maxY), new Vec2(maxX, maxY)));
        }

        var intervals = new List<CadInterval>();
        foreach (ExitEdgeReference edge in cad.Edges)
        {
            if (edge == null) continue;
            double first = edge.FirstParameter;
            double last = edge.LastParameter;
            if (!double.IsFinite(first) || !double.IsFinite(last) || last <= first) continue;
            double parameterEpsilon = ParameterEpsilonFor(first, last);

            var breaks = new List<double>(dividers.Count + 2) { first, last };
            foreach ((Vec2 a, Vec2 b) in dividers)
            {
                ExitEdgeRayResult result = Bridge.IntersectExitEdgeLine(cad.ContextId, cad.Revision, edge,
                    a.X, a.Y, b.X, b.Y);
                if (result == null)
                {
                    failure = "분할 영역 경계의 원본 CAD 교차를 계산하지 못했습니다.";
                    return false;
                }
                if (result.Status == ExitEdgeQueryStatus.Miss) continue;
                if (result.Status != ExitEdgeQueryStatus.Hit)
                {
                    failure = DescribeStatus("분할 영역 경계의 원본 CAD 교차", result.Status);
                    return false;
                }
                foreach (ExitEdgeHit hit in result.Hits ?? Array.Empty<ExitEdgeHit>())
                {
                    if (hit.Status != ExitEdgeQueryStatus.Hit)
                    {
                        failure = DescribeStatus("분할 영역 경계의 원본 CAD 교차", hit.Status);
                        return false;
                    }
                    if (!double.IsFinite(hit.Parameter)) continue;
                    if (hit.Parameter < first - parameterEpsilon || hit.Parameter > last + parameterEpsilon) continue;
                    breaks.Add(Math.Clamp(hit.Parameter, first, last));
                }
            }

            breaks.Sort();
            for (int i = 0; i + 1 < breaks.Count; i++)
            {
                double start = breaks[i];
                double end = breaks[i + 1];
                if (end - start <= parameterEpsilon) continue;
                ExitEdgeHit sample = Bridge.EvaluateExitEdgeParameter(cad.ContextId, cad.Revision, edge,
                    (start + end) * 0.5);
                if (sample == null || sample.Status != ExitEdgeQueryStatus.Hit)
                {
                    failure = "분할 영역 경계의 원본 CAD 구간을 확인하지 못했습니다.";
                    return false;
                }
                if (!TryDatum(sample, out Vec2 middle))
                {
                    failure = "분할 영역 경계의 원본 CAD 구간을 확인하지 못했습니다.";
                    return false;
                }
                if (!IsInsideGeometry(middle, planes, exclusionRects)) continue;
                intervals.Add(new CadInterval(edge, SubTrim(edge, start, end), start, end));
            }
        }

        snapshot = new SplitBoundarySnapshot(cad.ContextId, cad.Revision, intervals, planes, exclusionRects);
        return true;
    }

    /// <summary>
    /// 절단선이 이 자식의 어느 쪽을 유지하는지 판정한다. 자식 외곽이 선을 실제로
    /// 가로지르면(양쪽에 꼭짓점이 있으면) 분할 결과가 모순이므로 명시적으로 실패한다.
    /// </summary>
    private static bool TryResolveHalfPlane(Vec2 a, Vec2 b, IReadOnlyList<Vec2> ring, out HalfPlane plane)
    {
        plane = default;
        Vec2 direction = b - a;
        double length = direction.Length;
        if (!double.IsFinite(length) || length <= PointEpsilon) return false;
        direction = new Vec2(direction.X / length, direction.Y / length);

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (Vec2 point in ring)
        {
            double side = Cross(direction, point - a);
            if (!double.IsFinite(side)) return false;
            if (side < min) min = side;
            if (side > max) max = side;
        }

        // 자식 외곽은 클리퍼 격자 때문에 절단선에서 반 격자(0.005 mm) 안쪽/바깥쪽에 놓인다.
        // 그 오프셋은 '선을 가로지름'이 아니므로 유지측 판정에서 허용한다.
        const double gridTolerance = 0.01;
        if (min >= -gridTolerance)
        {
            plane = new HalfPlane(a, direction, KeepPositive: true);
            return true;
        }
        if (max <= gridTolerance)
        {
            plane = new HalfPlane(a, direction, KeepPositive: false);
            return true;
        }
        return false;
    }

    private static bool SameLine((Vec2 A, Vec2 B) left, (Vec2 A, Vec2 B) right)
    {
        Vec2 leftDirection = left.B - left.A;
        Vec2 rightDirection = right.B - right.A;
        double leftLength = leftDirection.Length;
        double rightLength = rightDirection.Length;
        if (!(leftLength > PointEpsilon) || !(rightLength > PointEpsilon)) return false;
        Vec2 leftUnit = new(leftDirection.X / leftLength, leftDirection.Y / leftLength);
        Vec2 rightUnit = new(rightDirection.X / rightLength, rightDirection.Y / rightLength);
        if (Math.Abs(Cross(leftUnit, rightUnit)) > 1e-9) return false;
        return DistanceToSegment(left.A, left.B, right.A) <= PointEpsilon &&
            DistanceToSegment(left.A, left.B, right.B) <= PointEpsilon;
    }

    private static double ParameterEpsilonFor(double first, double last) =>
        Math.Max(ParameterEpsilon, ParameterEpsilon * Math.Max(1.0, Math.Max(Math.Abs(first), Math.Abs(last))));

    private static ExitEdgeReference SubTrim(ExitEdgeReference edge, double first, double last)
    {
        ExitEdgeReference query = CadExitBoundary2D.CopyReference(edge);
        query.FirstParameter = first;
        query.LastParameter = last;
        return query;
    }

    private static string DescribeStatus(string context, ExitEdgeQueryStatus status) =>
        status switch
        {
            ExitEdgeQueryStatus.TangentOrOverlap => context + "가 접선이거나 겹칩니다.",
            ExitEdgeQueryStatus.Ambiguous => context + "가 모호합니다 — 같은 위치에 서로 다른 원본 해가 있습니다.",
            ExitEdgeQueryStatus.DegenerateProjection => context + "의 작업평면 투영이 퇴화했습니다.",
            ExitEdgeQueryStatus.StaleContext => context + "의 작업 컨텍스트가 오래되었습니다.",
            ExitEdgeQueryStatus.MissingReference => context + "의 원본 CAD 참조를 찾지 못했습니다.",
            ExitEdgeQueryStatus.InvalidInput => context + "의 입력이 올바르지 않습니다.",
            _ => context + "를 계산하지 못했습니다.",
        };

    internal string Describe()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"planes={_planes.Count} cad={_cad.Count}");
        foreach (CadInterval interval in _cad)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" [edge={interval.Original.EdgeKey} {interval.First:F6}..{interval.Last:F6}]");
        }
        return text.ToString();
    }
}
