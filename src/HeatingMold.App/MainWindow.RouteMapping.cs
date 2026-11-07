using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>
    /// 경로·핸들·미리보기·확정이 모두 사용하는 단일 표면 매핑 규칙.
    /// 인출점은 원본 CAD edge 평가를 우선하고, 평가가 불가능하면 실측 공유 모서리
    /// 폴리라인 보간으로만 대체한다. 일반 꼭지점은 선택 면 직접 히트를 우선한다.
    /// </summary>
    private Func<Vec2, SurfaceMappedSample?> BuildSurfaceMapDelegate(
        WorkingArea area, WorkingSideDefinition side, Vec2? movedAnchorDatum = null,
        CommittedRouteContext context = null)
    {
        context ??= CommittedRouteContext.Capture(area);
        int[] faces = context.FaceIds;
        var anchors = new List<(Vec2 Datum, Vec3 Point, int FaceId)>();

        void AddAnchor(Vec2 datum)
        {
            if (Serpentine.DistanceToChain(context.ExitChain, datum) > 0.05) return;
            SurfaceMappedSample? sample = MapExitDatum(side, context, datum);
            if (!sample.HasValue) return;
            foreach (var existing in anchors)
                if (Math.Abs(existing.Datum.X - datum.X) <= 1e-9 &&
                    Math.Abs(existing.Datum.Y - datum.Y) <= 1e-9) return;
            anchors.Add((datum, sample.Value.Point, sample.Value.FaceId));
        }

        if (movedAnchorDatum.HasValue) AddAnchor(movedAnchorDatum.Value);
        foreach (Vec2 start in context.CircuitStarts) AddAnchor(start);
        foreach (Vec2 end in context.CircuitEnds) AddAnchor(end);
        if (context.StartAnchor.HasValue) AddAnchor(context.StartAnchor.Value);
        if (context.EndAnchor.HasValue) AddAnchor(context.EndAnchor.Value);
        if (area.Pattern != null && area.Pattern.RoutePieces.Count > 0)
        {
            var pieces = area.Pattern.RoutePieces;
            if (pieces[0].Points.Count > 0) AddAnchor(pieces[0].Points[0]);
            if (pieces[^1].Points.Count > 0) AddAnchor(pieces[^1].Points[^1]);
        }

        long id = side.Id.Value, revision = side.Revision.Value;
        return uv =>
        {
            foreach ((Vec2 datum, Vec3 point, int faceId) in anchors)
                if (uv == datum) return new SurfaceMappedSample(uv, point, faceId);

            var direct = Bridge.MapWorkingPointOntoFaces(id, revision, faces, uv.X, uv.Y);
            if (direct != null && direct.Status == WorkingQueryStatus.Hit && direct.Point != null)
                return new SurfaceMappedSample(uv,
                    new Vec3(direct.Point[0], direct.Point[1], direct.Point[2]), direct.FaceId);

            var global = Bridge.QueryWorkingSurface(id, revision, uv.X, uv.Y);
            if (global != null &&
                (global.Status == WorkingQueryStatus.Hit || global.Status == WorkingQueryStatus.Tangent) &&
                global.Point != null)
                return new SurfaceMappedSample(uv,
                    new Vec3(global.Point[0], global.Point[1], global.Point[2]), global.FaceId);
            return null;
        };
    }

    /// <summary>
    /// 인출 경계 위 데이텀을 원본 CAD edge의 정확한 3D 점으로 평가한다.
    /// edge 참조가 있는데 평가에 실패하면 폴리라인으로 조용히 대체하지 않는다.
    /// </summary>
    private SurfaceMappedSample? MapExitDatum(
        WorkingSideDefinition side, CommittedRouteContext context, Vec2 datum)
    {
        if (!TryProjectExitDatum(side, context, datum, 5.0, out _, out SurfaceMappedSample sample))
            return null;
        return sample;
    }

    /// <summary>인출점 이동 확정/미리보기에서 해당 꼭지점만 대체할 표본을 만든다.</summary>
    private SurfaceMappedSample? BuildExitVertexOverride(
        WorkingSideDefinition side, CommittedRouteContext context, Vec2 datum) =>
        MapExitDatum(side, context, datum);

    /// <summary>
    /// 드래그 중 데이텀을 인출 edge 위로 투영한다. 원본 edge 참조가 있으면 곡선까지
    /// 정확히 투영하고, 실패하면 대체하지 않는다. 반환 데이텀은 실제 edge 위 좌표다.
    /// </summary>
    private bool TryProjectExitDatum(WorkingSideDefinition side, CommittedRouteContext context,
        Vec2 datum, double tolerance, out Vec2 projected, out SurfaceMappedSample sample)
    {
        projected = datum;
        sample = default;
        if (context.ExitEdgeRefs.Count > 0)
        {
            SurfaceMappedSample? best = null;
            Vec2 bestDatum = datum;
            double bestDistance = double.MaxValue;
            foreach (ExitEdgeReference reference in context.ExitEdgeRefs)
            {
                ExitEdgeHit hit = Bridge.EvaluateExitEdge(
                    side.Id.Value, side.Revision.Value,
                    reference.Revision, reference.EdgeKey,
                    datum.X, datum.Y, tolerance);
                if (hit == null || hit.Status != ExitEdgeQueryStatus.Hit ||
                    hit.Point == null || hit.Point.Length < 3 ||
                    hit.DatumPoint == null || hit.DatumPoint.Length < 2)
                    continue;
                Vec2 candidate = new(hit.DatumPoint[0], hit.DatumPoint[1]);
                double dx = candidate.X - datum.X, dy = candidate.Y - datum.Y;
                double distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestDatum = candidate;
                best = new SurfaceMappedSample(candidate,
                    new Vec3(hit.Point[0], hit.Point[1], hit.Point[2]), hit.FaceId);
            }
            if (!best.HasValue) return false;
            projected = bestDatum;
            sample = best.Value;
            return true;
        }

        if (context.ExitChain.Count < 2 || context.ExitChain3D.Count != context.ExitChain.Count)
            return false;
        projected = Serpentine.SnapOnChain(context.ExitChain, datum);
        Vec3 point = PointOnExitChain3D(context.ExitChain, context.ExitChain3D, projected, out int segment);
        if (projected.DistanceTo(datum) > tolerance) return false;
        int faceId = segment >= 1 && segment - 1 < context.ExitChainFaceIds.Count
            ? context.ExitChainFaceIds[segment - 1]
            : context.ExitFaceId;
        sample = new SurfaceMappedSample(projected, point, faceId);
        return true;
    }
}
