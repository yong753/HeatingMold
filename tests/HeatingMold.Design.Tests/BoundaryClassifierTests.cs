using HeatingMold.Core.Geometry;
using HeatingMold.Design.Regions;

namespace HeatingMold.Design.Tests;

/// <summary>경계 링의 포함 깊이와 비정상 교차를 검사합니다.</summary>
public sealed class BoundaryClassifierTests
{
    /// <summary>중첩된 링을 outer·hole·island 순서로 분류합니다.</summary>
    [Fact]
    public void Classify_NestedRingsAlternatesOuterAndHole()
    {
        IReadOnlyList<Vec2>[] rings =
        [
            Rectangle(0, 0, 100, 100),
            Rectangle(20, 20, 80, 80),
            Rectangle(40, 40, 60, 60)
        ];

        var result = new BoundaryClassifier().Classify(rings);

        Assert.True(result.IsSuccess);
        ClassifiedBoundaryRing[] classified = result.GetValueOrThrow().ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, classified.Select(ring => ring.NestingDepth));
        Assert.Equal(new[] { false, true, false }, classified.Select(ring => ring.IsHole));
    }

    /// <summary>서로 떨어진 성분은 각각 바깥 경계로 유지합니다.</summary>
    [Fact]
    public void Classify_DisjointRingsAreSeparateOuterBoundaries()
    {
        var result = new BoundaryClassifier().Classify(
        [
            Rectangle(0, 0, 10, 10),
            Rectangle(20, 0, 30, 10)
        ]);

        Assert.True(result.IsSuccess);
        Assert.All(result.GetValueOrThrow(), ring => Assert.False(ring.IsHole));
    }

    /// <summary>서로 교차하거나 접하는 링은 임의의 포함 순서로 분류하지 않습니다.</summary>
    [Fact]
    public void Classify_TouchingOrCrossingRingsReturnsFailure()
    {
        var result = new BoundaryClassifier().Classify(
        [
            Rectangle(0, 0, 10, 10),
            Rectangle(10, 2, 20, 8)
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "BoundaryLoopsTouchOrCross");
    }

    /// <summary>자기 교차 bow-tie 링을 유효한 바깥 경계로 취급하지 않습니다.</summary>
    [Fact]
    public void Classify_SelfIntersectingRingReturnsFailure()
    {
        IReadOnlyList<Vec2> bowTie = [new(0, 0), new(10, 10), new(0, 10), new(10, 0)];

        var result = new BoundaryClassifier().Classify([bowTie]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "SelfIntersectingBoundary" || issue.Code == "DegenerateBoundaryRing");
    }

    private static IReadOnlyList<Vec2> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)
    ];
}
