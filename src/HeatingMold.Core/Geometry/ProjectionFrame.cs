namespace HeatingMold.Core.Geometry;

/// <summary>모델 점과 작업 평면 좌표 사이를 변환하는 오른손 정사영 좌표계입니다.</summary>
public sealed class ProjectionFrame
{
    /// <summary>투영 평면의 원점입니다.</summary>
    public Vec3 Origin { get; }

    /// <summary>투영 평면의 단위 X축입니다.</summary>
    public Vec3 AxisX { get; }

    /// <summary>투영 평면의 단위 Y축입니다.</summary>
    public Vec3 AxisY { get; }

    /// <summary>투영 평면에 수직인 단위 법선입니다.</summary>
    public Vec3 Normal { get; }

    /// <summary>유한한 원점, 법선, 평면 내 X축 후보로 오른손 좌표계를 만듭니다.</summary>
    /// <param name="origin">투영 평면의 원점입니다.</param>
    /// <param name="normal">좌표계의 법선입니다.</param>
    /// <param name="preferredX">법선과 평행하지 않은 평면 내 X축 후보입니다.</param>
    public ProjectionFrame(Vec3 origin, Vec3 normal, Vec3 preferredX)
    {
        if (normal == default) throw new ArgumentException("법선은 영벡터일 수 없습니다.", nameof(normal));
        if (preferredX == default) throw new ArgumentException("X축 후보는 영벡터일 수 없습니다.", nameof(preferredX));

        Vec3 n = normal.Normalized();
        Vec3 preferredDirection = preferredX.Normalized();
        Vec3 projectedX = preferredDirection - n * Vec3.Dot(preferredDirection, n);
        if (projectedX.Length < 1e-9)
            throw new ArgumentException("X축 후보는 법선과 평행할 수 없습니다.", nameof(preferredX));

        Normal = n;
        AxisX = projectedX.Normalized();
        AxisY = Vec3.Cross(n, AxisX).Normalized();
        Origin = origin;
    }

    /// <summary>모델의 3차원 점을 작업 평면 좌표로 투영합니다.</summary>
    public Vec2 Project(Vec3 p)
    {
        Vec3 d = p - Origin;
        return new Vec2(Vec3.Dot(d, AxisX), Vec3.Dot(d, AxisY));
    }

    /// <summary>작업 평면 좌표를 모델의 3차원 점으로 변환합니다.</summary>
    public Vec3 Unproject(Vec2 p) => Origin + AxisX * p.X + AxisY * p.Y;
}
