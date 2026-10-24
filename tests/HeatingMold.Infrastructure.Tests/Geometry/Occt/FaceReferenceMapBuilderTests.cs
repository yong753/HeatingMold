using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>프로젝트 Face 참조 집합이 완전하고 일대일로 복원되는지 검사합니다.</summary>
public sealed class FaceReferenceMapBuilderTests
{
    private const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>모든 특징이 유일하게 해석되면 안정 ID와 현재 Native 번호를 연결합니다.</summary>
    [Fact]
    public void Build_ReturnsCompleteMapForUniqueReferences()
    {
        FaceReference firstReference = CreateReference(4);
        FaceReference secondReference = CreateReference(12, boundaryFingerprint: "boundary-12");
        DesignProject project = CreateProject([firstReference, secondReference]);

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(),
            [CreateFeature(4), CreateFeature(12, boundaryFingerprint: "boundary-12")]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(4, result.Value[firstReference.Id]);
        Assert.Equal(12, result.Value[secondReference.Id]);
    }

    /// <summary>한 참조라도 모호하면 다른 참조가 해석됐어도 부분 맵을 반환하지 않습니다.</summary>
    [Fact]
    public void Build_RejectsAmbiguousReferenceWithoutPartialMap()
    {
        FaceReference resolvedReference = CreateReference(4);
        FaceReference ambiguousReference = CreateReference(1);
        DesignProject project = CreateProject([resolvedReference, ambiguousReference]);

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(),
            [
                CreateFeature(1, boundaryFingerprint: "different"),
                CreateFeature(4),
                CreateFeature(6),
                CreateFeature(8)
            ]);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        ValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal("FaceReferenceAmbiguous", issue.Code);
        Assert.Equal([ambiguousReference.Id.Value], issue.EntityIds);
        Assert.Contains("4, 6, 8", issue.Evidence[1]);
    }

    /// <summary>현재 모델 해시가 바뀌면 저장 Face 대응을 중단합니다.</summary>
    [Fact]
    public void Build_RejectsDifferentCurrentModelIdentity()
    {
        FaceReference reference = CreateReference(4);
        DesignProject project = CreateProject([reference]);

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(new string('b', 64)),
            [CreateFeature(4)]);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("FaceReferenceSourceModelMismatch", Assert.Single(result.Issues).Code);
    }

    /// <summary>현재 구현이 지원하지 않는 저장 fingerprint 버전은 Face 대응을 시작하지 않습니다.</summary>
    [Fact]
    public void Build_RejectsUnsupportedStoredFingerprintVersion()
    {
        FaceReference reference = CreateReference(4);
        DesignProject project = CreateProject(
            [reference],
            CreateModelIdentity(faceFingerprintVersion: "face-fingerprint-legacy"));

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(),
            [CreateFeature(4)]);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        ValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal("UnsupportedFaceFingerprintVersion", issue.Code);
        Assert.Contains("face-fingerprint-legacy", issue.Evidence[0]);
        Assert.Contains(FaceReferenceFeatureBuilder.FingerprintVersion, issue.Evidence[0]);
    }

    /// <summary>저장 identity가 현재 특징 생성 알고리즘과 다르면 빈 설계도 복원하지 않습니다.</summary>
    [Fact]
    public void Build_RejectsUnsupportedCurrentFingerprintVersion()
    {
        DesignProject project = CreateProject([]);

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(faceFingerprintVersion: "face-fingerprint-legacy"),
            [CreateFeature(4)]);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        ValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal("UnsupportedFaceFingerprintVersion", issue.Code);
        Assert.Contains("face-fingerprint-legacy", issue.Evidence[0]);
    }

    /// <summary>Face 참조가 없는 프로젝트는 비어 있는 유효한 맵으로 복원합니다.</summary>
    [Fact]
    public void Build_ReturnsEmptyMapWhenProjectHasNoFaceReferences()
    {
        DesignProject project = CreateProject([]);

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(),
            [CreateFeature(4)]);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    /// <summary>서로 다른 영속 ID가 같은 Native 면을 가리키면 대응을 거부합니다.</summary>
    [Fact]
    public void Build_RejectsMultipleReferencesMappedToOneNativeFace()
    {
        FaceReference firstReference = CreateReference(4);
        FaceReference secondReference = CreateReference(9);
        DesignProject project = CreateProject([firstReference, secondReference]);

        var result = new FaceReferenceMapBuilder().Build(
            project,
            CreateModelIdentity(),
            [CreateFeature(4), CreateFeature(9, boundaryFingerprint: "different")]);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        ValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal("FaceReferenceNativeIndexCollision", issue.Code);
        Assert.Equal([firstReference.Id.Value, secondReference.Id.Value], issue.EntityIds);
    }

    /// <summary>중복된 Native 번호가 들어오면 개별 특징 대응을 시작하기 전에 실패합니다.</summary>
    [Fact]
    public void Build_RejectsDuplicateNativeFaceIndices()
    {
        var result = new FaceReferenceMapBuilder().Build(
            CreateProject([CreateReference(4)]),
            CreateModelIdentity(),
            [CreateFeature(4), CreateFeature(4, boundaryFingerprint: "different")]);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidNativeFaceSet", Assert.Single(result.Issues).Code);
    }

    private static DesignProject CreateProject(
        IEnumerable<FaceReference> faceReferences,
        ModelIdentity? modelIdentity = null)
    {
        DateTimeOffset created = DateTimeOffset.UnixEpoch;
        return new DesignProject(
            ProjectId.New(),
            schemaVersion: 1,
            new DesignRevision(0),
            "Face restore test",
            created,
            created,
            modelIdentity ?? CreateModelIdentity(),
            new WorkingCoordinateSystemDefinition(
                default,
                new Vec3(1, 0, 0),
                new Vec3(0, 1, 0),
                new Vec3(0, 0, 1)),
            faceReferences,
            [],
            wireSpecification: null,
            placementConstraints: null,
            [],
            [],
            [],
            []);
    }

    private static FaceReference CreateReference(
        int importIndexHint,
        string boundaryFingerprint = "boundary-4") => new(
        FaceRefId.New(),
        SourceHash,
        importIndexHint,
        FaceGeometryKind.Plane,
        new AreaMm2(1_200),
        new Vec3(10, 20, 30),
        boundaryFingerprint,
        []);

    private static ModelIdentity CreateModelIdentity(
        string sourceHash = SourceHash,
        string faceFingerprintVersion = FaceReferenceFeatureBuilder.FingerprintVersion) => new(
        sourceHash,
        sourceUnitToMillimeter: 1,
        importerName: "STEP",
        importerVersion: "reader-v1",
        faceFingerprintVersion: faceFingerprintVersion);

    private static NativeFaceFeature CreateFeature(
        int nativeFaceIndex,
        string boundaryFingerprint = "boundary-4") => new(
        nativeFaceIndex,
        FaceGeometryKind.Plane,
        new AreaMm2(1_200),
        new Vec3(10, 20, 30),
        boundaryFingerprint,
        []);
}
