using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;

namespace HeatingMold.Core.Model;

/// <summary>확정 설계를 판정하는 알고리즘 버전과 기하 설정입니다.</summary>
public sealed record DesignRuleSet
{
    /// <summary>경로 배치 규칙 버전입니다.</summary>
    public string GenerationRuleVersion { get; }

    /// <summary>기하·참조 검증 규칙 버전입니다.</summary>
    public string ValidationRuleVersion { get; }

    /// <summary>실제 면적 분할 규칙 버전입니다.</summary>
    public string AreaRuleVersion { get; }

    /// <summary>회로 전기 규칙 버전입니다.</summary>
    public string ElectricalRuleVersion { get; }

    /// <summary>목적별 기하 허용오차와 질의 한도입니다.</summary>
    public GeometryToleranceProfile GeometryTolerances { get; }

    /// <summary>각 규칙 버전과 유효 기하 설정을 보관합니다.</summary>
    public DesignRuleSet(
        string generationRuleVersion,
        string validationRuleVersion,
        string areaRuleVersion,
        string electricalRuleVersion,
        GeometryToleranceProfile geometryTolerances)
    {
        GenerationRuleVersion = RequireText(generationRuleVersion, nameof(generationRuleVersion));
        ValidationRuleVersion = RequireText(validationRuleVersion, nameof(validationRuleVersion));
        AreaRuleVersion = RequireText(areaRuleVersion, nameof(areaRuleVersion));
        ElectricalRuleVersion = RequireText(electricalRuleVersion, nameof(electricalRuleVersion));
        GeometryTolerances = geometryTolerances ?? throw new ArgumentNullException(nameof(geometryTolerances));
    }

    /// <summary>신규 프로젝트에 적용할 초기 규칙 묶음입니다.</summary>
    public static DesignRuleSet Initial { get; } = new(
        "heating-path-v1", "geometry-validation-v1", "surface-area-v3", "electrical-v1",
        GeometryToleranceProfile.Initial);

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("규칙 버전을 입력해야 합니다.", parameterName);

        return value.Trim();
    }
}

/// <summary>영속 도메인 값만으로 구성한 확정 설계 스냅샷입니다.</summary>
public sealed class DesignProject
{
    /// <summary>프로젝트의 안정 영속 ID입니다.</summary>
    public ProjectId Id { get; }

    /// <summary>설계 파일 JSON schema 버전입니다.</summary>
    public int SchemaVersion { get; }

    /// <summary>성공적으로 확정된 프로젝트 변경 횟수입니다.</summary>
    public DesignRevision Revision { get; }

    /// <summary>사용자가 지정한 프로젝트 이름입니다.</summary>
    public string Name { get; }

    /// <summary>프로젝트를 처음 만든 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>현재 확정 스냅샷의 UTC 수정 시각입니다.</summary>
    public DateTimeOffset ModifiedUtc { get; }

    /// <summary>원본 모델과 가져오기 구현의 영속 식별 정보입니다.</summary>
    public ModelIdentity SourceModel { get; }

    /// <summary>저장할 모델 고정 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystemDefinition WorkingCoordinateSystem { get; }

    /// <summary>재열기에서 다시 대응할 원본 Face 특징입니다.</summary>
    public IReadOnlyList<FaceReference> FaceReferences { get; }

    /// <summary>원본·포함·제외·유효 경계를 가진 발열 영역입니다.</summary>
    public IReadOnlyList<HeatingRegion> HeatingRegions { get; }

    /// <summary>설계 전체에 적용한 발열 도체 사양입니다.</summary>
    public WireSpecification? WireSpecification { get; }

    /// <summary>설계 전체에 적용한 레인 배치 조건입니다.</summary>
    public PlacementConstraints? PlacementConstraints { get; }

    /// <summary>생성 원본과 현재 편집 상태가 보존된 발열 경로입니다.</summary>
    public IReadOnlyList<HeatingPath> HeatingPaths { get; }

    /// <summary>회로별 조각 소속과 개별 전원을 보관합니다.</summary>
    public IReadOnlyList<Circuit> Circuits { get; }

    /// <summary>회로 경로 끝의 표면 인출과 외부 연결부입니다.</summary>
    public IReadOnlyList<ConnectionPoint> ConnectionPoints { get; }

    /// <summary>연결부까지의 재료 역할별 최종 경로입니다.</summary>
    public IReadOnlyList<ConnectionPath> ConnectionPaths { get; }

    /// <summary>설계·검증·면적·전기 계산 규칙 설정입니다.</summary>
    public DesignRuleSet Rules { get; }

    /// <summary>필요한 입력만 가진 경로 없는 새 프로젝트를 만듭니다.</summary>
    public static DesignProject CreateEmpty(
        ProjectId id,
        ModelIdentity sourceModel,
        WorkingCoordinateSystemDefinition workingCoordinateSystem,
        string name = "Untitled",
        DateTimeOffset? createdUtc = null)
    {
        DateTimeOffset created = (createdUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return new DesignProject(
            id, schemaVersion: 1, new DesignRevision(0), name, created, created,
            sourceModel, workingCoordinateSystem,
            Array.Empty<FaceReference>(), Array.Empty<HeatingRegion>(), null, null,
            Array.Empty<HeatingPath>(), Array.Empty<Circuit>(), Array.Empty<ConnectionPoint>(),
            Array.Empty<ConnectionPath>(), DesignRuleSet.Initial);
    }

    /// <summary>완전한 컬렉션을 방어 복사해 확정 프로젝트 스냅샷을 만듭니다.</summary>
    public DesignProject(
        ProjectId id,
        int schemaVersion,
        DesignRevision revision,
        string name,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        ModelIdentity sourceModel,
        WorkingCoordinateSystemDefinition workingCoordinateSystem,
        IEnumerable<FaceReference> faceReferences,
        IEnumerable<HeatingRegion> heatingRegions,
        WireSpecification? wireSpecification,
        PlacementConstraints? placementConstraints,
        IEnumerable<HeatingPath> heatingPaths,
        IEnumerable<Circuit> circuits,
        IEnumerable<ConnectionPoint> connectionPoints,
        IEnumerable<ConnectionPath> connectionPaths,
        DesignRuleSet? rules = null)
    {
        if (id.IsEmpty)
            throw new ArgumentException("프로젝트 ID는 비어 있을 수 없습니다.", nameof(id));
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("프로젝트 이름을 입력해야 합니다.", nameof(name));
        if (createdUtc.Offset != TimeSpan.Zero || modifiedUtc.Offset != TimeSpan.Zero || modifiedUtc < createdUtc)
            throw new ArgumentException("프로젝트 시각은 UTC이며 수정 시각은 생성 시각보다 앞설 수 없습니다.");

        Id = id;
        SchemaVersion = schemaVersion;
        Revision = revision;
        Name = name.Trim();
        CreatedUtc = createdUtc;
        ModifiedUtc = modifiedUtc;
        SourceModel = sourceModel ?? throw new ArgumentNullException(nameof(sourceModel));
        WorkingCoordinateSystem = workingCoordinateSystem ?? throw new ArgumentNullException(nameof(workingCoordinateSystem));
        FaceReferences = Copy(faceReferences, nameof(faceReferences));
        HeatingRegions = Copy(heatingRegions, nameof(heatingRegions));
        WireSpecification = wireSpecification;
        PlacementConstraints = placementConstraints;
        HeatingPaths = Copy(heatingPaths, nameof(heatingPaths));
        Circuits = Copy(circuits, nameof(circuits));
        ConnectionPoints = Copy(connectionPoints, nameof(connectionPoints));
        ConnectionPaths = Copy(connectionPaths, nameof(connectionPaths));
        Rules = rules ?? DesignRuleSet.Initial;
    }

    /// <summary>ID와 존재 참조만 검사하며 기하 거리·깊이·전기 계산은 수행하지 않습니다.</summary>
    public ValidationReport ValidateReferences()
    {
        var issues = new List<ValidationIssue>();
        string rule = Rules.ValidationRuleVersion;
        if (FaceReferences.Any(face => face.SourceModelSha256 != SourceModel.SourceSha256))
            AddIssue("FaceSourceModelMismatch", "Validation.FaceSourceModelMismatch", FaceReferences
                .Where(face => face.SourceModelSha256 != SourceModel.SourceSha256).Select(face => face.Id.Value));

        CheckUnique(FaceReferences, face => face.Id.Value, "DuplicateFaceReferenceId");
        CheckUnique(HeatingRegions, region => region.Id.Value, "DuplicateRegionId");
        CheckUnique(HeatingPaths, path => path.Id.Value, "DuplicateHeatingPathId");
        CheckUnique(Circuits, circuit => circuit.Id.Value, "DuplicateCircuitId");
        CheckUnique(ConnectionPoints, point => point.Id.Value, "DuplicateConnectionPointId");
        CheckUnique(ConnectionPaths, path => path.Id.Value, "DuplicateConnectionPathId");

        BoundaryLoop[] allBoundaryLoops = HeatingRegions.SelectMany(region =>
            region.OriginalBoundaryLoops.Concat(region.InclusionLoops)
                .Concat(region.ExclusionLoops).Concat(region.EffectiveBoundaryLoops)).ToArray();
        CheckUnique(allBoundaryLoops, loop => loop.Id.Value, "DuplicateBoundaryLoopId");
        CheckUnique(allBoundaryLoops.SelectMany(loop => loop.Segments), segment => segment.Id.Value, "DuplicateBoundarySegmentId");

        var faceIds = FaceReferences.Select(face => face.Id).ToHashSet();
        if (PlacementConstraints is { } projectConstraints)
        {
            ValidateAnchor(projectConstraints.StartAnchor, Id.Value);
            ValidateAnchor(projectConstraints.EndAnchor, Id.Value);
            var effectiveSegmentIds = HeatingRegions.SelectMany(region => region.EffectiveBoundaryLoops)
                .SelectMany(loop => loop.Segments).Select(segment => segment.Id).ToHashSet();
            if (projectConstraints.ExitSide.Ranges.Any(range => !effectiveSegmentIds.Contains(range.BoundarySegmentId)))
                AddIssue("MissingProjectExitSide", "Validation.MissingProjectExitSide", [Id.Value]);
        }

        foreach (HeatingRegion region in HeatingRegions)
        {
            foreach (FaceRefId faceId in region.FaceReferences.Where(faceId => !faceIds.Contains(faceId)))
                AddIssue("MissingFaceReference", "Validation.MissingFaceReference", [region.Id.Value, faceId.Value]);
        }

        foreach (HeatingPath path in HeatingPaths)
        {
            HeatingRegion? generationRegion = HeatingRegions.FirstOrDefault(region => region.Id == path.GenerationSource.RegionId);
            if (generationRegion is null)
                AddIssue("MissingHeatingRegion", "Validation.MissingHeatingRegion", [path.Id.Value, path.GenerationSource.RegionId.Value]);
            else
            {
                PlacementConstraints pathConstraints = path.GenerationSource.PlacementConstraints;
                ValidateAnchor(pathConstraints.StartAnchor, path.Id.Value);
                ValidateAnchor(pathConstraints.EndAnchor, path.Id.Value);
                HashSet<BoundarySegmentId> expectedExitSegments = generationRegion.ExitSide.Ranges
                    .Select(range => range.BoundarySegmentId).ToHashSet();
                if (pathConstraints.ExitSide.Ranges.Any(range => !expectedExitSegments.Contains(range.BoundarySegmentId)))
                    AddIssue("HeatingPathExitSideMismatch", "Validation.HeatingPathExitSideMismatch", [path.Id.Value, generationRegion.Id.Value]);
            }
        }

        CheckUnique(HeatingPaths.SelectMany(path => path.ControlPoints), point => point.Id.Value, "DuplicateControlPointId");
        var segmentGroups = HeatingPaths.SelectMany(path => path.Segments)
            .GroupBy(segment => segment.Id)
            .ToArray();
        foreach (IGrouping<HeatingPathSegmentId, HeatingPathSegment> group in segmentGroups.Where(group => group.Count() > 1))
            AddIssue("DuplicateHeatingPathSegmentId", "Validation.DuplicateHeatingPathSegmentId", [group.Key.Value]);
        var segments = segmentGroups.ToDictionary(group => group.Key, group => group.First());

        var circuitGroups = Circuits.GroupBy(circuit => circuit.Id).ToArray();
        var circuits = circuitGroups.ToDictionary(group => group.Key, group => group.First());
        var connectionGroups = ConnectionPoints.GroupBy(point => point.Id).ToArray();
        var connectionPoints = connectionGroups.ToDictionary(group => group.Key, group => group.First());

        var circuitSegmentReferences = Circuits.SelectMany(circuit => circuit.Segments
            .Select(reference => (Circuit: circuit, Reference: reference)))
            .GroupBy(item => item.Reference.SegmentId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (HeatingPathSegment segment in segments.Values)
        {
            int ownershipCount = circuitSegmentReferences.TryGetValue(segment.Id, out var owners) ? owners.Length : 0;
            if (segment.IsActive && ownershipCount == 0)
                AddIssue("UnassignedActiveHeatingPathSegment", "Validation.UnassignedActiveHeatingPathSegment", [segment.Id.Value]);
            if (!segment.IsActive && ownershipCount > 0)
                AddIssue("InactiveHeatingPathSegmentAssigned", "Validation.InactiveHeatingPathSegmentAssigned", [segment.Id.Value]);
            if (ownershipCount > 1)
                AddIssue("HeatingPathSegmentInMultipleCircuits", "Validation.HeatingPathSegmentInMultipleCircuits", [segment.Id.Value]);
        }

        foreach (Circuit circuit in Circuits)
        {
            foreach (CircuitSegmentReference reference in circuit.Segments)
            {
                if (!segments.TryGetValue(reference.SegmentId, out HeatingPathSegment? segment))
                {
                    AddIssue("MissingHeatingPathSegment", "Validation.MissingHeatingPathSegment", [circuit.Id.Value, reference.SegmentId.Value]);
                    continue;
                }
                if (!segment.IsActive)
                    AddIssue("InactiveHeatingPathSegmentAssigned", "Validation.InactiveHeatingPathSegmentAssigned", [circuit.Id.Value, segment.Id.Value]);
            }

            ValidateCircuitConnections(circuit, segments, connectionPoints, issues, rule);
        }

        var connectionPathGroups = ConnectionPaths.GroupBy(path => path.ConnectionPointId).ToArray();
        foreach (IGrouping<ConnectionPointId, ConnectionPath> group in connectionPathGroups.Where(group => group.Count() > 1))
            AddIssue("DuplicateConnectionPathForPoint", "Validation.DuplicateConnectionPathForPoint", [group.Key.Value]);

        foreach (ConnectionPoint point in ConnectionPoints)
        {
            if (!circuits.TryGetValue(point.CircuitId, out Circuit? circuit))
                AddIssue("MissingConnectionPointCircuit", "Validation.MissingConnectionPointCircuit", [point.Id.Value, point.CircuitId.Value]);
            else
            {
                ConnectionPointId expected = point.Role == ConnectionPointRole.Start
                    ? circuit.StartConnectionPointId
                    : circuit.EndConnectionPointId;
                if (point.Id != expected)
                    AddIssue("ConnectionPointCircuitRoleMismatch", "Validation.ConnectionPointCircuitRoleMismatch", [point.Id.Value, circuit.Id.Value]);
            }

            if (!segments.ContainsKey(point.HeatingPathSegmentId))
                AddIssue("MissingConnectionHeatingSegment", "Validation.MissingConnectionHeatingSegment", [point.Id.Value, point.HeatingPathSegmentId.Value]);

            if (!connectionPathGroups.Any(group => group.Key == point.Id))
                AddIssue("MissingConnectionPath", "Validation.MissingConnectionPath", [point.Id.Value]);
        }

        foreach (ConnectionPath path in ConnectionPaths)
        {
            if (!connectionPoints.ContainsKey(path.ConnectionPointId))
                AddIssue("MissingConnectionPathPoint", "Validation.MissingConnectionPathPoint", [path.Id.Value, path.ConnectionPointId.Value]);
        }
        CheckUnique(ConnectionPaths.SelectMany(path => path.Segments), segment => segment.Id.Value, "DuplicateConnectionPathSegmentId");

        return new ValidationReport(issues);

        void AddIssue(string code, string messageKey, IEnumerable<Guid> entityIds)
        {
            issues.Add(new ValidationIssue(code, ValidationSeverity.Error, messageKey, entityIds, rule));
        }

        void CheckUnique<T>(IEnumerable<T> items, Func<T, Guid> idSelector, string code)
        {
            foreach (IGrouping<Guid, T> group in items.GroupBy(idSelector).Where(group => group.Count() > 1))
                AddIssue(code, $"Validation.{code}", [group.Key]);
        }

        void ValidateAnchor(SurfaceAnchor anchor, Guid ownerId)
        {
            if (!faceIds.Contains(anchor.FaceReferenceId))
                AddIssue("MissingAnchorFaceReference", "Validation.MissingAnchorFaceReference", [ownerId, anchor.FaceReferenceId.Value]);
        }
    }

    private static void ValidateCircuitConnections(
        Circuit circuit,
        IReadOnlyDictionary<HeatingPathSegmentId, HeatingPathSegment> segments,
        IReadOnlyDictionary<ConnectionPointId, ConnectionPoint> connectionPoints,
        ICollection<ValidationIssue> issues,
        string ruleVersion)
    {
        CircuitSegmentReference? firstReference = null;
        CircuitSegmentReference? lastReference = null;
        CircuitSegmentReference? previousReference = null;
        foreach (CircuitSegmentReference reference in circuit.Segments)
        {
            if (!segments.TryGetValue(reference.SegmentId, out HeatingPathSegment? segment))
                continue;

            ControlPointId currentStart = reference.IsReversed ? segment.EndControlPointId : segment.StartControlPointId;
            ControlPointId currentEnd = reference.IsReversed ? segment.StartControlPointId : segment.EndControlPointId;
            if (previousReference is { } previous && segments.TryGetValue(previous.SegmentId, out HeatingPathSegment? previousSegment))
            {
                ControlPointId previousEnd = previous.IsReversed ? previousSegment.StartControlPointId : previousSegment.EndControlPointId;
                if (previousEnd != currentStart)
                    AddIssue("CircuitPathDiscontinuity", "Validation.CircuitPathDiscontinuity", [circuit.Id.Value, previous.SegmentId.Value, segment.Id.Value]);
            }

            firstReference ??= reference;
            lastReference = reference;
            previousReference = reference;
        }

        CheckConnection(circuit.StartConnectionPointId, ConnectionPointRole.Start, firstReference, true);
        CheckConnection(circuit.EndConnectionPointId, ConnectionPointRole.End, lastReference, false);

        void CheckConnection(
            ConnectionPointId connectionPointId,
            ConnectionPointRole role,
            CircuitSegmentReference? endpointReference,
            bool isStart)
        {
            if (!connectionPoints.TryGetValue(connectionPointId, out ConnectionPoint? point))
            {
                AddIssue("MissingCircuitConnectionPoint", "Validation.MissingCircuitConnectionPoint", [circuit.Id.Value, connectionPointId.Value]);
                return;
            }
            if (point.CircuitId != circuit.Id || point.Role != role)
                AddIssue("CircuitConnectionPointMismatch", "Validation.CircuitConnectionPointMismatch", [circuit.Id.Value, point.Id.Value]);

            if (!endpointReference.HasValue || !segments.TryGetValue(endpointReference.Value.SegmentId, out HeatingPathSegment? endpointSegment))
                return;
            bool isReversed = endpointReference.Value.IsReversed;
            ControlPointId expected = isStart
                ? (isReversed ? endpointSegment.EndControlPointId : endpointSegment.StartControlPointId)
                : (isReversed ? endpointSegment.StartControlPointId : endpointSegment.EndControlPointId);
            if (point.HeatingPathSegmentId != endpointSegment.Id || point.ControlPointId != expected)
                AddIssue("CircuitConnectionEndpointMismatch", "Validation.CircuitConnectionEndpointMismatch", [circuit.Id.Value, point.Id.Value, endpointSegment.Id.Value]);
        }

        void AddIssue(string code, string messageKey, IEnumerable<Guid> entityIds)
        {
            issues.Add(new ValidationIssue(code, ValidationSeverity.Error, messageKey, entityIds, ruleVersion));
        }
    }

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, string parameterName)
    {
        T[] copied = values?.ToArray() ?? throw new ArgumentNullException(parameterName);
        if (copied.Any(value => value is null))
            throw new ArgumentException("확정 프로젝트 목록은 null 항목을 포함할 수 없습니다.", parameterName);

        return new ReadOnlyCollection<T>(copied);
    }
}
