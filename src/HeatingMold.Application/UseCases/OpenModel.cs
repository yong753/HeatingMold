using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Application.UseCases;

/// <summary>후보 문서 준비가 끝난 뒤에만 새 모델을 빈 확정 프로젝트로 엽니다.</summary>
public sealed class OpenModel
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;

    /// <summary>현재 세션과 공통 실패 보존 확정기를 주입합니다.</summary>
    public OpenModel(ProjectSession session, CandidateCoordinator coordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>STEP 원본 해시와 초기 좌표계를 확인한 뒤 모델 후보를 원자 확정합니다.</summary>
    public CandidateOutcome Execute(
        ProjectId projectId,
        string projectName,
        ModelSource modelSource,
        WorkingCoordinateSystemDefinition initialWorkingCoordinateSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelSource);
        try
        {
            if (projectId.IsEmpty)
                throw new ArgumentException("새 프로젝트 ID는 비어 있을 수 없습니다.", nameof(projectId));
            ArgumentNullException.ThrowIfNull(initialWorkingCoordinateSystem);

            SessionSnapshot current = _session.Current;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DesignProject emptyProject = DesignProject.CreateEmpty(
                projectId,
                modelSource.Identity,
                initialWorkingCoordinateSystem,
                projectName,
                now);
            DesignProject candidate = current.CurrentProject is { } existing && existing.Id == projectId
                ? CopyWithRevision(emptyProject, existing.Revision.Next(), existing.CreatedUtc, now)
                : emptyProject;

            return _coordinator.ExecuteModel(
                candidate,
                current.CurrentProject?.Revision,
                modelSource,
                cancellationToken);
        }
        finally
        {
            if (!ReferenceEquals(_session.Current.SourceModel, modelSource))
                modelSource.Dispose();
        }
    }

    private static DesignProject CopyWithRevision(
        DesignProject project,
        DesignRevision revision,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc) => new(
            project.Id,
            project.SchemaVersion,
            revision,
            project.Name,
            createdUtc,
            modifiedUtc,
            project.SourceModel,
            project.WorkingCoordinateSystem,
            project.FaceReferences,
            project.HeatingRegions,
            project.WireSpecification,
            project.PlacementConstraints,
            project.HeatingPaths,
            project.Circuits,
            project.ConnectionPoints,
            project.ConnectionPaths,
            project.Rules);
}
