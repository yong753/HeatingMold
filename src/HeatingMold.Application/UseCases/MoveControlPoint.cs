using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Editing;

namespace HeatingMold.Application.UseCases;

/// <summary>공유 제어점을 옮기고 이를 참조하는 경로 조각을 함께 갱신합니다.</summary>
public sealed class MoveControlPoint
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly ControlPointEditService _editService;

    /// <summary>세션, 후보 확정기와 제어점 편집 서비스를 연결합니다.</summary>
    public MoveControlPoint(
        ProjectSession session,
        CandidateCoordinator coordinator,
        ControlPointEditService? editService = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _editService = editService ?? new ControlPointEditService();
    }

    /// <summary>수정 후보를 만들고 Native 기하 검증을 통과한 경우에만 확정합니다.</summary>
    public CandidateOutcome Execute(
        ControlPointEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return new CandidateOutcome(
                CandidateStatus.ValidationFailed,
                current,
                [CreateFailureIssue("ProjectNotLoaded", "제어점을 옮기려면 먼저 프로젝트를 열어야 합니다.", current)]);
        if (project.Revision != request.BaseRevision)
            return new CandidateOutcome(
                CandidateStatus.ValidationFailed,
                current,
                [CreateFailureIssue("StaleControlPointEdit", "제어점 편집의 기준 revision이 현재 설계와 다릅니다.", current)]);

        OperationResult<HeatingPathEditDraft> editResult = CreateDraft(request, project);
        if (!editResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, editResult.Issues);

        HeatingPathEditDraft edit = editResult.GetValueOrThrow();
        HeatingMold.Core.Routing.HeatingPath[] paths = project.HeatingPaths
            .Select(path => path.Id == edit.PathId ? edit.CandidatePath : path)
            .ToArray();
        var candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            heatingPaths: paths);

        return _coordinator.Execute(candidate, request.BaseRevision, cancellationToken);
    }

    /// <summary>확정이나 기하 staging 없이 화면 미리보기에 쓸 편집 후보를 만듭니다.</summary>
    public OperationResult<HeatingPathEditDraft> CreatePreviewDraft(ControlPointEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
            return OperationResult<HeatingPathEditDraft>.Failure(
                [CreateFailureIssue(
                    "ProjectNotLoaded",
                    "제어점을 옮기려면 먼저 프로젝트를 열어야 합니다.",
                    current)]);
        if (project.Revision != request.BaseRevision)
            return OperationResult<HeatingPathEditDraft>.Failure(
                [CreateFailureIssue(
                    "StaleControlPointEdit",
                    "제어점 편집의 기준 revision이 현재 설계와 다릅니다.",
                    current)]);

        return CreateDraft(request, project);
    }

    private OperationResult<HeatingPathEditDraft> CreateDraft(
        ControlPointEditRequest request,
        DesignProject project) => _editService.Move(request, project);

    private static ValidationIssue CreateFailureIssue(string code, string evidence, SessionSnapshot snapshot) =>
        new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            snapshot.CurrentProject is { } project ? [project.Id.Value] : [],
            "application-edit-v1",
            evidence: [evidence]);
}
