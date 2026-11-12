namespace HeatingMold.Core.Geometry;

/// <summary>
/// 경로 3D 표면 매핑의 보강 표본 정책. <see cref="GeometryTolerancePolicy"/>의 거리 허용값과
/// 달리 이 값은 "조회 간격"이며, 표면의 틈·끊김을 허용하는 공차가 아니다.
/// 1mm 이하의 틈·끊김도 오류로 처리하지만, 이 간격보다 좁은 결함은 표본 한계로
/// 검출이 보장되지 않는다(자동 보정·메우기 없음).
/// </summary>
public static class RouteSurfaceValidationPolicy
{
    /// <summary>표본 정책 버전. 캐시 키에 포함해 정책이 바뀐 표본을 재사용하지 않는다.</summary>
    public const string Version = "route-surface-v2";

    /// <summary>리프 구간 내부 보강 표본 간격(mm).</summary>
    public const double GapSpacing = 1.0;

    /// <summary>적응 분할의 같은 파라미터 3D chord 허용(mm). 표본 간격과 별개다.</summary>
    public const double ChordError = 0.05;

    /// <summary>경로 1회 매핑의 최대 표본 조회 수. 초과는 명시적 실패다.</summary>
    public const int MaxProbes = 100_000;
}
