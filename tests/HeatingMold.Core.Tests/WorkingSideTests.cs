using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class WorkingSideTests
{
    private static WorkingSideDefinition Side(long id, long revision, Vec3 n) =>
        new(new(id), new(revision), new(new(0,0,101), n, new(1,0,0)));

    [Fact]
    public void PreviewIsSeparateAndStaleConfirmationIsAtomic()
    {
        var state = new WorkingSideSession();
        var front = Side(1, 10, new(0,0,1));
        state.SetPreview(front); state.Confirm(new(10));
        var back = Side(2, 10, new(0,0,-1));
        state.SetPreview(back);
        Assert.Same(front,state.Confirmed);
        Assert.Throws<InvalidOperationException>(()=>state.Confirm(new(11)));
        Assert.Same(front,state.Confirmed);
        state.CancelPreview();
        Assert.Same(front,state.Confirmed);
        state.Invalidate();
        Assert.Equal(WorkingSideState.Unconfirmed,state.State);
    }

    [Fact]
    public void ConfirmedFrameDoesNotDependOnCameraAndRaysOpposeNormal()
    {
        var side = Side(1,1,new(0,0,1));
        var camera = new ProjectionFrame(new(100,40,-20),new(1,1,-1),new(1,0,0));
        var point = new Vec3(20,30,5);
        var before=side.Frame.Project(point);
        _=camera.Project(point);
        Assert.Equal(before,side.Frame.Project(point));
        Assert.Equal(new Vec3(0,0,-1),side.RayDirection);
        Assert.Equal(101,side.Frame.Unproject(before).Z);
    }

    private static IEnumerable<IReadOnlyList<Vec2>> Rect(double x0,double y0,double x1,double y1)
    {
        yield return new[]{new Vec2(x0,y0),new Vec2(x1,y0),new Vec2(x1,y1)};
        // Deliberately reverse the other triangle: projection union must not cancel overlaps.
        yield return new[]{new Vec2(x0,y0),new Vec2(x0,y1),new Vec2(x1,y1)};
    }

    [Fact]
    public void TriangleUnionPreservesHoleAndRemovesSharedSeams()
    {
        var triangles=Rect(0,0,10,2).Concat(Rect(0,8,10,10)).Concat(Rect(0,2,2,8)).Concat(Rect(8,2,10,8));
        var rings=WorkingBoundary.FromTriangles(triangles,.05);
        Assert.Equal(2,rings.Count);
        Assert.Single(rings.Where(r=>r.IsHole));
        Assert.Single(rings.Where(r=>!r.IsHole));
        Assert.Equal(64,rings.Sum(r=>RegionOps.SignedArea(r.Points)),5);
        Assert.All(rings,r=>Assert.Equal(.05,r.MeshDeflection));
        var shared=WorkingBoundary.FromTriangles(Rect(0,0,5,10).Concat(Rect(5,0,10,10)),.05);
        Assert.Single(shared);
        Assert.Equal(100,RegionOps.SignedArea(shared[0].Points),5);
    }

    [Fact]
    public void NestedIslandIsNotAThroughHole()
    {
        var triangles=Rect(0,0,10,2).Concat(Rect(0,8,10,10)).Concat(Rect(0,2,2,8)).Concat(Rect(8,2,10,8))
            .Concat(Rect(4,4,6,6));
        var rings=WorkingBoundary.FromTriangles(triangles,.05);
        Assert.Contains(rings,r=>r.NestingDepth==2 && !r.IsHole);
    }
}
