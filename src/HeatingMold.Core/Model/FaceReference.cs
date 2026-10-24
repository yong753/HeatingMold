using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Model;

/// <summary>원본 모델의 면을 재열기에서 다시 찾기 위한 영속 특징입니다.</summary>
public sealed record FaceReference
{
    /// <summary>프로젝트 내부의 안정 Face 참조 ID입니다.</summary>
    public FaceRefId Id { get; }

    /// <summary>Face 특징을 계산한 원본 모델 SHA-256입니다.</summary>
    public string SourceModelSha256 { get; }

    /// <summary>가져오기 당시 면 번호이며 재열기 대응의 참고값으로만 사용합니다.</summary>
    public int ImportIndexHint { get; }

    /// <summary>가져오기 구현이 판별한 기하 종류입니다.</summary>
    public FaceGeometryKind GeometryKind { get; }

    /// <summary>원본 면의 실면적(mm²)입니다.</summary>
    public AreaMm2 SurfaceArea { get; }

    /// <summary>원본 면의 중심점(mm)입니다.</summary>
    public Vec3 Center { get; }

    /// <summary>면 경계 모양을 나타내는 특징 문자열입니다.</summary>
    public string BoundaryFingerprint { get; }

    /// <summary>이웃 면의 형상 특징을 정렬해 보관한 읽기 전용 목록입니다.</summary>
    public IReadOnlyList<string> AdjacentFaceFingerprints { get; }

    /// <summary>영속 ID와 형상 특징을 검증하고 이웃 특징을 방어 복사합니다.</summary>
    public FaceReference(
        FaceRefId id,
        string sourceModelSha256,
        int importIndexHint,
        FaceGeometryKind geometryKind,
        AreaMm2 surfaceArea,
        Vec3 center,
        string boundaryFingerprint,
        IEnumerable<string> adjacentFaceFingerprints)
    {
        if (id.IsEmpty)
            throw new ArgumentException("Face 참조 ID는 비어 있을 수 없습니다.", nameof(id));
        if (sourceModelSha256 is null || sourceModelSha256.Length != 64 || sourceModelSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("원본 SHA-256은 16진수 64자로 입력해야 합니다.", nameof(sourceModelSha256));
        if (importIndexHint < 0)
            throw new ArgumentOutOfRangeException(nameof(importIndexHint), "가져오기 면 번호 힌트는 음수일 수 없습니다.");
        if (!Enum.IsDefined(geometryKind))
            throw new ArgumentOutOfRangeException(nameof(geometryKind));
        if (!double.IsFinite(surfaceArea.Value) || surfaceArea.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceArea), "면적 특징은 양의 유한값이어야 합니다.");
        if (string.IsNullOrWhiteSpace(boundaryFingerprint))
            throw new ArgumentException("면 경계 특징이 필요합니다.", nameof(boundaryFingerprint));

        string[] adjacentFeatures = adjacentFaceFingerprints?.ToArray()
            ?? throw new ArgumentNullException(nameof(adjacentFaceFingerprints));
        if (adjacentFeatures.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("이웃 면 특징은 비어 있을 수 없습니다.", nameof(adjacentFaceFingerprints));

        Id = id;
        SourceModelSha256 = sourceModelSha256.ToLowerInvariant();
        ImportIndexHint = importIndexHint;
        GeometryKind = geometryKind;
        SurfaceArea = surfaceArea;
        Center = center;
        BoundaryFingerprint = boundaryFingerprint.Trim();
        AdjacentFaceFingerprints = new ReadOnlyCollection<string>(adjacentFeatures.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Face 참조가 보관하는 기하 종류입니다.</summary>
public enum FaceGeometryKind
{
    /// <summary>평면입니다.</summary>
    Plane,

    /// <summary>원통면입니다.</summary>
    Cylinder,

    /// <summary>원뿔면입니다.</summary>
    Cone,

    /// <summary>구면입니다.</summary>
    Sphere,

    /// <summary>토러스면입니다.</summary>
    Torus,

    /// <summary>자유곡면 또는 기타 면입니다.</summary>
    Other
}
