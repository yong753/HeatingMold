using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;

namespace HeatingMold.Design.Tests;

/// <summary>선택 topology에서 안정 경계 ID를 보존·복제해 영역을 만드는지 검사합니다.</summary>
public sealed class RegionBuilderTests
{
    /// <summary>영역 생성은 원본 루프를 유지하고 출구 참조를 새 유효 경계 ID로 옮깁니다.</summary>
    [Fact]
    public void Build_ClonesEffectiveBoundaryAndRemapsExitSide()
    {
        DesignProject project = CreateProject([FaceRefId.New()]);
        FaceRefId faceId = project.FaceReferences[0].Id;
        BoundaryLoop sourceLoop = CreateLoop(faceId, 0);
        var topology = new SelectedTopology(
            [new TopologyComponent([faceId], [sourceLoop])],
            []);
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(sourceLoop.Segments[0].Id, 0.2, 0.8)]);

        var result = new RegionBuilder().Build(
            project,
            RegionId.New(),
            [faceId],
            GeometryQueryResult<SelectedTopology>.Succeeded(topology),
            exitSide);

        Assert.True(result.IsSuccess);
        HeatingRegion region = result.GetValueOrThrow();
        BoundaryLoop original = Assert.Single(region.OriginalBoundaryLoops);
        BoundaryLoop effective = Assert.Single(region.EffectiveBoundaryLoops);
        Assert.Equal(sourceLoop.Id, original.Id);
        Assert.NotEqual(original.Id, effective.Id);
        Assert.NotEqual(sourceLoop.Segments[0].Id, effective.Segments[0].Id);
        BoundarySegmentRange remappedRange = Assert.Single(region.ExitSide.Ranges);
        Assert.Equal(effective.Segments[0].Id, remappedRange.BoundarySegmentId);
        Assert.Equal(0.2, remappedRange.StartParameter);
        Assert.Equal(0.8, remappedRange.EndParameter);
        Assert.Empty(result.Issues);
    }

    /// <summary>분리 성분을 보존하되 한 경로 생성에 추가 조정이 필요하다는 경고를 줍니다.</summary>
    [Fact]
    public void Build_MultipleComponentsReturnsRegionWithWarning()
    {
        DesignProject project = CreateProject([FaceRefId.New(), FaceRefId.New()]);
        FaceRefId firstFace = project.FaceReferences[0].Id;
        FaceRefId secondFace = project.FaceReferences[1].Id;
        BoundaryLoop firstLoop = CreateLoop(firstFace, 0);
        BoundaryLoop secondLoop = CreateLoop(secondFace, 200);
        var topology = new SelectedTopology(
        [
            new TopologyComponent([firstFace], [firstLoop]),
            new TopologyComponent([secondFace], [secondLoop])
        ], []);
        var exitSide = new ExitSideReference(
        [
            new BoundarySegmentRange(firstLoop.Segments[0].Id, 0, 1),
            new BoundarySegmentRange(secondLoop.Segments[0].Id, 0, 1)
        ]);

        var result = new RegionBuilder().Build(
            project,
            RegionId.New(),
            [firstFace, secondFace],
            GeometryQueryResult<SelectedTopology>.Succeeded(topology),
            exitSide);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.GetValueOrThrow().OriginalBoundaryLoops.Count);
        Assert.Equal("MultipleRegionComponents", Assert.Single(result.Issues).Code);
        Assert.Equal(ValidationSeverity.Warning, result.Issues[0].Severity);
    }

    /// <summary>Native topology와 선택 Face 집합이 다르면 부분 영역을 만들지 않습니다.</summary>
    [Fact]
    public void Build_TopologyFaceMismatchReturnsFailure()
    {
        DesignProject project = CreateProject([FaceRefId.New(), FaceRefId.New()]);
        FaceRefId selectedFace = project.FaceReferences[0].Id;
        FaceRefId otherFace = project.FaceReferences[1].Id;
        BoundaryLoop loop = CreateLoop(otherFace, 0);
        var topology = new SelectedTopology(
            [new TopologyComponent([otherFace], [loop])],
            []);

        var result = new RegionBuilder().Build(
            project,
            RegionId.New(),
            [selectedFace],
            GeometryQueryResult<SelectedTopology>.Succeeded(topology),
            new ExitSideReference([new BoundarySegmentRange(loop.Segments[0].Id, 0, 1)]));

        Assert.False(result.IsSuccess);
        Assert.Equal("TopologyFaceSetMismatch", Assert.Single(result.Issues).Code);
    }

    private static DesignProject CreateProject(IReadOnlyList<FaceRefId> faceIds)
    {
        byte[] modelBytes = [10, 20, 30, 40];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant();
        var identity = new ModelIdentity(sourceHash, 1, "Fixture", "1", "face-v1");
        FaceReference[] faces = faceIds.Select((id, index) => new FaceReference(
            id, sourceHash, index, FaceGeometryKind.Plane, new AreaMm2(10_000), default,
            $"boundary-{index}", [])).ToArray();
        DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        return new DesignProject(
            ProjectId.New(), 1, new DesignRevision(0), "Region fixture", now, now,
            identity,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            faces, [], null, null, [], [], [], [], DesignRuleSet.Initial);
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId, double offset)
    {
        Vec2[] vertices =
        [
            new(offset, 0), new(offset + 100, 0), new(offset + 100, 100), new(offset, 100)
        ];
        BoundarySegment[] segments = vertices.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], faceId)).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }
}
