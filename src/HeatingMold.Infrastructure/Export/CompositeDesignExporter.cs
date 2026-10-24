using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Export;

/// <summary>출력 형식에 따라 DXF 구현 또는 네이티브 STEP writer로 위임합니다.</summary>
public sealed class CompositeDesignExporter : IDesignExporter
{
    private readonly DxfDesignExporter _dxfExporter;
    private readonly IStepDesignWriter? _stepWriter;

    /// <summary>DXF와 선택적인 네이티브 STEP writer를 연결합니다.</summary>
    public CompositeDesignExporter(
        DxfDesignExporter? dxfExporter = null,
        IStepDesignWriter? stepWriter = null)
    {
        _dxfExporter = dxfExporter ?? new DxfDesignExporter();
        _stepWriter = stepWriter;
    }

    /// <summary>요청한 형식으로 내보내고 사용할 수 없는 형식은 명확한 진단으로 반환합니다.</summary>
    public OperationResult<ExportReceipt> Export(
        ExportSnapshot snapshot,
        ExportOptions options,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        return options.Format switch
        {
            ExportFormat.Dxf => _dxfExporter.Export(snapshot, options, path, cancellationToken),
            ExportFormat.Step when _stepWriter is not null =>
                _stepWriter.Export(snapshot, options, path, cancellationToken),
            ExportFormat.Step => Failure(
                "StepExportBackendUnavailable",
                "최종 발열·연결 경로를 포함해 STEP을 쓸 네이티브 백엔드가 연결되지 않았습니다."),
            _ => Failure("UnsupportedExportFormat", "지원하지 않는 출력 형식입니다.")
        };
    }

    private static OperationResult<ExportReceipt> Failure(string code, string evidence) =>
        OperationResult<ExportReceipt>.Failure(
        [new ValidationIssue(code, ValidationSeverity.Error, $"Infrastructure.{code}", [], "export-dispatch-v1", evidence: [evidence])]);
}

/// <summary>네이티브 형상 어댑터가 최종 STEP 파일을 쓰기 위한 포트입니다.</summary>
public interface IStepDesignWriter
{
    /// <summary>원본 형상에 확정 설계를 반영해 STEP 파일을 저장합니다.</summary>
    OperationResult<ExportReceipt> Export(
        ExportSnapshot snapshot,
        ExportOptions options,
        string path,
        CancellationToken cancellationToken = default);
}
