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

    // 투영 2D → UV 역매핑 — 아핀 격자에서 보간으로 정확히 복원
    [Fact]
    public void UvProjectionGrid_RecoversUvByInterpolation()
    {
        const int n = 2;
        int count = (n + 1) * (n + 1);
        var u = new double[count];
        var v = new double[count];
        var x = new double[count];
        var y = new double[count];
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                int idx = i * (n + 1) + j;
                u[idx] = i / (double)n;
                v[idx] = j / (double)n;
                x[idx] = u[idx] * 100.0;
                y[idx] = v[idx] * 100.0;
            }

        var g = new UvProjectionGrid(n, u, v, x, y);

        Assert.True(g.TryMap(new Vec2(25, 75), out double uu, out double vv));
        Assert.Equal(0.25, uu, 6);
        Assert.Equal(0.75, vv, 6);

        // 격자 밖 → 최근접 노드 폴백 (실패하지 않음)
        Assert.True(g.TryMap(new Vec2(-20, -20), out _, out _));
    }
}
