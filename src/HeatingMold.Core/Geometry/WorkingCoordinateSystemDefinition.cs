namespace HeatingMold.Core.Geometry;

/// <summary>저장 설계에 포함하는 모델 고정 작업 좌표계 값입니다.</summary>
public sealed record WorkingCoordinateSystemDefinition
{
    /// <summary>작업 평면의 모델 좌표 원점(mm)입니다.</summary>
    public Vec3 Origin { get; }

    /// <summary>작업 좌표계의 단위 X축입니다.</summary>
    public Vec3 AxisX { get; }

    /// <summary>작업 좌표계의 단위 Y축입니다.</summary>
    public Vec3 AxisY { get; }

    /// <summary>작업 평면에서 바깥 방향을 가리키는 단위 법선입니다.</summary>
    public Vec3 Normal { get; }

    /// <summary>유한한 오른손 정규직교 기저만 저장 좌표계로 허용합니다.</summary>
    public WorkingCoordinateSystemDefinition(Vec3 origin, Vec3 axisX, Vec3 axisY, Vec3 normal)
    {
        const double orthogonalityTolerance = 1e-9;

        if (!IsUnit(axisX) || !IsUnit(axisY) || !IsUnit(normal))
            throw new ArgumentException("작업 좌표계의 세 축은 단위 벡터여야 합니다.");
        if (Math.Abs(Vec3.Dot(axisX, axisY)) > orthogonalityTolerance ||
            Math.Abs(Vec3.Dot(axisX, normal)) > orthogonalityTolerance ||
            Math.Abs(Vec3.Dot(axisY, normal)) > orthogonalityTolerance)
        {
            throw new ArgumentException("작업 좌표계의 축은 서로 직교해야 합니다.");
        }

        if (Vec3.Dot(Vec3.Cross(axisX, axisY), normal) < 1 - orthogonalityTolerance)
            throw new ArgumentException("작업 좌표계는 오른손 방향이어야 합니다.");

        Origin = origin;
        AxisX = axisX;
        AxisY = axisY;
        Normal = normal;
    }

    /// <summary>작업 좌표계에서 금형 안쪽을 향하는 질의 방향을 반환합니다.</summary>
    public Vec3 RayDirection => Normal * -1;

    /// <summary>유한한 축이 허용 오차 안에서 길이 1인지 확인합니다.</summary>
    private static bool IsUnit(Vec3 axis) => double.IsFinite(axis.Length) && Math.Abs(axis.Length - 1) <= 1e-9;
}
