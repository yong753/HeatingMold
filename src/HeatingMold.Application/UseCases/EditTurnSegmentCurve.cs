using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Model;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Editing;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Application.UseCases;

/// <summary>회전 구간 내부 곡선 편집을 후보 검증과 원자 확정 흐름에 연결합니다.</summary>
public sealed class EditTurnSegmentCurve
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly TurnSegmentCurveEditService _editService;

    /// <summary>확정 세션, 후보 확정기와 Design 편집 규칙을 연결합니다.</summary>
    public EditTurnSegmentCurve(
        ProjectSession session,
        CandidateCoordinator coordinator,
        TurnSegmentCurveEditService? editService = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _editService = editService ?? new TurnSegmentCurveEditService();
    }

    /// <summary>회전 곡선 후보를 만들고 전체 기하 검증에 통과한 경우에만 확정합니다.</summary>
    public CandidateOutcome Execute(
        TurnSegmentCurveEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
        {
            return new CandidateOutcome(
                CandidateStatus.ValidationFailed,
                current,
                [CreateFailureIssue("ProjectNotLoaded", "회전 곡선을 편집하려면 먼저 프로젝트를 열어야 합니다.", current)]);
        }

        OperationResult<TurnSegmentCurveEditDraft> result = CreateDraft(request, project);
        if (!result.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, result.Issues);

        TurnSegmentCurveEditDraft draft = result.GetValueOrThrow();
        CoreHeatingPath[] paths = project.HeatingPaths
            .Select(path => path.Id == draft.PathId ? draft.CandidatePath : path)
            .ToArray();
        DesignProject candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            heatingPaths: paths);

        return _coordinator.Execute(candidate, request.BaseRevision, cancellationToken);
    }

    /// <summary>확정이나 Native staging 없이 화면 preview에 사용할 편집 후보를 만듭니다.</summary>
    public OperationResult<TurnSegmentCurveEditDraft> CreatePreviewDraft(TurnSegmentCurveEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionSnapshot current = _session.Current;
        if (current.CurrentProject is not { } project)
        {
            return OperationResult<TurnSegmentCurveEditDraft>.Failure(
                [CreateFailureIssue("ProjectNotLoaded", "회전 곡선을 편집하려면 먼저 프로젝트를 열어야 합니다.", current)]);
        }

        return CreateDraft(request, project);
    }

    private OperationResult<TurnSegmentCurveEditDraft> CreateDraft(
        TurnSegmentCurveEditRequest request,
        DesignProject project)
    {
        if (request.BaseRevision != project.Revision)
        {
            return OperationResult<TurnSegmentCurveEditDraft>.Failure(
                [CreateFailureIssue("StaleTurnSegmentEdit", "회전 구간 편집의 기준 revision이 현재 설계와 다릅니다.", _session.Current)]);
        }

        return _editService.CreateDraft(request, project);
    }

    private static ValidationIssue CreateFailureIssue(string code, string evidence, SessionSnapshot snapshot) =>
        new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            snapshot.CurrentProject is { } project ? [project.Id.Value] : [],
            "application-turn-segment-edit-v1",
            evidence: [evidence]);
}
