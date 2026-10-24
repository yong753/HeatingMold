using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.UseCases;

/// <summary>불변 세션 snapshot을 저장하고 저장한 revision만 clean으로 표시합니다.</summary>
public sealed class SaveProject
{
    private readonly ProjectSession _session;
    private readonly IProjectPackageStore _packageStore;

    /// <summary>프로젝트 세션과 패키지 저장 포트를 연결합니다.</summary>
    public SaveProject(ProjectSession session, IProjectPackageStore packageStore)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _packageStore = packageStore ?? throw new ArgumentNullException(nameof(packageStore));
    }

    /// <summary>현재 확정 revision을 저장하고 저장 도중 변경도 dirty로 유지합니다.</summary>
    public OperationResult<SaveProjectOutcome> Execute(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return OperationResult<SaveProjectOutcome>.Failure([Issue("InvalidPath", "저장 경로가 비어 있습니다.")]);
        if (_session.WorkflowState == WorkflowState.Busy)
            return OperationResult<SaveProjectOutcome>.Failure([Issue("SessionBusy", "설계 변경 작업 중에는 저장할 수 없습니다.")]);

        SessionSnapshot capturedSession = _session.Current;
        ProjectSaveSnapshot saveSnapshot;
        try
        {
            saveSnapshot = new ProjectSaveSnapshot(capturedSession);
        }
        catch (InvalidOperationException exception)
        {
            return OperationResult<SaveProjectOutcome>.Failure([Issue("SaveSnapshotInvalid", exception.Message)]);
        }

        OperationResult<ProjectPackageWriteReceipt> writeResult = _packageStore.Write(
            path.Trim(), saveSnapshot, cancellationToken);
        if (!writeResult.IsSuccess)
            return OperationResult<SaveProjectOutcome>.Failure(writeResult.Issues);

        ProjectPackageWriteReceipt receipt = writeResult.GetValueOrThrow();
        if (receipt.SavedRevision != saveSnapshot.Project.Revision)
        {
            return OperationResult<SaveProjectOutcome>.Failure(
                [Issue("SavedRevisionMismatch", "저장 포트가 요청한 프로젝트 revision과 다른 revision을 기록했습니다.")]);
        }

        bool markedCurrentRevisionClean = _session.TryMarkSaved(receipt.SavedRevision);
        return OperationResult<SaveProjectOutcome>.Success(
            new SaveProjectOutcome(receipt, markedCurrentRevisionClean));
    }

    private static ValidationIssue Issue(string code, string evidence) => new(
        code,
        ValidationSeverity.Error,
        $"Application.{code}",
        [],
        "application-save-v1",
        evidence: [evidence]);
}

/// <summary>파일 저장과 현재 세션 저장 상태를 분리해 전달합니다.</summary>
public sealed record SaveProjectOutcome(
    ProjectPackageWriteReceipt Receipt,
    bool MarkedCurrentRevisionClean);
