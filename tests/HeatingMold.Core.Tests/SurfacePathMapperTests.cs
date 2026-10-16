using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class SurfacePathMapperTests
{
    private static Vec3 SmoothSurface(Vec2 p) =>
        new(p.X, p.Y, p.X <= 0 ? 0 : 0.1 * p.X * p.X);

    private static void AssertSmoothSegments(IReadOnlyList<Vec3> mapped)
    {
        for (int i = 1; i < mapped.Count; i++)
        {
            Vec3 a = mapped[i - 1], b = mapped[i];
            Assert.NotEqual(a, b);
            Assert.InRange(new Vec2(a.X, a.Y).DistanceTo(new Vec2(b.X, b.Y)), 0, 1.0);
            Vec3 midpoint = SmoothSurface(new Vec2((a.X + b.X) / 2, (a.Y + b.Y) / 2));
            Assert.InRange((midpoint - (a * 0.5 + b * 0.5)).Length, 0, 0.05);
        }
    }

    [Fact]
    public void ContinuousPlaneToCurvedPatchMapsAsOnePolyline()
    {
        Vec2[] path = { new(-5, 2), new(5, 2) };

        Assert.True(SurfacePathMapper.TryMap(path, p => SmoothSurface(p), out var mapped, out var failure), failure);

        Assert.Empty(failure);
        Assert.Equal(SmoothSurface(path[0]), mapped[0]);
        Assert.Equal(SmoothSurface(path[^1]), mapped[^1]);
        Assert.Contains(mapped, p => p.X == 0);
        Assert.Contains(mapped, p => p.X < 0);
        Assert.Contains(mapped, p => p.X > 0 && p.Z > 0);
        AssertSmoothSegments(mapped);
    }

    [Fact]
    public void CurvatureRefinesBelowOneMillimetreSpacing()
    {
        Vec2[] path = { new(0, 0), new(1, 0) };
        Assert.True(SurfacePathMapper.TryMap(path, p => new Vec3(p.X, p.Y, 10 * p.X * p.X),
            out var mapped, out var failure), failure);

        Assert.True(mapped.Count > 2);
        for (int i = 1; i < mapped.Count; i++)
        {
            Vec3 a = mapped[i - 1], b = mapped[i];
            double x = (a.X + b.X) / 2;
            Assert.InRange(Math.Abs(10 * x * x - (a.Z + b.Z) / 2), 0, 0.05);
        }
    }

    [Fact]
    public void MissingSurfaceInMiddleRejectsEntirePath()
    {
        Vec2[] path = { new(-4, 0), new(-2, 0), new(2, 0) };
        Assert.False(SurfacePathMapper.TryMap(path,
            p => Math.Abs(p.X) < 0.2 ? null : new Vec3(p.X, p.Y, 0), out var mapped, out var failure));
        Assert.Empty(mapped);
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void NarrowGapAwayFromSegmentMidpointRejectsEntirePath()
    {
        Vec2[] path = { new(0, 0), new(1, 0) };
        Assert.False(SurfacePathMapper.TryMap(path,
            p => p.X >= 0.12 && p.X <= 0.13 ? null : new Vec3(p.X, p.Y, 0),
            out var mapped, out var failure));
        Assert.Empty(mapped);
        Assert.NotEmpty(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JumpDiscontinuityRejectsEntirePath(bool jumpAlongChord)
    {
        Vec2[] path = { new(-2, 0), new(-1, 0), new(2, 0) };
        Assert.False(SurfacePathMapper.TryMap(path, p => jumpAlongChord
            ? new Vec3(p.X + (p.X < 0 ? 0 : 10), p.Y, 0)
            : new Vec3(p.X, p.Y, p.X < 0 ? 0 : 10), out var mapped, out var failure));
        Assert.Empty(mapped);
        Assert.Contains("depth 18", failure);
    }

    [Fact]
    public void StraightPlanarPathIsDeterministicAndPreservesEndpointsWithoutDuplicates()
    {
        Vec2[] path = { new(0, 0), new(0, 0), new(2, 0), new(2, 0), new(4, 0) };
        Vec3? Hit(Vec2 p) => new(p.X, p.Y, 7);
        Assert.True(SurfacePathMapper.TryMap(path, Hit, out var first, out var failure), failure);
        Assert.True(SurfacePathMapper.TryMap(path, Hit, out var second, out failure), failure);

        Assert.Equal(257,first.Count);
        Assert.Equal(first,second);
        for(int i=0;i<first.Count;i++) Assert.InRange(Math.Abs(first[i].X-i*0.015625),0,1e-12);
    }

    [Fact]
    public void MetadataSamplesValidateOrderedAtoBtoCTransitionsNotProbeOrder()
    {
        var path=new[]{new Vec2(-1,0),new Vec2(1,0)};
        SurfaceMappedSample? Hit(Vec2 p) => new(p,new Vec3(p.X,p.Y,0),p.X < -0.2 ? 10 : p.X > 0.2 ? 30 : 20);
        Assert.True(SurfacePathMapper.TryMapWithMetadata(path,Hit,out var mapped,out var failure),failure);
        Assert.Equal(10,mapped[0].FaceId); Assert.Equal(30,mapped[^1].FaceId);
        var transitions=Enumerable.Range(1,mapped.Count-1)
            .Where(i=>mapped[i-1].FaceId!=mapped[i].FaceId)
            .Select(i=>(mapped[i-1].FaceId,mapped[i].FaceId)).Distinct().ToArray();
        Assert.Contains((10,20),transitions); Assert.Contains((20,30),transitions);
        Assert.DoesNotContain((10,30),transitions);
        bool Adjacent(int a,int b) => (a==10&&b==20)||(a==20&&b==10)||(a==20&&b==30)||(a==30&&b==20);
        Assert.True(SurfacePathMapper.AreSamplesContinuous(mapped,Adjacent));
        Assert.False(SurfacePathMapper.AreSamplesContinuous(new[]{mapped[0],mapped[^1]},Adjacent));
    }

    [Fact]
    public void AcceptedLeafRetainsNarrowDisconnectedFaceBetweenSameFaceEndpoints()
    {
        Vec2[] path={new(0,0),new(0.5,0)};
        SurfaceMappedSample? Hit(Vec2 p)
        {
            // This narrow island is not at the former 0.5-mm leaf's quarter probe (0.125).
            int face=p.X>=0.145 && p.X<=0.16 ? 2 : 1;
            return new SurfaceMappedSample(p,new Vec3(p.X,p.Y,0),face);
        }
        Assert.True(SurfacePathMapper.TryMapWithMetadata(path,Hit,out var mapped,out var failure),failure);
        Assert.Equal(1,mapped[0].FaceId); Assert.Equal(1,mapped[^1].FaceId);
        Assert.Contains(mapped,s=>s.FaceId==2);
        Assert.False(SurfacePathMapper.AreSamplesContinuous(mapped,(a,b)=>false));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteHitsRejectEntirePath(double invalid)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Vec3 bad = axis == 0 ? new(invalid, 0, 0) : axis == 1 ? new(0, invalid, 0) : new(0, 0, invalid);
            Assert.False(SurfacePathMapper.TryMap(new[] { new Vec2(-2, 0), new Vec2(2, 0) },
                p => p.X == 0 ? bad : new Vec3(p.X, p.Y, 0), out var mapped, out var failure));
            Assert.Empty(mapped);
            Assert.NotEmpty(failure);
        }
    }

    [Fact]
    public void ProbeBudgetRejectsWithoutReturningPartialPath()
    {
        int probes = 0;
        Assert.False(SurfacePathMapper.TryMap(new[] { new Vec2(0, 0), new Vec2(100_000, 0) },
            p => { probes++; return new Vec3(p.X, p.Y, 0); }, out var mapped, out var failure, maxProbes:40));
        Assert.Equal(40, probes);
        Assert.Empty(mapped);
        Assert.Contains("probe limit", failure);
    }

    [Fact]
    public void AdjacentPolygonUnionGeneratesOneSerpentineAcrossBothPatches()
    {
        Vec2[] left = { new(-10, 0), new(0, 0), new(0, 8), new(-10, 8) };
        Vec2[] right = { new(0, 0), new(10, 0), new(10, 8), new(0, 8) };
        var union = RegionOps.UnionPolygons(new IReadOnlyList<Vec2>[] { left, right });
        var outline = Assert.Single(union);
        var pattern = Serpentine.Generate(outline, null, new PatternParams { Margin = 1, Spacing = 2 });

        Vec3? Hit(Vec2 p) => p.X < -10 || p.X > 10 || p.Y < 0 || p.Y > 8 ? null : SmoothSurface(p);
        Assert.True(SurfacePathMapper.TryMap(pattern.Path, Hit, out var mapped, out var failure), failure);

        Assert.True(pattern.LaneCount > 1);
        Assert.Equal(SmoothSurface(pattern.Path[0]), mapped[0]);
        Assert.Equal(SmoothSurface(pattern.Path[^1]), mapped[^1]);
        Assert.Contains(mapped, p => p.X < 0);
        Assert.Contains(mapped, p => p.X > 0);
        Assert.True(Enumerable.Range(1, mapped.Count - 1)
            .Count(i => (mapped[i - 1].X < 0) != (mapped[i].X < 0)) >= pattern.LaneCount);
        AssertSmoothSegments(mapped);
    }

    [Fact]
    public void ConcaveConnectedUnionRoutesAroundNotchInsteadOfBridgingIt()
    {
        Vec2[] outline = { new(-20, 0), new(20, 0), new(20, 20), new(10, 20),
            new(10, 5), new(-10, 5), new(-10, 20), new(-20, 20) };
        var parameters = new PatternParams { Margin = 0, Spacing = 3 };
        Vec3? Hit(Vec2 p) => p.Y > 5 && p.X > -10 && p.X < 10
            ? null : SmoothSurface(p);

        var pattern = Serpentine.Generate(outline, null, parameters, constrainConnectors: true);
        Assert.True(SurfacePathMapper.TryMap(pattern.Path, Hit, out var mapped, out var failure), failure);
        Assert.Contains(mapped, p => p.X < -10 && p.Y > 5);
        Assert.Contains(mapped, p => p.X > 10 && p.Y > 5);
        Assert.DoesNotContain(mapped, p => p.Y > 5 && p.X > -10 && p.X < 10);
        AssertSmoothSegments(mapped);
    }

    [Fact]
    public void InvalidInputsRejectOrThrowWithoutCallingHit()
    {
        Vec3? Hit(Vec2 _) => throw new InvalidOperationException("Must not probe invalid input.");
        Assert.Throws<ArgumentNullException>(() => SurfacePathMapper.TryMap(null!, Hit, out _, out _));
        Assert.Throws<ArgumentNullException>(() => SurfacePathMapper.TryMap(Array.Empty<Vec2>(), null!, out _, out _));
        Assert.False(SurfacePathMapper.TryMap(Array.Empty<Vec2>(), Hit, out var empty, out var emptyFailure));
        Assert.Empty(empty);
        Assert.NotEmpty(emptyFailure);
        Assert.False(SurfacePathMapper.TryMap(new[] { new Vec2(double.NaN, 0) }, Hit, out var invalid, out var invalidFailure));
        Assert.Empty(invalid);
        Assert.NotEmpty(invalidFailure);
    }
}
