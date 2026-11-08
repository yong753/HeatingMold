namespace HeatingMold.Core.Geometry;

/// <summary>
/// 작업평면 2D 기하 판정에 사용하는 허용오차 정책.
/// 거리 허용값(acceptance band)과 수치 epsilon은 의미가 다르므로 반드시 분리한다.
/// 수치 epsilon을 거리 허용값으로 올리면 교차점 중복 제거·동률·퇴화 판정이 함께 변한다.
/// </summary>
public static class GeometryTolerancePolicy
{
    /// <summary>캐시 키와 후보 지문에 기록하는 정책 버전.</summary>
    public const string Version = "geom-accept-v1";

    /// <summary>
    /// 설계 허용 거리(mm). 경계 침범 깊이, CAD 평가점/투영 좌표 잔차,
    /// 지지면 거리, 실제 3D 조각 간격에 공통 적용한다.
    /// </summary>
    public const double DistanceTolerance = 0.1;

    /// <summary>
    /// 경계 위 판정과 광선 교차점 중복 제거에 쓰는 수치 epsilon(mm).
    /// 거리 허용값이 아니며, 좁은 홈/돌출부의 교차를 하나로 합치지 않기 위해 작게 유지한다.
    /// </summary>
    public const double OnBoundaryTolerance = 1e-6;

    /// <summary>방향·구간 파라미터 퇴화 판정용 수치 epsilon.</summary>
    public const double DirectionEpsilon = 1e-9;
}
