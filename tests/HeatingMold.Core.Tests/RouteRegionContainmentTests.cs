using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class RouteRegionContainmentTests
{
    private static Vec2[] Rect(double x0, double y0, double x1, double y1) =>
        new Vec2[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    private static IReadOnlyList<Vec2>[] Region(params Vec2[][] rings) => rings;

    private static bool StaysInRegion(IReadOnlyList<IReadOnlyList<Vec2>> region, Vec2 a, Vec2 b) =>
        RouteVertexValidator.SegmentsStayInRegion(region, new[] { (a, b) }, out _);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsHalfMillimeterHoleWhoseEdgesCoincideWithOldSamples(bool reverse)
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5.5, 0.5));
        Vec2 a = new(0, 0), b = new(10, 0);

        // Old 0.5 mm samples hit x=5 and x=5.5, both allowed boundary points,
        // but never the open excluded interval between them.
        bool valid = RouteVertexValidator.SegmentsStayInRegion(region,
            new[] { reverse ? (b, a) : (a, b) }, out string failure);

        Assert.False(valid);
        Assert.Contains("벗어납니다", failure);
    }

    [Theory]
    [InlineData(0.000004)]
    [InlineData(0.00001)]
    [InlineData(0.0039)]
    [InlineData(0.4999)]
    public void RejectsNarrowExclusionsWithoutRoundingToClipperPrecision(double width)
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5 + width, 0.5));

        Assert.False(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0)));
    }

    [Fact]
    public void DoesNotMergeDistinctIntersectionsUsingFixedParameterTolerance()
    {
        var region = Region(Rect(-1, -1, 1_000_000_001, 1),
            Rect(500_000_000, -0.5, 500_000_000.00001, 0.5));

        Assert.False(StaysInRegion(region, new Vec2(0, 0), new Vec2(1_000_000_000, 0)));
    }

    [Fact]
    public void RejectsVeryShortSegmentBetweenTwoHoleBoundaryPoints()
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5.000004, 0.5));

        Assert.False(StaysInRegion(region, new Vec2(5, 0), new Vec2(5.000004, 0)));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(90)]
    [InlineData(137)]
    [InlineData(-31)]
    public void RotationAndTranslationPreserveCrossingAndBoundaryFollowing(double degrees)
    {
        double angle = degrees * Math.PI / 180;
        Vec2 Transform(Vec2 p) => p.Rotate(angle) + new Vec2(123.25, -71.5);
        var region = Region(Rect(-1, -1, 11, 1).Select(Transform).ToArray(),
            Rect(5, -0.5, 5.5, 0.5).Select(Transform).ToArray());

        Assert.False(StaysInRegion(region, Transform(new Vec2(0, 0)), Transform(new Vec2(10, 0))));
        Assert.True(StaysInRegion(region, Transform(new Vec2(0, 0.5)), Transform(new Vec2(10, 0.5))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllowsCollinearOverlapWithOuterAndHoleEdges(bool reverse)
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5.5, 0.5));
        Vec2 a = new(0, 0.5), b = new(10, 0.5);
        Vec2 c = new(-1, -1), d = new(11, -1);

        Assert.True(StaysInRegion(region, reverse ? b : a, reverse ? a : b));
        Assert.True(StaysInRegion(region, reverse ? d : c, reverse ? c : d));
    }

    [Fact]
    public void AllowsVertexTangencyButRejectsCrossingThroughHoleVertices()
    {
        var tangent = Region(Rect(-1, -1, 11, 2),
            new Vec2[] { new(4, 1), new(5, 0), new(6, 1) });
        var crossing = Region(Rect(-1, -1, 11, 2),
            new Vec2[] { new(4, 0), new(5, -0.5), new(6, 0), new(5, 0.5) });

        Assert.True(StaysInRegion(tangent, new Vec2(0, 0), new Vec2(10, 0)));
        Assert.False(StaysInRegion(crossing, new Vec2(0, 0), new Vec2(10, 0)));
    }

    [Fact]
    public void AllowsBoundaryEndpointWhenRemainingSegmentStaysInside()
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5.5, 0.5));

        Assert.True(StaysInRegion(region, new Vec2(0, 0), new Vec2(5, 0)));
        Assert.True(StaysInRegion(region, new Vec2(5.5, 0), new Vec2(10, 0)));
        Assert.False(StaysInRegion(region, new Vec2(5, 0), new Vec2(5.5, 0)));
    }

    [Fact]
    public void RejectsConcaveNotchEvenWhenEndpointsAndOldSamplesAreInsideOrOn()
    {
        var region = Region(new Vec2[]
        {
            new(-1, -1), new(11, -1), new(11, 1), new(5.5, 1),
            new(5.5, -0.5), new(5, -0.5), new(5, 1), new(-1, 1),
        });

        Assert.False(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0)));
        Assert.True(StaysInRegion(region, new Vec2(0, -0.5), new Vec2(10, -0.5)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UsesEvenOddAcrossNestedRingsRegardlessOfWinding(bool reverseOuter, bool reverseHole)
    {
        Vec2[] outer = Rect(0, 0, 10, 10), hole = Rect(2, 2, 8, 8);
        if (reverseOuter) Array.Reverse(outer);
        if (reverseHole) Array.Reverse(hole);
        var region = Region(outer, hole, Rect(4, 4, 6, 6));

        Assert.True(StaysInRegion(region, new Vec2(4.5, 5), new Vec2(5.5, 5)));
        Assert.False(StaysInRegion(region, new Vec2(1, 5), new Vec2(9, 5)));
        Assert.False(StaysInRegion(region, new Vec2(3, 3), new Vec2(7, 3)));
    }

    [Fact]
    public void RejectsGapBetweenDisjointOuterRings()
    {
        var region = Region(Rect(0, -1, 5, 1), Rect(5.5, -1, 10, 1));

        Assert.False(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0)));
    }

    [Theory]
    [InlineData(0.0000005, true)]
    [InlineData(0.000001, true)]
    [InlineData(0.0000011, false)]
    public void PreservesExistingBoundaryTolerance(double offset, bool expected)
    {
        var region = Region(Rect(0, 0, 10, 10));

        Assert.Equal(expected, StaysInRegion(region, new Vec2(1, -offset), new Vec2(9, -offset)));
    }

    [Fact]
    public void AllowsHoleNarrowerThanExistingBoundaryTolerance()
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5.0000015, 0.5));

        Assert.True(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0)));
    }

    [Fact]
    public void HandlesExplicitlyClosedRingsAndRepeatedVertices()
    {
        var region = Region(new Vec2[]
        {
            new(0, 0), new(5, 0), new(5, 0), new(10, 0),
            new(10, 10), new(0, 10), new(0, 0),
        });

        Assert.True(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0)));
        Assert.True(StaysInRegion(region, new Vec2(1, 1), new Vec2(9, 9)));
        Assert.False(StaysInRegion(region, new Vec2(-0.00001, 5), new Vec2(9, 5)));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void RejectsNonFiniteSegmentLength(double x, double y)
    {
        var region = Region(Rect(0, 0, 10, 10));
        var invalid = new Vec2(x, y);

        Assert.False(RouteVertexValidator.SegmentsStayInRegion(region,
            new[] { (new Vec2(1, 1), invalid) }, out string failure));
        Assert.Contains("유한", failure);
        Assert.False(StaysInRegion(region, invalid, new Vec2(1, 1)));
    }

    [Fact]
    public void PreservesDegenerateSegmentAndEmptyListHandling()
    {
        var region = Region(Rect(0, 0, 10, 10));

        Assert.True(StaysInRegion(region, new Vec2(-5, -5), new Vec2(-5, -5)));
        Assert.True(StaysInRegion(region, new Vec2(-5, -5), new Vec2(-5 + 0.0000000005, -5)));
        Assert.True(RouteVertexValidator.SegmentsStayInRegion(region,
            Array.Empty<(Vec2 A, Vec2 B)>(), out string failure));
        Assert.Empty(failure);
    }

    [Fact]
    public void RequiresValidOuterRingAndIgnoresShortAdditionalRings()
    {
        var segment = new[] { (new Vec2(1, 1), new Vec2(9, 9)) };

        Assert.False(RouteVertexValidator.SegmentsStayInRegion(Array.Empty<IReadOnlyList<Vec2>>(),
            segment, out string failure));
        Assert.Contains("정보", failure);
        Assert.False(RouteVertexValidator.SegmentsStayInRegion(Region(new Vec2[] { new(0, 0) }),
            segment, out _));
        Assert.True(RouteVertexValidator.SegmentsStayInRegion(
            Region(Rect(0, 0, 10, 10), new Vec2[] { new(5, 5), new(6, 6) }), segment, out _));
    }
}
