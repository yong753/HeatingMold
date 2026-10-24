using HeatingMold.Core.Geometry;
using HeatingMold.Design.Legacy;
using HeatingMold.Design.Regions;

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
    public void SerpentineComponentsNeverBridgeExcludedGap()
    {
        var paths = HeatingPathGenerator.GenerateComponents(Rect(0,0,100,40), new[] { (40d, 0d, 60d, 40d) },
            new PatternParameters { BoundaryMarginMm = 0, CenterPitchMm = 10 });
        Assert.Equal(8, paths.Count);
        Assert.All(paths, path => Assert.Equal(2, path.Count));
        Assert.All(paths, path => Assert.True(path.Max(p => p.X) <= 40 || path.Min(p => p.X) >= 60));
    }
}
