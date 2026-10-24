using System.Collections.ObjectModel;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.Application.UseCases;

/// <summary>활성 경로의 연결 성분을 회로로 나누고 회로 ID와 공급 설정을 유지합니다.</summary>
public sealed class ChangeCircuitTopology
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly CircuitPathTopologyService _topologyService;
    private readonly CircuitIdentityMapper _identityMapper;
    private readonly ConnectionPathPlanner _connectionPathPlanner = new();

    /// <summary>세션, 후보 확정기와 회로 연결성 분석 서비스를 연결합니다.</summary>
    public ChangeCircuitTopology(
        ProjectSession session,
        CandidateCoordinator coordinator,
        CircuitPathTopologyService? topologyService = null,
        CircuitIdentityMapper? identityMapper = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _topologyService = topologyService ?? new CircuitPathTopologyService();
        _identityMapper = identityMapper ?? new CircuitIdentityMapper();
    }

    /// <summary>요청한 회전 상태를 반영해 경로 성분과 미리보기 회로 배정을 만듭니다.</summary>
    public OperationResult<CircuitTopologyPlan> Analyze(
        DesignRevision expectedBaseRevision,
        CircuitId? requestedMergeSurvivor = null,
        IReadOnlyDictionary<HeatingPathSegmentId, bool>? turnSegmentStates = null)
    {
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure<CircuitTopologyPlan>("ProjectNotLoaded", "회로 구성을 바꾸려면 먼저 프로젝트를 열어야 합니다.");
        if (project.Revision != expectedBaseRevision)
            return Failure<CircuitTopologyPlan>("StaleCircuitTopology", "회로 분석의 기준 revision이 현재 설계와 다릅니다.");

        OperationResult<HeatingPath[]> pathStateResult = ApplyTurnSegmentStates(
            project.HeatingPaths,
            turnSegmentStates);
        if (!pathStateResult.IsSuccess)
            return OperationResult<CircuitTopologyPlan>.Failure(pathStateResult.Issues);

        HeatingPath[] topologyPaths = pathStateResult.GetValueOrThrow();
        CircuitPathTopologyAnalysis topology = _topologyService.Analyze(
            topologyPaths.SelectMany(path => path.Segments),
            project.Rules.ValidationRuleVersion);
        OperationResult<CircuitIdentityMapping> mapping = _identityMapper.Map(
            topology,
            project.Circuits,
            project.ConnectionPoints,
            requestedMergeSurvivor,
            project.Rules.ValidationRuleVersion);
        if (!mapping.IsSuccess)
            return OperationResult<CircuitTopologyPlan>.Failure(mapping.Issues);

        return OperationResult<CircuitTopologyPlan>.Success(
            new CircuitTopologyPlan(
                project.Id,
                project.Revision,
                topology,
                mapping.GetValueOrThrow(),
                turnSegmentStates));
    }

    /// <summary>미리보기 배정, 연결부와 필요한 연결 경로를 함께 후보 회로로 확정합니다.</summary>
    public CandidateOutcome Execute(
        CircuitTopologyPlan plan,
        IReadOnlyDictionary<CircuitId, CircuitEndpointPair>? newEndpoints = null,
        IReadOnlyDictionary<CircuitId, Supply>? newSupplies = null,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPath>? newConnectionPaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<ConnectionPointId, CircuitTopologyConnectionPathInput>? newConnectionPathInputs = null,
        IGeometryQueries? geometryQueries = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure(current, "ProjectNotLoaded", "회로 구성을 바꾸려면 먼저 프로젝트를 열어야 합니다.");
        if (project.Id != plan.ProjectId || project.Revision != plan.BaseRevision)
            return Failure(current, "StaleCircuitTopology", "회로 분석 이후 설계가 바뀌어 후보를 적용할 수 없습니다.");

        OperationResult<HeatingPath[]> pathStateResult = ApplyTurnSegmentStates(
            project.HeatingPaths,
            plan.TurnSegmentStates);
        if (!pathStateResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, pathStateResult.Issues);

        HeatingPath[] candidateHeatingPaths = pathStateResult.GetValueOrThrow();
        CircuitPathTopologyAnalysis currentTopology = _topologyService.Analyze(
            candidateHeatingPaths.SelectMany(path => path.Segments),
            project.Rules.ValidationRuleVersion);
        if (!MatchesTopology(plan.Topology, currentTopology))
            return Failure(current, "CircuitTopologyPlanMismatch", "회로 미리보기의 경로 연결성이 현재 설계와 다릅니다.");
        if (!MatchesMapping(plan.Topology, plan.Mapping))
            return Failure(current, "CircuitTopologyMappingMismatch", "회로 미리보기의 ID 배정이 분석된 경로 성분과 일치하지 않습니다.");

        Dictionary<ConnectionPointId, ConnectionPoint> previousPoints = project.ConnectionPoints
            .ToDictionary(point => point.Id);
        Dictionary<CircuitId, Circuit> previousCircuits = project.Circuits
            .ToDictionary(circuit => circuit.Id);
        var circuits = new List<Circuit>(plan.Mapping.Assignments.Count);
        var connectionPoints = new List<ConnectionPoint>(plan.Mapping.Assignments.Count * 2);
        var usedPointIds = new HashSet<ConnectionPointId>();

        for (int index = 0; index < plan.Mapping.Assignments.Count; index++)
        {
            CircuitIdentityAssignment assignment = plan.Mapping.Assignments[index];
            CircuitPathComponent component = assignment.Component;
            CircuitEndpointPair? requestedEndpoints = newEndpoints is not null &&
                newEndpoints.TryGetValue(assignment.CircuitId, out CircuitEndpointPair? endpointPair)
                    ? endpointPair
                    : null;

            if (HasIdentityMismatch(assignment.StartConnectionPointId, requestedEndpoints?.Start) ||
                HasIdentityMismatch(assignment.EndConnectionPointId, requestedEndpoints?.End))
            {
                return Failure(
                    current,
                    "CircuitEndpointIdentityMismatch",
                    "기존 회로 identity를 유지하는 끝점의 ConnectionPointId는 변경할 수 없습니다.",
                    assignment.CircuitId.Value);
            }

            if (!TryCreateEndpoint(
                    assignment.StartConnectionPointId,
                    requestedEndpoints?.Start,
                    assignment.CircuitId,
                    ConnectionPointRole.Start,
                    component.Segments[0],
                    component.StartControlPointId,
                    previousPoints,
                    out ConnectionPoint startPoint) ||
                !TryCreateEndpoint(
                    assignment.EndConnectionPointId,
                    requestedEndpoints?.End,
                    assignment.CircuitId,
                    ConnectionPointRole.End,
                    component.Segments[^1],
                    component.EndControlPointId,
                    previousPoints,
                    out ConnectionPoint endPoint))
            {
                return Failure(
                    current,
                    "CircuitEndpointResolutionRequired",
                    "새 회로 끝점에는 안정 ID, 표면 앵커와 외부 연결 위치를 지정해야 합니다.",
                    assignment.CircuitId.Value,
                    component.StartControlPointId.Value,
                    component.EndControlPointId.Value);
            }

            if (startPoint.Id == endPoint.Id || !usedPointIds.Add(startPoint.Id) || !usedPointIds.Add(endPoint.Id))
            {
                return Failure(current, "DuplicateCircuitEndpointId", "회로 연결부 ID는 각 끝점에서 고유해야 합니다.", assignment.CircuitId.Value);
            }

            Supply? supply = assignment.PreservedSupply;
            if (supply is null && assignment.ParentCircuitId is { } parentId && previousCircuits.TryGetValue(parentId, out Circuit? parent))
                supply = parent.Supply;
            if (supply is null && newSupplies is not null)
                newSupplies.TryGetValue(assignment.CircuitId, out supply);
            if (supply is null)
            {
                return Failure(current, "CircuitSupplyResolutionRequired", "새 회로에는 사용할 공급 전압과 한도를 지정해야 합니다.", assignment.CircuitId.Value);
            }

            CircuitSegmentReference[] references = component.Segments.ToArray();
            circuits.Add(new Circuit(
                assignment.CircuitId,
                index,
                references,
                startPoint.Id,
                endPoint.Id,
                supply));
            connectionPoints.Add(startPoint);
            connectionPoints.Add(endPoint);
        }

        OperationResult<Dictionary<ConnectionPointId, ConnectionPath>> plannedConnectionPathResult = PlanConnectionPaths(
            project,
            current,
            candidateHeatingPaths,
            circuits,
            connectionPoints,
            newConnectionPaths,
            newConnectionPathInputs,
            geometryQueries,
            cancellationToken);
        if (!plannedConnectionPathResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, plannedConnectionPathResult.Issues);

        OperationResult<ConnectionPath[]> connectionPathResult = ResolveConnectionPaths(
            connectionPoints,
            previousPoints,
            project.ConnectionPaths.ToDictionary(path => path.ConnectionPointId),
            plannedConnectionPathResult.GetValueOrThrow());
        if (!connectionPathResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, connectionPathResult.Issues);

        var candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            heatingPaths: candidateHeatingPaths,
            circuits: circuits,
            connectionPoints: connectionPoints,
            connectionPaths: connectionPathResult.GetValueOrThrow());
        return _coordinator.Execute(candidate, plan.BaseRevision, cancellationToken);
    }

    /// <summary>새 끝점의 경로를 계획하고 기존 연결 경로와 합쳐 결과 목록을 만듭니다.</summary>
    private OperationResult<Dictionary<ConnectionPointId, ConnectionPath>> PlanConnectionPaths(
        DesignProject project,
        SessionSnapshot snapshot,
        IReadOnlyList<HeatingPath> candidateHeatingPaths,
        IReadOnlyList<Circuit> candidateCircuits,
        IReadOnlyList<ConnectionPoint> candidatePoints,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPath>? requestedPaths,
        IReadOnlyDictionary<ConnectionPointId, CircuitTopologyConnectionPathInput>? pathInputs,
        IGeometryQueries? geometryQueries,
        CancellationToken cancellationToken)
    {
        var resolvedPaths = requestedPaths?.ToDictionary(pair => pair.Key, pair => pair.Value)
            ?? new Dictionary<ConnectionPointId, ConnectionPath>();
        if (pathInputs is null || pathInputs.Count == 0)
            return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Success(resolvedPaths);

        if (geometryQueries is null || snapshot.Geometry is not { } geometry)
        {
            return Failure<Dictionary<ConnectionPointId, ConnectionPath>>(
                "CircuitConnectionGeometryRequired",
                "새 연결 경로를 검증할 현재 기하 스냅샷과 Native 질의가 필요합니다.");
        }

        HashSet<ConnectionPointId> candidatePointIds = candidatePoints
            .Select(point => point.Id)
            .ToHashSet();
        ConnectionPointId[] invalidInputIds = pathInputs.Keys
            .Where(pointId => !candidatePointIds.Contains(pointId) || resolvedPaths.ContainsKey(pointId))
            .ToArray();
        if (invalidInputIds.Length > 0)
        {
            return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(
            [Issue(
                "CircuitConnectionPathInputMismatch",
                "새 연결 경로 입력은 후보의 새 연결부 하나에만 지정해야 합니다.",
                invalidInputIds.Select(pointId => pointId.Value))]);
        }

        Dictionary<CircuitId, Circuit> circuitsById = candidateCircuits.ToDictionary(circuit => circuit.Id);
        Dictionary<HeatingPathSegmentId, HeatingPathSegment> segmentsById = candidateHeatingPaths
            .SelectMany(path => path.Segments)
            .ToDictionary(segment => segment.Id);
        Dictionary<HeatingPathSegmentId, HeatingPath> pathsBySegmentId = candidateHeatingPaths
            .SelectMany(path => path.Segments.Select(segment => (segment.Id, Path: path)))
            .ToDictionary(item => item.Id, item => item.Path);
        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> curvesBySegmentId = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId.GetValueOrDefault());
        Dictionary<ConnectionPointId, ConnectionPoint> pointsById = candidatePoints
            .ToDictionary(point => point.Id);

        foreach (KeyValuePair<ConnectionPointId, CircuitTopologyConnectionPathInput> entry in pathInputs)
        {
            if (cancellationToken.IsCancellationRequested)
                return Failure<Dictionary<ConnectionPointId, ConnectionPath>>("OperationCancelled", "연결 경로 계획이 취소되었습니다.");

            ConnectionPoint point = pointsById[entry.Key];
            CircuitTopologyConnectionPathInput input = entry.Value;
            if (input is null)
            {
                return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(
                [Issue(
                    "CircuitConnectionPathInputMissing",
                    "새 연결부의 경로 계획 입력이 비어 있습니다.",
                    [point.Id.Value])]);
            }

            if (input.ExpectedModelRevision != geometry.ModelRevision ||
                input.CoordinateSystem.Revision != geometry.ModelRevision)
            {
                return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(
                [Issue(
                    "StaleCircuitConnectionGeometry",
                    "작업 좌표계와 측정 기하 스냅샷의 revision이 다릅니다.",
                    [point.Id.Value])]);
            }

            if (!circuitsById.TryGetValue(point.CircuitId, out Circuit? circuit) ||
                !segmentsById.TryGetValue(point.HeatingPathSegmentId, out HeatingPathSegment? segment) ||
                !pathsBySegmentId.TryGetValue(point.HeatingPathSegmentId, out HeatingPath? endpointPath) ||
                !curvesBySegmentId.TryGetValue(point.HeatingPathSegmentId, out CurveGeometrySnapshot? measuredCurve))
            {
                return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(
                [Issue(
                    "CircuitConnectionEndpointGeometryMissing",
                    "새 연결부에 대응하는 회로, 활성 발열 구간 또는 Native 측정 곡선을 찾지 못했습니다.",
                    [point.Id.Value, point.HeatingPathSegmentId.Value])]);
            }

            HeatingRegion? endpointRegion = project.HeatingRegions.SingleOrDefault(region =>
                region.Id == endpointPath.GenerationSource.RegionId);
            if (endpointRegion is null || !endpointRegion.FaceReferences.Contains(point.SurfaceAnchor.FaceReferenceId))
            {
                return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(
                [Issue(
                    "CircuitConnectionAnchorOutsideRegion",
                    "새 연결부의 표면 앵커는 발열 경로가 생성된 영역의 Face를 가리켜야 합니다.",
                    [point.Id.Value, point.SurfaceAnchor.FaceReferenceId.Value])]);
            }

            bool curveRunsForward = segment.Curve.Direction == CurveDirection.Forward;
            Vec3 heatingWireEndpoint;
            if (point.ControlPointId == segment.StartControlPointId)
                heatingWireEndpoint = curveRunsForward ? measuredCurve.Start : measuredCurve.End;
            else if (point.ControlPointId == segment.EndControlPointId)
                heatingWireEndpoint = curveRunsForward ? measuredCurve.End : measuredCurve.Start;
            else
            {
                return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(
                [Issue(
                    "CircuitConnectionEndpointMismatch",
                    "연결부 제어점이 발열 구간의 시작점이나 끝점과 일치하지 않습니다.",
                    [point.Id.Value, point.HeatingPathSegmentId.Value])]);
            }

            var request = new ConnectionPathPlanningRequest(
                circuit,
                point,
                segment,
                heatingWireEndpoint,
                input.Legs,
                input.CoordinateSystem,
                input.ExpectedModelRevision);
            OperationResult<ConnectionPath> planned = _connectionPathPlanner.Plan(
                request,
                geometryQueries,
                project.Rules.GeometryTolerances.ConnectionToleranceMm,
                project.Rules.ValidationRuleVersion,
                cancellationToken);
            if (!planned.IsSuccess)
                return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Failure(planned.Issues);

            resolvedPaths.Add(point.Id, planned.GetValueOrThrow());
        }

        return OperationResult<Dictionary<ConnectionPointId, ConnectionPath>>.Success(resolvedPaths);
    }

    private static OperationResult<ConnectionPath[]> ResolveConnectionPaths(
        IReadOnlyList<ConnectionPoint> resultingPoints,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPoint> previousPoints,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPath> previousPaths,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPath>? requestedPaths)
    {
        HashSet<ConnectionPointId> resultingPointIds = resultingPoints
            .Select(point => point.Id)
            .ToHashSet();
        ConnectionPointId[] unexpectedPathIds = requestedPaths?.Keys
            .Where(pointId => !resultingPointIds.Contains(pointId))
            .ToArray() ?? [];
        if (unexpectedPathIds.Length > 0)
        {
            return OperationResult<ConnectionPath[]>.Failure(
            [
                Issue(
                    "UnexpectedCircuitConnectionPath",
                    "새 연결 경로가 후보 회로의 연결부를 가리키지 않습니다.",
                    unexpectedPathIds.Select(pointId => pointId.Value))
            ]);
        }

        var resolvedPaths = new List<ConnectionPath>(resultingPoints.Count);
        foreach (ConnectionPoint point in resultingPoints)
        {
            if (requestedPaths is not null && requestedPaths.TryGetValue(point.Id, out ConnectionPath? requestedPath))
            {
                if (requestedPath.ConnectionPointId != point.Id)
                {
                    return OperationResult<ConnectionPath[]>.Failure(
                    [Issue(
                        "CircuitConnectionPathOwnerMismatch",
                        "새 연결 경로의 소유 ConnectionPointId가 입력 ID와 다릅니다.",
                        [point.Id.Value])]);
                }

                resolvedPaths.Add(requestedPath);
                continue;
            }

            if (previousPoints.TryGetValue(point.Id, out ConnectionPoint? previousPoint) &&
                previousPaths.TryGetValue(point.Id, out ConnectionPath? previousPath) &&
                HasSamePhysicalEndpoint(previousPoint, point))
            {
                resolvedPaths.Add(previousPath);
                continue;
            }

            return OperationResult<ConnectionPath[]>.Failure(
            [Issue(
                "CircuitConnectionPathRequired",
                "새 연결부 또는 이동한 연결부에는 검증된 ConnectionPath가 필요합니다.",
                [point.Id.Value])]);
        }

        return OperationResult<ConnectionPath[]>.Success(resolvedPaths.ToArray());
    }

    private static bool HasSamePhysicalEndpoint(ConnectionPoint previous, ConnectionPoint candidate) =>
        previous.Id == candidate.Id &&
        previous.HeatingPathSegmentId == candidate.HeatingPathSegmentId &&
        previous.ControlPointId == candidate.ControlPointId &&
        previous.SurfaceAnchor.Equals(candidate.SurfaceAnchor) &&
        previous.ExternalConnectionPoint == candidate.ExternalConnectionPoint;

    private static bool HasIdentityMismatch(
        ConnectionPointId? preservedId,
        CircuitEndpointDefinition? requested) =>
        preservedId.HasValue && requested is not null && requested.Id != preservedId.Value;

    /// <summary>요청한 회전 구간 상태를 후보 경로 복사본에 반영합니다.</summary>
    private static OperationResult<HeatingPath[]> ApplyTurnSegmentStates(
        IReadOnlyList<HeatingPath> paths,
        IReadOnlyDictionary<HeatingPathSegmentId, bool>? requestedStates)
    {
        if (requestedStates is null || requestedStates.Count == 0)
            return OperationResult<HeatingPath[]>.Success(paths.ToArray());

        HeatingPathSegment[] allSegments = paths.SelectMany(path => path.Segments).ToArray();
        if (allSegments.Select(segment => segment.Id).Distinct().Count() != allSegments.Length)
        {
            return OperationResult<HeatingPath[]>.Failure(
            [Issue(
                "DuplicateHeatingPathSegmentId",
                "경로 조각 ID가 중복되어 회전 구간 상태를 적용할 수 없습니다.")]);
        }

        Dictionary<HeatingPathSegmentId, HeatingPathSegment> segmentsById = allSegments
            .ToDictionary(segment => segment.Id);
        HeatingPathSegmentId[] invalidSegmentIds = requestedStates.Keys
            .Where(segmentId => !segmentsById.TryGetValue(segmentId, out HeatingPathSegment? segment) ||
                                segment.Kind != HeatingPathSegmentKind.Turn)
            .ToArray();
        if (invalidSegmentIds.Length > 0)
        {
            return OperationResult<HeatingPath[]>.Failure(
            [Issue(
                "TurnSegmentStateTargetInvalid",
                "회전 구간 상태는 현재 경로에 있는 Turn 조각에만 지정할 수 있습니다.",
                invalidSegmentIds.Select(segmentId => segmentId.Value))]);
        }

        HeatingPath[] updatedPaths = paths.Select(path =>
        {
            HeatingPathSegment[] updatedSegments = path.Segments
                .Select(segment => ApplyTurnSegmentState(segment, requestedStates))
                .ToArray();
            bool hasChanges = updatedSegments.Where((segment, index) =>
                !ReferenceEquals(segment, path.Segments[index])).Any();

            return hasChanges
                ? new HeatingPath(
                    path.Id,
                    path.GenerationSource,
                    path.ControlPoints,
                    path.GeneratedSegments,
                    updatedSegments)
                : path;
        }).ToArray();

        return OperationResult<HeatingPath[]>.Success(updatedPaths);
    }

    /// <summary>상태가 바뀐 회전 구간만 새 값으로 복사합니다.</summary>
    private static HeatingPathSegment ApplyTurnSegmentState(
        HeatingPathSegment segment,
        IReadOnlyDictionary<HeatingPathSegmentId, bool> requestedStates)
    {
        if (!requestedStates.TryGetValue(segment.Id, out bool isActive) || segment.IsActive == isActive)
            return segment;

        return new HeatingPathSegment(
            segment.Id,
            segment.Kind,
            segment.StartControlPointId,
            segment.EndControlPointId,
            segment.Sequence,
            isActive,
            segment.Curve,
            segment.LaneIndex);
    }

    private static bool TryCreateEndpoint(
        ConnectionPointId? preservedId,
        CircuitEndpointDefinition? requested,
        CircuitId circuitId,
        ConnectionPointRole role,
        CircuitSegmentReference endpointReference,
        ControlPointId controlPointId,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPoint> previousPoints,
        out ConnectionPoint point)
    {
        if (preservedId is { } id && previousPoints.TryGetValue(id, out ConnectionPoint? previous))
        {
            if (requested is not null && requested.Id != id)
            {
                point = null!;
                return false;
            }

            point = new ConnectionPoint(
                previous.Id,
                circuitId,
                role,
                endpointReference.SegmentId,
                controlPointId,
                requested?.SurfaceAnchor ?? previous.SurfaceAnchor,
                requested?.ExternalConnectionPoint ?? previous.ExternalConnectionPoint);
            return true;
        }

        if (requested is null)
        {
            point = null!;
            return false;
        }

        point = new ConnectionPoint(
            requested.Id,
            circuitId,
            role,
            endpointReference.SegmentId,
            controlPointId,
            requested.SurfaceAnchor,
            requested.ExternalConnectionPoint);
        return true;
    }

    private static bool MatchesTopology(
        CircuitPathTopologyAnalysis expected,
        CircuitPathTopologyAnalysis actual)
    {
        if (!actual.IsValid || expected.Components.Count != actual.Components.Count)
            return false;

        Dictionary<HeatingPathSegmentId, CircuitPathComponent> actualBySegment = actual.Components
            .ToDictionary(component => component.Segments[0].SegmentId);
        foreach (CircuitPathComponent expectedComponent in expected.Components)
        {
            if (!actualBySegment.TryGetValue(expectedComponent.Segments[0].SegmentId, out CircuitPathComponent? actualComponent) ||
                expectedComponent.StartControlPointId != actualComponent.StartControlPointId ||
                expectedComponent.EndControlPointId != actualComponent.EndControlPointId ||
                !expectedComponent.Segments.SequenceEqual(actualComponent.Segments))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesMapping(
        CircuitPathTopologyAnalysis topology,
        CircuitIdentityMapping mapping)
    {
        if (!topology.IsValid || mapping.Assignments.Count != topology.Components.Count)
            return false;

        var unmatchedComponents = topology.Components.ToList();
        foreach (CircuitIdentityAssignment assignment in mapping.Assignments)
        {
            int matchingIndex = unmatchedComponents.FindIndex(component =>
                ComponentsMatchIgnoringDirection(component, assignment.Component));
            if (matchingIndex < 0)
                return false;

            unmatchedComponents.RemoveAt(matchingIndex);
        }

        return unmatchedComponents.Count == 0;
    }

    private static bool ComponentsMatchIgnoringDirection(
        CircuitPathComponent expected,
        CircuitPathComponent actual)
    {
        if (expected.StartControlPointId == actual.StartControlPointId &&
            expected.EndControlPointId == actual.EndControlPointId)
        {
            return expected.Segments.SequenceEqual(actual.Segments);
        }

        if (expected.StartControlPointId != actual.EndControlPointId ||
            expected.EndControlPointId != actual.StartControlPointId ||
            expected.Segments.Count != actual.Segments.Count)
        {
            return false;
        }

        for (int index = 0; index < actual.Segments.Count; index++)
        {
            CircuitSegmentReference expectedReference = expected.Segments[expected.Segments.Count - index - 1];
            CircuitSegmentReference actualReference = actual.Segments[index];
            if (actualReference.SegmentId != expectedReference.SegmentId ||
                actualReference.Sequence != index ||
                actualReference.IsReversed == expectedReference.IsReversed)
            {
                return false;
            }
        }

        return true;
    }

    private static OperationResult<T> Failure<T>(string code, string evidence) =>
        OperationResult<T>.Failure([Issue(code, evidence)]);

    private static CandidateOutcome Failure(
        SessionSnapshot snapshot,
        string code,
        string evidence,
        params Guid[] entityIds) =>
        new(CandidateStatus.ValidationFailed, snapshot, [Issue(code, evidence, entityIds)]);

    private static ValidationIssue Issue(string code, string evidence, IEnumerable<Guid>? entityIds = null) =>
        new(code, ValidationSeverity.Error, $"Application.{code}", entityIds ?? [], "application-circuit-v1", evidence: [evidence]);
}

/// <summary>설계 revision에 고정된 연결성 분석과 회로 ID 배정 결과입니다.</summary>
public sealed class CircuitTopologyPlan
{
    /// <summary>분석한 프로젝트의 ID입니다.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>계획을 만든 기준 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>활성 발열 경로의 연결 성분 분석입니다.</summary>
    public CircuitPathTopologyAnalysis Topology { get; }

    /// <summary>기존 회로 ID와 신규 회로 ID의 배정입니다.</summary>
    public CircuitIdentityMapping Mapping { get; }

    /// <summary>회로 분석에서 변경을 요청한 회전 구간 활성 상태입니다.</summary>
    public IReadOnlyDictionary<HeatingPathSegmentId, bool> TurnSegmentStates { get; }

    /// <summary>프로젝트와 분석 결과를 revision에 고정합니다.</summary>
    public CircuitTopologyPlan(
        ProjectId projectId,
        DesignRevision baseRevision,
        CircuitPathTopologyAnalysis topology,
        CircuitIdentityMapping mapping,
        IReadOnlyDictionary<HeatingPathSegmentId, bool>? turnSegmentStates = null)
    {
        if (projectId.IsEmpty)
            throw new ArgumentException("프로젝트 ID가 필요합니다.", nameof(projectId));
        ProjectId = projectId;
        BaseRevision = baseRevision;
        Topology = topology ?? throw new ArgumentNullException(nameof(topology));
        Mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        KeyValuePair<HeatingPathSegmentId, bool>[] copiedStates = turnSegmentStates?.ToArray() ?? [];
        if (copiedStates.Any(pair => pair.Key.IsEmpty) ||
            copiedStates.Select(pair => pair.Key).Distinct().Count() != copiedStates.Length)
        {
            throw new ArgumentException("회전 구간 상태는 고유한 유효 조각 ID를 가져야 합니다.", nameof(turnSegmentStates));
        }

        TurnSegmentStates = new ReadOnlyDictionary<HeatingPathSegmentId, bool>(
            copiedStates.ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}

/// <summary>새 회로의 시작과 끝 연결부 정의를 함께 보관합니다.</summary>
public sealed record CircuitEndpointPair(CircuitEndpointDefinition Start, CircuitEndpointDefinition End);

/// <summary>회로 끝점의 안정 ID, 표면 위치와 외부 연결 위치를 정의합니다.</summary>
public sealed record CircuitEndpointDefinition
{
    /// <summary>연결부의 안정 ID입니다.</summary>
    public ConnectionPointId Id { get; }

    /// <summary>발열 경로에 닿는 표면 위치입니다.</summary>
    public SurfaceAnchor SurfaceAnchor { get; }

    /// <summary>외부 리드가 도달할 모델 좌표입니다.</summary>
    public Vec3 ExternalConnectionPoint { get; }

    /// <summary>유효한 연결부 입력을 보관합니다.</summary>
    public CircuitEndpointDefinition(
        ConnectionPointId id,
        SurfaceAnchor surfaceAnchor,
        Vec3 externalConnectionPoint)
    {
        if (id.IsEmpty)
            throw new ArgumentException("연결부 ID가 필요합니다.", nameof(id));
        Id = id;
        SurfaceAnchor = surfaceAnchor ?? throw new ArgumentNullException(nameof(surfaceAnchor));
        ExternalConnectionPoint = externalConnectionPoint;
    }
}
