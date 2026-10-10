namespace HeatingMold.Core.Geometry;

/// <summary>
/// 면의 정사영(orthographic projection) 프레임.
/// 원점 + 법선 + 평면 내 직교 기저(X,Y)로, 3D 점을 투영 평면의 2D 좌표로 사영한다.
/// 전개(unfold)를 대체하는 "투영도면" 기준계다.
/// </summary>
public sealed class ProjectionFrame
{
    public Vec3 Origin { get; }
    public Vec3 AxisX { get; }
    public Vec3 AxisY { get; }
    public Vec3 Normal { get; }

    /// <param name="origin">투영 평면 원점 (보통 면 경계 중심).</param>
    /// <param name="normal">투영 방향(면 중심 법선).</param>
    /// <param name="preferredX">평면 내 X축 후보 (법선 성분은 제거됨). 영벡터면 자동 폴백.</param>
    public ProjectionFrame(Vec3 origin, Vec3 normal, Vec3 preferredX)
    {
        Vec3 n = normal.Normalized();
        if (n.Length < 1e-12) throw new ArgumentException("법선이 0입니다", nameof(normal));
        Normal = n;

        Vec3 x = preferredX - n * Vec3.Dot(preferredX, n);
        if (x.Length < 1e-9)
        {
            Vec3 fb = Math.Abs(n.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
            x = fb - n * Vec3.Dot(fb, n);
        }
        AxisX = x.Normalized();
        AxisY = Vec3.Cross(n, AxisX).Normalized();
        Origin = origin;
    }

    /// <summary>3D → 투영 평면 2D.</summary>
    public Vec2 Project(Vec3 p)
    {
        Vec3 d = p - Origin;
        return new Vec2(Vec3.Dot(d, AxisX), Vec3.Dot(d, AxisY));
    }
}
