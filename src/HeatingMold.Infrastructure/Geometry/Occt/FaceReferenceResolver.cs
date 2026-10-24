using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>현재 가져온 모델의 한 Face 특징 집합입니다.</summary>
public sealed class NativeFaceFeature
{
    /// <summary>현재 OCCT 문서에서 사용할 Face 번호입니다.</summary>
    public int NativeFaceIndex { get; }

    /// <summary>가져온 면의 기하 종류입니다.</summary>
    public FaceGeometryKind GeometryKind { get; }

    /// <summary>원본 면의 실면적(mm²) 특징입니다.</summary>
    public AreaMm2 SurfaceArea { get; }

    /// <summary>원본 면 중심점(mm) 특징입니다.</summary>
    public Vec3 Center { get; }

    /// <summary>경계 모양을 정규화한 특징 문자열입니다.</summary>
    public string BoundaryFingerprint { get; }

    /// <summary>이웃 면 경계 특징의 정렬된 목록입니다.</summary>
    public IReadOnlyList<string> AdjacentFaceFingerprints { get; }

    /// <summary>유효 번호와 비어 있지 않은 특징으로 현재 Face 정보를 만듭니다.</summary>
    public NativeFaceFeature(
        int nativeFaceIndex,
        FaceGeometryKind geometryKind,
        AreaMm2 surfaceArea,
        Vec3 center,
        string boundaryFingerprint,
        IEnumerable<string> adjacentFaceFingerprints)
    {
        if (nativeFaceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(nativeFaceIndex));
        if (!Enum.IsDefined(geometryKind))
            throw new ArgumentOutOfRangeException(nameof(geometryKind));
        if (!double.IsFinite(surfaceArea.Value) || surfaceArea.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceArea));
        if (string.IsNullOrWhiteSpace(boundaryFingerprint))
            throw new ArgumentException("면 경계 특징이 필요합니다.", nameof(boundaryFingerprint));

        string[] adjacent = adjacentFaceFingerprints?.ToArray()
            ?? throw new ArgumentNullException(nameof(adjacentFaceFingerprints));
        if (adjacent.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("이웃 면 특징은 비어 있을 수 없습니다.", nameof(adjacentFaceFingerprints));

        NativeFaceIndex = nativeFaceIndex;
        GeometryKind = geometryKind;
        SurfaceArea = surfaceArea;
        Center = center;
        BoundaryFingerprint = boundaryFingerprint.Trim();
        AdjacentFaceFingerprints = new ReadOnlyCollection<string>(
            adjacent.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Face 특징 대응의 확정 또는 재선택 필요 상태입니다.</summary>
public enum FaceReferenceResolutionStatus
{
    /// <summary>고유한 현재 Face를 찾았습니다.</summary>
    Resolved,

    /// <summary>패키지 원본 모델 해시와 현재 모델 해시가 다릅니다.</summary>
    SourceModelMismatch,

    /// <summary>Face 참조와 해당 프로젝트의 모델 identity가 서로 다릅니다.</summary>
    ReferenceModelMismatch,

    /// <summary>현재 모델의 단위 또는 가져오기·fingerprint 버전이 저장 조건과 다릅니다.</summary>
    ImportIdentityMismatch,

    /// <summary>저장된 특징과 일치하는 현재 Face가 없습니다.</summary>
    NotFound,

    /// <summary>동일한 특징을 가진 현재 Face가 여러 개라 재선택이 필요합니다.</summary>
    Ambiguous,

    /// <summary>현재 모델 Face 목록에 중복 번호가 있습니다.</summary>
    InvalidNativeFaceSet
}

/// <summary>영속 Face 참조의 현재 문서 대응과 재선택 후보입니다.</summary>
public sealed class FaceReferenceResolution
{
    /// <summary>복원할 영속 Face 참조 ID입니다.</summary>
    public FaceRefId FaceReferenceId { get; }

    /// <summary>특징 대응 판정 결과입니다.</summary>
    public FaceReferenceResolutionStatus Status { get; }

    /// <summary>대응이 유일할 때만 설정하는 현재 Native Face 번호입니다.</summary>
    public int? NativeFaceIndex { get; }

    /// <summary>모호성 진단에 표시할 특징 일치 Face 번호입니다.</summary>
    public IReadOnlyList<int> CandidateNativeFaceIndices { get; }

    internal FaceReferenceResolution(
        FaceRefId faceReferenceId,
        FaceReferenceResolutionStatus status,
        int? nativeFaceIndex,
        IEnumerable<int> candidateNativeFaceIndices)
    {
        FaceReferenceId = faceReferenceId;
        Status = status;
        NativeFaceIndex = nativeFaceIndex;
        CandidateNativeFaceIndices = new ReadOnlyCollection<int>(candidateNativeFaceIndices.ToArray());
    }
}

/// <summary>저장된 Face 특징으로 재열기 Face를 대응하고 모호한 자동 선택을 거부합니다.</summary>
public sealed class FaceReferenceResolver
{
    /// <summary>저장·현재 모델의 전체 import identity와 Face 특징을 확인해 고유하게 대응합니다.</summary>
    public FaceReferenceResolution Resolve(
        FaceReference reference,
        ModelIdentity storedModelIdentity,
        ModelIdentity currentModelIdentity,
        IEnumerable<NativeFaceFeature> currentFaces)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(storedModelIdentity);
        ArgumentNullException.ThrowIfNull(currentModelIdentity);
        ArgumentNullException.ThrowIfNull(currentFaces);

        if (!string.Equals(
                reference.SourceModelSha256,
                storedModelIdentity.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(reference.Id, FaceReferenceResolutionStatus.ReferenceModelMismatch);
        }

        if (!string.Equals(
                storedModelIdentity.SourceSha256,
                currentModelIdentity.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(reference.Id, FaceReferenceResolutionStatus.SourceModelMismatch);
        }

        if (!HasSameImportIdentity(storedModelIdentity, currentModelIdentity))
            return Failure(reference.Id, FaceReferenceResolutionStatus.ImportIdentityMismatch);

        NativeFaceFeature[] faces = currentFaces.ToArray();
        if (faces.GroupBy(face => face.NativeFaceIndex).Any(group => group.Count() > 1))
            return Failure(reference.Id, FaceReferenceResolutionStatus.InvalidNativeFaceSet);

        NativeFaceFeature? hintedFace = faces.SingleOrDefault(
            face => face.NativeFaceIndex == reference.ImportIndexHint);
        if (hintedFace is not null && Matches(reference, hintedFace))
            return Resolved(reference.Id, hintedFace.NativeFaceIndex);

        NativeFaceFeature[] matches = faces.Where(face => Matches(reference, face)).ToArray();
        return matches.Length switch
        {
            0 => Failure(reference.Id, FaceReferenceResolutionStatus.NotFound),
            1 => Resolved(reference.Id, matches[0].NativeFaceIndex),
            _ => Ambiguous(reference.Id, matches.Select(face => face.NativeFaceIndex))
        };
    }

    private static bool Matches(FaceReference reference, NativeFaceFeature candidate) =>
        reference.GeometryKind == candidate.GeometryKind &&
        reference.SurfaceArea == candidate.SurfaceArea &&
        reference.Center == candidate.Center &&
        string.Equals(reference.BoundaryFingerprint, candidate.BoundaryFingerprint, StringComparison.Ordinal) &&
        reference.AdjacentFaceFingerprints.SequenceEqual(
            candidate.AdjacentFaceFingerprints,
            StringComparer.Ordinal);

    private static bool HasSameImportIdentity(ModelIdentity stored, ModelIdentity current) =>
        stored.SourceUnitToMillimeter == current.SourceUnitToMillimeter &&
        string.Equals(stored.ImporterName, current.ImporterName, StringComparison.Ordinal) &&
        string.Equals(stored.ImporterVersion, current.ImporterVersion, StringComparison.Ordinal) &&
        string.Equals(stored.FaceFingerprintVersion, current.FaceFingerprintVersion, StringComparison.Ordinal);

    private static FaceReferenceResolution Resolved(FaceRefId referenceId, int nativeFaceIndex) =>
        new(referenceId, FaceReferenceResolutionStatus.Resolved, nativeFaceIndex, [nativeFaceIndex]);

    private static FaceReferenceResolution Ambiguous(FaceRefId referenceId, IEnumerable<int> candidates) =>
        new(
            referenceId,
            FaceReferenceResolutionStatus.Ambiguous,
            nativeFaceIndex: null,
            candidates.OrderBy(index => index));

    private static FaceReferenceResolution Failure(
        FaceRefId referenceId,
        FaceReferenceResolutionStatus status) =>
        new(referenceId, status, nativeFaceIndex: null, []);
}
