using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.UseCases;

/// <summary>Native HLR 정사영 결과를 출력 포트로 전달합니다.</summary>
public sealed class ExportOrthographicViews
{
    private readonly IOrthographicDxfExporter _exporter;

    /// <summary>정사영 DXF 출력 포트를 연결합니다.</summary>
    public ExportOrthographicViews(IOrthographicDxfExporter exporter)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    }

    /// <summary>경로와 정사영 결과를 확인한 뒤 DXF 출력을 요청합니다.</summary>
    public OperationResult<ExportReceipt> Execute(
        string path,
        IReadOnlyList<OrthographicDxfView> views,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperationResult<ExportReceipt>.Failure(
            [
                new ValidationIssue(
                    "InvalidPath",
                    ValidationSeverity.Error,
                    "Application.InvalidPath",
                    [],
                    "application-orthographic-export-v1",
                    evidence: ["DXF 출력 경로가 비어 있습니다."])
            ]);
        }
        ArgumentNullException.ThrowIfNull(views);

        return _exporter.ExportViews(path.Trim(), views, cancellationToken);
    }
}
