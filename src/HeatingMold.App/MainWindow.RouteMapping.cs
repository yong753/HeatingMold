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
        WorkingArea area, WorkingSideDefinition side,
        CommittedRouteContext context = null)
    {
        Func<Vec2, SurfaceQueryResult> query = BuildSurfaceQueryDelegate(area, side, context);
        return uv =>
        {
            SurfaceQueryResult result = query(uv);
            return result.IsHit ? result.Sample : null;
        };
    }

    /// <summary>
    /// 위 규칙의 typed 판정. Miss(표면 없음)와 Failure(거부)를 구분해 제한 근사 정책이
    /// 문자열/마지막 오류 공유 없이 판정할 수 있게 한다.
    /// </summary>
    /// <remarks>
    /// 과거 nullable 규칙과 같은 순서·좌표를 보존한다: 선택 면 → 전역 표면 → 정확 경계 중
    /// <b>유효한 Hit가 나오면 즉시 그 결과</b>를 쓴다. 앞 단계의 하드 상태(예: 선택 면 조회의
    /// GeometryFailure)는 뒤 단계의 Hit를 막지 않는다. Hit가 전혀 없을 때만 하드 상태를
    /// Failure로 판정하고, 순수 Miss(선택 면/전역 표면 Miss + 경계 NoIntersection)만 근사
    /// 허용 Miss로 남긴다(<see cref="SurfaceProbePolicy"/>).
    /// </remarks>
    private Func<Vec2, SurfaceQueryResult> BuildSurfaceQueryDelegate(
        WorkingArea area, WorkingSideDefinition side,
        CommittedRouteContext context = null)
    {
        context ??= CommittedRouteContext.Capture(area);
        int[] faces = context.FaceIds;
        long id = side.Id.Value, revision = side.Revision.Value;
        return uv =>
        {
            if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y))
                return SurfaceQueryResult.FromFailure("작업평면 좌표가 유한하지 않습니다");

            SurfaceProbeOutcome selected = ProbeSelectedFace(uv, id, revision, faces);
            if (selected.State == SurfaceCandidateState.Hit)
                return SurfaceProbePolicy.Resolve(selected, SurfaceProbePolicy.NotRun, SurfaceProbePolicy.NotRun);

            SurfaceProbeOutcome global = ProbeGlobalSurface(uv, id, revision);
            if (global.State == SurfaceCandidateState.Hit)
                return SurfaceProbePolicy.Resolve(selected, global, SurfaceProbePolicy.NotRun);

            SurfaceProbeOutcome boundary = ProbeExactBoundary(uv, side, context);
            SurfaceQueryResult result = SurfaceProbePolicy.Resolve(selected, global, boundary);
            if (result.Outcome == SurfaceQueryOutcome.Miss)
                _lastSurfaceMissDetail = "ctx=" + id + "/" + revision + " faces=" + faces.Length
                    + " uv=(" + uv.X.ToString("R") + "," + uv.Y.ToString("R") + ")"
                    + " " + SurfaceProbePolicy.Describe("선택면", selected)
                    + " " + SurfaceProbePolicy.Describe("전역", global)
                    + " " + SurfaceProbePolicy.Describe("경계", boundary);
            return result;
        };
    }

    private static SurfaceProbeOutcome ProbeSelectedFace(Vec2 uv, long id, long revision, int[] faces)
    {
        var direct = Bridge.MapWorkingPointOntoFaces(id, revision, faces, uv.X, uv.Y);
        if (direct == null) return SurfaceProbeOutcome.Hard("조회 결과 없음");
        if (direct.Status == WorkingQueryStatus.Miss) return SurfaceProbeOutcome.Miss("Miss");
        if (direct.Status != WorkingQueryStatus.Hit)
            return SurfaceProbeOutcome.Hard(direct.Status.ToString());
        if (direct.Point == null || direct.Point.Length < 3)
            return SurfaceProbeOutcome.Hard("Hit인데 3D 점이 없습니다");
        // 여기서 값을 대체·보정하지 않는다. malformed Hit(비유한 좌표·지지 Face 없음·데이텀
        // 불일치)는 공통 factory가 Hard로 분류하므로, 다음 후보(전역 표면 → 정확 경계)를 계속
        // 시도하고 유효 후보가 하나도 없을 때만 Failure가 된다(단순 Miss/근사로 낮추지 않는다).
        return SurfaceProbeOutcome.FromHit(new SurfaceMappedSample(uv,
            new Vec3(direct.Point[0], direct.Point[1], direct.Point[2]), direct.FaceId), uv);
    }

    private static SurfaceProbeOutcome ProbeGlobalSurface(Vec2 uv, long id, long revision)
    {
        var global = Bridge.QueryWorkingSurface(id, revision, uv.X, uv.Y);
        if (global == null) return SurfaceProbeOutcome.Hard("조회 결과 없음");
        if (global.Status == WorkingQueryStatus.Miss) return SurfaceProbeOutcome.Miss("Miss");
        if (global.Status != WorkingQueryStatus.Hit && global.Status != WorkingQueryStatus.Tangent)
            return SurfaceProbeOutcome.Hard(global.Status.ToString());
        if (global.Point == null || global.Point.Length < 3)
            return SurfaceProbeOutcome.Hard("Hit인데 3D 점이 없습니다");
        return SurfaceProbeOutcome.FromHit(new SurfaceMappedSample(uv,
            new Vec3(global.Point[0], global.Point[1], global.Point[2]), global.FaceId), uv);
    }

    /// <summary>
    /// 선택 영역의 정확한 경계 위 점은 네이티브 면 교차가 경계 시작점을 Miss로 돌려줄 수 있다.
    /// 이때만 원본 경계 provenance의 parameter로 3D를 평가한다. 내부의 표면 틈을 최근접 점으로
    /// 대체하는 경로가 아니다. 경계 NoIntersection만 Miss로 보고, 모호/오래된/기하 오류는
    /// 하드 실패로 남긴다.
    /// </summary>
    private static SurfaceProbeOutcome ProbeExactBoundary(Vec2 uv, WorkingSideDefinition side,
        CommittedRouteContext context)
    {
        if (context.Boundary == null) return SurfaceProbeOutcome.Miss("경계 없음");
        ExitBoundaryResolution onBoundary = context.Boundary.Project(uv, ExitBoundaryConstraint.Tolerance);
        if (onBoundary.Succeeded)
        {
            double distance = onBoundary.Point.Value.Point.DistanceTo(uv);
            if (distance > ExitBoundaryConstraint.Tolerance)
                return SurfaceProbeOutcome.Miss("투영점 거리 " + distance.ToString("0.####") + "mm");
            return TryEvaluateExitSample(side, context, onBoundary.Point, uv,
                    out SurfaceMappedSample boundarySample, out string boundaryFailure)
                ? SurfaceProbeOutcome.FromHit(boundarySample, uv)
                : SurfaceProbeOutcome.Hard(boundaryFailure);
        }
        return onBoundary.Failure == ExitBoundaryFailureReason.NoIntersection
            ? SurfaceProbeOutcome.Miss(nameof(ExitBoundaryFailureReason.NoIntersection))
            : SurfaceProbeOutcome.Hard(onBoundary.Failure?.ToString() ?? "알 수 없는 경계 실패");
    }

    /// <summary>마지막 표면 매핑 실패 지점의 진단 문자열. 오류 메시지에만 쓰고 판정에는 쓰지 않는다.</summary>
    private string _lastSurfaceMissDetail = string.Empty;

    /// <summary>인출점 이동 확정/미리보기에서 해당 꼭지점만 대체할 표본을 만든다.</summary>
    private SurfaceMappedSample? BuildExitVertexOverride(
        WorkingSideDefinition side, CommittedRouteContext context, CircuitExitPoint exit)
    {
        ExitBoundaryPoint? point = exit == null ? null : ToBoundary(exit);
        if (!TryEvaluateExitSample(side, context, point, point?.Point,
                out SurfaceMappedSample sample, out string failure))
            throw new InvalidOperationException("인출 경계 평가 실패 — " + failure);
        return sample;
    }

    /// <summary>
    /// 드래그는 저장한 한 edge/trim에서만 2D 최근접 위치를 구한다.
    /// snap 허용 거리는 커서용이며, 확정 parameter의 평가 공차를 완화하지 않는다.
    /// </summary>
    private bool TryProjectExitDatum(WorkingSideDefinition side, CommittedRouteContext context,
        ExitBoundaryPoint? exit, Vec2 datum, double tolerance,
        out ExitBoundaryPoint projected, out SurfaceMappedSample sample, out string failure)
    {
        projected = default;
        sample = default;
        failure = "MissingReference: 인출점의 원본 edge/parameter가 없습니다";
        if (context?.Boundary == null || !exit.HasValue) return false;
        ExitBoundaryPoint saved = exit.Value;
        int kinds = (saved.Binding.HasValue ? 1 : 0)
            + (saved.ArtificialBinding.HasValue ? 1 : 0)
            + (saved.SplitBinding.HasValue ? 1 : 0);
        if (kinds != 1) return false;
        ExitBoundaryResolution result;
        if (saved.Binding.HasValue)
        {
            result = context.Boundary.ProjectBound(saved.Binding.Value, datum, tolerance);
        }
        else if (saved.ArtificialBinding.HasValue)
        {
            if (context.Boundary is not IArtificialExitBoundary2D artificial)
            { failure = "MissingReference: 인공 외곽선 경계가 없습니다"; return false; }
            result = artificial.ProjectArtificial(saved.ArtificialBinding.Value, datum, tolerance);
        }
        else
        {
            if (context.Boundary is not IArtificialSplitBoundary2D split)
            { failure = "MissingReference: 분할 절단선 경계가 없습니다"; return false; }
            result = split.ProjectSplitLine(saved.SplitBinding!.Value, datum, tolerance);
        }
        if (!result.Succeeded)
        { failure = result.Failure + ": 지정한 인출 경계에서 이동 위치를 정하지 못했습니다"; return false; }
        projected = result.Point.Value;
        if (!TryEvaluateExitSample(side, context, projected, projected.Point, out sample, out failure))
            return false;
        return true;
    }

    private static bool TryEvaluateExitSample(WorkingSideDefinition side, CommittedRouteContext context,
        ExitBoundaryPoint? exit, Vec2? expected,
        out SurfaceMappedSample sample, out string failure)
    {
        sample = default;
        failure = "MissingReference: 인출 경계의 원본 식별자가 없습니다";
        if (context?.Boundary == null || !exit.HasValue) return false;
        ExitBoundaryPoint saved = exit.Value;
        int kinds = (saved.Binding.HasValue ? 1 : 0)
            + (saved.ArtificialBinding.HasValue ? 1 : 0)
            + (saved.SplitBinding.HasValue ? 1 : 0);
        if (kinds != 1) return false;
        if (saved.ArtificialBinding.HasValue)
            return TryEvaluateArtificialExitSample(side, context, saved.ArtificialBinding.Value,
                expected, out sample, out failure);
        if (saved.SplitBinding.HasValue)
            return TryEvaluateSplitExitSample(side, context, saved.SplitBinding.Value,
                expected, out sample, out failure);

        ExitEdgeBinding binding = saved.Binding!.Value;
        CadExitBoundary2D boundary = context.Boundary as CadExitBoundary2D
            ?? (context.Boundary as EffectiveRegionBoundary2D)?.CadBoundary
            ?? (context.Boundary as SplitRegionBoundary2D)?.CadBoundary;
        if (boundary == null) return false;
        if (side == null || binding.WorkingContextId != side.Id.Value || binding.WorkingContextRevision != side.Revision.Value)
        { failure = "StaleContext: 인출점과 확정 작업평면이 일치하지 않습니다"; return false; }
        ExitEdgeHit hit = boundary.EvaluateNative(binding);
        string provenance = "edge=" + binding.EdgeKey + ", revision=" + binding.ModelRevision +
            ", context=" + binding.WorkingContextId + ", trim=[" + binding.FirstParameter + "," +
            binding.LastParameter + "], parameter=" + binding.Parameter;
        if (hit?.Status != ExitEdgeQueryStatus.Hit || hit.Point is not { Length: >= 3 } ||
            hit.DatumPoint is not { Length: >= 2 })
        { failure = (hit?.Status.ToString() ?? "GeometryFailure") + ": " + provenance; return false; }
        Vec2 actual = new(hit.DatumPoint[0], hit.DatumPoint[1]);
        Vec3 point = new(hit.Point[0], hit.Point[1], hit.Point[2]);
        if (!double.IsFinite(actual.X) || !double.IsFinite(actual.Y) ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z) ||
            (expected.HasValue && actual.DistanceTo(expected.Value) > ExitBoundaryConstraint.AcceptanceTolerance) ||
            side.Frame.Project(point).DistanceTo(actual) > ExitBoundaryConstraint.AcceptanceTolerance)
        { failure = "GeometryFailure: 원본 parameter의 2D/3D 좌표 불일치 — " + provenance; return false; }
        sample = new SurfaceMappedSample(actual, point, hit.FaceId);
        failure = null;
        return true;
    }

    private static bool TryEvaluateArtificialExitSample(WorkingSideDefinition side,
        CommittedRouteContext context, ArtificialEdgeBinding saved, Vec2? expected,
        out SurfaceMappedSample sample, out string failure)
    {
        sample = default;
        failure = "MissingReference: 인공 외곽선 경계가 없습니다";
        if (side == null || saved.WorkingContextId != side.Id.Value ||
            saved.WorkingContextRevision != side.Revision.Value ||
            context.Boundary is not IArtificialExitBoundary2D boundary)
            return false;
        ExitBoundaryResolution result = boundary.EvaluateArtificial(saved);
        if (!result.Succeeded || result.Point is not { } evaluated || !evaluated.ArtificialBinding.HasValue)
        { failure = (result.Failure ?? ExitBoundaryFailureReason.GeometryFailure) +
            ": 인공 외곽선 parameter 평가 실패"; return false; }
        ArtificialEdgeBinding actualBinding = evaluated.ArtificialBinding.Value;
        if (!SameArtificial(actualBinding, saved))
        { failure = "StaleContext: 인공 외곽선 식별자가 일치하지 않습니다"; return false; }
        return TryMapPlanePointToSurface(side, context, evaluated.Point, expected,
            "인공 외곽선", out sample, out failure);
    }

    /// <summary>분할로 생긴 절단선 위 점을 기존 인공 경계와 같은 표면 매핑 규칙으로 3D에 옮긴다.</summary>
    private static bool TryEvaluateSplitExitSample(WorkingSideDefinition side,
        CommittedRouteContext context, SplitLineBinding saved, Vec2? expected,
        out SurfaceMappedSample sample, out string failure)
    {
        sample = default;
        failure = "MissingReference: 분할 절단선 경계가 없습니다";
        if (side == null || saved.WorkingContextId != side.Id.Value ||
            saved.WorkingContextRevision != side.Revision.Value ||
            context.Boundary is not IArtificialSplitBoundary2D boundary)
            return false;
        ExitBoundaryResolution result = boundary.EvaluateSplitLine(saved);
        if (!result.Succeeded || result.Point is not { } evaluated || !evaluated.SplitBinding.HasValue)
        { failure = (result.Failure ?? ExitBoundaryFailureReason.GeometryFailure) +
            ": 분할 절단선 parameter 평가 실패"; return false; }
        if (evaluated.SplitBinding.Value != saved)
        { failure = "StaleContext: 분할 절단선 식별자가 일치하지 않습니다"; return false; }
        return TryMapPlanePointToSurface(side, context, evaluated.Point, expected,
            "분할 절단선", out sample, out failure);
    }

    private static bool TryMapPlanePointToSurface(WorkingSideDefinition side,
        CommittedRouteContext context, Vec2 actual, Vec2? expected, string label,
        out SurfaceMappedSample sample, out string failure)
    {
        sample = default;
        if (expected.HasValue && actual.DistanceTo(expected.Value) > ExitBoundaryConstraint.AcceptanceTolerance)
        { failure = "GeometryFailure: " + label + " parameter의 2D 좌표 불일치"; return false; }
        long id = side.Id.Value, revision = side.Revision.Value;
        var direct = Bridge.MapWorkingPointOntoFaces(id, revision, context.FaceIds, actual.X, actual.Y);
        double[] point = direct != null && direct.Status == WorkingQueryStatus.Hit ? direct.Point : null;
        int faceId = direct?.FaceId ?? -1;
        if (point == null || point.Length < 3)
        {
            var global = Bridge.QueryWorkingSurface(id, revision, actual.X, actual.Y);
            if (global != null &&
                (global.Status == WorkingQueryStatus.Hit || global.Status == WorkingQueryStatus.Tangent))
            {
                point = global.Point;
                faceId = global.FaceId;
            }
        }
        if (point == null || point.Length < 3)
        { failure = "GeometryFailure: " + label + " 점을 표면에 매핑하지 못했습니다"; return false; }
        Vec3 mapped = new(point[0], point[1], point[2]);
        if (!double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y) || !double.IsFinite(mapped.Z) ||
            side.Frame.Project(mapped).DistanceTo(actual) > ExitBoundaryConstraint.AcceptanceTolerance)
        { failure = "GeometryFailure: " + label + " 점의 2D/3D 좌표 불일치"; return false; }
        sample = new SurfaceMappedSample(actual, mapped, faceId);
        failure = null;
        return true;
    }

    private static bool SameArtificial(ArtificialEdgeBinding a, ArtificialEdgeBinding b) =>
        a.WorkingContextId == b.WorkingContextId &&
        a.WorkingContextRevision == b.WorkingContextRevision &&
        a.MinX == b.MinX && a.MinY == b.MinY && a.MaxX == b.MaxX && a.MaxY == b.MaxY &&
        a.Side == b.Side &&
        Math.Abs(a.FirstParameter - b.FirstParameter) <= ExitBoundaryConstraint.Tolerance &&
        Math.Abs(a.LastParameter - b.LastParameter) <= ExitBoundaryConstraint.Tolerance &&
        Math.Abs(a.Parameter - b.Parameter) <= ExitBoundaryConstraint.Tolerance;
}
