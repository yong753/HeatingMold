using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;

namespace HeatingMold.Design.Circuits;

/// <summary>최종 기하 측정값과 회로 재료 역할로 길이 원장을 만듭니다.</summary>
public sealed class ConnectionLengthLedgerBuilder
{
    /// <summary>본 경로와 양 끝 연결 경로를 한 회로의 중복 없는 원장으로 변환합니다.</summary>
    public OperationResult<LengthBreakdown> Build(
        DesignProject project,
        Circuit circuit,
        GeometrySnapshot geometry)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(geometry);

        if (!project.Circuits.Any(candidate => candidate.Id == circuit.Id))
            return Failure(project, "CircuitNotInProject", "원장 대상 회로가 프로젝트에 없습니다.", circuit.Id.Value);
        if (geometry.DependencyStamp.DesignRevision != project.Revision ||
            geometry.DependencyStamp.SourceModelSha256 != project.SourceModel.SourceSha256)
        {
            return Failure(project, "StaleGeometrySnapshot", "기하 스냅샷이 현재 프로젝트 revision 또는 원본 모델과 다릅니다.", project.Id.Value);
        }

        ValidationReport referenceReport = project.ValidateReferences();
        if (!referenceReport.IsValid)
            return OperationResult<LengthBreakdown>.Failure(referenceReport.Issues);

        Dictionary<HeatingPathSegmentId, HeatingPathSegment> heatingSegments = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .ToDictionary(segment => segment.Id);
        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> heatingCurves = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value);
        Dictionary<ConnectionPathSegmentId, CurveGeometrySnapshot> connectionCurves = geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .ToDictionary(curve => curve.ConnectionPathSegmentId!.Value);
        Dictionary<ConnectionPointId, ConnectionPoint> connectionPoints = project.ConnectionPoints
            .ToDictionary(point => point.Id);
        Dictionary<ConnectionPointId, CoreConnectionPath> connectionPaths = project.ConnectionPaths
            .ToDictionary(path => path.ConnectionPointId);

        var entries = new List<LengthLedgerEntry>();
        foreach (CircuitSegmentReference reference in circuit.Segments)
        {
            if (!heatingSegments.TryGetValue(reference.SegmentId, out HeatingPathSegment? segment) ||
                !segment.IsActive ||
                !heatingCurves.TryGetValue(reference.SegmentId, out CurveGeometrySnapshot? curve))
            {
                return Failure(project, "MissingHeatingCurveMeasurement",
                    "회로 발열 구간의 최종 Native 길이를 찾을 수 없습니다.", circuit.Id.Value, reference.SegmentId.Value);
            }

            entries.Add(new LengthLedgerEntry(
                reference.SegmentId,
                null,
                LengthContributionKind.HeatingBody,
                GeometryDomain.OnMold,
                curve.LengthMm,
                new LengthMm(0)));
        }

        foreach (ConnectionPointId pointId in new[] { circuit.StartConnectionPointId, circuit.EndConnectionPointId })
        {
            if (!connectionPoints.TryGetValue(pointId, out ConnectionPoint? point) || point.CircuitId != circuit.Id)
            {
                return Failure(project, "MissingCircuitConnectionPoint",
                    "회로 끝점에 연결부 위치가 없거나 다른 회로가 소유합니다.", circuit.Id.Value, pointId.Value);
            }
            if (!connectionPaths.TryGetValue(pointId, out CoreConnectionPath? path))
            {
                return Failure(project, "MissingConnectionPath",
                    "회로 연결부까지의 확정 경로가 없습니다.", circuit.Id.Value, pointId.Value);
            }

            foreach (ConnectionPathSegment segment in path.Segments)
            {
                if (!connectionCurves.TryGetValue(segment.Id, out CurveGeometrySnapshot? curve))
                {
                    return Failure(project, "MissingConnectionCurveMeasurement",
                        "회로 연결 경로의 최종 Native 길이를 찾을 수 없습니다.", circuit.Id.Value, segment.Id.Value);
                }

                LengthContributionKind contributionKind = segment.ConductorRole switch
                {
                    ConductorRole.HeatingWireExtension => LengthContributionKind.HeatingWireExtension,
                    ConductorRole.SeparateConnectionWire => LengthContributionKind.SeparateConnectionWire,
                    _ => throw new InvalidOperationException("지원하지 않는 연결 도체 역할입니다.")
                };
                entries.Add(new LengthLedgerEntry(
                    null,
                    segment.Id,
                    contributionKind,
                    segment.GeometryDomain,
                    curve.LengthMm,
                    segment.AdditionalAllowanceMm));
            }
        }

        return OperationResult<LengthBreakdown>.Success(new LengthBreakdown(entries));
    }

    private static OperationResult<LengthBreakdown> Failure(
        DesignProject project,
        string code,
        string evidence,
        params Guid[] entityIds)
    {
        var issue = new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            entityIds,
            project.Rules.ValidationRuleVersion,
            evidence: [evidence]);
        return OperationResult<LengthBreakdown>.Failure([issue]);
    }
}
