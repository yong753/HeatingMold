using System.Collections.ObjectModel;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;
using CoreHeatingPathSegment = HeatingMold.Core.Routing.HeatingPathSegment;

namespace HeatingMold.Design.Validation;

/// <summary>발열 경로의 순서와 공유 절점으로 간격 검사에서 제외할 국소 접점을 구합니다.</summary>
public sealed class HeatingPathCurveAdjacencyResolver
{
    /// <summary>활성 곡선 스냅샷에 모두 존재하는 연속 발열 구간 쌍을 안정 ID로 반환합니다.</summary>
    public IReadOnlyList<CurveSegmentAdjacency> Resolve(
        IReadOnlyList<CoreHeatingPath> heatingPaths,
        IReadOnlyList<CurveGeometrySnapshot> curveSnapshots)
    {
        ArgumentNullException.ThrowIfNull(heatingPaths);
        ArgumentNullException.ThrowIfNull(curveSnapshots);
        if (heatingPaths.Any(path => path is null) || curveSnapshots.Any(curve => curve is null))
            throw new ArgumentException("경로와 곡선 스냅샷은 null 항목을 포함할 수 없습니다.");

        HeatingPathSegmentId[] snapshotIds = curveSnapshots
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .Select(curve => curve.HeatingPathSegmentId!.Value)
            .ToArray();
        if (snapshotIds.Distinct().Count() != snapshotIds.Length)
            throw new ArgumentException("간격 스냅샷에는 발열 구간 ID가 중복될 수 없습니다.", nameof(curveSnapshots));

        var availableSegmentIds = snapshotIds.ToHashSet();
        var adjacencyIds = new HashSet<(Guid From, Guid To)>();
        var resolved = new List<CurveSegmentAdjacency>();

        foreach (CoreHeatingPath path in heatingPaths)
        {
            IReadOnlyList<CoreHeatingPathSegment> segments = path.Segments;
            for (int index = 0; index + 1 < segments.Count; index++)
            {
                AddIfContinuous(segments[index], segments[index + 1]);
            }

            if (segments.Count > 1)
                AddIfContinuous(segments[^1], segments[0]);
        }

        return new ReadOnlyCollection<CurveSegmentAdjacency>(resolved);

        void AddIfContinuous(CoreHeatingPathSegment first, CoreHeatingPathSegment second)
        {
            if (!first.IsActive || !second.IsActive || first.EndControlPointId != second.StartControlPointId ||
                !availableSegmentIds.Contains(first.Id) || !availableSegmentIds.Contains(second.Id))
            {
                return;
            }

            Guid firstId = first.Id.Value;
            Guid secondId = second.Id.Value;
            (Guid From, Guid To) key = (firstId, secondId);
            if (adjacencyIds.Add(key))
                resolved.Add(new CurveSegmentAdjacency(first.Id, second.Id));
        }
    }
}
