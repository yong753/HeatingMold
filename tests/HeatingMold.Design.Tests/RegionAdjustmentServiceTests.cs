using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Design.Regions;

namespace HeatingMold.Design.Tests;

/// <summary>영역 포함·제외 Boolean과 출구 측면 보존 규칙을 검사합니다.</summary>
public sealed class RegionAdjustmentServiceTests
{
    /// <summary>내부 제외 도형은 hole이 되고 외곽 출구 측면은 유지됩니다.</summary>
    [Fact]
    public void Apply_ExclusionCreatesHoleAndKeepsExitSide()
    {
        HeatingRegion region = CreateRegion();
        var result = new RegionAdjustmentService().Apply(
            region,
            [],
            [Rectangle(40, 40, 60, 60)]);

        Assert.True(result.IsSuccess);
        HeatingRegion adjusted = result.GetValueOrThrow();
        Assert.Equal(2, adjusted.EffectiveBoundaryLoops.Count);
        Assert.Single(adjusted.EffectiveBoundaryLoops.Where(loop => loop.IsHole));
        Assert.Single(adjusted.EffectiveBoundaryLoops.Where(loop => !loop.IsHole));
        Assert.Single(adjusted.ExclusionLoops);
        BoundarySegmentId exitSegmentId = Assert.Single(adjusted.ExitSide.Ranges).BoundarySegmentId;
        Assert.Contains(adjusted.EffectiveBoundaryLoops.SelectMany(loop => loop.Segments), segment => segment.Id == exitSegmentId);
    }

    /// <summary>선택 Face 바깥으로 나간 포함 도형은 선택 표면과의 교집합만 남깁니다.</summary>
    [Fact]
    public void Apply_InclusionCannotExpandBeyondSelectedSurface()
    {
        HeatingRegion region = CreateRegion();
        var result = new RegionAdjustmentService().Apply(
            region,
            [Rectangle(80, -20, 120, 20)],
            []);

        Assert.True(result.IsSuccess);
        IReadOnlyList<Vec2> vertices = Assert.Single(result.GetValueOrThrow().EffectiveBoundaryLoops).Vertices;
        Assert.Equal(80, vertices.Min(point => point.X), 6);
        Assert.Equal(100, vertices.Max(point => point.X), 6);
        Assert.Equal(0, vertices.Min(point => point.Y), 6);
        Assert.Equal(20, vertices.Max(point => point.Y), 6);
    }

    /// <summary>기존 인출 측면이 조정으로 사라지면 새 영역을 확정할 수 없게 합니다.</summary>
    [Fact]
    public void Apply_RemovingExitSideReturnsFailure()
    {
        HeatingRegion region = CreateRegion();
        var result = new RegionAdjustmentService().Apply(
            region,
            [Rectangle(20, 20, 80, 80)],
            []);

        Assert.False(result.IsSuccess);
        Assert.Equal("ExitSideRemovedByAdjustment", Assert.Single(result.Issues).Code);
    }

    /// <summary>영역 전체를 제거하는 제외 입력은 빈 발열 영역 오류로 반환합니다.</summary>
    [Fact]
    public void Apply_RemovingEntireSurfaceReturnsFailure()
    {
        HeatingRegion region = CreateRegion();
        var result = new RegionAdjustmentService().Apply(
            region,
            [],
            [Rectangle(-1, -1, 101, 101)]);

        Assert.False(result.IsSuccess);
        Assert.Equal("EmptyEffectiveRegion", Assert.Single(result.Issues).Code);
    }

    /// <summary>자기 교차 입력은 Clipper 처리 전에 경계 오류로 반환합니다.</summary>
    [Fact]
    public void Apply_SelfIntersectingAdjustmentReturnsFailure()
    {
        HeatingRegion region = CreateRegion();
        IReadOnlyList<Vec2> bowTie = [new(10, 10), new(90, 90), new(10, 90), new(90, 10)];

        var result = new RegionAdjustmentService().Apply(region, [bowTie], []);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code is "SelfIntersectingBoundary" or "DegenerateBoundaryRing");
    }

    private static HeatingRegion CreateRegion()
    {
        FaceRefId faceId = FaceRefId.New();
        BoundaryLoop original = CreateLoop(faceId, 0);
        BoundaryLoop effective = CreateLoop(faceId, 0);
        return new HeatingRegion(
            RegionId.New(),
            [faceId],
            [original],
            [],
            [],
            [effective],
            new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0, 1)]));
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId, double offset)
    {
        Vec2[] vertices = [new(offset, 0), new(offset + 100, 0), new(offset + 100, 100), new(offset, 100)];
        BoundarySegment[] segments = vertices.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], faceId)).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }

    private static IReadOnlyList<Vec2> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)
    ];
}
