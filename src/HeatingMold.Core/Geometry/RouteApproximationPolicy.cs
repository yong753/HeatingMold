namespace HeatingMold.Core.Geometry;

/// <summary>
/// 표면을 찾지 못한 짧은 내부 표본을 앞뒤 정상점의 직선으로 연결하는 명시적 근사 정책.
/// <see cref="RouteSurfaceValidationPolicy"/>의 strict 보증을 완화하는 별도 규칙이며,
/// strict API(<see cref="SurfacePathMapper.TryMapRouteStrict"/>)의 실패 동작은 바뀌지 않는다.
/// 이 정책은 제품 스테이징/수동 편집이 strict 실패 뒤에만 사용하는 fallback이다.
/// </summary>
/// <remarks>
/// 판정 기준(사용자 확정 2026-11-15): 앞뒤 정상점 사이의 <b>원래 2D 경로 진행 길이</b>와
/// <b>3D 직선거리</b>가 모두 10.0 mm 이하여야 한다. 누락 표본 개수에는 별도 제한을 두지 않는다
/// (기존 표본/자원 한도는 그대로 적용된다). 누락 구간은 각각 독립 판정한다.
/// 인출점·원본 조각 끝점·원래 꺾임점(경로 꼭짓점)은 절대 생략하지 않는다.
/// </remarks>
public static class RouteApproximationPolicy
{
    /// <summary>근사 정책 버전. 표본/retain 키에 포함해 이전 보증과 섞이지 않게 한다.</summary>
    public const string Version = "route-approx-v1";

    /// <summary>앞뒤 정상점 사이 원래 2D 경로 진행 길이 한도(mm).</summary>
    public const double MaxBridgeAdvanceMm = 10.0;

    /// <summary>앞뒤 정상점의 3D 직선거리 한도(mm). 깊이 점프를 배제한다.</summary>
    public const double MaxBridge3DGapMm = 10.0;

    /// <summary>
    /// 누락 구간의 앞뒤 경계를 찾을 때의 추가 조회 간격(mm). 실제 Hit 지점만 경계로
    /// 채택하며, 실제로 조회에 성공한 앞뒤 점 사이의 2D 진행 길이와 3D 직선거리를 측정한다.
    /// 이 값은 미검출 결함 전체 범위나 표면 오차를 증명하지 않는다.
    /// </summary>
    public const double ProbeSpacingMm = 1.0;
}
