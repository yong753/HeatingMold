using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Circuits;

/// <summary>경로 절단·통합 topology에 기존 회로 ID와 계보를 대응합니다.</summary>
public sealed class CircuitIdentityMapper
{
    /// <summary>기존 시작 연결부를 보존하고 통합 survivor를 명시했는지 확인합니다.</summary>
    public OperationResult<CircuitIdentityMapping> Map(
        CircuitPathTopologyAnalysis topology,
        IEnumerable<Circuit> previousCircuits,
        IEnumerable<ConnectionPoint> previousConnectionPoints,
        CircuitId? requestedSurvivorCircuitId = null,
        string ruleVersion = "circuit-identity-v1")
    {
        ArgumentNullException.ThrowIfNull(topology);
        Circuit[] circuits = previousCircuits?.ToArray() ?? throw new ArgumentNullException(nameof(previousCircuits));
        ConnectionPoint[] connectionPoints = previousConnectionPoints?.ToArray()
            ?? throw new ArgumentNullException(nameof(previousConnectionPoints));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("회로 ID 대응 규칙 버전이 필요합니다.", nameof(ruleVersion));
        if (!topology.IsValid)
            return OperationResult<CircuitIdentityMapping>.Failure(topology.Issues);
        if (circuits.Select(circuit => circuit.Id).Distinct().Count() != circuits.Length)
            return Failure("DuplicatePreviousCircuitId", "이전 회로 ID가 중복되어 identity를 대응할 수 없습니다.", ruleVersion);
        if (connectionPoints.Any(point => !circuits.Any(circuit => circuit.Id == point.CircuitId)))
            return Failure("ConnectionPointOwnerMissing", "이전 연결부가 존재하지 않는 회로를 참조합니다.", ruleVersion);
        if (connectionPoints.GroupBy(point => (point.CircuitId, point.Role)).Any(group => group.Count() > 1))
            return Failure("DuplicateCircuitConnectionRole", "한 회로의 시작 또는 끝 역할 연결부가 중복됐습니다.", ruleVersion);

        Circuit? requestedSurvivor = null;
        if (requestedSurvivorCircuitId.HasValue)
        {
            requestedSurvivor = circuits.SingleOrDefault(circuit => circuit.Id == requestedSurvivorCircuitId.Value);
            if (requestedSurvivor is null)
                return Failure("RequestedSurvivorNotFound", "사용자가 지정한 통합 survivor 회로가 없습니다.", ruleVersion,
                    requestedSurvivorCircuitId.Value.Value);
        }

        Dictionary<HeatingPathSegmentId, Circuit[]> ownersBySegment = circuits
            .SelectMany(circuit => circuit.Segments.Select(segment => (segment.SegmentId, Circuit: circuit)))
            .GroupBy(item => item.SegmentId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Circuit).DistinctBy(circuit => circuit.Id).ToArray());

        foreach (Circuit circuit in circuits)
        {
            CircuitPathComponent[] ownedComponents = topology.Components
                .Where(component => component.Segments.Any(segment =>
                    ownersBySegment.TryGetValue(segment.SegmentId, out Circuit[]? owners) &&
                    owners.Any(owner => owner.Id == circuit.Id)))
                .ToArray();
            if (ownedComponents.Length == 0)
                continue;
            bool isMergedIntoOneComponent = ownedComponents.Length == 1 && ownedComponents[0].Segments
                .SelectMany(segment => ownersBySegment.TryGetValue(segment.SegmentId, out Circuit[]? owners)
                    ? owners
                    : [])
                .Select(owner => owner.Id)
                .Distinct()
                .Count() > 1;
            if (isMergedIntoOneComponent)
                continue;

            ConnectionPoint? oldStart = GetEndpoint(circuit, ConnectionPointRole.Start, connectionPoints);
            int startMatches = oldStart is null
                ? 0
                : ownedComponents.Count(component => ComponentHasEndpoint(component, oldStart));
            if (startMatches != 1)
            {
                return Failure(
                    "ExistingCircuitStartEndpointLost",
                    "절단 후에도 기존 CircuitId를 이어받을 성분을 찾을 수 있도록 시작 연결부가 정확히 한 성분 끝점에 있어야 합니다.",
                    ruleVersion,
                    circuit.Id.Value);
            }
        }

        CircuitIdentityAssignment[] assignments = new CircuitIdentityAssignment[topology.Components.Count];
        var usedCircuitIds = new HashSet<CircuitId>();

        for (int index = 0; index < topology.Components.Count; index++)
        {
            CircuitPathComponent component = topology.Components[index];
            HashSet<HeatingPathSegmentId> componentSegmentIds = component.Segments
                .Select(segment => segment.SegmentId)
                .ToHashSet();
            Circuit[] owners = componentSegmentIds
                .Where(ownersBySegment.ContainsKey)
                .SelectMany(segmentId => ownersBySegment[segmentId])
                .DistinctBy(circuit => circuit.Id)
                .OrderBy(circuit => circuit.Id.Value)
                .ToArray();

            if (owners.Length > 1 && (requestedSurvivor is null || !owners.Any(circuit => circuit.Id == requestedSurvivor.Id)))
            {
                return Failure("CircuitMergeRequiresSurvivor", "둘 이상의 기존 회로가 합쳐지므로 유지할 CircuitId를 명시해야 합니다.",
                    ruleVersion,
                    owners.Select(circuit => circuit.Id.Value).ToArray());
            }

            Circuit? survivor = owners.Length > 1
                ? requestedSurvivor
                : FindStartOwner(component, owners, connectionPoints);
            if (owners.Length == 1 && survivor is null)
            {
                CircuitId? startPointOwner = FindStartPointOwner(component, owners, connectionPoints);
                if (startPointOwner.HasValue)
                    survivor = owners.Single(circuit => circuit.Id == startPointOwner.Value);
            }

            CircuitPathComponent orientedComponent = component;
            ConnectionPoint? startPoint = survivor is null
                ? null
                : GetEndpoint(survivor, ConnectionPointRole.Start, connectionPoints);
            if (startPoint is not null && component.EndControlPointId == startPoint.ControlPointId)
            {
                orientedComponent = Reverse(component);
            }
            else if (startPoint is not null && component.StartControlPointId != startPoint.ControlPointId)
            {
                return Failure("SurvivorStartEndpointUnavailable", "통합 survivor의 기존 시작 연결부를 결과 회로 끝점으로 유지할 수 없습니다.",
                    ruleVersion, survivor!.Id.Value);
            }

            CircuitId circuitId = survivor?.Id ?? CircuitId.New();
            if (!usedCircuitIds.Add(circuitId))
                return Failure("CircuitIdentityAssignedTwice", "한 기존 회로 ID가 둘 이상의 결과 성분에 배정되었습니다.", ruleVersion, circuitId.Value);

            ConnectionPoint? mappedStart = FindPointAt(component.StartControlPointId, survivor, connectionPoints);
            ConnectionPoint? mappedEnd = FindPointAt(component.EndControlPointId, survivor, connectionPoints);
            if (orientedComponent.StartControlPointId != component.StartControlPointId)
                (mappedStart, mappedEnd) = (mappedEnd, mappedStart);

            CircuitId? parentId = survivor is null && owners.Length == 1 ? owners[0].Id : null;
            CircuitId[] mergedIds = survivor is null
                ? []
                : owners.Where(owner => owner.Id != survivor.Id).Select(owner => owner.Id).ToArray();
            assignments[index] = new CircuitIdentityAssignment(
                circuitId,
                parentId,
                survivor is not null,
                orientedComponent,
                mappedStart?.Id,
                mappedEnd?.Id,
                survivor?.Supply,
                mergedIds);
        }

        return OperationResult<CircuitIdentityMapping>.Success(new CircuitIdentityMapping(assignments));
    }

    private static Circuit? FindStartOwner(
        CircuitPathComponent component,
        IReadOnlyList<Circuit> owners,
        IReadOnlyList<ConnectionPoint> connectionPoints)
    {
        Circuit[] matches = owners.Where(circuit =>
        {
            ConnectionPoint? start = GetEndpoint(circuit, ConnectionPointRole.Start, connectionPoints);
            return start is not null && ComponentHasEndpoint(component, start);
        }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static CircuitId? FindStartPointOwner(
        CircuitPathComponent component,
        IReadOnlyList<Circuit> owners,
        IReadOnlyList<ConnectionPoint> connectionPoints) => owners
        .Where(circuit => GetEndpoint(circuit, ConnectionPointRole.Start, connectionPoints) is { } point &&
                          ComponentHasEndpoint(component, point))
        .Select(circuit => (CircuitId?)circuit.Id)
        .SingleOrDefault();

    private static ConnectionPoint? GetEndpoint(
        Circuit circuit,
        ConnectionPointRole role,
        IReadOnlyList<ConnectionPoint> connectionPoints) => connectionPoints.SingleOrDefault(point =>
        point.CircuitId == circuit.Id && point.Role == role);

    private static ConnectionPoint? FindPointAt(
        ControlPointId endpoint,
        Circuit? preferredCircuit,
        IReadOnlyList<ConnectionPoint> connectionPoints) => connectionPoints
        .Where(point => point.ControlPointId == endpoint &&
                        (preferredCircuit is null || point.CircuitId == preferredCircuit.Id))
        .OrderBy(point => point.Role)
        .FirstOrDefault();

    private static bool ComponentHasEndpoint(CircuitPathComponent component, ConnectionPoint? point) =>
        point is not null && (component.StartControlPointId == point.ControlPointId ||
                              component.EndControlPointId == point.ControlPointId);

    private static CircuitPathComponent Reverse(CircuitPathComponent component)
    {
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

    private static ValidationIssue Error(string code, string evidence, string ruleVersion, IEnumerable<Guid> entityIds) => new(
        code,
        ValidationSeverity.Error,
        $"Design.{code}",
        entityIds,
        ruleVersion,
        evidence: [evidence]);

    private static OperationResult<CircuitIdentityMapping> Failure(
        string code,
        string evidence,
        string ruleVersion,
        params Guid[] entityIds) => OperationResult<CircuitIdentityMapping>.Failure(
        [Error(code, evidence, ruleVersion, entityIds)]);
}

/// <summary>결과 회로에 유지할 ID, 부모 계보와 새 경로 순서를 보관합니다.</summary>
public sealed class CircuitIdentityAssignment
{
    /// <summary>결과 회로의 안정 ID입니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>분할로 새 ID를 만들 때 계보가 되는 원 회로입니다.</summary>
    public CircuitId? ParentCircuitId { get; }

    /// <summary>이전 회로 ID를 이어받았는지 나타냅니다.</summary>
    public bool PreservesExistingIdentity { get; }

    /// <summary>새 회로 방향으로 배치한 연속 경로 성분입니다.</summary>
    public CircuitPathComponent Component { get; }

    /// <summary>기존 시작 연결부를 그대로 대응할 수 있으면 그 ID입니다.</summary>
    public ConnectionPointId? StartConnectionPointId { get; }

    /// <summary>기존 끝 연결부를 그대로 대응할 수 있으면 그 ID입니다.</summary>
    public ConnectionPointId? EndConnectionPointId { get; }

    /// <summary>기존 ID를 보존할 때만 전달하는 전원 설정입니다.</summary>
    public Supply? PreservedSupply { get; }

    /// <summary>통합으로 결과에서 은퇴하는 이전 회로 ID입니다.</summary>
    public IReadOnlyList<CircuitId> MergedCircuitIds { get; }

    /// <summary>ID와 계보, 끝점 대응 결과를 함께 기록합니다.</summary>
    public CircuitIdentityAssignment(
        CircuitId circuitId,
        CircuitId? parentCircuitId,
        bool preservesExistingIdentity,
        CircuitPathComponent component,
        ConnectionPointId? startConnectionPointId,
        ConnectionPointId? endConnectionPointId,
        Supply? preservedSupply,
        IEnumerable<CircuitId> mergedCircuitIds)
    {
        CircuitId[] merged = mergedCircuitIds?.Distinct().ToArray()
            ?? throw new ArgumentNullException(nameof(mergedCircuitIds));
        if (circuitId.IsEmpty || parentCircuitId is { IsEmpty: true } ||
            startConnectionPointId is { IsEmpty: true } || endConnectionPointId is { IsEmpty: true })
        {
            throw new ArgumentException("회로 대응에는 유효한 ID만 사용할 수 있습니다.");
        }
        if (preservesExistingIdentity != (preservedSupply is not null))
            throw new ArgumentException("기존 ID를 보존하는 경우에만 기존 전원 설정을 전달해야 합니다.", nameof(preservedSupply));

        CircuitId = circuitId;
        ParentCircuitId = parentCircuitId;
        PreservesExistingIdentity = preservesExistingIdentity;
        Component = component ?? throw new ArgumentNullException(nameof(component));
        StartConnectionPointId = startConnectionPointId;
        EndConnectionPointId = endConnectionPointId;
        PreservedSupply = preservedSupply;
        MergedCircuitIds = new ReadOnlyCollection<CircuitId>(merged);
    }
}

/// <summary>결과 성분과 기존 회로 ID 대응을 보관합니다.</summary>
public sealed class CircuitIdentityMapping
{
    /// <summary>topology 성분 순서와 같은 결과 회로 할당입니다.</summary>
    public IReadOnlyList<CircuitIdentityAssignment> Assignments { get; }

    /// <summary>결과 회로 ID와 계보를 방어 복사합니다.</summary>
    public CircuitIdentityMapping(IEnumerable<CircuitIdentityAssignment> assignments)
    {
        CircuitIdentityAssignment[] copied = assignments?.ToArray()
            ?? throw new ArgumentNullException(nameof(assignments));
        if (copied.Any(assignment => assignment is null) ||
            copied.Select(assignment => assignment.CircuitId).Distinct().Count() != copied.Length)
        {
            throw new ArgumentException("회로 identity mapping은 유효한 고유 회로 ID를 가져야 합니다.", nameof(assignments));
        }

        Assignments = new ReadOnlyCollection<CircuitIdentityAssignment>(copied);
    }
}
