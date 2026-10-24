using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Editing;

/// <summary>공유 절점을 이동한 발열 경로 후보를 안전하게 만듭니다.</summary>
public sealed class ControlPointEditService
{
    /// <summary>revision, 표면 참조와 곡선 종류를 확인한 뒤 연결된 모든 조각을 함께 갱신합니다.</summary>
    public OperationResult<HeatingPathEditDraft> Move(ControlPointEditRequest request, DesignProject currentProject)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentProject);

        if (request.BaseRevision != currentProject.Revision)
            return Failure("StaleControlPointEdit", "절점 편집 요청의 기준 revision이 현재 설계와 다릅니다.", request, currentProject);

        CoreHeatingPath? path = currentProject.HeatingPaths.SingleOrDefault(candidate => candidate.Id == request.PathId);
        if (path is null)
            return Failure("HeatingPathNotFound", "편집 대상 발열 경로가 현재 설계에 없습니다.", request, currentProject);

        HeatingRegion? region = currentProject.HeatingRegions.SingleOrDefault(
            candidate => candidate.Id == path.GenerationSource.RegionId);
        if (region is null || !region.FaceReferences.Contains(request.TargetAnchor.FaceReferenceId))
            return Failure("ControlPointAnchorOutsideRegion", "새 표면 앵커가 발열 영역의 선택 Face에 속하지 않습니다.", request, currentProject);

        ControlPoint? movedPoint = path.ControlPoints.SingleOrDefault(point => point.Id == request.ControlPointId);
        if (movedPoint is null)
            return Failure("ControlPointNotFound", "편집 대상 제어점을 현재 발열 경로에서 찾을 수 없습니다.", request, currentProject);

        HeatingPathSegment[] affectedSegments = path.Segments
            .Where(segment => segment.StartControlPointId == movedPoint.Id || segment.EndControlPointId == movedPoint.Id)
            .ToArray();
        if (affectedSegments.Length == 0 || !affectedSegments.Any(segment => segment.IsActive))
            return Failure("ControlPointNotEditable", "활성 경로 조각에 연결되지 않은 제어점은 이동할 수 없습니다.", request, currentProject);

        var replacements = new Dictionary<HeatingPathSegmentId, HeatingPathSegment>();
        foreach (HeatingPathSegment segment in affectedSegments)
        {
            bool isPathStart = segment.StartControlPointId == movedPoint.Id;
            if (!CurveEndpointEditor.TryMoveEndpoint(
                    segment.Curve,
                    isPathStart,
                    request.TargetAnchor.ModelPoint,
                    request.EndpointToleranceMm,
                    out CurveDefinition? updatedCurve,
                    out string failure))
            {
                return Failure("UnsupportedControlPointEdit", failure, request, currentProject, segment.Id.Value);
            }

            replacements.Add(segment.Id, new HeatingPathSegment(
                segment.Id,
                segment.Kind,
                segment.StartControlPointId,
                segment.EndControlPointId,
                segment.Sequence,
                segment.IsActive,
                updatedCurve!,
                segment.LaneIndex));
        }

        ControlPoint[] controlPoints = path.ControlPoints
            .Select(point => point.Id == movedPoint.Id
                ? new ControlPoint(point.Id, request.TargetAnchor.ModelPoint)
                : point)
            .ToArray();
        HeatingPathSegment[] candidateSegments = path.Segments
            .Select(segment => replacements.TryGetValue(segment.Id, out HeatingPathSegment? replacement)
                ? replacement
                : segment)
            .ToArray();
        var candidatePath = new CoreHeatingPath(
            path.Id,
            path.GenerationSource,
            controlPoints,
            path.GeneratedSegments,
            candidateSegments);
        var draft = new HeatingPathEditDraft(
            request.BaseRevision,
            path.Id,
            movedPoint.Id,
            movedPoint.Position,
            request.TargetAnchor,
            candidatePath,
            affectedSegments.Select(segment => segment.Id));

        return OperationResult<HeatingPathEditDraft>.Success(draft);
    }

    private static OperationResult<HeatingPathEditDraft> Failure(
        string code,
        string evidence,
        ControlPointEditRequest request,
        DesignProject project,
        params Guid[] additionalIds) => OperationResult<HeatingPathEditDraft>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            new[] { project.Id.Value, request.PathId.Value, request.ControlPointId.Value }.Concat(additionalIds),
            project.Rules.ValidationRuleVersion,
            request.TargetAnchor.ModelPoint,
            request.TargetAnchor.WorkingUv,
            evidence: [evidence])]);
}

/// <summary>이동할 공유 절점과 표면 앵커, 편집 허용오차를 지정합니다.</summary>
public sealed record ControlPointEditRequest
{
    /// <summary>편집 대상 경로의 안정 ID입니다.</summary>
    public HeatingPathId PathId { get; }

    /// <summary>함께 이동할 공유 제어점 ID입니다.</summary>
    public ControlPointId ControlPointId { get; }

    /// <summary>요청이 기반으로 삼은 확정 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>Native 표면 재검증에 넘길 새 위치입니다.</summary>
    public SurfaceAnchor TargetAnchor { get; }

    /// <summary>원호 이동에서 평면과 반지름을 유지하는 허용오차(mm)입니다.</summary>
    public double EndpointToleranceMm { get; }

    /// <summary>유효 경로·절점·앵커와 양의 편집 허용오차를 보관합니다.</summary>
    public ControlPointEditRequest(
        HeatingPathId pathId,
        ControlPointId controlPointId,
        DesignRevision baseRevision,
        SurfaceAnchor targetAnchor,
        double endpointToleranceMm)
    {
        if (pathId.IsEmpty || controlPointId.IsEmpty)
            throw new ArgumentException("절점 편집에는 경로와 제어점 안정 ID가 필요합니다.");
        if (endpointToleranceMm <= 0 || !double.IsFinite(endpointToleranceMm))
            throw new ArgumentOutOfRangeException(nameof(endpointToleranceMm));

        PathId = pathId;
        ControlPointId = controlPointId;
        BaseRevision = baseRevision;
        TargetAnchor = targetAnchor ?? throw new ArgumentNullException(nameof(targetAnchor));
        EndpointToleranceMm = endpointToleranceMm;
    }
}

/// <summary>Native 재생성과 전체 경로 재검증을 기다리는 절점 이동 후보입니다.</summary>
public sealed class HeatingPathEditDraft
{
    /// <summary>후보가 기반으로 삼은 확정 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>편집한 발열 경로 ID입니다.</summary>
    public HeatingPathId PathId { get; }

    /// <summary>공유 이동 절점 ID입니다.</summary>
    public ControlPointId ControlPointId { get; }

    /// <summary>편집 전 모델 좌표입니다.</summary>
    public Vec3 PreviousPosition { get; }

    /// <summary>사용자가 지정한 새 표면 위치입니다.</summary>
    public SurfaceAnchor TargetAnchor { get; }

    /// <summary>공유 절점 변경을 반영한 관리형 경로 후보입니다.</summary>
    public CoreHeatingPath CandidatePath { get; }

    /// <summary>이번 편집으로 다시 생성할 경로 조각 안정 ID입니다.</summary>
    public IReadOnlyList<HeatingPathSegmentId> AffectedSegmentIds { get; }

    /// <summary>기준과 영향 범위를 방어 복사해 이동 후보를 만듭니다.</summary>
    public HeatingPathEditDraft(
        DesignRevision baseRevision,
        HeatingPathId pathId,
        ControlPointId controlPointId,
        Vec3 previousPosition,
        SurfaceAnchor targetAnchor,
        CoreHeatingPath candidatePath,
        IEnumerable<HeatingPathSegmentId> affectedSegmentIds)
    {
        HeatingPathSegmentId[] ids = affectedSegmentIds?.Distinct().ToArray()
            ?? throw new ArgumentNullException(nameof(affectedSegmentIds));
        if (pathId.IsEmpty || controlPointId.IsEmpty || candidatePath.Id != pathId || ids.Length == 0)
            throw new ArgumentException("절점 편집 후보에는 경로·절점 ID와 영향 구간이 필요합니다.");

        BaseRevision = baseRevision;
        PathId = pathId;
        ControlPointId = controlPointId;
        PreviousPosition = previousPosition;
        TargetAnchor = targetAnchor ?? throw new ArgumentNullException(nameof(targetAnchor));
        CandidatePath = candidatePath ?? throw new ArgumentNullException(nameof(candidatePath));
        AffectedSegmentIds = new ReadOnlyCollection<HeatingPathSegmentId>(ids);
    }
}

/// <summary>지원하는 곡선 정의의 시작 또는 끝만 이동하고 형식 정보를 보존합니다.</summary>
internal static class CurveEndpointEditor
{
    public static bool TryMoveEndpoint(
        CurveDefinition curve,
        bool isPathStart,
        Vec3 target,
        double toleranceMm,
        out CurveDefinition? updatedCurve,
        out string failure)
    {
        bool isDefinitionStart = isPathStart == (curve.Direction == CurveDirection.Forward);
        try
        {
            updatedCurve = curve switch
            {
                LineCurveDefinition line => MoveLine(line, isDefinitionStart, target),
                PolylineCurveDefinition polyline => MovePolyline(polyline, isDefinitionStart, target),
                ArcCurveDefinition arc => MoveArc(arc, isDefinitionStart, target, toleranceMm),
                BSplineCurveDefinition => null,
                _ => null
            };
        }
        catch (ArgumentException)
        {
            updatedCurve = null;
        }

        if (updatedCurve is null)
        {
            failure = curve is BSplineCurveDefinition
                ? "B-spline 샘플을 제어 극점으로 간주하지 않습니다. Native 곡선 편집기가 필요합니다."
                : "곡선 끝점을 이동해도 유효한 곡선이 유지되지 않습니다.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static CurveDefinition MoveLine(LineCurveDefinition line, bool definitionStart, Vec3 target) =>
        definitionStart
            ? new LineCurveDefinition(target, line.End, line.Direction)
            : new LineCurveDefinition(line.Start, target, line.Direction);

    private static CurveDefinition MovePolyline(PolylineCurveDefinition polyline, bool definitionStart, Vec3 target)
    {
        Vec3[] points = polyline.Points.ToArray();
        int endpointIndex = definitionStart ? 0 : points.Length - 1;
        points[endpointIndex] = target;
        return new PolylineCurveDefinition(points, polyline.Direction);
    }

    private static CurveDefinition? MoveArc(ArcCurveDefinition arc, bool definitionStart, Vec3 target, double toleranceMm)
    {
        Vec3 delta = target - arc.Center;
        double normalOffset = Math.Abs(Vec3.Dot(delta, arc.Plane.Normal));
        double x = Vec3.Dot(delta, arc.Plane.AxisX);
        double y = Vec3.Dot(delta, arc.Plane.AxisY);
        double radius = Math.Sqrt(x * x + y * y);
        if (normalOffset > toleranceMm || Math.Abs(radius - arc.RadiusMm.Value) > toleranceMm)
            return null;

        double angle = Math.Atan2(y, x);
        double start = definitionStart ? angle : arc.StartAngleRad;
        double end = definitionStart ? arc.EndAngleRad : angle;
        if (Math.Abs(start - end) <= 1e-12)
            return null;

        return new ArcCurveDefinition(arc.Plane, arc.Center, arc.RadiusMm, start, end, arc.Direction);
    }
}
