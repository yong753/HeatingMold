using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Regions;

/// <summary>선택 Face topology를 검증하고 영속 발열 영역 후보를 만듭니다.</summary>
public sealed class RegionBuilder
{
    private readonly BoundaryClassifier _boundaryClassifier;

    /// <summary>Native 경계의 포함 깊이를 재검사할 분류기를 연결합니다.</summary>
    public RegionBuilder(BoundaryClassifier? boundaryClassifier = null)
    {
        _boundaryClassifier = boundaryClassifier ?? new BoundaryClassifier();
    }

    /// <summary>선택 Face와 실제 공유 경계 루프, 확정 출구 측면을 새 영역으로 묶습니다.</summary>
    public OperationResult<HeatingRegion> Build(
        DesignProject project,
        RegionId regionId,
        IEnumerable<FaceRefId> selectedFaces,
        GeometryQueryResult<SelectedTopology> topologyResult,
        ExitSideReference selectedExitSide)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(topologyResult);
        ArgumentNullException.ThrowIfNull(selectedExitSide);
        if (regionId.IsEmpty)
            throw new ArgumentException("발열 영역 ID는 비어 있을 수 없습니다.", nameof(regionId));

        FaceRefId[] faces = selectedFaces?.ToArray()
            ?? throw new ArgumentNullException(nameof(selectedFaces));
        if (faces.Length == 0 || faces.Any(face => face.IsEmpty) || faces.Distinct().Count() != faces.Length)
            return Failure(project, "InvalidSelectedFaces", "영역 선택에는 중복 없는 Face 참조가 하나 이상 필요합니다.");

        HashSet<FaceRefId> knownFaces = project.FaceReferences.Select(face => face.Id).ToHashSet();
        FaceRefId[] missingFaces = faces.Where(face => !knownFaces.Contains(face)).ToArray();
        if (missingFaces.Length > 0)
        {
            return Failure(project, "MissingSelectedFaceReference",
                "선택 Face가 현재 원본 모델의 영속 참조에 없습니다.", missingFaces.Select(face => face.Value).ToArray());
        }

        if (topologyResult.Status != GeometryQueryStatus.Success || topologyResult.Value is null)
        {
            if (topologyResult.Issues.Count > 0)
                return OperationResult<HeatingRegion>.Failure(topologyResult.Issues);
            return Failure(project, "SelectedTopologyUnavailable",
                $"선택 Face topology 질의 상태가 {topologyResult.Status}입니다.", faces.Select(face => face.Value).ToArray());
        }

        TopologyComponent[] components = topologyResult.Value.Components.ToArray();
        FaceRefId[] topologyFaces = components.SelectMany(component => component.FaceReferences).ToArray();
        if (topologyFaces.Distinct().Count() != topologyFaces.Length ||
            !faces.ToHashSet().SetEquals(topologyFaces))
        {
            return Failure(project, "TopologyFaceSetMismatch",
                "Native 연결 성분의 Face 집합이 사용자가 선택한 Face 집합과 다릅니다.", faces.Concat(topologyFaces).Select(face => face.Value).ToArray());
        }

        BoundaryLoop[] originalLoops = components.SelectMany(component => component.BoundaryLoops).ToArray();
        if (originalLoops.Length == 0 || originalLoops.Select(loop => loop.Id).Distinct().Count() != originalLoops.Length ||
            originalLoops.SelectMany(loop => loop.Segments).Select(segment => segment.Id).Distinct().Count() !=
            originalLoops.Sum(loop => loop.Segments.Count))
        {
            return Failure(project, "InvalidBoundaryTopology",
                "선택 topology에 경계가 없거나 안정 경계 ID가 중복됩니다.", faces.Select(face => face.Value).ToArray());
        }

        foreach (TopologyComponent component in components)
        {
            if (component.BoundaryLoops.Count == 0)
                return Failure(project, "MissingComponentBoundary", "선택 연결 성분에 실제 경계 루프가 없습니다.", component.FaceReferences.Select(face => face.Value).ToArray());

            OperationResult<IReadOnlyList<ClassifiedBoundaryRing>> classification = _boundaryClassifier.Classify(
                component.BoundaryLoops.Select(loop => loop.Vertices),
                project.Rules.ValidationRuleVersion);
            if (!classification.IsSuccess)
                return OperationResult<HeatingRegion>.Failure(classification.Issues);

            ClassifiedBoundaryRing[] classifiedLoops = classification.GetValueOrThrow().ToArray();
            for (int index = 0; index < classifiedLoops.Length; index++)
            {
                if (classifiedLoops[index].IsHole != component.BoundaryLoops[index].IsHole)
                {
                    return Failure(project, "BoundaryHoleClassificationMismatch",
                        "Native가 분류한 hole 상태와 경계 포함 깊이가 다릅니다.",
                        component.FaceReferences.Select(face => face.Value).ToArray());
                }
            }
            if (!classifiedLoops.Any(loop => !loop.IsHole))
                return Failure(project, "MissingOuterBoundary", "연결 성분에 바깥 경계가 없습니다.", component.FaceReferences.Select(face => face.Value).ToArray());
        }

        var effectiveIdMap = new Dictionary<BoundarySegmentId, BoundarySegmentId>();
        BoundaryLoop[] effectiveLoops = originalLoops.Select(loop => CloneLoop(loop, effectiveIdMap)).ToArray();
        BoundarySegmentRange[] remappedRanges = new BoundarySegmentRange[selectedExitSide.Ranges.Count];
        for (int index = 0; index < selectedExitSide.Ranges.Count; index++)
        {
            BoundarySegmentRange sourceRange = selectedExitSide.Ranges[index];
            if (!effectiveIdMap.TryGetValue(sourceRange.BoundarySegmentId, out BoundarySegmentId effectiveSegmentId))
            {
                return Failure(project, "ExitSideOutsideSelectedBoundary",
                    "출구 측면이 선택 topology 경계에 속하지 않습니다.", sourceRange.BoundarySegmentId.Value);
            }

            remappedRanges[index] = new BoundarySegmentRange(
                effectiveSegmentId,
                sourceRange.StartParameter,
                sourceRange.EndParameter);
        }

        var region = new HeatingRegion(
            regionId,
            faces,
            originalLoops,
            [],
            [],
            effectiveLoops,
            new ExitSideReference(remappedRanges));
        IEnumerable<ValidationIssue> warnings = topologyResult.Issues;
        if (components.Length > 1)
        {
            warnings = warnings.Append(new ValidationIssue(
                "MultipleRegionComponents",
                ValidationSeverity.Warning,
                "Design.MultipleRegionComponents",
                [regionId.Value],
                project.Rules.ValidationRuleVersion,
                evidence: ["영역은 보존되지만 하나의 연속 경로 생성에는 단일 연결 성분이 필요합니다."]));
        }
        return OperationResult<HeatingRegion>.Success(region, warnings);
    }

    private static BoundaryLoop CloneLoop(
        BoundaryLoop source,
        IDictionary<BoundarySegmentId, BoundarySegmentId> idMap)
    {
        BoundarySegment[] segments = source.Segments.Select(segment =>
        {
            BoundarySegmentId newId = BoundarySegmentId.New();
            idMap.Add(segment.Id, newId);
            return new BoundarySegment(newId, segment.Start, segment.End, segment.SourceFaceReferenceId);
        }).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, source.IsHole);
    }

    private static OperationResult<HeatingRegion> Failure(
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
        return OperationResult<HeatingRegion>.Failure([issue]);
    }
}
