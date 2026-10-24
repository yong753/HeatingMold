using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Editing;

/// <summary>안정 조각 ID를 기준으로 회전 구간의 내부 다중선 형상만 편집합니다.</summary>
public sealed class TurnSegmentCurveEditService
{
    /// <summary>revision과 공유 끝점을 유지하는 회전 곡선 편집 후보를 만듭니다.</summary>
    public OperationResult<TurnSegmentCurveEditDraft> CreateDraft(
        TurnSegmentCurveEditRequest request,
        DesignProject currentProject)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentProject);

        if (request.BaseRevision != currentProject.Revision)
            return Failure("StaleTurnSegmentEdit", "회전 구간 편집의 기준 revision이 현재 설계와 다릅니다.", request, currentProject);

        CoreHeatingPath? path = currentProject.HeatingPaths.SingleOrDefault(candidate => candidate.Id == request.PathId);
        if (path is null)
            return Failure("HeatingPathNotFound", "편집 대상 발열 경로가 현재 설계에 없습니다.", request, currentProject);

        HeatingPathSegment? segment = path.Segments.SingleOrDefault(candidate => candidate.Id == request.SegmentId);
        if (segment is null)
            return Failure("TurnSegmentNotFound", "편집 대상 회전 구간이 현재 발열 경로에 없습니다.", request, currentProject);
        if (segment.Kind != HeatingPathSegmentKind.Turn)
            return Failure("TurnSegmentTargetInvalid", "회전 구간 역할의 조각만 내부 곡선을 편집할 수 있습니다.", request, currentProject);
        if (!segment.IsActive)
            return Failure("InactiveTurnSegmentNotEditable", "비활성 회전 구간을 편집하려면 먼저 회로 topology에서 활성화해야 합니다.", request, currentProject);
        if (segment.Curve is not PolylineCurveDefinition previousCurve)
            return Failure("UnsupportedTurnSegmentCurve", "현재 편집기는 다중선 회전 곡선만 지원합니다.", request, currentProject);

        PolylineCurveDefinition candidateCurve = request.CandidateCurve;
        if (candidateCurve.Direction != previousCurve.Direction)
            return Failure("TurnSegmentDirectionChanged", "내부 형상 편집에서는 회전 구간의 진행 방향을 바꿀 수 없습니다.", request, currentProject);
        if (candidateCurve.Points.Count < 3)
            return Failure("TurnSegmentShapeMissing", "회전 구간 형상에는 내부 점이 하나 이상 필요합니다.", request, currentProject);
        if (previousCurve.Points.SequenceEqual(candidateCurve.Points))
            return Failure("TurnSegmentCurveUnchanged", "회전 구간의 내부 형상 변경이 없습니다.", request, currentProject);

        ControlPoint startPoint = path.ControlPoints.Single(point => point.Id == segment.StartControlPointId);
        ControlPoint endPoint = path.ControlPoints.Single(point => point.Id == segment.EndControlPointId);
        (Vec3 previousStart, Vec3 previousEnd) = GetPathEndpoints(previousCurve);
        if (previousStart != startPoint.Position || previousEnd != endPoint.Position)
        {
            return Failure("TurnSegmentEndpointMismatch", "현재 회전 곡선의 끝점이 공유 절점 좌표와 일치하지 않습니다.", request, currentProject);
        }

        (Vec3 candidateStart, Vec3 candidateEnd) = GetPathEndpoints(candidateCurve);
        if (candidateStart != startPoint.Position || candidateEnd != endPoint.Position)
        {
            return Failure("TurnSegmentEndpointChangeRejected", "곡선 편집은 공유 시작·끝 절점을 이동할 수 없습니다.", request, currentProject);
        }

        var replacement = new HeatingPathSegment(
            segment.Id,
            segment.Kind,
            segment.StartControlPointId,
            segment.EndControlPointId,
            segment.Sequence,
            segment.IsActive,
            candidateCurve,
            segment.LaneIndex);
        HeatingPathSegment[] candidateSegments = path.Segments
            .Select(current => current.Id == segment.Id ? replacement : current)
            .ToArray();
        var candidatePath = new CoreHeatingPath(
            path.Id,
            path.GenerationSource,
            path.ControlPoints,
            path.GeneratedSegments,
            candidateSegments);

        return OperationResult<TurnSegmentCurveEditDraft>.Success(new TurnSegmentCurveEditDraft(
            request.BaseRevision,
            path.Id,
            segment.Id,
            previousCurve,
            candidateCurve,
            candidatePath));
    }

    private static (Vec3 Start, Vec3 End) GetPathEndpoints(PolylineCurveDefinition curve) =>
        curve.Direction == CurveDirection.Forward
            ? (curve.Points[0], curve.Points[^1])
            : (curve.Points[^1], curve.Points[0]);

    private static OperationResult<TurnSegmentCurveEditDraft> Failure(
        string code,
        string evidence,
        TurnSegmentCurveEditRequest request,
        DesignProject project) => OperationResult<TurnSegmentCurveEditDraft>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            [project.Id.Value, request.PathId.Value, request.SegmentId.Value],
            project.Rules.ValidationRuleVersion,
            evidence: [evidence])]);
}

/// <summary>경로·회전 구간의 안정 ID와 교체할 다중선 정의를 지정합니다.</summary>
public sealed record TurnSegmentCurveEditRequest
{
    /// <summary>편집 대상 발열 경로의 안정 ID입니다.</summary>
    public HeatingPathId PathId { get; }

    /// <summary>편집 대상 회전 구간의 안정 ID입니다.</summary>
    public HeatingPathSegmentId SegmentId { get; }

    /// <summary>편집 요청이 기반으로 삼는 확정 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>공유 끝점을 유지할 새 내부 다중선 형상입니다.</summary>
    public PolylineCurveDefinition CandidateCurve { get; }

    /// <summary>안정 참조와 비어 있지 않은 교체 곡선을 보관합니다.</summary>
    public TurnSegmentCurveEditRequest(
        HeatingPathId pathId,
        HeatingPathSegmentId segmentId,
        DesignRevision baseRevision,
        PolylineCurveDefinition candidateCurve)
    {
        if (pathId.IsEmpty || segmentId.IsEmpty)
            throw new ArgumentException("회전 구간 편집에는 경로와 조각 안정 ID가 필요합니다.");

        PathId = pathId;
        SegmentId = segmentId;
        BaseRevision = baseRevision;
        CandidateCurve = candidateCurve ?? throw new ArgumentNullException(nameof(candidateCurve));
    }
}

/// <summary>이전 곡선과 제안 형상, 영향을 받은 경로 후보를 보관합니다.</summary>
public sealed class TurnSegmentCurveEditDraft
{
    /// <summary>후보가 기반으로 삼은 확정 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>편집한 발열 경로의 안정 ID입니다.</summary>
    public HeatingPathId PathId { get; }

    /// <summary>편집한 회전 구간의 안정 ID입니다.</summary>
    public HeatingPathSegmentId SegmentId { get; }

    /// <summary>편집 전의 원본 다중선 곡선입니다.</summary>
    public PolylineCurveDefinition PreviousCurve { get; }

    /// <summary>공유 끝점을 보존한 새 다중선 곡선입니다.</summary>
    public PolylineCurveDefinition CandidateCurve { get; }

    /// <summary>회전 구간 편집만 반영한 발열 경로 후보입니다.</summary>
    public CoreHeatingPath CandidatePath { get; }

    /// <summary>검증한 입력과 후보 경로로 편집 초안을 만듭니다.</summary>
    public TurnSegmentCurveEditDraft(
        DesignRevision baseRevision,
        HeatingPathId pathId,
        HeatingPathSegmentId segmentId,
        PolylineCurveDefinition previousCurve,
        PolylineCurveDefinition candidateCurve,
        CoreHeatingPath candidatePath)
    {
        if (pathId.IsEmpty || segmentId.IsEmpty || candidatePath?.Id != pathId)
            throw new ArgumentException("회전 구간 편집 초안에 일치하는 경로와 조각 ID가 필요합니다.");

        BaseRevision = baseRevision;
        PathId = pathId;
        SegmentId = segmentId;
        PreviousCurve = previousCurve ?? throw new ArgumentNullException(nameof(previousCurve));
        CandidateCurve = candidateCurve ?? throw new ArgumentNullException(nameof(candidateCurve));
        CandidatePath = candidatePath;
        if (!CandidatePath.Segments.Any(segment => segment.Id == segmentId &&
                ReferenceEquals(segment.Curve, candidateCurve)))
        {
            throw new ArgumentException("후보 경로에 편집한 안정 ID와 곡선이 있어야 합니다.", nameof(candidatePath));
        }
    }
}
