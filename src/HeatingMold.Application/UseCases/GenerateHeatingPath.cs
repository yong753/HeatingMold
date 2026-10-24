using HeatingMold.Application.Ports.Geometry;
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
using HeatingMold.Design.Editing;
using HeatingMold.Design.ConnectionPath;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.GeometryMapping;
using HeatingMold.Design.HeatingPath;

namespace HeatingMold.Application.UseCases;

/// <summary>2차원 경로를 표면에 매핑하고 회로·연결 경로를 포함한 후보를 원자 확정합니다.</summary>
public sealed class GenerateHeatingPath
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly HeatingPathMaterializer _materializer;
    private readonly AssessHeatingPathRegeneration _regenerationAssessment;
    private readonly ManualEditPreserver _manualEditPreserver;
    private readonly ConnectionPathPlanner _connectionPathPlanner = new();

    /// <summary>경로 계획, 표면 변환과 후보 확정 서비스를 연결합니다.</summary>
    public GenerateHeatingPath(
        ProjectSession session,
        CandidateCoordinator coordinator,
        ConstrainedHeatingPathPlanner? planner = null,
        HeatingPathMaterializer? materializer = null,
        RegenerationCompatibility? regenerationCompatibility = null,
        ManualEditPreserver? manualEditPreserver = null,
        AssessHeatingPathRegeneration? regenerationAssessment = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ConstrainedHeatingPathPlanner configuredPlanner = planner ?? new ConstrainedHeatingPathPlanner();
        RegenerationCompatibility configuredCompatibility = regenerationCompatibility ?? new RegenerationCompatibility();
        _regenerationAssessment = regenerationAssessment ?? new AssessHeatingPathRegeneration(
            configuredPlanner,
            configuredCompatibility);
        _materializer = materializer ?? new HeatingPathMaterializer();
        _manualEditPreserver = manualEditPreserver ?? new ManualEditPreserver();
    }

    /// <summary>회로 설정 없이 실행해 연결 정보 누락을 명시적으로 반환합니다.</summary>
    public CandidateOutcome Execute(
        HeatingPathGenerationRequest request,
        IGeometryQueries geometryQueries,
        CancellationToken cancellationToken = default)
    {
        return Execute(request, geometryQueries, circuitSetup: null, cancellationToken);
    }

    /// <summary>경로·회로·연결 경로를 하나의 revision 후보로 준비하고 확정합니다.</summary>
    public CandidateOutcome Execute(
        HeatingPathGenerationRequest request,
        IGeometryQueries geometryQueries,
        HeatingPathCircuitSetup? circuitSetup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(geometryQueries);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure(current, "ProjectNotLoaded", "발열 경로를 만들려면 먼저 프로젝트를 열어야 합니다.");
        if (project.Id != request.ProjectId || project.Revision != request.BaseRevision)
            return Failure(current, "StaleHeatingPathRequest", "경로 생성 요청의 프로젝트 또는 기준 revision이 현재 설계와 다릅니다.");
        if (request.CoordinateSystem.Revision != request.ExpectedModelRevision)
            return Failure(current, "StaleSurfaceMappingContext", "작업 좌표계와 모델 기하 revision이 일치하지 않습니다.");
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(current, request.Region.Id);

        HeatingRegion? region = project.HeatingRegions.SingleOrDefault(candidate => candidate.Id == request.Region.Id);
        if (region is null || !HaveSameRegionBoundary(region, request.Region))
            return Failure(current, "StaleHeatingRegion", "경로 생성 요청의 영역 경계가 현재 설계와 다릅니다.", request.Region.Id.Value);
        if (circuitSetup is null)
            return Failure(current, "CircuitSetupRequired", "확정 경로에는 회로의 Supply, 양 끝 연결부와 ConnectionPath가 필요합니다.");
        if (!request.Region.FaceReferences.Contains(circuitSetup.Endpoints.Start.SurfaceAnchor.FaceReferenceId) ||
            !request.Region.FaceReferences.Contains(circuitSetup.Endpoints.End.SurfaceAnchor.FaceReferenceId))
        {
            return Failure(
                current,
                "CircuitEndpointAnchorOutsideRegion",
                "회로 인출 앵커는 생성 영역에 속한 Face를 가리켜야 합니다.",
                circuitSetup.Endpoints.Start.Id.Value,
                circuitSetup.Endpoints.End.Id.Value);
        }
        ValidationIssue? routeInputIssue = ValidateConnectionRouteInputs(
            request,
            circuitSetup,
            project.Rules.GeometryTolerances.ConnectionToleranceMm,
            project.Rules.ValidationRuleVersion);
        if (routeInputIssue is not null)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, [routeInputIssue]);

        OperationResult<HeatingPathRegenerationAssessment> assessmentResult =
            _regenerationAssessment.Execute(project, request, cancellationToken);
        if (!assessmentResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, assessmentResult.Issues);

        HeatingPathRegenerationAssessment assessment = assessmentResult.GetValueOrThrow();
        if (assessment.RequiresAdditionalPathReplacement)
        {
            return Failure(
                current,
                "AdditionalHeatingPathsRequireRegeneration",
                "도체 또는 배치 조건 변경이 다른 발열 경로에도 영향을 줍니다. 여러 영역을 함께 재생성하는 계획이 필요합니다.",
                assessment.Impact.HeatingPathIds.Select(pathId => pathId.Value).ToArray());
        }

        HeatingPathDraft draft = assessment.Draft;
        HeatingPath? previousPath = assessment.ExistingPath;
        RegenerationCompatibilityResult? editsToPreserve = null;
        if (previousPath is not null && request.ManualEditPolicy == ManualEditPolicy.PreserveIfCompatible)
        {
            RegenerationCompatibilityResult compatibilityResult = assessment.Compatibility;
            if (compatibilityResult.Status == RegenerationCompatibilityStatus.ReplaceRequired)
            {
                CandidateOutcome confirmationRequired = Failure(
                    current,
                    "ManualPathReplacementConfirmationRequired",
                    "기존 수동 편집을 안전하게 대응할 수 없습니다. ReplaceEditedHeatingPath 정책으로 다시 요청해야 합니다.",
                    previousPath.Id.Value);
                return new CandidateOutcome(
                    confirmationRequired.Status,
                    confirmationRequired.Snapshot,
                    confirmationRequired.Issues.Concat(compatibilityResult.Issues));
            }

            if (compatibilityResult.Status == RegenerationCompatibilityStatus.CanPreserveOverrides)
                editsToPreserve = compatibilityResult;
        }

        OperationResult<HeatingPath> materialized;
        try
        {
            materialized = _materializer.Materialize(
                draft,
                project,
                request.WireSpecification,
                request.PlacementConstraints,
                request.CoordinateSystem,
                request.ExpectedModelRevision,
                geometryQueries,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(current, request.Region.Id);
        }
        if (!materialized.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, materialized.Issues);

        HeatingPath newPath = materialized.GetValueOrThrow();
        if (previousPath is not null && editsToPreserve is not null)
        {
            OperationResult<HeatingPath> preserveResult = _manualEditPreserver.Preserve(
                previousPath,
                newPath,
                editsToPreserve);
            if (!preserveResult.IsSuccess)
                return new CandidateOutcome(CandidateStatus.ValidationFailed, current, preserveResult.Issues);

            newPath = preserveResult.GetValueOrThrow();
        }

        Circuit? previousCircuit = project.Circuits
            .SingleOrDefault(circuit => circuit.Id == circuitSetup.CircuitId);
        int sequence = previousCircuit?.Sequence ??
            project.Circuits.Select(circuit => circuit.Sequence).DefaultIfEmpty(-1).Max() + 1;
        OperationResult<GeneratedCircuit> circuitResult = CreateGeneratedCircuit(
            project,
            newPath,
            draft,
            circuitSetup,
            sequence);
        if (!circuitResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, circuitResult.Issues);
        GeneratedCircuit generatedCircuit = circuitResult.GetValueOrThrow();

        OperationResult<IReadOnlyDictionary<ConnectionPointId, ConnectionPath>> connectionPathResult =
            BuildConnectionPaths(
                newPath,
                generatedCircuit,
                circuitSetup,
                request,
                geometryQueries,
                project,
                cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(current, request.Region.Id);
        if (!connectionPathResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, connectionPathResult.Issues);

        IReadOnlyDictionary<ConnectionPointId, ConnectionPath> generatedConnectionPaths =
            connectionPathResult.GetValueOrThrow();

        HashSet<HeatingPathId> invalidPathIds = assessment.Impact.HeatingPathIds.ToHashSet();
        HashSet<CircuitId> invalidCircuitIds = assessment.Impact.CircuitIds.ToHashSet();
        HashSet<ConnectionPointId> invalidConnectionPointIds = assessment.Impact.ConnectionPointIds.ToHashSet();

        HashSet<CircuitId> retainedCircuitIds = project.Circuits
            .Where(circuit => !invalidCircuitIds.Contains(circuit.Id))
            .Select(circuit => circuit.Id)
            .ToHashSet();
        if (retainedCircuitIds.Contains(circuitSetup.CircuitId))
        {
            return Failure(
                current,
                "GeneratedCircuitIdConflict",
                "새 경로의 CircuitId가 교체 대상이 아닌 기존 회로와 충돌합니다.",
                circuitSetup.CircuitId.Value);
        }

        HashSet<ConnectionPointId> retainedPointIds = project.ConnectionPoints
            .Where(point => !invalidConnectionPointIds.Contains(point.Id))
            .Select(point => point.Id)
            .ToHashSet();
        if (retainedPointIds.Contains(generatedCircuit.StartPoint.Id) ||
            retainedPointIds.Contains(generatedCircuit.EndPoint.Id))
        {
            return Failure(
                current,
                "GeneratedConnectionPointIdConflict",
                "새 회로 연결부 ID가 교체 대상이 아닌 기존 연결부와 충돌합니다.",
                generatedCircuit.StartPoint.Id.Value,
                generatedCircuit.EndPoint.Id.Value);
        }

        HashSet<ConnectionPathId> retainedConnectionPathIds = project.ConnectionPaths
            .Where(path => !invalidConnectionPointIds.Contains(path.ConnectionPointId))
            .Select(path => path.Id)
            .ToHashSet();
        if (generatedConnectionPaths.Values.Any(path => retainedConnectionPathIds.Contains(path.Id)))
        {
            return Failure(
                current,
                "GeneratedConnectionPathIdConflict",
                "새 ConnectionPath ID가 교체 대상이 아닌 기존 연결 경로와 충돌합니다.",
                generatedConnectionPaths.Values
                    .Where(path => retainedConnectionPathIds.Contains(path.Id))
                    .Select(path => path.Id.Value)
                    .ToArray());
        }

        var candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            heatingPaths: project.HeatingPaths
                .Where(path => !invalidPathIds.Contains(path.Id))
                .Append(newPath),
            circuits: project.Circuits
                .Where(circuit => !invalidCircuitIds.Contains(circuit.Id))
                .Append(generatedCircuit.Circuit),
            connectionPoints: project.ConnectionPoints
                .Where(point => !invalidCircuitIds.Contains(point.CircuitId))
                .Append(generatedCircuit.StartPoint)
                .Append(generatedCircuit.EndPoint),
            connectionPaths: project.ConnectionPaths
                .Where(path => !invalidConnectionPointIds.Contains(path.ConnectionPointId))
                .Concat(generatedConnectionPaths.Values),
            wireSpecification: request.WireSpecification,
            replaceWireSpecification: true,
            placementConstraints: request.PlacementConstraints,
            replacePlacementConstraints: true);

        return _coordinator.Execute(candidate, request.BaseRevision, cancellationToken);
    }

    /// <summary>생성한 회로 끝점과 사용자 경로 입력에서 두 확정 연결 경로를 준비합니다.</summary>
    private OperationResult<IReadOnlyDictionary<ConnectionPointId, ConnectionPath>> BuildConnectionPaths(
        HeatingPath path,
        GeneratedCircuit generatedCircuit,
        HeatingPathCircuitSetup circuitSetup,
        HeatingPathGenerationRequest request,
        IGeometryQueries geometryQueries,
        DesignProject project,
        CancellationToken cancellationToken)
    {
        if (circuitSetup.ConnectionPathLegs.Count == 0)
        {
            return OperationResult<IReadOnlyDictionary<ConnectionPointId, ConnectionPath>>.Success(
                circuitSetup.ConnectionPaths);
        }

        var paths = new Dictionary<ConnectionPointId, ConnectionPath>();
        ConnectionPoint[] endpoints = [generatedCircuit.StartPoint, generatedCircuit.EndPoint];
        foreach (ConnectionPoint endpoint in endpoints)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return OperationResult<IReadOnlyDictionary<ConnectionPointId, ConnectionPath>>.Failure(
                    [new ValidationIssue(
                        "OperationCancelled",
                        ValidationSeverity.Error,
                        "Application.OperationCancelled",
                        [endpoint.Id.Value],
                        project.Rules.ValidationRuleVersion)]);
            }

            HeatingPathSegment endpointSegment = path.Segments.Single(
                segment => segment.Id == endpoint.HeatingPathSegmentId);
            Vec3 heatingEndpoint = path.ControlPoints.Single(
                point => point.Id == endpoint.ControlPointId).Position;
            IReadOnlyList<ConnectionPathLegRequest> legs = circuitSetup.ConnectionPathLegs[endpoint.Id];
            ConnectionPathPlanningRequest planningRequest = new(
                generatedCircuit.Circuit,
                endpoint,
                endpointSegment,
                heatingEndpoint,
                legs,
                request.CoordinateSystem,
                request.ExpectedModelRevision);
            OperationResult<ConnectionPath> plannedPath = _connectionPathPlanner.Plan(
                planningRequest,
                geometryQueries,
                project.Rules.GeometryTolerances.ConnectionToleranceMm,
                project.Rules.ValidationRuleVersion,
                cancellationToken);
            if (!plannedPath.IsSuccess)
                return OperationResult<IReadOnlyDictionary<ConnectionPointId, ConnectionPath>>.Failure(
                    plannedPath.Issues);

            paths.Add(endpoint.Id, plannedPath.GetValueOrThrow());
        }

        return OperationResult<IReadOnlyDictionary<ConnectionPointId, ConnectionPath>>.Success(
            new System.Collections.ObjectModel.ReadOnlyDictionary<ConnectionPointId, ConnectionPath>(paths));
    }

    /// <summary>표면 질의 전에 연결 전이와 끝 좌표가 입력 앵커에 맞는지 검사합니다.</summary>
    private static ValidationIssue? ValidateConnectionRouteInputs(
        HeatingPathGenerationRequest request,
        HeatingPathCircuitSetup circuitSetup,
        double connectionToleranceMm,
        string ruleVersion)
    {
        if (circuitSetup.ConnectionPathLegs.Count == 0)
            return null;

        (ConnectionPointId Id, CircuitEndpointDefinition Endpoint, SurfaceAnchor HeatingAnchor)[] endpoints =
        [
            (circuitSetup.Endpoints.Start.Id, circuitSetup.Endpoints.Start, request.PlacementConstraints.StartAnchor),
            (circuitSetup.Endpoints.End.Id, circuitSetup.Endpoints.End, request.PlacementConstraints.EndAnchor)
        ];

        foreach ((ConnectionPointId id, CircuitEndpointDefinition endpoint, SurfaceAnchor heatingAnchor) in endpoints)
        {
            IReadOnlyList<ConnectionPathLegRequest> legs = circuitSetup.ConnectionPathLegs[id];
            int[] transitionIndices = legs
                .Select((leg, index) => (leg, index))
                .Where(item => item.leg.IsMoldExitTransition)
                .Select(item => item.index)
                .ToArray();
            if (transitionIndices.Length != 1)
            {
                return ConnectionRouteIssue(
                    "InvalidMoldExitTransition",
                    "각 연결 경로에는 금형 이탈 표식이 정확히 하나 필요합니다.",
                    id,
                    ruleVersion);
            }

            int transitionIndex = transitionIndices[0];
            if (transitionIndex < 1 || legs[transitionIndex].GeometryDomain != GeometryDomain.External ||
                legs[transitionIndex - 1].GeometryDomain != GeometryDomain.OnMold ||
                legs.Take(transitionIndex).Any(leg => leg.GeometryDomain != GeometryDomain.OnMold) ||
                legs.Skip(transitionIndex).Any(leg => leg.GeometryDomain != GeometryDomain.External))
            {
                return ConnectionRouteIssue(
                    "ConnectionPathDomainOrderInvalid",
                    "연결 구간은 OnMold에서 표식된 한 번의 이탈 뒤 External로 이어져야 합니다.",
                    id,
                    ruleVersion);
            }

            if ((legs[transitionIndex - 1].EndPoint - endpoint.SurfaceAnchor.ModelPoint).Length > connectionToleranceMm)
            {
                return ConnectionRouteIssue(
                    "MoldExitAnchorMismatch",
                    "마지막 OnMold 구간이 연결부의 표면 인출 앵커에서 끝나야 합니다.",
                    id,
                    ruleVersion);
            }

            if ((legs[^1].EndPoint - endpoint.ExternalConnectionPoint).Length > connectionToleranceMm)
            {
                return ConnectionRouteIssue(
                    "ExternalConnectionPointMismatch",
                    "마지막 연결 구간이 지정한 외부 연결 좌표에서 끝나야 합니다.",
                    id,
                    ruleVersion);
            }

            Vec3 previous = heatingAnchor.ModelPoint;
            foreach (ConnectionPathLegRequest leg in legs)
            {
                if ((leg.EndPoint - previous).Length <= connectionToleranceMm)
                {
                    return ConnectionRouteIssue(
                        "DegenerateConnectionPathLeg",
                        "연속 연결 절점 사이에는 허용오차보다 긴 구간이 필요합니다.",
                        id,
                        ruleVersion);
                }

                previous = leg.EndPoint;
            }
        }

        return null;
    }

    /// <summary>연결 경로 입력 검증 결과를 관련 연결부 ID와 함께 만듭니다.</summary>
    private static ValidationIssue ConnectionRouteIssue(
        string code,
        string evidence,
        ConnectionPointId connectionPointId,
        string ruleVersion) =>
        new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            [connectionPointId.Value],
            ruleVersion,
            evidence: [evidence]);

    private static OperationResult<GeneratedCircuit> CreateGeneratedCircuit(
        DesignProject project,
        HeatingPath path,
        HeatingPathDraft draft,
        HeatingPathCircuitSetup circuitSetup,
        int sequence)
    {
        CircuitPathTopologyAnalysis topology = new CircuitPathTopologyService().Analyze(
            path.Segments,
            project.Rules.ValidationRuleVersion);
        if (!topology.IsValid)
            return OperationResult<GeneratedCircuit>.Failure(topology.Issues);
        if (topology.Components.Count != 1)
        {
            return Failure<GeneratedCircuit>(
                project,
                "GeneratedCircuitTopologyInvalid",
                "한 경로 생성 요청은 하나의 연속 회로 성분을 만들어야 합니다.",
                path.Id.Value);
        }

        CircuitPathComponent component = topology.Components[0];
        ControlPointId requestedStart = draft.ControlPoints[0].Id;
        ControlPointId requestedEnd = draft.ControlPoints[^1].Id;
        CircuitPathComponent? orientedComponent = OrientComponent(component, requestedStart);
        if (orientedComponent is null || orientedComponent.EndControlPointId != requestedEnd)
        {
            return Failure<GeneratedCircuit>(
                project,
                "GeneratedCircuitEndpointMismatch",
                "회로 경로의 topology 끝점이 생성 요청의 시작·끝 절점과 일치하지 않습니다.",
                path.Id.Value,
                requestedStart.Value,
                requestedEnd.Value);
        }

        CircuitSegmentReference firstReference = orientedComponent.Segments[0];
        CircuitSegmentReference lastReference = orientedComponent.Segments[^1];
        var circuit = new Circuit(
            circuitSetup.CircuitId,
            sequence,
            orientedComponent.Segments,
            circuitSetup.Endpoints.Start.Id,
            circuitSetup.Endpoints.End.Id,
            circuitSetup.Supply);
        var startPoint = new ConnectionPoint(
            circuitSetup.Endpoints.Start.Id,
            circuitSetup.CircuitId,
            ConnectionPointRole.Start,
            firstReference.SegmentId,
            requestedStart,
            circuitSetup.Endpoints.Start.SurfaceAnchor,
            circuitSetup.Endpoints.Start.ExternalConnectionPoint);
        var endPoint = new ConnectionPoint(
            circuitSetup.Endpoints.End.Id,
            circuitSetup.CircuitId,
            ConnectionPointRole.End,
            lastReference.SegmentId,
            requestedEnd,
            circuitSetup.Endpoints.End.SurfaceAnchor,
            circuitSetup.Endpoints.End.ExternalConnectionPoint);

        return OperationResult<GeneratedCircuit>.Success(new GeneratedCircuit(circuit, startPoint, endPoint));
    }

    private static CircuitPathComponent? OrientComponent(
        CircuitPathComponent component,
        ControlPointId requestedStart)
    {
        if (component.StartControlPointId == requestedStart)
            return component;
        if (component.EndControlPointId != requestedStart)
            return null;

        CircuitSegmentReference[] reversedSegments = component.Segments
            .Reverse()
            .Select((segment, index) => new CircuitSegmentReference(
                segment.SegmentId,
                index,
                !segment.IsReversed))
            .ToArray();
        return new CircuitPathComponent(
            reversedSegments,
            component.EndControlPointId,
            component.StartControlPointId);
    }

    private static bool HaveSameRegionBoundary(HeatingRegion first, HeatingRegion second) =>
        first.FaceReferences.SequenceEqual(second.FaceReferences) &&
        first.EffectiveBoundaryLoops.Select(loop => loop.Id).SequenceEqual(second.EffectiveBoundaryLoops.Select(loop => loop.Id)) &&
        first.ExitSide.Ranges.SequenceEqual(second.ExitSide.Ranges);

    private static CandidateOutcome Failure(
        SessionSnapshot snapshot,
        string code,
        string evidence,
        params Guid[] entityIds) =>
        new(
            CandidateStatus.ValidationFailed,
            snapshot,
            [new ValidationIssue(code, ValidationSeverity.Error, $"Application.{code}", entityIds, "application-generation-v1", evidence: [evidence])]);

    private static CandidateOutcome Cancelled(SessionSnapshot snapshot, RegionId regionId) =>
        new(
            CandidateStatus.Cancelled,
            snapshot,
            [new ValidationIssue(
                "OperationCancelled",
                ValidationSeverity.Error,
                "Application.OperationCancelled",
                [regionId.Value],
                "application-generation-v1")]);

    private static OperationResult<T> Failure<T>(
        DesignProject project,
        string code,
        string evidence,
        params Guid[] entityIds) => OperationResult<T>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            entityIds,
            project.Rules.ValidationRuleVersion,
            evidence: [evidence])]);

    private sealed record GeneratedCircuit(
        Circuit Circuit,
        ConnectionPoint StartPoint,
        ConnectionPoint EndPoint);
}

/// <summary>생성된 경로의 회로 전원, 두 연결부와 연결 경로를 보관합니다.</summary>
public sealed class HeatingPathCircuitSetup
{
    /// <summary>새 회로의 안정 ID입니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>생성 경로 양 끝의 연결부 정의입니다.</summary>
    public CircuitEndpointPair Endpoints { get; }

    /// <summary>새 회로의 전압과 전기 한도입니다.</summary>
    public Supply Supply { get; }

    /// <summary>끝 연결부마다 검증할 최종 연결 경로입니다.</summary>
    public IReadOnlyDictionary<ConnectionPointId, ConnectionPath> ConnectionPaths { get; }

    /// <summary>신규 생성 뒤 검사할 연결 경로 구간 입력입니다.</summary>
    public IReadOnlyDictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>> ConnectionPathLegs { get; }

    /// <summary>회로 양 끝 정의와 정확히 대응하는 연결 경로를 복사합니다.</summary>
    public HeatingPathCircuitSetup(
        CircuitId circuitId,
        CircuitEndpointPair endpoints,
        Supply supply,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPath> connectionPaths)
        : this(
            circuitId,
            endpoints,
            supply,
            connectionPaths,
            (IReadOnlyDictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>>?)null)
    {
    }

    /// <summary>회로 양 끝 정의와 사용자 지정 연결 구간 입력을 복사합니다.</summary>
    public HeatingPathCircuitSetup(
        CircuitId circuitId,
        CircuitEndpointPair endpoints,
        Supply supply,
        IReadOnlyDictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>> connectionPathLegs)
        : this(
            circuitId,
            endpoints,
            supply,
            (IReadOnlyDictionary<ConnectionPointId, ConnectionPath>?)null,
            connectionPathLegs)
    {
    }

    private HeatingPathCircuitSetup(
        CircuitId circuitId,
        CircuitEndpointPair endpoints,
        Supply supply,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPath>? connectionPaths,
        IReadOnlyDictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>>? connectionPathLegs)
    {
        if (circuitId.IsEmpty)
            throw new ArgumentException("새 회로 ID가 필요합니다.", nameof(circuitId));
        CircuitId = circuitId;
        Endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        Supply = supply ?? throw new ArgumentNullException(nameof(supply));
        if (Endpoints.Start is null || Endpoints.End is null || Endpoints.Start.Id == Endpoints.End.Id)
            throw new ArgumentException("새 회로에는 서로 다른 시작·끝 연결부가 필요합니다.", nameof(endpoints));

        HashSet<ConnectionPointId> endpointIds = [Endpoints.Start.Id, Endpoints.End.Id];
        if ((connectionPaths is null) == (connectionPathLegs is null))
            throw new ArgumentException("확정 경로 또는 경로 구간 입력 중 하나만 제공해야 합니다.");

        if (connectionPaths is not null)
        {
            Dictionary<ConnectionPointId, ConnectionPath> copiedPaths = connectionPaths
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (!endpointIds.SetEquals(copiedPaths.Keys) ||
                copiedPaths.Any(pair => pair.Key.IsEmpty || pair.Value is null || pair.Value.ConnectionPointId != pair.Key) ||
                copiedPaths.Values.Select(path => path.Id).Distinct().Count() != copiedPaths.Count)
            {
                throw new ArgumentException("연결 경로는 양 끝 ID와 정확히 일치하고 서로 고유해야 합니다.", nameof(connectionPaths));
            }

            ConnectionPaths = new System.Collections.ObjectModel.ReadOnlyDictionary<ConnectionPointId, ConnectionPath>(
                copiedPaths);
            ConnectionPathLegs = new System.Collections.ObjectModel.ReadOnlyDictionary<
                ConnectionPointId,
                IReadOnlyList<ConnectionPathLegRequest>>(
                new Dictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>>());
            return;
        }

        Dictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>> copiedLegs = connectionPathLegs!
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ConnectionPathLegRequest>)Array.AsReadOnly(
                    pair.Value?.ToArray() ?? throw new ArgumentException("연결 구간 목록이 필요합니다.", nameof(connectionPathLegs))));
        if (!endpointIds.SetEquals(copiedLegs.Keys) ||
            copiedLegs.Any(pair => pair.Key.IsEmpty || pair.Value.Count < 2 || pair.Value.Any(leg => leg is null)))
        {
            throw new ArgumentException("연결 구간 입력은 양 끝 ID와 정확히 대응하고 구간을 두 개 이상 포함해야 합니다.", nameof(connectionPathLegs));
        }

        ConnectionPaths = new System.Collections.ObjectModel.ReadOnlyDictionary<ConnectionPointId, ConnectionPath>(
            new Dictionary<ConnectionPointId, ConnectionPath>());
        ConnectionPathLegs = new System.Collections.ObjectModel.ReadOnlyDictionary<
            ConnectionPointId,
            IReadOnlyList<ConnectionPathLegRequest>>(copiedLegs);
    }
}
