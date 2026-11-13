using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class RegionOpsTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0,y0), new(x1,y0), new(x1,y1), new(x0,y1) };

    [Fact]
    public void UnionPolygonsMergesOverlappingFaces()
    {
        var union = RegionOps.UnionPolygons(new IReadOnlyList<Vec2>[] { Rect(0,0,10,10), Rect(5,0,15,10) });
        Assert.Single(union);
        Assert.Equal(150, Math.Abs(RegionOps.SignedArea(union[0])), 6);
    }

    [Fact]
    public void SubtractRectanglesRemovesExclusion()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0,0,20,10) },
            new[] { (8d, 2d, 12d, 8d) });
        Assert.Equal(176, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesSplitsRegionIntoTwoRings()
    {
        // 제외가 영역 높이를 완전히 가로지르면 남은 발열영역은 두 조각으로 분리된다.
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (8d, -1d, 12d, 11d) });
        Assert.Equal(2, remaining.Count);
        Assert.Equal(160, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void BuildGenerationRegionSubtractsRawExclusions()
    {
        // 외곽 0..40x0..20에서 제외 10..14x6..10을 그대로 차감한다.
        // 800 - 16 = 784가 남고, 원본 제외영역은 그대로다.
        var exclusions = new[] { (10d, 6d, 14d, 10d) };
        var remaining = RegionOps.BuildGenerationRegion(Rect(0, 0, 40, 20), exclusions);

        Assert.Equal(784, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
        Assert.Equal((10d, 6d, 14d, 10d), exclusions[0]);
    }

    [Fact]
    public void BuildGenerationRegionMatchesSubtractionOfPatternRegion()
    {
        var outer = new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) };
        var exclusions = new[] { (8d, 2d, 12d, 8d) };

        var generation = RegionOps.BuildGenerationRegion(outer[0], exclusions);
        var existing = RegionOps.SubtractRectangles(
            RegionOps.BuildPatternRegion(outer[0], null), exclusions);

        Assert.Equal(existing.Sum(r => RegionOps.SignedArea(r)),
            generation.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesUnionsOverlappingExclusions()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (2d, 2d, 8d, 8d), (6d, 2d, 12d, 8d) });
        Assert.Equal(140, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesOutsideRegionKeepsArea()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (30d, 30d, 40d, 40d) });
        Assert.Equal(200, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesCoveringRegionReturnsEmpty()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (-1d, -1d, 21d, 11d) });
        Assert.Empty(remaining);
    }

    // ---- 제외영역 정책: 발열선은 발열영역(외곽−제외) 안에서만 생성되고, 턴커넥터는 이전 발열선
    // 끝점에서 다음 발열선 시작점까지의 최단 직선(2점)이다. 제외영역이 영역을 가로질러 발열영역이
    // 분리되면 연결 성분마다 독립 경로가 된다. 갈라진 구간은 같은 레인에서 이어지지 않는다.

    /// <summary>100×40 / Margin 0 / Spacing 10 → 레인 y=5,15,25,35 (각 런 0~100). 분리되면 실패한다.</summary>
}
