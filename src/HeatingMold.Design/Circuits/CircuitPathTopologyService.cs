using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Circuits;

/// <summary>활성 경로 조각 그래프를 회로로 쓸 수 있는 연속 성분으로 분석합니다.</summary>
public sealed class CircuitPathTopologyService
{
    /// <summary>비활성 조각을 제외하고 분기·폐회로·분리 성분을 검사합니다.</summary>
    public CircuitPathTopologyAnalysis Analyze(
        IEnumerable<HeatingPathSegment> pathSegments,
        string ruleVersion = "circuit-topology-v1")
    {
        HeatingPathSegment[] copiedSegments = pathSegments?.ToArray()
            ?? throw new ArgumentNullException(nameof(pathSegments));
        if (copiedSegments.Any(segment => segment is null))
            throw new ArgumentException("경로 조각 목록은 null 항목을 포함할 수 없습니다.", nameof(pathSegments));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("회로 topology 규칙 버전이 필요합니다.", nameof(ruleVersion));

        var issues = new List<ValidationIssue>();
        IGrouping<HeatingPathSegmentId, HeatingPathSegment>[] duplicateGroups = copiedSegments
            .GroupBy(segment => segment.Id)
            .Where(group => group.Count() > 1)
            .ToArray();
        foreach (IGrouping<HeatingPathSegmentId, HeatingPathSegment> duplicate in duplicateGroups)
        {
            issues.Add(CreateIssue(
                "DuplicatePathSegmentId",
                "같은 안정 ID를 가진 경로 조각을 하나의 topology로 분석할 수 없습니다.",
                ruleVersion,
                duplicate.Key.Value));
        }
        if (duplicateGroups.Length > 0)
            return new CircuitPathTopologyAnalysis([], issues);

        HeatingPathSegment[] activeSegments = copiedSegments.Where(segment => segment.IsActive).ToArray();
        var segmentsById = activeSegments.ToDictionary(segment => segment.Id);
        var incidentSegments = BuildIncidenceMap(activeSegments);
        var unvisited = activeSegments.Select(segment => segment.Id).ToHashSet();
        var components = new List<CircuitPathComponent>();

        while (unvisited.Count > 0)
        {
            HeatingPathSegmentId firstId = unvisited.OrderBy(id => id.Value).First();
            HashSet<HeatingPathSegmentId> componentIds = CollectComponent(firstId, unvisited, segmentsById, incidentSegments);
            HeatingPathSegment[] componentSegments = componentIds.Select(id => segmentsById[id]).ToArray();
            Dictionary<ControlPointId, List<HeatingPathSegment>> componentIncidence = BuildIncidenceMap(componentSegments);

            if (!TryGetEndpoints(componentIncidence, componentSegments, ruleVersion, out ControlPointId start, issues))
                continue;

            CircuitPathComponent? component = OrderComponent(start, componentSegments, componentIncidence);
            if (component is null)
            {
                issues.Add(CreateIssue(
                    "DisconnectedPathTraversal",
                    "활성 경로 조각을 하나의 연속 순서로 순회할 수 없습니다.",
                    ruleVersion,
                    componentSegments.Select(segment => segment.Id.Value).ToArray()));
                continue;
            }

            components.Add(component);
        }

        CircuitPathComponent[] orderedComponents = components
            .OrderBy(component => component.Segments[0].SegmentId.Value)
            .ToArray();
        return new CircuitPathTopologyAnalysis(orderedComponents, issues);
    }

    private static Dictionary<ControlPointId, List<HeatingPathSegment>> BuildIncidenceMap(
        IEnumerable<HeatingPathSegment> segments)
    {
        var incidence = new Dictionary<ControlPointId, List<HeatingPathSegment>>();
        foreach (HeatingPathSegment segment in segments)
        {
            Add(segment.StartControlPointId, segment);
            Add(segment.EndControlPointId, segment);
        }

        return incidence;

        void Add(ControlPointId pointId, HeatingPathSegment segment)
        {
            if (!incidence.TryGetValue(pointId, out List<HeatingPathSegment>? connected))
            {
                connected = [];
                incidence.Add(pointId, connected);
            }

            connected.Add(segment);
        }
    }

    private static HashSet<HeatingPathSegmentId> CollectComponent(
        HeatingPathSegmentId firstId,
        ISet<HeatingPathSegmentId> unvisited,
        IReadOnlyDictionary<HeatingPathSegmentId, HeatingPathSegment> segmentsById,
        IReadOnlyDictionary<ControlPointId, List<HeatingPathSegment>> incidentSegments)
    {
        var component = new HashSet<HeatingPathSegmentId>();
        var pending = new Stack<HeatingPathSegmentId>();
        pending.Push(firstId);

        while (pending.Count > 0)
        {
            HeatingPathSegmentId segmentId = pending.Pop();
            if (!unvisited.Remove(segmentId))
                continue;

            component.Add(segmentId);
            HeatingPathSegment segment = segmentsById[segmentId];
            foreach (ControlPointId pointId in new[] { segment.StartControlPointId, segment.EndControlPointId })
            {
                foreach (HeatingPathSegment neighbor in incidentSegments[pointId])
                {
                    if (unvisited.Contains(neighbor.Id))
                        pending.Push(neighbor.Id);
                }
            }
        }

        return component;
    }

    private static bool TryGetEndpoints(
        IReadOnlyDictionary<ControlPointId, List<HeatingPathSegment>> incidence,
        IReadOnlyCollection<HeatingPathSegment> componentSegments,
        string ruleVersion,
        out ControlPointId start,
        ICollection<ValidationIssue> issues)
    {
        ControlPointId[] branchPoints = incidence
            .Where(pair => pair.Value.Count > 2)
            .Select(pair => pair.Key)
            .OrderBy(id => id.Value)
            .ToArray();
        if (branchPoints.Length > 0)
        {
            issues.Add(CreateIssue(
                "BranchedCircuitPath",
                "경로 절점의 차수가 3 이상이라 회로 순서를 하나로 정할 수 없습니다.",
                ruleVersion,
                branchPoints.Select(point => point.Value).ToArray()));
            start = default;
            return false;
        }

        ControlPointId[] endpoints = incidence
            .Where(pair => pair.Value.Count == 1)
            .Select(pair => pair.Key)
            .OrderBy(id => id.Value)
            .ToArray();
        if (endpoints.Length == 0)
        {
            issues.Add(CreateIssue(
                "ClosedCircuitPath",
                "활성 경로가 폐회로이므로 두 끝점을 가진 회로 경로로 사용할 수 없습니다.",
                ruleVersion,
                componentSegments.Select(segment => segment.Id.Value).ToArray()));
            start = default;
            return false;
        }
        if (endpoints.Length != 2)
        {
            issues.Add(CreateIssue(
                "InvalidCircuitEndpointCount",
                "하나의 회로 경로 성분에는 degree-1 끝점이 정확히 두 개 있어야 합니다.",
                ruleVersion,
                endpoints.Select(point => point.Value).ToArray()));
            start = default;
            return false;
        }

        start = endpoints[0];
        return true;
    }

    private static CircuitPathComponent? OrderComponent(
        ControlPointId start,
        IReadOnlyCollection<HeatingPathSegment> componentSegments,
        IReadOnlyDictionary<ControlPointId, List<HeatingPathSegment>> incidence)
    {
        var remaining = componentSegments.Select(segment => segment.Id).ToHashSet();
        var orderedReferences = new List<CircuitSegmentReference>(componentSegments.Count);
        ControlPointId currentPoint = start;

        while (remaining.Count > 0)
        {
            HeatingPathSegment? nextSegment = incidence[currentPoint]
                .Where(segment => remaining.Contains(segment.Id))
                .OrderBy(segment => segment.Id.Value)
                .FirstOrDefault();
            if (nextSegment is null)
                return null;

            bool isReversed = nextSegment.EndControlPointId == currentPoint;
            orderedReferences.Add(new CircuitSegmentReference(
                nextSegment.Id,
                orderedReferences.Count,
                isReversed));
            remaining.Remove(nextSegment.Id);
            currentPoint = isReversed
                ? nextSegment.StartControlPointId
                : nextSegment.EndControlPointId;
        }

        return new CircuitPathComponent(orderedReferences, start, currentPoint);
    }

    private static ValidationIssue CreateIssue(
        string code,
        string evidence,
        string ruleVersion,
        params Guid[] entityIds) => new(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            entityIds,
            ruleVersion,
            evidence: [evidence]);
}

/// <summary>한 그래프 성분의 연속 회로 순서와 절단될 양 끝점을 보관합니다.</summary>
public sealed class CircuitPathComponent
{
    /// <summary>시작 절점부터 끝 절점까지의 발열 구간과 방향입니다.</summary>
    public IReadOnlyList<CircuitSegmentReference> Segments { get; }

    /// <summary>경로 순회에서 선택한 첫 절점입니다.</summary>
    public ControlPointId StartControlPointId { get; }

    /// <summary>경로 순회에서 도달한 마지막 절점입니다.</summary>
    public ControlPointId EndControlPointId { get; }

    /// <summary>서로 다른 끝점과 연속 순서를 가진 읽기 전용 성분을 만듭니다.</summary>
    public CircuitPathComponent(
        IEnumerable<CircuitSegmentReference> segments,
        ControlPointId startControlPointId,
        ControlPointId endControlPointId)
    {
        CircuitSegmentReference[] copiedSegments = segments?.ToArray()
            ?? throw new ArgumentNullException(nameof(segments));
        if (copiedSegments.Length == 0 || copiedSegments.Any(reference => reference.SegmentId.IsEmpty))
            throw new ArgumentException("회로 성분에는 유효한 발열 경로 구간이 필요합니다.", nameof(segments));
        if (startControlPointId.IsEmpty || endControlPointId.IsEmpty || startControlPointId == endControlPointId)
            throw new ArgumentException("회로 성분의 시작·끝 절점은 서로 다른 유효 ID여야 합니다.");
        if (copiedSegments.Select(reference => reference.SegmentId).Distinct().Count() != copiedSegments.Length ||
            copiedSegments.Select(reference => reference.Sequence).Distinct().Count() != copiedSegments.Length ||
            copiedSegments.Select(reference => reference.Sequence).Order().Where((sequence, index) => sequence != index).Any())
        {
            throw new ArgumentException("회로 성분은 중복 ID·순서 없이 0부터 연속 순서를 가져야 합니다.", nameof(segments));
        }

        Segments = new ReadOnlyCollection<CircuitSegmentReference>(copiedSegments);
        StartControlPointId = startControlPointId;
        EndControlPointId = endControlPointId;
    }
}

/// <summary>그래프 분석 결과와 유효한 연속 성분, 차단 진단입니다.</summary>
public sealed class CircuitPathTopologyAnalysis
{
    /// <summary>문제가 없는 활성 연속 경로 성분 목록입니다.</summary>
    public IReadOnlyList<CircuitPathComponent> Components { get; }

    /// <summary>분기·폐회로 등 topology 오류입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>회로 topology를 막는 오류가 없는지 나타냅니다.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    /// <summary>성분과 진단을 방어 복사해 분석 결과를 만듭니다.</summary>
    public CircuitPathTopologyAnalysis(
        IEnumerable<CircuitPathComponent> components,
        IEnumerable<ValidationIssue> issues)
    {
        CircuitPathComponent[] copiedComponents = components?.ToArray()
            ?? throw new ArgumentNullException(nameof(components));
        ValidationIssue[] copiedIssues = issues?.ToArray()
            ?? throw new ArgumentNullException(nameof(issues));
        if (copiedComponents.Any(component => component is null) || copiedIssues.Any(issue => issue is null))
            throw new ArgumentException("분석 결과는 null 성분이나 진단을 포함할 수 없습니다.");

        Components = new ReadOnlyCollection<CircuitPathComponent>(copiedComponents);
        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
    }
}
