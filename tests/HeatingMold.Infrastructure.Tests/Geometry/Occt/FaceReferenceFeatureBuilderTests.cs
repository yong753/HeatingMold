using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>Face 특징 변환과 경계 fingerprint의 결정성을 확인합니다.</summary>
public sealed class FaceReferenceFeatureBuilderTests
{
    private static readonly Vec3[] Square =
    [
        new(0, 0, 0),
        new(2, 0, 0),
        new(2, 2, 0),
        new(0, 2, 0)
    ];

    /// <summary>시작점과 진행 방향이 다른 같은 경계에서 같은 fingerprint를 만듭니다.</summary>
    [Fact]
    public void Build_CanonicalizesBoundaryStartAndDirection()
    {
        NativeFaceFeature first = Build(Snapshot(0, Square));
        NativeFaceFeature second = Build(Snapshot(0, [Square[2], Square[1], Square[0], Square[3]]));

        Assert.Equal(first.BoundaryFingerprint, second.BoundaryFingerprint);
    }

    /// <summary>홀 경계와 외곽 경계의 구분 및 루프 집합을 fingerprint에 반영합니다.</summary>
    [Fact]
    public void Build_IncludesHoleRoleAndAllBoundaryLoops()
    {
        NativeFaceFeature withoutHole = Build(Snapshot(0, Square));
        NativeFaceFeature withHole = Build(Snapshot(
            0,
            Square,
            new FaceBoundaryLoopSnapshot(true, true,
            [
                new Vec3(0.5, 0.5, 0),
                new Vec3(0.5, 1, 0),
                new Vec3(1, 1, 0),
                new Vec3(1, 0.5, 0)
            ])));

        Assert.NotEqual(withoutHole.BoundaryFingerprint, withHole.BoundaryFingerprint);
    }

    /// <summary>자연 경계 와이어가 없는 닫힌 곡면도 명시적 fingerprint를 가집니다.</summary>
    [Fact]
    public void Build_RecordsClosedSurfaceWithoutBoundaryWires()
    {
        var face = new NativeFaceFeatureSnapshot(
            0,
            FaceGeometryKind.Sphere,
            new AreaMm2(12),
            default,
            [],
            []);

        NativeFaceFeature feature = Build(face);

        Assert.NotEmpty(feature.BoundaryFingerprint);
    }

    /// <summary>공유 Edge 인접 Face 경계 특징을 이웃 목록에 연결합니다.</summary>
    [Fact]
    public void Build_UsesNeighborBoundaryFingerprints()
    {
        NativeFaceFeature[] features = new FaceReferenceFeatureBuilder().Build(
        [
            Snapshot(0, Square, adjacent: [1]),
            Snapshot(1,
            [
                new Vec3(0, 0, 0),
                new Vec3(2, 0, 0),
                new Vec3(2, 0, 2),
                new Vec3(0, 0, 2)
            ],
            adjacent: [0])
        ]).ToArray();

        Assert.Equal(2, features.Length);
        Assert.Equal(features[1].BoundaryFingerprint, Assert.Single(features[0].AdjacentFaceFingerprints));
        Assert.Equal(features[0].BoundaryFingerprint, Assert.Single(features[1].AdjacentFaceFingerprints));
    }

    /// <summary>열린 경계나 대칭이 아닌 인접 자료를 특징으로 사용하지 않습니다.</summary>
    [Fact]
    public void Build_RejectsIncompleteBoundaryAndAsymmetricAdjacency()
    {
        var openBoundary = Snapshot(0, Square, closed: false);
        NativeFaceFeatureSnapshot[] asymmetricAdjacency =
        [
            Snapshot(0, Square, adjacent: [1]),
            Snapshot(1, Square)
        ];
        var builder = new FaceReferenceFeatureBuilder();

        Assert.Throws<ArgumentException>(() => builder.Build([openBoundary]));
        Assert.Throws<ArgumentException>(() => builder.Build(asymmetricAdjacency));
    }

    private static NativeFaceFeature Build(NativeFaceFeatureSnapshot snapshot) =>
        Assert.Single(new FaceReferenceFeatureBuilder().Build([snapshot]));

    private static NativeFaceFeatureSnapshot Snapshot(
        int index,
        IReadOnlyList<Vec3> outer,
        FaceBoundaryLoopSnapshot? hole = null,
        int[]? adjacent = null,
        bool closed = true) => new(
            index,
            FaceGeometryKind.Plane,
            new AreaMm2(4),
            default,
            adjacent ?? [],
            hole is null
                ? [new FaceBoundaryLoopSnapshot(false, closed, outer)]
                : [new FaceBoundaryLoopSnapshot(false, closed, outer), hole]);
}
