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

    [Fact]
    public void SerpentineComponentsNeverBridgeExcludedGap()
    {
        var paths = Serpentine.GenerateComponents(Rect(0,0,100,40), new[] { (40d, 0d, 60d, 40d) },
            new PatternParams { Margin = 0, Spacing = 10 });
        Assert.Equal(8, paths.Count);
        Assert.All(paths, path => Assert.Equal(2, path.Count));
        Assert.All(paths, path => Assert.True(path.Max(p => p.X) <= 40 || path.Min(p => p.X) >= 60));
    }
}
