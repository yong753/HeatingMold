using System.Collections.ObjectModel;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>저장 Face 참조 전체를 현재 Native 면 번호에 대응합니다.</summary>
public sealed class FaceReferenceMapBuilder
{
    /// <summary>Face 대응 실패 진단에 사용하는 규칙 버전입니다.</summary>
    public const string RuleVersion = "face-reference-map-v1";

    private readonly FaceReferenceResolver _resolver;

    /// <summary>지정한 특징 resolver 또는 기본 resolver로 대응기를 만듭니다.</summary>
    public FaceReferenceMapBuilder(FaceReferenceResolver? resolver = null)
    {
        _resolver = resolver ?? new FaceReferenceResolver();
    }

    /// <summary>모든 영속 Face가 하나씩 대응될 때만 완전한 Native 번호 맵을 반환합니다.</summary>
    public OperationResult<IReadOnlyDictionary<FaceRefId, int>> Build(
        DesignProject project,
        ModelIdentity currentModelIdentity,
        IEnumerable<NativeFaceFeature> currentFaces)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(currentModelIdentity);
        ArgumentNullException.ThrowIfNull(currentFaces);

        string supportedFingerprintVersion = FaceReferenceFeatureBuilder.FingerprintVersion;
        if (!string.Equals(
                project.SourceModel.FaceFingerprintVersion,
                supportedFingerprintVersion,
                StringComparison.Ordinal))
        {
            return Failure(
                "UnsupportedFaceFingerprintVersion",
                $"저장 프로젝트의 Face fingerprint 버전 '{project.SourceModel.FaceFingerprintVersion}'을(를) " +
                $"현재 지원 버전 '{supportedFingerprintVersion}'으로 복원할 수 없습니다.");
        }

        if (!string.Equals(
                currentModelIdentity.FaceFingerprintVersion,
                supportedFingerprintVersion,
                StringComparison.Ordinal))
        {
            return Failure(
                "UnsupportedFaceFingerprintVersion",
                $"현재 원본 모델 identity의 Face fingerprint 버전 '{currentModelIdentity.FaceFingerprintVersion}'은(는) " +
                $"특징 생성 버전 '{supportedFingerprintVersion}'과 일치하지 않습니다.");
        }

        NativeFaceFeature[] faces = currentFaces.ToArray();
        if (faces.Any(face => face is null))
        {
            return Failure(
                "InvalidNativeFaceSet",
                "현재 Native 면 특징 목록에 null 항목이 있습니다.");
        }

        int[] duplicateIndices = faces
            .GroupBy(face => face.NativeFaceIndex)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(index => index)
            .ToArray();
        if (duplicateIndices.Length > 0)
        {
            return Failure(
                "InvalidNativeFaceSet",
                $"현재 Native 면 번호가 중복되었습니다: {string.Join(", ", duplicateIndices)}");
        }

        FaceReference[] references = project.FaceReferences.ToArray();
        FaceReference[] duplicateReferences = references
            .GroupBy(reference => reference.Id)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        if (duplicateReferences.Length > 0)
        {
            return OperationResult<IReadOnlyDictionary<FaceRefId, int>>.Failure(
            [new ValidationIssue(
                "DuplicateFaceReferenceId",
                ValidationSeverity.Error,
                "Infrastructure.DuplicateFaceReferenceId",
                duplicateReferences.Select(reference => reference.Id.Value),
                RuleVersion,
                evidence: ["프로젝트에 중복된 영속 Face 참조 ID가 있습니다."])]);
        }

        var nativeIndicesByReference = new Dictionary<FaceRefId, int>();
        var referencesByNativeIndex = new Dictionary<int, FaceRefId>();
        var issues = new List<ValidationIssue>();

        foreach (FaceReference reference in references)
        {
            FaceReferenceResolution resolution = _resolver.Resolve(
                reference,
                project.SourceModel,
                currentModelIdentity,
                faces);

            if (resolution.Status != FaceReferenceResolutionStatus.Resolved ||
                resolution.NativeFaceIndex is not int nativeFaceIndex)
            {
                issues.Add(CreateResolutionIssue(resolution));
                continue;
            }

            if (referencesByNativeIndex.TryGetValue(nativeFaceIndex, out FaceRefId existingReferenceId))
            {
                issues.Add(new ValidationIssue(
                    "FaceReferenceNativeIndexCollision",
                    ValidationSeverity.Error,
                    "Infrastructure.FaceReferenceNativeIndexCollision",
                    [existingReferenceId.Value, reference.Id.Value],
                    RuleVersion,
                    evidence: [$"서로 다른 영속 Face 참조가 Native 면 {nativeFaceIndex}에 대응됩니다."]));
                continue;
            }

            referencesByNativeIndex.Add(nativeFaceIndex, reference.Id);
            nativeIndicesByReference.Add(reference.Id, nativeFaceIndex);
        }

        if (issues.Count > 0)
            return OperationResult<IReadOnlyDictionary<FaceRefId, int>>.Failure(issues);

        IReadOnlyDictionary<FaceRefId, int> readOnlyMap =
            new ReadOnlyDictionary<FaceRefId, int>(nativeIndicesByReference);
        return OperationResult<IReadOnlyDictionary<FaceRefId, int>>.Success(readOnlyMap);
    }

    private static ValidationIssue CreateResolutionIssue(FaceReferenceResolution resolution)
    {
        string candidates = resolution.CandidateNativeFaceIndices.Count == 0
            ? "없음"
            : string.Join(", ", resolution.CandidateNativeFaceIndices);

        return new ValidationIssue(
            $"FaceReference{resolution.Status}",
            ValidationSeverity.Error,
            $"Infrastructure.FaceReference{resolution.Status}",
            [resolution.FaceReferenceId.Value],
            RuleVersion,
            evidence:
            [
                $"대응 상태: {resolution.Status}",
                $"후보 Native 면 번호: {candidates}"
            ]);
    }

    private static OperationResult<IReadOnlyDictionary<FaceRefId, int>> Failure(
        string code,
        string evidence) =>
        OperationResult<IReadOnlyDictionary<FaceRefId, int>>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Infrastructure.{code}",
            [],
            RuleVersion,
            evidence: [evidence])]);
}
