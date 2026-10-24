using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.UseCases;

/// <summary>패키지를 검증하고 staged Native 복원에 성공한 뒤에만 세션을 교체합니다.</summary>
public sealed class OpenProject
{
    private readonly ProjectSession _session;
    private readonly IProjectPackageStore _packageStore;
    private readonly CandidateCoordinator _coordinator;

    /// <summary>현재 세션과 저장 포트, 공통 복원 트랜잭션을 연결합니다.</summary>
    public OpenProject(
        ProjectSession session,
        IProjectPackageStore packageStore,
        CandidateCoordinator coordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _packageStore = packageStore ?? throw new ArgumentNullException(nameof(packageStore));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>패키지를 읽고 기존 프로젝트 revision을 기준으로 복원 후보를 확정합니다.</summary>
    public CandidateOutcome Execute(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("InvalidPath", "프로젝트 경로가 비어 있습니다.");

        OperationResult<ProjectPackageReadResult> readResult = _packageStore.Read(path.Trim(), cancellationToken);
        if (!readResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, _session.Current, readResult.Issues);

        ProjectPackageReadResult package = readResult.GetValueOrThrow();
        try
        {
            SessionSnapshot current = _session.Current;
            DesignProject candidate = package.Project;
            if (current.CurrentProject is { } existing && existing.Id == candidate.Id)
                candidate = CopyWithRevision(candidate, existing.Revision.Next());

            return _coordinator.ExecuteRestore(
                candidate,
                current.CurrentProject?.Revision,
                package.SourceModel,
                cancellationToken,
                package.AreaAllocation,
                package.Project.Revision);
        }
        finally
        {
            if (!ReferenceEquals(_session.Current.SourceModel, package.SourceModel))
                package.SourceModel.Dispose();
        }
    }

    private CandidateOutcome Failure(string code, string evidence)
    {
        ValidationIssue issue = new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            [],
            "application-open-v1",
            evidence: [evidence]);
        return new CandidateOutcome(CandidateStatus.ValidationFailed, _session.Current, [issue]);
    }

    private static DesignProject CopyWithRevision(DesignProject project, DesignRevision revision) => new(
        project.Id,
        project.SchemaVersion,
        revision,
        project.Name,
        project.CreatedUtc,
        project.ModifiedUtc,
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
