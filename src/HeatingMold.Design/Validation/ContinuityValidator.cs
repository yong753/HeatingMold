using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Circuits;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;

namespace HeatingMold.Design.Validation;

/// <summary>경로 절점, 회로 순서와 연결 경로 곡선의 실제 접속을 검사합니다.</summary>
public sealed class ContinuityValidator
{
    private readonly CircuitPathTopologyService _topologyService;

    /// <summary>회로 topology 분석기를 연결해 연속성 검사를 준비합니다.</summary>
    public ContinuityValidator(CircuitPathTopologyService? topologyService = null)
    {
        _topologyService = topologyService ?? new CircuitPathTopologyService();
    }

    /// <summary>확정 경로와 최종 기하 스냅샷의 절점·끝점·접점을 비교합니다.</summary>
    public ValidationReport Validate(
        DesignProject project,
        GeometrySnapshot geometry,
        double connectionToleranceMm,
        string? ruleVersion = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(connectionToleranceMm) || connectionToleranceMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(connectionToleranceMm));

        string rule = ruleVersion ?? project.Rules.ValidationRuleVersion;
        var issues = new List<ValidationIssue>();
        var pathsBySegment = project.HeatingPaths
            .SelectMany(path => path.Segments.Select(segment => (Path: path, Segment: segment)))
            .ToDictionary(item => item.Segment.Id);
        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> heatingCurves = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value);
        Dictionary<ConnectionPathSegmentId, CurveGeometrySnapshot> connectionCurves = geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .ToDictionary(curve => curve.ConnectionPathSegmentId!.Value);
        Dictionary<ControlPointId, ControlPoint> controlPoints = project.HeatingPaths
            .SelectMany(path => path.ControlPoints)
            .DistinctBy(point => point.Id)
            .ToDictionary(point => point.Id);

        foreach ((CoreHeatingPath path, HeatingPathSegment segment) in pathsBySegment.Values)
        {
            if (!segment.IsActive)
                continue;
            if (!heatingCurves.TryGetValue(segment.Id, out CurveGeometrySnapshot? curve))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingHeatingCurveMeasurement",
                    "활성 발열 구간의 최종 Native 곡선 측정값이 없습니다.",
                    rule,
                    [path.Id.Value, segment.Id.Value]));
                continue;
            }

            if (!curve.Approximation.IsComplete)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "IncompleteCurveApproximation",
                    "곡선 표본 근사가 완료되지 않아 연속성을 확정할 수 없습니다.",
                    rule,
                    [path.Id.Value, segment.Id.Value]));
            }

            if (!controlPoints.TryGetValue(segment.StartControlPointId, out ControlPoint? startPoint) ||
                !controlPoints.TryGetValue(segment.EndControlPointId, out ControlPoint? endPoint))
            {
                continue;
            }

            Vec3 expectedStart = segment.Curve.Direction == CurveDirection.Forward
                ? startPoint.Position
                : endPoint.Position;
            Vec3 expectedEnd = segment.Curve.Direction == CurveDirection.Forward
                ? endPoint.Position
                : startPoint.Position;
            AddEndpointIssue(curve.Start, expectedStart, "HeatingCurveStartMismatch", path.Id.Value, segment.Id.Value);
            AddEndpointIssue(curve.End, expectedEnd, "HeatingCurveEndMismatch", path.Id.Value, segment.Id.Value);
        }

        Dictionary<ConnectionPointId, ConnectionPoint> pointsById = project.ConnectionPoints
            .ToDictionary(point => point.Id);
        foreach (Circuit circuit in project.Circuits)
        {
            HeatingPathSegment[] circuitSegments = circuit.Segments
                .Select(reference => pathsBySegment.TryGetValue(reference.SegmentId, out var item) ? item.Segment : null)
                .Where(segment => segment is not null)
                .Cast<HeatingPathSegment>()
                .ToArray();
            if (circuitSegments.Length != circuit.Segments.Count)
                continue;

            CircuitPathTopologyAnalysis topology = _topologyService.Analyze(circuitSegments, rule);
            issues.AddRange(topology.Issues);
            if (!topology.IsValid || topology.Components.Count != 1)
            {
                if (topology.IsValid)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "DisconnectedCircuitPath",
                        "회로가 둘 이상의 분리된 활성 경로 성분을 참조합니다.",
                        rule,
                        [circuit.Id.Value]));
                }
                continue;
            }

            CircuitSegmentReference[] orderedReferences = circuit.Segments.OrderBy(reference => reference.Sequence).ToArray();
            ControlPointId? previousEnd = null;
            foreach (CircuitSegmentReference reference in orderedReferences)
            {
                if (!pathsBySegment.TryGetValue(reference.SegmentId, out var item) || !item.Segment.IsActive)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "CircuitReferencesInactiveSegment",
                        "회로가 없거나 비활성인 발열 경로 구간을 참조합니다.",
                        rule,
                        [circuit.Id.Value, reference.SegmentId.Value]));
                    continue;
                }

                HeatingPathSegment segment = item.Segment;
                ControlPointId startId = reference.IsReversed ? segment.EndControlPointId : segment.StartControlPointId;
                ControlPointId endId = reference.IsReversed ? segment.StartControlPointId : segment.EndControlPointId;
                if (previousEnd.HasValue && previousEnd.Value != startId)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "DisconnectedCircuitSequence",
                        "회로의 순서가 바뀌는 지점에서 이웃 경로 구간의 공유 절점 ID가 다릅니다.",
                        rule,
                        [circuit.Id.Value, reference.SegmentId.Value]));
                }

                if (previousEnd.HasValue && heatingCurves.TryGetValue(reference.SegmentId, out CurveGeometrySnapshot? curve))
                {
                    Vec3 previousPosition = controlPoints[previousEnd.Value].Position;
                    Vec3 nextPosition = reference.IsReversed ? curve.End : curve.Start;
                    double joinGap = (previousPosition - nextPosition).Length;
                    if (joinGap > connectionToleranceMm)
                    {
                        issues.Add(ValidationIssueFactory.Error(
                            "CircuitJoinGeometryMismatch",
                            "회로 이웃 구간의 실제 Native 끝점이 공유 절점 허용오차를 벗어납니다.",
                            rule,
                            [circuit.Id.Value, reference.SegmentId.Value],
                            nextPosition,
                            measuredValue: joinGap,
                            requiredValue: connectionToleranceMm,
                            unit: "mm"));
                    }
                }

                previousEnd = endId;
            }

            if (orderedReferences.Length > 0)
            {
                HeatingPathSegment firstSegment = pathsBySegment[orderedReferences[0].SegmentId].Segment;
                HeatingPathSegment lastSegment = pathsBySegment[orderedReferences[^1].SegmentId].Segment;
                ControlPointId expectedStart = orderedReferences[0].IsReversed
                    ? firstSegment.EndControlPointId
                    : firstSegment.StartControlPointId;
                ControlPointId expectedEnd = orderedReferences[^1].IsReversed
                    ? lastSegment.StartControlPointId
                    : lastSegment.EndControlPointId;
                CheckConnectionPoint(circuit.StartConnectionPointId, expectedStart, ConnectionPointRole.Start);
                CheckConnectionPoint(circuit.EndConnectionPointId, expectedEnd, ConnectionPointRole.End);
            }
        }

        foreach (CoreConnectionPath path in project.ConnectionPaths)
        {
            if (!pointsById.TryGetValue(path.ConnectionPointId, out ConnectionPoint? connectionPoint) ||
                !controlPoints.TryGetValue(connectionPoint.ControlPointId, out ControlPoint? heatingPoint))
            {
                continue;
            }

            CurveGeometrySnapshot[] curves = path.Segments
                .Select(segment => connectionCurves.TryGetValue(segment.Id, out CurveGeometrySnapshot? curve) ? curve : null)
                .Where(curve => curve is not null)
                .Cast<CurveGeometrySnapshot>()
                .ToArray();
            if (curves.Length != path.Segments.Count)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingConnectionCurveMeasurement",
                    "연결 경로의 모든 구간에 최종 Native 곡선 측정값이 필요합니다.",
                    rule,
                    [path.Id.Value, connectionPoint.Id.Value]));
                continue;
            }

            for (int index = 1; index < curves.Length; index++)
            {
                double gap = (curves[index - 1].End - curves[index].Start).Length;
                if (gap > connectionToleranceMm)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "ConnectionPathDiscontinuity",
                        "이웃 연결 경로 구간의 실제 Native 끝점이 접속 허용오차를 벗어납니다.",
                        rule,
                        [path.Id.Value, connectionPoint.Id.Value],
                        curves[index].Start,
                        measuredValue: gap,
                        requiredValue: connectionToleranceMm,
                        unit: "mm"));
                }
            }

            double startGap = (curves[0].Start - heatingPoint.Position).Length;
            if (startGap > connectionToleranceMm)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "ConnectionPathHeatingEndpointMismatch",
                    "연결 경로가 회로 발열 구간의 끝점에서 시작하지 않습니다.",
                    rule,
                    [path.Id.Value, connectionPoint.Id.Value],
                    curves[0].Start,
                    measuredValue: startGap,
                    requiredValue: connectionToleranceMm,
                    unit: "mm"));
            }

            double endGap = (curves[^1].End - connectionPoint.ExternalConnectionPoint).Length;
            if (endGap > connectionToleranceMm)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "ConnectionPathExternalEndpointMismatch",
                    "연결 경로가 지정된 외부 연결 좌표에서 끝나지 않습니다.",
                    rule,
                    [path.Id.Value, connectionPoint.Id.Value],
                    curves[^1].End,
                    measuredValue: endGap,
                    requiredValue: connectionToleranceMm,
                    unit: "mm"));
            }
        }

        return new ValidationReport(issues);

        void AddEndpointIssue(Vec3 actual, Vec3 expected, string code, Guid pathId, Guid segmentId)
        {
            double gap = (actual - expected).Length;
            if (gap > connectionToleranceMm)
            {
                issues.Add(ValidationIssueFactory.Error(
                    code,
                    "Native 곡선 끝점이 안정 제어점 좌표와 접속 허용오차 안에서 일치하지 않습니다.",
                    rule,
                    [pathId, segmentId],
                    actual,
                    measuredValue: gap,
                    requiredValue: connectionToleranceMm,
                    unit: "mm"));
            }
        }

        void CheckConnectionPoint(ConnectionPointId connectionPointId, ControlPointId expectedControlPointId, ConnectionPointRole expectedRole)
        {
            if (!pointsById.TryGetValue(connectionPointId, out ConnectionPoint? connectionPoint))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingCircuitConnectionPoint",
                    "회로 순서와 연결부 끝점을 대조할 연결부가 없습니다.",
                    rule,
                    [connectionPointId.Value]));
                return;
            }

            if (connectionPoint.Role != expectedRole || connectionPoint.ControlPointId != expectedControlPointId)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "CircuitConnectionEndpointMismatch",
                    "회로의 시작·끝 역할과 실제 발열 경로 절점 참조가 일치하지 않습니다.",
                    rule,
                    [connectionPoint.Id.Value, expectedControlPointId.Value]));
            }
        }
    }
}
