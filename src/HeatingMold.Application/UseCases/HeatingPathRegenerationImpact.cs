using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.HeatingPath;

namespace HeatingMold.Application.UseCases;

/// <summary>경로를 재생성할 때 함께 교체될 기존 경로·회로·연결부를 나타냅니다.</summary>
public sealed class HeatingPathRegenerationImpact
{
    /// <summary>재생성 후보와 같은 트랜잭션에서 교체될 경로 ID입니다.</summary>
    public IReadOnlyList<HeatingPathId> HeatingPathIds { get; }

    /// <summary>영향 경로 조각을 참조해 함께 교체될 회로 ID입니다.</summary>
    public IReadOnlyList<CircuitId> CircuitIds { get; }

    /// <summary>영향 회로가 소유해 함께 교체될 표면 연결부 ID입니다.</summary>
    public IReadOnlyList<ConnectionPointId> ConnectionPointIds { get; }

    /// <summary>영향 연결부가 소유해 함께 교체될 연결 경로 ID입니다.</summary>
    public IReadOnlyList<ConnectionPathId> ConnectionPathIds { get; }

    /// <summary>변경 영향 집합을 방어 복사해 보관합니다.</summary>
    internal HeatingPathRegenerationImpact(
        IEnumerable<HeatingPathId> heatingPathIds,
        IEnumerable<CircuitId> circuitIds,
        IEnumerable<ConnectionPointId> connectionPointIds,
        IEnumerable<ConnectionPathId> connectionPathIds)
    {
        HeatingPathIds = CopyDistinct(heatingPathIds, nameof(heatingPathIds));
        CircuitIds = CopyDistinct(circuitIds, nameof(circuitIds));
        ConnectionPointIds = CopyDistinct(connectionPointIds, nameof(connectionPointIds));
        ConnectionPathIds = CopyDistinct(connectionPathIds, nameof(connectionPathIds));
    }

    /// <summary>유효하고 중복 없는 안정 ID 목록을 읽기 전용으로 만듭니다.</summary>
    private static IReadOnlyList<TId> CopyDistinct<TId>(IEnumerable<TId> ids, string parameterName)
        where TId : struct
    {
        TId[] copied = ids?.ToArray() ?? throw new ArgumentNullException(parameterName);
        if (copied.Distinct().Count() != copied.Length)
            throw new ArgumentException("변경 영향 ID는 중복될 수 없습니다.", parameterName);
        return new ReadOnlyCollection<TId>(copied);
    }
}

/// <summary>확정 설계 연결 관계에 따라 재생성의 파급 범위를 계산합니다.</summary>
internal static class HeatingPathRegenerationImpactAnalyzer
{
    /// <summary>요청 경로와 같은 회로에 속한 경로·연결부까지 변경 영향에 포함합니다.</summary>
    public static HeatingPathRegenerationImpact Calculate(
        DesignProject project,
        HeatingPathGenerationRequest request)
    {
        bool sharedConstraintsChanged =
            project.WireSpecification != request.WireSpecification ||
            project.PlacementConstraints != request.PlacementConstraints;
        HashSet<HeatingPathId> affectedPathIds = sharedConstraintsChanged
            ? project.HeatingPaths.Select(path => path.Id).ToHashSet()
            : project.HeatingPaths
                .Where(path => path.GenerationSource.RegionId == request.Region.Id)
                .Select(path => path.Id)
                .ToHashSet();

        Dictionary<HeatingPathSegmentId, HeatingPathId> pathIdBySegment = project.HeatingPaths
            .SelectMany(path => path.Segments.Select(segment =>
                (SegmentId: segment.Id, PathId: path.Id)))
            .ToDictionary(item => item.SegmentId, item => item.PathId);
        var affectedCircuitIds = new HashSet<CircuitId>();
        bool addedRelatedPath;
        do
        {
            int previousPathCount = affectedPathIds.Count;
            HashSet<HeatingPathSegmentId> affectedSegmentIds = project.HeatingPaths
                .Where(path => affectedPathIds.Contains(path.Id))
                .SelectMany(path => path.Segments)
                .Select(segment => segment.Id)
                .ToHashSet();

            affectedCircuitIds.UnionWith(project.Circuits
                .Where(circuit => circuit.Segments.Any(segment => affectedSegmentIds.Contains(segment.SegmentId)))
                .Select(circuit => circuit.Id));

            foreach (Circuit circuit in project.Circuits.Where(circuit => affectedCircuitIds.Contains(circuit.Id)))
            {
                foreach (CircuitSegmentReference segment in circuit.Segments)
                {
                    if (pathIdBySegment.TryGetValue(segment.SegmentId, out HeatingPathId pathId))
                        affectedPathIds.Add(pathId);
                }
            }

            addedRelatedPath = affectedPathIds.Count != previousPathCount;
        }
        while (addedRelatedPath);

        HashSet<ConnectionPointId> affectedConnectionPointIds = project.ConnectionPoints
            .Where(point => affectedCircuitIds.Contains(point.CircuitId))
            .Select(point => point.Id)
            .ToHashSet();
        HashSet<ConnectionPathId> affectedConnectionPathIds = project.ConnectionPaths
            .Where(path => affectedConnectionPointIds.Contains(path.ConnectionPointId))
            .Select(path => path.Id)
            .ToHashSet();

        return new HeatingPathRegenerationImpact(
            project.HeatingPaths.Where(path => affectedPathIds.Contains(path.Id)).Select(path => path.Id),
            project.Circuits.Where(circuit => affectedCircuitIds.Contains(circuit.Id)).Select(circuit => circuit.Id),
            project.ConnectionPoints.Where(point => affectedConnectionPointIds.Contains(point.Id)).Select(point => point.Id),
            project.ConnectionPaths.Where(path => affectedConnectionPathIds.Contains(path.Id)).Select(path => path.Id));
    }
}
