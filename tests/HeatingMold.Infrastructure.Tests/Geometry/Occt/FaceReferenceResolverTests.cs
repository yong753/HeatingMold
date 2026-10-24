using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Geometry;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>Face 복원에서 import hint 검증과 모호한 특징 진단을 확인합니다.</summary>
public sealed class FaceReferenceResolverTests
{
    private const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>가져오기 번호의 면 특징이 일치하면 저장된 Face를 바로 대응합니다.</summary>
    [Fact]
    public void Resolve_UsesHintWhenItsFeatureMatches()
    {
        FaceReference reference = CreateReference(importIndexHint: 4);
        var resolver = new FaceReferenceResolver();

        FaceReferenceResolution result = resolver.Resolve(
            reference,
            CreateModelIdentity(),
            CreateModelIdentity(),
            [CreateFeature(4), CreateFeature(9)]);

        Assert.Equal(FaceReferenceResolutionStatus.Resolved, result.Status);
        Assert.Equal(4, result.NativeFaceIndex);
        Assert.Equal([4], result.CandidateNativeFaceIndices);
    }

    /// <summary>가져오기 번호의 특징이 다르면 전체 Face에서 유일한 특징 일치만 선택합니다.</summary>
    [Fact]
    public void Resolve_FallsBackToUniqueFeatureWhenHintChanged()
    {
        FaceReference reference = CreateReference(importIndexHint: 4);
        var resolver = new FaceReferenceResolver();

        FaceReferenceResolution result = resolver.Resolve(
            reference,
            CreateModelIdentity(),
            CreateModelIdentity(),
            [CreateFeature(4, boundaryFingerprint: "other-boundary"), CreateFeature(12)]);

        Assert.Equal(FaceReferenceResolutionStatus.Resolved, result.Status);
        Assert.Equal(12, result.NativeFaceIndex);
    }

    /// <summary>힌트가 어긋난 뒤 특징이 여러 면에 맞으면 임의 번호를 선택하지 않습니다.</summary>
    [Fact]
    public void Resolve_ReportsAllCandidatesWhenFeatureIsAmbiguous()
    {
        FaceReference reference = CreateReference(importIndexHint: 1);
        var resolver = new FaceReferenceResolver();

        FaceReferenceResolution result = resolver.Resolve(
            reference,
            CreateModelIdentity(),
            CreateModelIdentity(),
            [CreateFeature(1, boundaryFingerprint: "other-boundary"), CreateFeature(6), CreateFeature(3)]);

        Assert.Equal(FaceReferenceResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.NativeFaceIndex);
        Assert.Equal([3, 6], result.CandidateNativeFaceIndices);
    }

    /// <summary>저장된 특징과 일치하는 면이 없으면 재선택이 필요한 상태를 반환합니다.</summary>
    [Fact]
    public void Resolve_ReportsNotFoundWhenNoFeatureMatches()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 1),
            CreateModelIdentity(),
            CreateModelIdentity(),
            [CreateFeature(1, geometryKind: FaceGeometryKind.Cylinder)]);

        Assert.Equal(FaceReferenceResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.CandidateNativeFaceIndices);
    }

    /// <summary>원본 모델 해시가 다르면 Face 특징을 비교하지 않습니다.</summary>
    [Fact]
    public void Resolve_RejectsDifferentSourceModelHash()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(new string('b', 64)),
            [CreateFeature(4)]);

        Assert.Equal(FaceReferenceResolutionStatus.SourceModelMismatch, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    /// <summary>현재 모델 Face 번호가 중복되면 잘못된 매핑 입력으로 반환합니다.</summary>
    [Fact]
    public void Resolve_RejectsDuplicateNativeFaceIndices()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(),
            [CreateFeature(4), CreateFeature(4, boundaryFingerprint: "other-boundary")]);

        Assert.Equal(FaceReferenceResolutionStatus.InvalidNativeFaceSet, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    /// <summary>경계가 같아도 면적 특징이 다르면 같은 Face로 복원하지 않습니다.</summary>
    [Fact]
    public void Resolve_RequiresAreaAndCenterFeaturesToMatch()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(),
            [CreateFeature(4, surfaceArea: new AreaMm2(1_200.1))]);

        Assert.Equal(FaceReferenceResolutionStatus.NotFound, result.Status);
    }

    /// <summary>같은 파일이어도 원본 단위 변환이 달라지면 특징 비교를 중단합니다.</summary>
    [Fact]
    public void Resolve_RejectsDifferentSourceUnitConversion()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(sourceUnitToMillimeter: 25.4),
            [CreateFeature(4)]);

        Assert.Equal(FaceReferenceResolutionStatus.ImportIdentityMismatch, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    /// <summary>같은 파일도 다른 importer 구현으로 읽었다면 특징 비교를 중단합니다.</summary>
    [Fact]
    public void Resolve_RejectsDifferentImporterName()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(importerName: "Other STEP reader"),
            [CreateFeature(4)]);

        Assert.Equal(FaceReferenceResolutionStatus.ImportIdentityMismatch, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    /// <summary>같은 파일의 importer 버전이 다르면 동일한 Face라고 단정하지 않습니다.</summary>
    [Fact]
    public void Resolve_RejectsDifferentImporterVersion()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(importerVersion: "reader-v2"),
            [CreateFeature(4)]);

        Assert.Equal(FaceReferenceResolutionStatus.ImportIdentityMismatch, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    /// <summary>저장된 fingerprint 규칙과 현재 규칙이 다르면 경계 특징 비교를 중단합니다.</summary>
    [Fact]
    public void Resolve_RejectsDifferentFingerprintVersion()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(),
            CreateModelIdentity(faceFingerprintVersion: "face-v2"),
            [CreateFeature(4)]);

        Assert.Equal(FaceReferenceResolutionStatus.ImportIdentityMismatch, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    /// <summary>Face 참조의 원본 해시가 프로젝트 모델 identity와 다르면 대응하지 않습니다.</summary>
    [Fact]
    public void Resolve_RejectsReferenceFromDifferentStoredModel()
    {
        FaceReferenceResolution result = new FaceReferenceResolver().Resolve(
            CreateReference(importIndexHint: 4),
            CreateModelIdentity(new string('b', 64)),
            CreateModelIdentity(new string('b', 64)),
            [CreateFeature(4)]);

        Assert.Equal(FaceReferenceResolutionStatus.ReferenceModelMismatch, result.Status);
        Assert.Null(result.NativeFaceIndex);
    }

    private static FaceReference CreateReference(int importIndexHint) => new(
        FaceRefId.New(),
        SourceHash,
        importIndexHint,
        FaceGeometryKind.Plane,
        new AreaMm2(1_200),
        new Vec3(10, 20, 30),
        "outer-loop-a1",
        ["adjacent-b", "adjacent-a"]);

    private static ModelIdentity CreateModelIdentity(
        string sourceHash = SourceHash,
        double sourceUnitToMillimeter = 1,
        string importerName = "STEP",
        string importerVersion = "reader-v1",
        string faceFingerprintVersion = "face-v1") => new(
        sourceHash,
        sourceUnitToMillimeter,
        importerName,
        importerVersion,
        faceFingerprintVersion);

    private static NativeFaceFeature CreateFeature(
        int nativeFaceIndex,
        FaceGeometryKind geometryKind = FaceGeometryKind.Plane,
        string boundaryFingerprint = "outer-loop-a1",
        AreaMm2? surfaceArea = null,
        Vec3? center = null) => new(
        nativeFaceIndex,
        geometryKind,
        surfaceArea ?? new AreaMm2(1_200),
        center ?? new Vec3(10, 20, 30),
        boundaryFingerprint,
        ["adjacent-a", "adjacent-b"]);
}
