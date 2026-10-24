using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Export;

/// <summary>세션 snapshot에서 같은 revision의 내보내기 입력을 검증해 만듭니다.</summary>
public sealed class ExportSnapshotBuilder
{
    /// <summary>draft, stale 기하와 오래된 파생 결과가 없는 경우에만 snapshot을 반환합니다.</summary>
    public OperationResult<ExportSnapshot> Build(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            return OperationResult<ExportSnapshot>.Success(new ExportSnapshot(session));
        }
        catch (InvalidOperationException exception)
        {
            return OperationResult<ExportSnapshot>.Failure(
            [new ValidationIssue(
                "ExportSnapshotInvalid",
                ValidationSeverity.Error,
                "Infrastructure.ExportSnapshotInvalid",
                [],
                "export-snapshot-v1",
                evidence: [exception.Message])]);
        }
    }
}
