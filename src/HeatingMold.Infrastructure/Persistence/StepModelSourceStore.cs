using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Persistence;

/// <summary>STEP 원본을 읽고 단위 및 importer 정보를 포함한 불변 모델 원본을 만듭니다.</summary>
public sealed class StepModelSourceStore : IModelSourceStore
{
    private readonly double _sourceUnitToMillimeter;
    private readonly string _importerVersion;
    private readonly string _faceFingerprintVersion;
    private readonly long _inMemoryThresholdBytes;
    private readonly string? _temporaryDirectory;

    /// <summary>가져오기 단위와 면 fingerprint 버전을 명시적으로 설정합니다.</summary>
    public StepModelSourceStore(
        double sourceUnitToMillimeter,
        string importerVersion,
        string faceFingerprintVersion,
        long inMemoryThresholdBytes = ModelSource.DefaultInMemoryStorageThresholdBytes,
        string? temporaryDirectory = null)
    {
        if (!double.IsFinite(sourceUnitToMillimeter) || sourceUnitToMillimeter <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceUnitToMillimeter));
        if (string.IsNullOrWhiteSpace(importerVersion))
            throw new ArgumentException("가져오기 버전이 필요합니다.", nameof(importerVersion));
        if (string.IsNullOrWhiteSpace(faceFingerprintVersion))
            throw new ArgumentException("Face fingerprint 버전이 필요합니다.", nameof(faceFingerprintVersion));
        if (inMemoryThresholdBytes < 0 || inMemoryThresholdBytes > ModelSource.MaximumSourceModelBytes)
            throw new ArgumentOutOfRangeException(nameof(inMemoryThresholdBytes));

        _sourceUnitToMillimeter = sourceUnitToMillimeter;
        _importerVersion = importerVersion.Trim();
        _faceFingerprintVersion = faceFingerprintVersion.Trim();
        _inMemoryThresholdBytes = inMemoryThresholdBytes;
        _temporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? null
            : Path.GetFullPath(temporaryDirectory);
    }

    /// <summary>STEP 파일을 읽고 해시가 일치하는 원본 바이트를 보관합니다.</summary>
    public OperationResult<ModelSource> Capture(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("InvalidPath", "STEP 파일 경로가 비어 있습니다.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new FileStream(path.Trim(), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length == 0)
                return Failure("EmptyModelSource", "STEP 파일이 비어 있습니다.");

            ModelSource modelSource = ModelSource.CaptureFromStream(
                source,
                source.Length,
                _sourceUnitToMillimeter,
                "STEP",
                _importerVersion,
                _faceFingerprintVersion,
                cancellationToken,
                _inMemoryThresholdBytes,
                _temporaryDirectory);
            return OperationResult<ModelSource>.Success(modelSource);
        }
        catch (OperationCanceledException)
        {
            return Failure("OperationCancelled", "STEP 원본 읽기가 취소되었습니다.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure("ModelSourceReadFailed", exception.Message);
        }
    }

    private static OperationResult<ModelSource> Failure(string code, string evidence) =>
        OperationResult<ModelSource>.Failure(
        [new ValidationIssue(code, ValidationSeverity.Error, $"Infrastructure.{code}", [], "model-source-v1", evidence: [evidence])]);
}
