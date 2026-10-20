using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class ProjectionMappingTests
{
    // 정사영 — 법선 방향 성분 제거, 평면 기저로 2D 산출
    [Fact]
    public void ProjectionFrame_ProjectsOrthographically()
    {
        var f = new ProjectionFrame(new Vec3(0, 0, 0), new Vec3(0, 0, 2), new Vec3(1, 0, 0));

        var p = f.Project(new Vec3(3, 4, 5));   // normal +Z → (3,4)
        Assert.Equal(3.0, p.X, 9);
        Assert.Equal(4.0, p.Y, 9);
    }

    // 기저가 정규직교이고 법선과 직교해야 함
    [Fact]
    public void ProjectionFrame_BasisOrthonormal()
    {
        var f = new ProjectionFrame(new Vec3(1, 2, 3), new Vec3(1, 1, 0), new Vec3(0, 0, 1));

        Assert.Equal(1.0, f.Normal.Length, 9);
        Assert.Equal(1.0, f.AxisX.Length, 9);
        Assert.Equal(1.0, f.AxisY.Length, 9);
        Assert.Equal(0.0, Vec3.Dot(f.AxisX, f.Normal), 9);
        Assert.Equal(0.0, Vec3.Dot(f.AxisY, f.Normal), 9);
        Assert.Equal(0.0, Vec3.Dot(f.AxisX, f.AxisY), 9);
    }

}
