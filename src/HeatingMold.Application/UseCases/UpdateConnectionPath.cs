using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.Application.UseCases;

/// <summary>명시된 연결 구간을 검사해 한 연결부의 확정 경로를 교체합니다.</summary>
public sealed class UpdateConnectionPath
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly ConnectionPathPlanner _planner;

    /// <summary>세션, 후보 확정기와 연결 경로 계획기를 연결합니다.</summary>
    public UpdateConnectionPath(
        ProjectSession session,
        CandidateCoordinator coordinator,
        ConnectionPathPlanner? planner = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _planner = planner ?? new ConnectionPathPlanner();
    }

    /// <summary>경로를 계획하고 Native 장애물 검사를 포함한 후보 검증을 수행합니다.</summary>
    public CandidateOutcome Execute(
        ConnectionPathPlanningRequest request,
        IGeometryQueries geometryQueries,
        double connectionToleranceMm,
        HeatingMold.Core.Primitives.DesignRevision expectedBaseRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(geometryQueries);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure(current, "ProjectNotLoaded", "연결 경로를 바꾸려면 먼저 프로젝트를 열어야 합니다.");
        if (project.Revision != expectedBaseRevision)
            return Failure(current, "StaleConnectionEdit", "연결 경로 편집의 기준 revision이 현재 설계와 다릅니다.");
        Circuit? currentCircuit = project.Circuits.SingleOrDefault(circuit => circuit.Id == request.Circuit.Id);
        ConnectionPoint? currentConnectionPoint = project.ConnectionPoints.SingleOrDefault(point =>
            point.Id == request.ConnectionPoint.Id && point.CircuitId == request.Circuit.Id);
        HeatingPathSegment? currentEndpointSegment = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .SingleOrDefault(segment => segment.Id == request.ConnectionPoint.HeatingPathSegmentId);
        if (currentCircuit is null || currentConnectionPoint is null || currentEndpointSegment is null)
        {
            return Failure(current, "ConnectionOwnerNotFound", "연결 경로 요청이 현재 설계의 회로와 연결부를 가리키지 않습니다.", request.ConnectionPoint.Id.Value);
        }
        if (!HasSameEndpoint(currentConnectionPoint, request.ConnectionPoint) ||
            !HasSameEndpointSegment(currentEndpointSegment, request.EndpointSegment) ||
            !HasSameCircuit(currentCircuit, request.Circuit))
        {
            return Failure(
                current,
                "ConnectionEndpointChanged",
                "연결 경로만 수정할 수 있으며 회로, 연결부 위치와 안정 참조는 바꿀 수 없습니다.",
                request.ConnectionPoint.Id.Value);
        }

        OperationResult<ConnectionPath> planResult = _planner.Plan(
            request,
            geometryQueries,
            connectionToleranceMm,
            project.Rules.ValidationRuleVersion,
            cancellationToken);
        if (!planResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, planResult.Issues);

        ConnectionPath replacement = planResult.GetValueOrThrow();
        var paths = project.ConnectionPaths
            .Where(path => path.ConnectionPointId != replacement.ConnectionPointId)
            .Append(replacement)
            .ToArray();
        var candidate = DesignProjectCandidateFactory.CreateNext(project, connectionPaths: paths);
        return _coordinator.Execute(candidate, expectedBaseRevision, cancellationToken);
    }

    private static CandidateOutcome Failure(
        SessionSnapshot snapshot,
        string code,
        string evidence,
        params Guid[] entityIds) =>
        new(
            CandidateStatus.ValidationFailed,
            snapshot,
            [new ValidationIssue(code, ValidationSeverity.Error, $"Application.{code}", entityIds, "application-connection-v1", evidence: [evidence])]);

    /// <summary>경로 수정 요청이 기존 연결부의 물리 위치와 안정 참조를 보존하는지 확인합니다.</summary>
    private static bool HasSameEndpoint(ConnectionPoint current, ConnectionPoint requested) =>
        current.Id == requested.Id &&
        current.CircuitId == requested.CircuitId &&
        current.Role == requested.Role &&
        current.HeatingPathSegmentId == requested.HeatingPathSegmentId &&
        current.ControlPointId == requested.ControlPointId &&
        Equals(current.SurfaceAnchor, requested.SurfaceAnchor) &&
        current.ExternalConnectionPoint == requested.ExternalConnectionPoint;

    /// <summary>경로 수정 요청이 현재 발열 구간 정의를 그대로 참조하는지 확인합니다.</summary>
    private static bool HasSameEndpointSegment(HeatingPathSegment current, HeatingPathSegment requested) =>
        current == requested;

    /// <summary>경로 수정 요청의 회로 순서와 양 끝 연결부가 확정 설계와 같은지 확인합니다.</summary>
    private static bool HasSameCircuit(Circuit current, Circuit requested) =>
        current.Id == requested.Id &&
        current.Sequence == requested.Sequence &&
        current.StartConnectionPointId == requested.StartConnectionPointId &&
        current.EndConnectionPointId == requested.EndConnectionPointId &&
        Equals(current.Supply, requested.Supply) &&
        current.Segments.SequenceEqual(requested.Segments);
}
