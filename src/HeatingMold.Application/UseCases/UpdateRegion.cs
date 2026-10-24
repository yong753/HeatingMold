using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;

namespace HeatingMold.Application.UseCases;

/// <summary>영역 경계를 갱신하고 해당 영역에서 파생된 경로와 회로를 무효화합니다.</summary>
public sealed class UpdateRegion
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly RegionAdjustmentService _adjustmentService;

    /// <summary>프로젝트 세션과 후보 확정기를 연결합니다.</summary>
    public UpdateRegion(
        ProjectSession session,
        CandidateCoordinator coordinator,
        RegionAdjustmentService? adjustmentService = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _adjustmentService = adjustmentService ?? new RegionAdjustmentService();
    }

    /// <summary>같은 영역 ID의 경계를 바꾸고 영향받는 결과만 제거한 후보를 확정합니다.</summary>
    public CandidateOutcome Execute(
        HeatingRegion replacement,
        DesignRevision expectedBaseRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure(current, "ProjectNotLoaded", "영역을 바꾸려면 먼저 프로젝트를 열어야 합니다.");
        if (project.Revision != expectedBaseRevision)
            return Failure(current, "StaleRegionEdit", "영역 편집의 기준 revision이 현재 설계와 다릅니다.");

        int regionIndex = IndexOfRegion(project.HeatingRegions, replacement.Id);
        if (regionIndex < 0)
            return Failure(current, "HeatingRegionNotFound", "교체할 영역 ID가 현재 설계에 없습니다.", replacement.Id.Value);

        HeatingRegion[] regions = project.HeatingRegions.ToArray();
        regions[regionIndex] = replacement;

        HashSet<HeatingPathId> invalidPathIds = project.HeatingPaths
            .Where(path => path.GenerationSource.RegionId == replacement.Id)
            .Select(path => path.Id)
            .ToHashSet();
        HashSet<HeatingPathSegmentId> invalidSegmentIds = project.HeatingPaths
            .Where(path => invalidPathIds.Contains(path.Id))
            .SelectMany(path => path.Segments)
            .Select(segment => segment.Id)
            .ToHashSet();
        HashSet<CircuitId> invalidCircuitIds = project.Circuits
            .Where(circuit => circuit.Segments.Any(segment => invalidSegmentIds.Contains(segment.SegmentId)))
            .Select(circuit => circuit.Id)
            .ToHashSet();
        HashSet<ConnectionPointId> invalidConnectionPointIds = project.ConnectionPoints
            .Where(point => invalidCircuitIds.Contains(point.CircuitId))
            .Select(point => point.Id)
            .ToHashSet();

        DesignProject candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            heatingRegions: regions,
            heatingPaths: project.HeatingPaths.Where(path => !invalidPathIds.Contains(path.Id)),
            circuits: project.Circuits.Where(circuit => !invalidCircuitIds.Contains(circuit.Id)),
            connectionPoints: project.ConnectionPoints.Where(point => !invalidCircuitIds.Contains(point.CircuitId)),
            connectionPaths: project.ConnectionPaths.Where(path => !invalidConnectionPointIds.Contains(path.ConnectionPointId)));

        return _coordinator.Execute(candidate, expectedBaseRevision, cancellationToken);
    }

    /// <summary>포함·제외 도형을 계산하고 영역 및 파생 경로를 하나의 후보로 확정합니다.</summary>
    public CandidateOutcome ApplyAdjustment(
        RegionId regionId,
        IEnumerable<IReadOnlyList<Vec2>> inclusionPolygons,
        IEnumerable<IReadOnlyList<Vec2>> exclusionPolygons,
        DesignRevision expectedBaseRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inclusionPolygons);
        ArgumentNullException.ThrowIfNull(exclusionPolygons);

        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return Failure(current, "ProjectNotLoaded", "영역을 조정하려면 먼저 프로젝트를 열어야 합니다.");
        if (project.Revision != expectedBaseRevision)
            return Failure(current, "StaleRegionEdit", "영역 조정의 기준 revision이 현재 설계와 다릅니다.", regionId.Value);

        int regionIndex = IndexOfRegion(project.HeatingRegions, regionId);
        if (regionIndex < 0)
            return Failure(current, "HeatingRegionNotFound", "조정할 영역 ID가 현재 설계에 없습니다.", regionId.Value);

        OperationResult<HeatingRegion> adjustment = _adjustmentService.Apply(
            project.HeatingRegions[regionIndex],
            inclusionPolygons,
            exclusionPolygons);
        if (!adjustment.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, adjustment.Issues);

        return Execute(adjustment.GetValueOrThrow(), expectedBaseRevision, cancellationToken);
    }

    private static int IndexOfRegion(IReadOnlyList<HeatingRegion> regions, RegionId id)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            if (regions[index].Id == id)
                return index;
        }

        return -1;
    }

    private static CandidateOutcome Failure(
        SessionSnapshot snapshot,
        string code,
        string evidence,
        params Guid[] entityIds)
    {
        ValidationIssue issue = new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            entityIds,
            "application-region-v1",
            evidence: [evidence]);
        return new CandidateOutcome(CandidateStatus.ValidationFailed, snapshot, [issue]);
    }
}
