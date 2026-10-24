using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.UseCases;

/// <summary>현재 확정 형상과 파생 결과가 유효할 때만 파일 출력을 호출합니다.</summary>
public sealed class ExportDesign
{
    private readonly ProjectSession _session;
    private readonly IDesignExporter _exporter;

    /// <summary>세션과 형식별 출력 포트를 연결합니다.</summary>
    public ExportDesign(ProjectSession session, IDesignExporter exporter)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    }

    /// <summary>draft·stale·구조 오류를 확인한 snapshot만 출력 포트에 넘깁니다.</summary>
    public OperationResult<ExportReceipt> Execute(
        string path,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(path))
            return Failure("InvalidPath", "출력 경로가 비어 있습니다.");
        if (_session.WorkflowState == WorkflowState.Busy)
            return Failure("SessionBusy", "설계 변경 작업 중에는 출력할 수 없습니다.");

        ExportSnapshot snapshot;
        try
        {
            snapshot = new ExportSnapshot(_session.Current);
        }
        catch (InvalidOperationException exception)
        {
            return Failure("ExportSnapshotInvalid", exception.Message);
        }

        return _exporter.Export(snapshot, options, path.Trim(), cancellationToken);
    }

    private static OperationResult<ExportReceipt> Failure(string code, string evidence)
    {
        ValidationIssue issue = new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            [],
            "application-export-v1",
            evidence: [evidence]);
        return OperationResult<ExportReceipt>.Failure([issue]);
    }
}
