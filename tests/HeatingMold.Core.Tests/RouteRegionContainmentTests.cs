using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class RouteRegionContainmentTests
{
    private static Vec2[] Rect(double x0, double y0, double x1, double y1) =>
        new Vec2[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    /// <summary>직선 변 폴리곤을 정확 경계 질의로 흉내내는 테스트 어댑터.</summary>
    private sealed class SegmentBoundary : IExactRegionBoundary
    {
        private readonly List<(Vec2 A, Vec2 B)> _segments = new();

        public SegmentBoundary(params Vec2[][] rings)
        {
            foreach (Vec2[] ring in rings)
                for (int i = 0; i < ring.Length; i++)
                    _segments.Add((ring[i], ring[(i + 1) % ring.Length]));
        }

        public RegionRayResult CastRay(Vec2 origin, Vec2 direction)
        {
            double dx = direction.X, dy = direction.Y;
            if (!double.IsFinite(dx) || !double.IsFinite(dy) ||
                !double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
                return new RegionRayResult(RegionRayStatus.Failure, Array.Empty<RegionBoundaryHit>(),
                    "유한하지 않은 질의");
            if (_segments.Count == 0)
                return new RegionRayResult(RegionRayStatus.Failure, Array.Empty<RegionBoundaryHit>(),
                    "경계 정보가 없습니다");
            var hits = new List<RegionBoundaryHit>();
            foreach ((Vec2 a, Vec2 b) in _segments)
            {
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double ox = a.X - origin.X, oy = a.Y - origin.Y;
                double denominator = dx * ey - dy * ex;
                if (Math.Abs(denominator) <= 1e-12)
                {
                    // 광선과 평행. 같은 직선 위에 있으면 겹침(모호)이다.
                    if (Math.Abs(dx * oy - dy * ox) <= 1e-9 && (ex * dx + ey * dy) < 0)
                        continue;
                    if (Math.Abs(dx * oy - dy * ox) <= 1e-9)
                        return new RegionRayResult(RegionRayStatus.Ambiguous,
                            Array.Empty<RegionBoundaryHit>(), "경계와 겹칩니다");
                    continue;
                }
                double t = (ox * ey - oy * ex) / denominator;
                double u = (ox * dy - oy * dx) / denominator;
                if (t <= 0) continue;
                if (u < 0 || u > 1) continue;
                Vec2 point = new(origin.X + dx * t, origin.Y + dy * t);
                hits.Add(new RegionBoundaryHit(point, u <= 1e-9 || u >= 1 - 1e-9));
            }
            return hits.Count == 0
                ? new RegionRayResult(RegionRayStatus.Miss, Array.Empty<RegionBoundaryHit>(), string.Empty)
                : new RegionRayResult(RegionRayStatus.Hit, hits, string.Empty);
        }

        public RegionBoundaryProbe ProbeDistance(Vec2 point, double maxDistance)
        {
            if (_segments.Count == 0)
                return new RegionBoundaryProbe(RegionRayStatus.Failure, 0, "경계 정보가 없습니다");
            double nearest = double.PositiveInfinity;
            foreach ((Vec2 a, Vec2 b) in _segments)
            {
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double lengthSquared = ex * ex + ey * ey;
                double u = lengthSquared <= 0 ? 0 :
                    ((point.X - a.X) * ex + (point.Y - a.Y) * ey) / lengthSquared;
                u = Math.Clamp(u, 0, 1);
                Vec2 closest = new(a.X + ex * u, a.Y + ey * u);
                nearest = Math.Min(nearest, closest.DistanceTo(point));
            }
            return nearest <= maxDistance
                ? new RegionBoundaryProbe(RegionRayStatus.Hit, nearest, string.Empty)
                : new RegionBoundaryProbe(RegionRayStatus.Miss, 0, string.Empty);
        }
    }

    private static bool StaysInRegion(ExactRegionContainment containment, Vec2 a, Vec2 b) =>
        containment.ValidateSegments(new[] { (a, b) },
            Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(), out _);

    private static bool StaysInRegion(ExactRegionContainment containment, Vec2 a, Vec2 b,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions) =>
        containment.ValidateSegments(new[] { (a, b) }, exclusions, out _);

    private static ExactRegionContainment Region(params Vec2[][] rings) =>
        new(new SegmentBoundary(rings));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsHalfMillimeterHoleWhoseEdgesCoincideWithOldSamples(bool reverse)
    {
        var region = Region(Rect(-1, -1, 11, 1), Rect(5, -0.5, 5.5, 0.5));
        Vec2 a = new(0, 0), b = new(10, 0);

        bool valid = region.ValidateSegments(new[] { (reverse ? b : a, reverse ? a : b) },
            Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(), out string failure);

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
        var region = Region(Rect(-1, -1, 11, 1));
        var exclusions = new[] { (5.0, -0.5, 5.0 + width, 0.5) };

        Assert.False(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0), exclusions));
    }

    [Fact]
    public void DoesNotMergeDistinctIntersectionsUsingFixedParameterTolerance()
    {
        var region = Region(Rect(-1, -1, 1_000_000_001, 1));
        var exclusions = new[] { (500_000_000.0, -0.5, 500_000_000.00001, 0.5) };

        Assert.False(StaysInRegion(region, new Vec2(0, 0), new Vec2(1_000_000_000, 0), exclusions));
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
        Assert.True(StaysInRegion(region, Transform(new Vec2(0, 0.6)), Transform(new Vec2(10, 0.6))));
    }

    [Fact]
    public void AllowsSegmentAlongOuterBoundary()
    {
        var region = Region(Rect(0, 0, 10, 10));

        Assert.True(StaysInRegion(region, new Vec2(0, 0), new Vec2(10, 0)));
        Assert.True(StaysInRegion(region, new Vec2(0, 0), new Vec2(0, 10)));
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
            new(10, 10), new(0, 10),
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

        Assert.False(region.ValidateSegments(new[] { (new Vec2(1, 1), invalid) },
            Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(), out string failure));
        Assert.Contains("유한", failure);
        Assert.False(StaysInRegion(region, invalid, new Vec2(1, 1)));
    }

    [Fact]
    public void PreservesDegenerateSegmentAndEmptyListHandling()
    {
        var region = Region(Rect(0, 0, 10, 10));

        Assert.True(StaysInRegion(region, new Vec2(-5, -5), new Vec2(-5, -5)));
        Assert.True(StaysInRegion(region, new Vec2(-5, -5), new Vec2(-5 + 0.0000000005, -5)));
        Assert.True(region.ValidateSegments(Array.Empty<(Vec2 A, Vec2 B)>(),
            Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(), out string failure));
        Assert.Empty(failure);
    }

    [Fact]
    public void RejectsMissingBoundaryInformation()
    {
        var region = new ExactRegionContainment(new SegmentBoundary());

        Assert.False(region.ValidateSegments(new[] { (new Vec2(1, 1), new Vec2(9, 9)) },
            Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(), out string failure));
        Assert.Contains("경계", failure);
    }
}
