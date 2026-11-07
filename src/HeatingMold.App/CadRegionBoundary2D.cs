using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 확정 작업평면에 투영된 원본 CAD 영역 경계에 대한 정확 질의.
/// 표시용 폴리라인/현 근사를 사용하지 않으며, 오래된 참조나 모호한 해는 실패로 보고한다.
/// </summary>
internal sealed class CadRegionBoundary2D : IExactRegionBoundary
{
    private readonly long _contextId;
    private readonly long _revision;
    private readonly ExitEdgeReference[] _edges;

    public CadRegionBoundary2D(long contextId, long revision, IEnumerable<ExitEdgeReference> edges)
    {
        _contextId = contextId;
        _revision = revision;
        _edges = edges == null
            ? Array.Empty<ExitEdgeReference>()
            : System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(edges, CadExitBoundary2D.CopyReference));
    }

    public RegionRayResult CastRay(Vec2 origin, Vec2 direction)
    {
        if (_edges.Length == 0) return Fail("영역 경계의 원본 CAD 참조가 없습니다.");
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) ||
            !double.IsFinite(direction.X) || !double.IsFinite(direction.Y))
            return Fail("경계 질의 좌표가 유한하지 않습니다.");
        var hits = new List<RegionBoundaryHit>();
        foreach (ExitEdgeReference edge in _edges)
        {
            ExitEdgeRayResult result = Bridge.IntersectExitEdgeRay(_contextId, _revision, edge,
                origin.X, origin.Y, direction.X, direction.Y, 0.0);
            if (result == null) return Fail("영역 경계 광선 질의가 실패했습니다.");
            if (result.Status == ExitEdgeQueryStatus.Miss) continue;
            if (result.Status == ExitEdgeQueryStatus.TangentOrOverlap)
                return new RegionRayResult(RegionRayStatus.Ambiguous, Array.Empty<RegionBoundaryHit>(),
                    "경계 곡선과 접하거나 겹칩니다.");
            if (result.Status != ExitEdgeQueryStatus.Hit) return Fail(Detail(result.Status));
            foreach (ExitEdgeHit hit in result.Hits ?? Array.Empty<ExitEdgeHit>())
            {
                if (hit == null || hit.Status != ExitEdgeQueryStatus.Hit) return Fail(Detail(hit?.Status));
                if (hit.DatumPoint is not { Length: >= 2 } || !double.IsFinite(hit.DatumPoint[0]) ||
                    !double.IsFinite(hit.DatumPoint[1]))
                    return Fail("경계 교차점 좌표가 유효하지 않습니다.");
                hits.Add(new RegionBoundaryHit(new Vec2(hit.DatumPoint[0], hit.DatumPoint[1]),
                    AtTrimEndpoint(hit, edge)));
            }
        }
        return hits.Count == 0
            ? new RegionRayResult(RegionRayStatus.Miss, Array.Empty<RegionBoundaryHit>(), string.Empty)
            : new RegionRayResult(RegionRayStatus.Hit, hits, string.Empty);
    }

    public RegionBoundaryProbe ProbeDistance(Vec2 point, double maxDistance)
    {
        if (_edges.Length == 0) return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0,
            "영역 경계의 원본 CAD 참조가 없습니다.");
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(maxDistance) ||
            maxDistance < 0.0)
            return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0, "경계 거리 질의 입력이 유효하지 않습니다.");
        double nearest = double.PositiveInfinity;
        foreach (ExitEdgeReference edge in _edges)
        {
            ExitEdgeHit hit = Bridge.ProjectExitEdge(_contextId, _revision, edge, point.X, point.Y, maxDistance);
            if (hit == null) return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0,
                "영역 경계 최근접 질의가 실패했습니다.");
            if (hit.Status == ExitEdgeQueryStatus.Miss) continue;
            if (hit.Status != ExitEdgeQueryStatus.Hit)
                return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0, Detail(hit.Status));
            if (hit.DatumPoint is not { Length: >= 2 } ||
                !double.IsFinite(hit.DatumPoint[0]) || !double.IsFinite(hit.DatumPoint[1]))
                return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0, "경계 최근접점 좌표가 유효하지 않습니다.");
            double distance = new Vec2(hit.DatumPoint[0], hit.DatumPoint[1]).DistanceTo(point);
            if (!double.IsFinite(distance))
                return new RegionBoundaryProbe(RegionRayStatus.Failure, 0.0, "경계 거리가 유한하지 않습니다.");
            if (distance < nearest) nearest = distance;
        }
        return double.IsFinite(nearest)
            ? new RegionBoundaryProbe(RegionRayStatus.Hit, nearest, string.Empty)
            : new RegionBoundaryProbe(RegionRayStatus.Miss, 0.0, string.Empty);
    }

    private static bool AtTrimEndpoint(ExitEdgeHit hit, ExitEdgeReference edge)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(edge.FirstParameter), Math.Abs(edge.LastParameter)));
        double epsilon = 1e-9 * scale;
        return Math.Abs(hit.Parameter - edge.FirstParameter) <= epsilon ||
               Math.Abs(hit.Parameter - edge.LastParameter) <= epsilon;
    }

    private static RegionRayResult Fail(string detail) =>
        new(RegionRayStatus.Failure, Array.Empty<RegionBoundaryHit>(), detail);

    private static string Detail(ExitEdgeQueryStatus? status) => status switch
    {
        ExitEdgeQueryStatus.StaleContext => "영역 경계 참조가 현재 모델/작업 컨텍스트와 맞지 않습니다 — 영역을 다시 확정하세요.",
        ExitEdgeQueryStatus.MissingReference => "영역 경계의 원본 CAD 참조를 찾지 못했습니다.",
        ExitEdgeQueryStatus.GeometryFailure => "영역 경계 기하 질의가 실패했습니다.",
        ExitEdgeQueryStatus.InvalidInput => "영역 경계 질의 입력이 유효하지 않습니다.",
        ExitEdgeQueryStatus.DegenerateProjection => "영역 경계 곡선의 작업평면 투영이 퇴화했습니다.",
        ExitEdgeQueryStatus.Ambiguous => "영역 경계 질의 결과가 모호합니다.",
        ExitEdgeQueryStatus.TangentOrOverlap => "경계 곡선과 접하거나 겹칩니다.",
        ExitEdgeQueryStatus.Miss => string.Empty,
        _ => "영역 경계 질의가 실패했습니다.",
    };
}
