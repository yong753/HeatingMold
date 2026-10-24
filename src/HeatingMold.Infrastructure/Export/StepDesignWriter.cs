using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Export;

/// <summary>회로별 확정 곡선을 Native writer에 전달하고 STEP 파일을 원자적으로 저장합니다.</summary>
public sealed class StepDesignWriter : IStepDesignWriter
{
    private readonly IStepGeometryWriterBridge _bridge;
    private readonly StepDesignPayloadBuilder _payloadBuilder;

    /// <summary>Native geometry 경계와 확정 설계 payload 변환기를 연결합니다.</summary>
    public StepDesignWriter(
        IStepGeometryWriterBridge bridge,
        StepDesignPayloadBuilder? payloadBuilder = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _payloadBuilder = payloadBuilder ?? new StepDesignPayloadBuilder();
    }

    /// <summary>검증된 곡선을 임시 STEP으로 쓴 뒤 완성된 파일만 목적지에 교체합니다.</summary>
    public OperationResult<ExportReceipt> Export(
        ExportSnapshot snapshot,
        ExportOptions options,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Format != ExportFormat.Step)
            return Failure("UnsupportedExportFormat", "STEP writer에는 STEP 출력 옵션이 필요합니다.");
        if (string.IsNullOrWhiteSpace(path))
            return Failure("InvalidPath", "STEP 출력 경로가 비어 있습니다.");

        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationResult<IReadOnlyList<StepDesignGroup>> payloadResult = _payloadBuilder.Build(snapshot);
            if (!payloadResult.IsSuccess)
                return OperationResult<ExportReceipt>.Failure(payloadResult.Issues);

            string fullPath = Path.GetFullPath(path.Trim());
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                return Failure("InvalidPath", "STEP 출력 경로에 상위 폴더가 없습니다.");

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp.step");
            StepGeometryWriteResult writeResult = _bridge.Write(
                temporaryPath,
                snapshot.SourceModel,
                payloadResult.GetValueOrThrow(),
                options.IncludeSourceModel,
                cancellationToken)
                ?? throw new InvalidOperationException("STEP geometry bridge가 결과를 반환하지 않았습니다.");
            cancellationToken.ThrowIfCancellationRequested();
            if (writeResult.Status != StepGeometryWriteStatus.Success)
                return Failure(MapFailureCode(writeResult.Status), writeResult.Diagnostic ?? "Native STEP writer가 파일을 만들지 못했습니다.");

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                return Failure("StepOutputMissing", "Native STEP writer가 비어 있지 않은 출력 파일을 만들지 않았습니다.");

            ExportReceipt receipt = AtomicExportFile.Commit(
                temporaryPath,
                fullPath,
                ExportFormat.Step,
                cancellationToken);
            temporaryPath = null;

            return OperationResult<ExportReceipt>.Success(receipt);
        }
        catch (OperationCanceledException)
        {
            return Failure("OperationCancelled", "STEP 출력이 취소되었습니다.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or ArithmeticException)
        {
            return Failure("StepExportFailed", exception.Message);
        }
        finally
        {
            AtomicExportFile.TryDelete(temporaryPath);
        }
    }

    private static string MapFailureCode(StepGeometryWriteStatus status) => status switch
    {
        StepGeometryWriteStatus.InvalidInput => "StepGeometryInputInvalid",
        StepGeometryWriteStatus.SourceModelReadFailed => "StepSourceModelReadFailed",
        StepGeometryWriteStatus.CurveDefinitionInvalid => "StepCurveDefinitionInvalid",
        StepGeometryWriteStatus.WireBuildFailed => "StepWireBuildFailed",
        StepGeometryWriteStatus.WriteFailed => "StepNativeWriteFailed",
        StepGeometryWriteStatus.GeometryFailure => "StepNativeGeometryFailure",
        _ => "StepNativeWriteFailed"
    };

    private static OperationResult<ExportReceipt> Failure(string code, string evidence) =>
        OperationResult<ExportReceipt>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Infrastructure.{code}",
            [],
            "step-design-export-v1",
            evidence: [evidence])]);
}
