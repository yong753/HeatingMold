using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class WorkingCoordinateSystemTests
{
    private static WorkingCoordinateSystem CoordinateSystem(long id, long revision, Vec3 n) =>
        new(new(id), new(revision), new(new(0,0,101), n, new(1,0,0)));

    [Fact]
    public void ConfirmedCoordinateSystemDoesNotDependOnCameraAndRaysOpposeNormal()
    {
        var coordinateSystem = CoordinateSystem(1,1,new(0,0,1));
        var camera = new ProjectionFrame(new(100,40,-20),new(1,1,-1),new(1,0,0));
        var point = new Vec3(20,30,5);
        var before=coordinateSystem.Projection.Project(point);
        _=camera.Project(point);
        Assert.Equal(before,coordinateSystem.Projection.Project(point));
        Assert.Equal(new Vec3(0,0,-1),coordinateSystem.RayDirection);
        Assert.Equal(101,coordinateSystem.Projection.Unproject(before).Z);
    }

}
