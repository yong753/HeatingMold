using HeatingMold.Core.Geometry;
using HeatingMold.Design.Regions;

namespace HeatingMold.Design.Tests;

/// <summary>투영 메시 경계가 링·홀·중첩 성분을 유지하는지 검증합니다.</summary>
public sealed class ProjectedModelBoundaryTests
{
    /// <summary>삼각형 합집합이 홀과 공유 seam을 올바르게 처리합니다.</summary>
    [Fact]
    public void TriangleUnionPreservesHoleAndRemovesSharedSeams()
    {
        IEnumerable<IReadOnlyList<Vec2>> triangles = Rect(0, 0, 10, 2)
            .Concat(Rect(0, 8, 10, 10))
            .Concat(Rect(0, 2, 2, 8))
            .Concat(Rect(8, 2, 10, 8));

        IReadOnlyList<ProjectedModelBoundaryRing> rings = ProjectedModelBoundary.FromTriangles(triangles, 0.05);

        Assert.Equal(2, rings.Count);
        Assert.Single(rings.Where(ring => ring.IsHole));
        Assert.Single(rings.Where(ring => !ring.IsHole));
        Assert.Equal(64, rings.Sum(ring => RegionOps.SignedArea(ring.Points)), 5);
        Assert.All(rings, ring => Assert.Equal(0.05, ring.MeshDeflection));

        IReadOnlyList<ProjectedModelBoundaryRing> sharedEdge = ProjectedModelBoundary.FromTriangles(
            Rect(0, 0, 5, 10).Concat(Rect(5, 0, 10, 10)), 0.05);

        Assert.Single(sharedEdge);
        Assert.Equal(100, RegionOps.SignedArea(sharedEdge[0].Points), 5);
    }

    /// <summary>홀 안에 포함된 별도 섬은 통과 구멍으로 분류하지 않습니다.</summary>
    [Fact]
    public void NestedIslandIsNotAThroughHole()
    {
        IEnumerable<IReadOnlyList<Vec2>> triangles = Rect(0, 0, 10, 2)
            .Concat(Rect(0, 8, 10, 10))
            .Concat(Rect(0, 2, 2, 8))
            .Concat(Rect(8, 2, 10, 8))
            .Concat(Rect(4, 4, 6, 6));

        IReadOnlyList<ProjectedModelBoundaryRing> rings = ProjectedModelBoundary.FromTriangles(triangles, 0.05);

        Assert.Contains(rings, ring => ring.NestingDepth == 2 && !ring.IsHole);
    }

    private static IEnumerable<IReadOnlyList<Vec2>> Rect(double x0, double y0, double x1, double y1)
    {
        yield return new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1) };
        yield return new[] { new Vec2(x0, y0), new Vec2(x0, y1), new Vec2(x1, y1) };
    }
}
