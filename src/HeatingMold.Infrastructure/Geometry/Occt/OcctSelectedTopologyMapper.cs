using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>Native 선택 topology를 안정 Face ID와 작업 평면 경계로 변환합니다.</summary>
internal static class OcctSelectedTopologyMapper
{
    private const string RuleVersion = "occt-geometry-query-v1";
    private const double ProjectionClosureToleranceMm = 1e-8;

    /// <summary>Native payload의 Face 집합·인접 그래프·닫힌 루프를 검증한 뒤 공통 계약으로 바꿉니다.</summary>
    public static GeometryQueryResult<SelectedTopology> Map(
        OcctSelectedTopologyResult nativeResult,
        IReadOnlyCollection<FaceRefId> selectedFaceReferences,
        IReadOnlyDictionary<int, FaceRefId> faceReferencesByNativeIndex,
        ProjectionFrame projectionFrame,
        ModelRevision modelRevision)
    {
        ArgumentNullException.ThrowIfNull(nativeResult);
        ArgumentNullException.ThrowIfNull(selectedFaceReferences);
        ArgumentNullException.ThrowIfNull(faceReferencesByNativeIndex);
        ArgumentNullException.ThrowIfNull(projectionFrame);

        if (nativeResult.Status != OcctSelectedTopologyStatus.Success)
            return MapFailure(nativeResult.Status, selectedFaceReferences);

        try
        {
            return MapSuccess(
                nativeResult,
                selectedFaceReferences,
                faceReferencesByNativeIndex,
                projectionFrame,
                modelRevision);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException or OverflowException)
        {
            return Unresolved(
                "InvalidSelectedBoundaryTopology",
                $"Native 경계를 공통 선택 topology로 바꾸지 못했습니다: {exception.Message}",
                selectedFaceReferences.Select(face => face.Value));
        }
    }

    private static GeometryQueryResult<SelectedTopology> MapSuccess(
        OcctSelectedTopologyResult nativeResult,
        IReadOnlyCollection<FaceRefId> selectedFaceReferences,
        IReadOnlyDictionary<int, FaceRefId> faceReferencesByNativeIndex,
        ProjectionFrame projectionFrame,
        ModelRevision modelRevision)
    {
        if (nativeResult.Components.Count == 0)
            throw new InvalidOperationException("성공 상태에 연결 성분이 없습니다.");

        HashSet<FaceRefId> requestedFaces = selectedFaceReferences.ToHashSet();
        var mappedComponents = new List<TopologyComponent>(nativeResult.Components.Count);
        var componentByNativeFace = new Dictionary<int, int>();

        for (int componentIndex = 0; componentIndex < nativeResult.Components.Count; componentIndex++)
        {
            OcctTopologyComponent nativeComponent = nativeResult.Components[componentIndex];
            FaceRefId[] componentFaces = nativeComponent.NativeFaceIndices
                .Select(nativeIndex => ResolveFace(nativeIndex, requestedFaces, faceReferencesByNativeIndex))
                .ToArray();
            if (nativeComponent.BoundaryLoops.Count == 0)
                throw new InvalidOperationException("연결 성분에 닫힌 경계 루프가 없습니다.");

            foreach (int nativeFaceIndex in nativeComponent.NativeFaceIndices)
            {
                if (!componentByNativeFace.TryAdd(nativeFaceIndex, componentIndex))
                    throw new InvalidOperationException("Native Face가 둘 이상의 연결 성분에 중복 포함되었습니다.");
            }

            IReadOnlyList<BoundaryLoop> loops = MapBoundaryLoops(
                nativeComponent,
                requestedFaces,
                faceReferencesByNativeIndex,
                projectionFrame);
            mappedComponents.Add(new TopologyComponent(componentFaces, loops));
        }

        FaceRefId[] mappedFaces = mappedComponents.SelectMany(component => component.FaceReferences).ToArray();
        if (mappedFaces.Distinct().Count() != mappedFaces.Length || !requestedFaces.SetEquals(mappedFaces))
            throw new InvalidOperationException("Native 연결 성분의 Face 집합이 요청한 선택 Face 집합과 다릅니다.");

        FaceAdjacency[] adjacencies = MapAdjacencies(
            nativeResult.Adjacencies,
            requestedFaces,
            faceReferencesByNativeIndex,
            componentByNativeFace);
        ValidateComponentGraph(nativeResult.Components, nativeResult.Adjacencies, componentByNativeFace);

        return GeometryQueryResult<SelectedTopology>.Succeeded(
            new SelectedTopology(mappedComponents, adjacencies, modelRevision));
    }

    private static IReadOnlyList<BoundaryLoop> MapBoundaryLoops(
        OcctTopologyComponent component,
        IReadOnlySet<FaceRefId> requestedFaces,
        IReadOnlyDictionary<int, FaceRefId> faceReferencesByNativeIndex,
        ProjectionFrame projectionFrame)
    {
        var projectedLoops = new List<ProjectedLoop>(component.BoundaryLoops.Count);
        foreach (OcctTopologyBoundaryLoop nativeLoop in component.BoundaryLoops)
        {
            if (!nativeLoop.IsClosed)
                throw new InvalidOperationException("Native 경계 루프가 닫혀 있지 않습니다.");

            Vec2[] vertices = nativeLoop.ModelVertices.Select(projectionFrame.Project).ToArray();
            FaceRefId[] sourceFaces = nativeLoop.NativeSegmentFaceIndices
                .Select(nativeIndex => ResolveFace(nativeIndex, requestedFaces, faceReferencesByNativeIndex))
                .ToArray();
            if (vertices.Length != sourceFaces.Length)
                throw new InvalidOperationException("경계 정점과 원본 Face 대응 수가 다릅니다.");

            ValidateSimpleLoop(vertices);
            projectedLoops.Add(new ProjectedLoop(vertices, sourceFaces));
        }

        ValidateLoopRelationships(projectedLoops);
        return projectedLoops.Select((loop, index) => CreateBoundaryLoop(
            loop,
            isHole: CountContainingLoops(loop.Vertices[0], projectedLoops, index) % 2 == 1)).ToArray();
    }

    private static BoundaryLoop CreateBoundaryLoop(ProjectedLoop projectedLoop, bool isHole)
    {
        BoundarySegment[] segments = new BoundarySegment[projectedLoop.Vertices.Length];
        for (int index = 0; index < projectedLoop.Vertices.Length; index++)
        {
            Vec2 start = projectedLoop.Vertices[index];
            Vec2 end = projectedLoop.Vertices[(index + 1) % projectedLoop.Vertices.Length];
            segments[index] = new BoundarySegment(
                BoundarySegmentId.New(),
                start,
                end,
                projectedLoop.SourceFaces[index]);
        }

        return new BoundaryLoop(
            BoundaryLoopId.New(),
            segments,
            isHole,
            ProjectionClosureToleranceMm);
    }

    private static FaceAdjacency[] MapAdjacencies(
        IReadOnlyList<OcctTopologyAdjacency> nativeAdjacencies,
        IReadOnlySet<FaceRefId> requestedFaces,
        IReadOnlyDictionary<int, FaceRefId> faceReferencesByNativeIndex,
        IReadOnlyDictionary<int, int> componentByNativeFace)
    {
        var seenPairs = new HashSet<(int First, int Second)>();
        var result = new List<FaceAdjacency>(nativeAdjacencies.Count);
        foreach (OcctTopologyAdjacency adjacency in nativeAdjacencies)
        {
            int firstIndex = adjacency.FirstNativeFaceIndex;
            int secondIndex = adjacency.SecondNativeFaceIndex;
            if (firstIndex < 0 || secondIndex < 0 || firstIndex == secondIndex)
                throw new InvalidOperationException("Native 인접 관계에 유효하지 않은 Face 쌍이 있습니다.");

            (int First, int Second) pair = firstIndex < secondIndex
                ? (firstIndex, secondIndex)
                : (secondIndex, firstIndex);
            if (!seenPairs.Add(pair))
                throw new InvalidOperationException("Native 인접 관계가 중복되었습니다.");

            FaceRefId first = ResolveFace(firstIndex, requestedFaces, faceReferencesByNativeIndex);
            FaceRefId second = ResolveFace(secondIndex, requestedFaces, faceReferencesByNativeIndex);
            if (!componentByNativeFace.TryGetValue(firstIndex, out int firstComponent) ||
                !componentByNativeFace.TryGetValue(secondIndex, out int secondComponent) ||
                firstComponent != secondComponent)
            {
                throw new InvalidOperationException("공유 Edge 인접 쌍이 서로 다른 연결 성분에 배치되었습니다.");
            }

            result.Add(new FaceAdjacency(first, second));
        }

        return result.ToArray();
    }

    private static void ValidateComponentGraph(
        IReadOnlyList<OcctTopologyComponent> components,
        IReadOnlyList<OcctTopologyAdjacency> adjacencies,
        IReadOnlyDictionary<int, int> componentByNativeFace)
    {
        foreach (OcctTopologyComponent component in components)
        {
            int[] faces = component.NativeFaceIndices.ToArray();
            if (faces.Length == 1)
                continue;

            var visited = new HashSet<int> { faces[0] };
            var pending = new Queue<int>();
            pending.Enqueue(faces[0]);
            while (pending.TryDequeue(out int current))
            {
                foreach (OcctTopologyAdjacency adjacency in adjacencies)
                {
                    int adjacent = adjacency.FirstNativeFaceIndex == current
                        ? adjacency.SecondNativeFaceIndex
                        : adjacency.SecondNativeFaceIndex == current
                            ? adjacency.FirstNativeFaceIndex
                            : -1;
                    if (adjacent >= 0 && componentByNativeFace[adjacent] == componentByNativeFace[current] && visited.Add(adjacent))
                        pending.Enqueue(adjacent);
                }
            }

            if (visited.Count != faces.Length)
                throw new InvalidOperationException("Native 연결 성분의 인접 그래프가 서로 연결되지 않았습니다.");
        }
    }

    private static FaceRefId ResolveFace(
        int nativeFaceIndex,
        IReadOnlySet<FaceRefId> requestedFaces,
        IReadOnlyDictionary<int, FaceRefId> faceReferencesByNativeIndex)
    {
        if (!faceReferencesByNativeIndex.TryGetValue(nativeFaceIndex, out FaceRefId faceReferenceId) ||
            !requestedFaces.Contains(faceReferenceId))
        {
            throw new InvalidOperationException("경계의 Native Face를 요청한 안정 Face 참조에 연결하지 못했습니다.");
        }

        return faceReferenceId;
    }

    private static void ValidateSimpleLoop(IReadOnlyList<Vec2> vertices)
    {
        if (vertices.Count < 3)
            throw new InvalidOperationException("투영된 경계 루프에 정점이 세 개 미만입니다.");

        for (int first = 0; first < vertices.Count; first++)
        {
            Vec2 firstStart = vertices[first];
            Vec2 firstEnd = vertices[(first + 1) % vertices.Count];
            if (firstStart.DistanceTo(firstEnd) <= ProjectionClosureToleranceMm)
                throw new InvalidOperationException("투영된 경계에 길이가 0에 가까운 구간이 있습니다.");

            for (int second = first + 1; second < vertices.Count; second++)
            {
                if (second == first + 1 || first == 0 && second == vertices.Count - 1)
                    continue;

                Vec2 secondStart = vertices[second];
                Vec2 secondEnd = vertices[(second + 1) % vertices.Count];
                if (SegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd))
                    throw new InvalidOperationException("투영된 경계 루프가 자기 자신과 교차합니다.");
            }
        }
    }

    private static void ValidateLoopRelationships(IReadOnlyList<ProjectedLoop> loops)
    {
        for (int firstLoop = 0; firstLoop < loops.Count; firstLoop++)
        {
            for (int secondLoop = firstLoop + 1; secondLoop < loops.Count; secondLoop++)
            {
                IReadOnlyList<Vec2> first = loops[firstLoop].Vertices;
                IReadOnlyList<Vec2> second = loops[secondLoop].Vertices;
                for (int firstEdge = 0; firstEdge < first.Count; firstEdge++)
                {
                    Vec2 firstStart = first[firstEdge];
                    Vec2 firstEnd = first[(firstEdge + 1) % first.Count];
                    for (int secondEdge = 0; secondEdge < second.Count; secondEdge++)
                    {
                        if (SegmentsIntersect(
                            firstStart,
                            firstEnd,
                            second[secondEdge],
                            second[(secondEdge + 1) % second.Count]))
                        {
                            throw new InvalidOperationException("선택 경계 루프가 서로 교차하거나 맞닿습니다.");
                        }
                    }
                }
            }
        }
    }

    private static int CountContainingLoops(Vec2 point, IReadOnlyList<ProjectedLoop> loops, int excludedIndex)
    {
        int depth = 0;
        for (int index = 0; index < loops.Count; index++)
        {
            if (index != excludedIndex && ContainsPoint(loops[index].Vertices, point))
                depth++;
        }

        return depth;
    }

    private static bool ContainsPoint(IReadOnlyList<Vec2> polygon, Vec2 point)
    {
        bool inside = false;
        for (int currentIndex = 0, previousIndex = polygon.Count - 1;
             currentIndex < polygon.Count;
             previousIndex = currentIndex++)
        {
            Vec2 current = polygon[currentIndex];
            Vec2 previous = polygon[previousIndex];
            if ((current.Y > point.Y) != (previous.Y > point.Y) &&
                point.X < (previous.X - current.X) * (point.Y - current.Y) / (previous.Y - current.Y) + current.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool SegmentsIntersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double first = Cross(a, b, c);
        double second = Cross(a, b, d);
        double third = Cross(c, d, a);
        double fourth = Cross(c, d, b);
        const double tolerance = 1e-10;

        if (Math.Abs(first) <= tolerance && IsOnSegment(a, b, c, tolerance)) return true;
        if (Math.Abs(second) <= tolerance && IsOnSegment(a, b, d, tolerance)) return true;
        if (Math.Abs(third) <= tolerance && IsOnSegment(c, d, a, tolerance)) return true;
        if (Math.Abs(fourth) <= tolerance && IsOnSegment(c, d, b, tolerance)) return true;

        return (first > 0) != (second > 0) && (third > 0) != (fourth > 0);
    }

    private static double Cross(Vec2 start, Vec2 end, Vec2 point) =>
        (end.X - start.X) * (point.Y - start.Y) - (end.Y - start.Y) * (point.X - start.X);

    private static bool IsOnSegment(Vec2 start, Vec2 end, Vec2 point, double tolerance) =>
        point.X >= Math.Min(start.X, end.X) - tolerance &&
        point.X <= Math.Max(start.X, end.X) + tolerance &&
        point.Y >= Math.Min(start.Y, end.Y) - tolerance &&
        point.Y <= Math.Max(start.Y, end.Y) + tolerance;

    private static GeometryQueryResult<SelectedTopology> MapFailure(
        OcctSelectedTopologyStatus status,
        IEnumerable<FaceRefId> selectedFaceReferences) => status switch
    {
        OcctSelectedTopologyStatus.StaleContext => new GeometryQueryResult<SelectedTopology>(
            GeometryQueryStatus.StaleGeometryContext,
            default,
            [CreateIssue("StaleGeometryContext", "선택 topology 질의 context가 현재 모델 revision에 속하지 않습니다.", selectedFaceReferences.Select(face => face.Value))]),
        OcctSelectedTopologyStatus.GeometryFailure => new GeometryQueryResult<SelectedTopology>(
            GeometryQueryStatus.Failure,
            default,
            [CreateIssue("OcctSelectedTopologyFailed", "OCCT 선택 topology 처리에서 기하 오류가 발생했습니다.", selectedFaceReferences.Select(face => face.Value))]),
        OcctSelectedTopologyStatus.InvalidFace => Unresolved(
            "InvalidNativeSelectedFace",
            "Native 선택 목록에 현재 문서에 없는 Face 번호가 있습니다.",
            selectedFaceReferences.Select(face => face.Value)),
        OcctSelectedTopologyStatus.InvalidTopology => Unresolved(
            "InvalidBoundaryTopology",
            "선택 면의 경계가 열려 있거나 분기되어 닫힌 연결 topology를 만들 수 없습니다.",
            selectedFaceReferences.Select(face => face.Value)),
        OcctSelectedTopologyStatus.InvalidInput => Unresolved(
            "InvalidSelectedTopologyRequest",
            "Native 선택 topology 질의 입력이 유효하지 않습니다.",
            selectedFaceReferences.Select(face => face.Value)),
        _ => Unresolved(
            "UnknownSelectedTopologyStatus",
            "OCCT가 알 수 없는 선택 topology 상태를 반환했습니다.",
            selectedFaceReferences.Select(face => face.Value))
    };

    private static GeometryQueryResult<SelectedTopology> Unresolved(
        string code,
        string evidence,
        IEnumerable<Guid> entityIds) => GeometryQueryResult<SelectedTopology>.Unresolved(
        [CreateIssue(code, evidence, entityIds)]);

    private static ValidationIssue CreateIssue(string code, string evidence, IEnumerable<Guid> entityIds) => new(
        code,
        ValidationSeverity.Error,
        $"Geometry.{code}",
        entityIds,
        RuleVersion,
        evidence: [evidence]);

    private sealed record ProjectedLoop(Vec2[] Vertices, FaceRefId[] SourceFaces);
}
