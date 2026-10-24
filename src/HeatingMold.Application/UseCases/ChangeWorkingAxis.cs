using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.UseCases;

/// <summary>작업 좌표계를 바꿀 때 이전 축에 종속된 영역·경로·회로를 새 후보에서 초기화합니다.</summary>
public sealed class ChangeWorkingAxis
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;

    /// <summary>현재 세션과 공통 후보 확정기를 주입합니다.</summary>
    public ChangeWorkingAxis(ProjectSession session, CandidateCoordinator coordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>같은 축은 no-op으로 두고 새 축은 모든 종속 기하를 초기화해 확정합니다.</summary>
    public CandidateOutcome Execute(
        WorkingCoordinateSystemDefinition nextWorkingCoordinateSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextWorkingCoordinateSystem);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            throw new InvalidOperationException("작업 좌표계를 바꾸려면 열린 프로젝트가 필요합니다.");

        if (project.WorkingCoordinateSystem == nextWorkingCoordinateSystem)
            return new CandidateOutcome(CandidateStatus.NoOp, current, Array.Empty<ValidationIssue>());

        DesignProject candidate = new(
            project.Id,
            project.SchemaVersion,
            project.Revision.Next(),
            project.Name,
            project.CreatedUtc,
            DateTimeOffset.UtcNow,
            project.SourceModel,
            nextWorkingCoordinateSystem,
            project.FaceReferences,
            Array.Empty<HeatingRegion>(),
            project.WireSpecification,
            placementConstraints: null,
            heatingPaths: Array.Empty<HeatingPath>(),
            circuits: Array.Empty<Circuit>(),
            connectionPoints: Array.Empty<ConnectionPoint>(),
            connectionPaths: Array.Empty<ConnectionPath>(),
            project.Rules);

        return _coordinator.Execute(
            candidate,
            project.Revision,
            _ => OperationResult<CandidateDerivedState>.Success(new CandidateDerivedState()),
            cancellationToken);
    }
}
