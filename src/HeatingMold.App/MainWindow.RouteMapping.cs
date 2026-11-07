using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>
    /// 일반점의 단일 표면 매핑 규칙. 인출점은 좌표 동등성으로 치환하지 않고
    /// 해당 endpoint occurrence의 원본 edge/parameter override로만 적용한다.
    /// </summary>
    private Func<Vec2, SurfaceMappedSample?> BuildSurfaceMapDelegate(
        WorkingArea area, WorkingSideDefinition side, Vec2? movedAnchorDatum = null,
        CommittedRouteContext context = null)
    {
        context ??= CommittedRouteContext.Capture(area);
        int[] faces = context.FaceIds;
        long id = side.Id.Value, revision = side.Revision.Value;
        return uv =>
        {
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
    /// 저장한 원본 parameter를 직접 평가한다. 재검색/스냅/보간은 하지 않는다.
    /// </summary>
    private SurfaceMappedSample? MapExitDatum(
        WorkingSideDefinition side, CommittedRouteContext context, CircuitExitPoint exit)
    {
        return TryEvaluateExitSample(side, context, exit?.Binding, exit?.Point,
            out SurfaceMappedSample sample, out _) ? sample : null;
    }

    /// <summary>인출점 이동 확정/미리보기에서 해당 꼭지점만 대체할 표본을 만든다.</summary>
    private SurfaceMappedSample? BuildExitVertexOverride(
        WorkingSideDefinition side, CommittedRouteContext context, CircuitExitPoint exit)
    {
        if (!TryEvaluateExitSample(side, context, exit?.Binding, exit?.Point,
                out SurfaceMappedSample sample, out string failure))
            throw new InvalidOperationException("인출점 원본 CAD 평가 실패 — " + failure);
        return sample;
    }

    /// <summary>
    /// 드래그는 저장한 한 edge/trim에서만 2D 최근접 위치를 구한다.
    /// snap 허용 거리는 커서용이며, 확정 parameter의 평가 공차를 완화하지 않는다.
    /// </summary>
    private bool TryProjectExitDatum(WorkingSideDefinition side, CommittedRouteContext context,
        ExitEdgeBinding? binding, Vec2 datum, double tolerance,
        out ExitBoundaryPoint projected, out SurfaceMappedSample sample, out string failure)
    {
        projected = default;
        sample = default;
        failure = "MissingReference: 인출점의 원본 edge/parameter가 없습니다";
        if (!binding.HasValue || context?.Boundary == null) return false;
        ExitBoundaryResolution result = context.Boundary.ProjectBound(binding.Value, datum, tolerance);
        if (!result.Succeeded)
        { failure = result.Failure + ": 지정한 원본 edge에서 이동 위치를 정하지 못했습니다"; return false; }
        projected = result.Point.Value;
        if (!TryEvaluateExitSample(side, context, projected.Binding, projected.Point, out sample, out failure))
            return false;
        return true;
    }

    private static bool TryEvaluateExitSample(WorkingSideDefinition side, CommittedRouteContext context,
        ExitEdgeBinding? binding, Vec2? expected, out SurfaceMappedSample sample, out string failure)
    {
        sample = default;
        failure = "MissingReference: 원본 edge/parameter 또는 투영 경계가 없습니다";
        if (!binding.HasValue || context?.Boundary is not CadExitBoundary2D boundary) return false;
        ExitEdgeBinding saved = binding.Value;
        if (side == null || saved.WorkingContextId != side.Id.Value || saved.WorkingContextRevision != side.Revision.Value)
        { failure = "StaleContext: 인출점과 확정 작업평면이 일치하지 않습니다"; return false; }
        ExitEdgeHit hit = boundary.EvaluateNative(saved);
        string provenance = "edge=" + saved.EdgeKey + ", revision=" + saved.ModelRevision +
            ", context=" + saved.WorkingContextId + ", trim=[" + saved.FirstParameter + "," +
            saved.LastParameter + "], parameter=" + saved.Parameter;
        if (hit?.Status != ExitEdgeQueryStatus.Hit || hit.Point is not { Length: >= 3 } ||
            hit.DatumPoint is not { Length: >= 2 })
        { failure = (hit?.Status.ToString() ?? "GeometryFailure") + ": " + provenance; return false; }
        Vec2 actual = new(hit.DatumPoint[0], hit.DatumPoint[1]);
        Vec3 point = new(hit.Point[0], hit.Point[1], hit.Point[2]);
        if (!double.IsFinite(actual.X) || !double.IsFinite(actual.Y) ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z) ||
            (expected.HasValue && actual.DistanceTo(expected.Value) > ExitBoundaryConstraint.Tolerance) ||
            side.Frame.Project(point).DistanceTo(actual) > ExitBoundaryConstraint.Tolerance)
        { failure = "GeometryFailure: 원본 parameter의 2D/3D 좌표 불일치 — " + provenance; return false; }
        sample = new SurfaceMappedSample(actual, point, hit.FaceId);
        failure = null;
        return true;
    }
}
