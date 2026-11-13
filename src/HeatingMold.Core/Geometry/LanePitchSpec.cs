namespace HeatingMold.Core.Geometry;

/// <summary>
/// 발열사 배치 간격의 정본. 발주처 요구는 인접 발열사의 <b>외곽 사이 거리</b>이므로
/// 사용자 입력은 외곽 간격이고, 생성기가 실제로 사용하는 레인 중심 피치는
/// 외곽 간격에 전선 직경을 더해 파생한다.
/// </summary>
public readonly record struct LanePitchSpec
{
    /// <summary>간격 의미·계산 정책 버전. 값 의미가 바뀌면 올린다.</summary>
    public const string PolicyVersion = "outer-gap-v1";

    public LanePitchSpec(double outerGapMm, double wireDiameterMm)
    {
        OuterGapMm = outerGapMm;
        WireDiameterMm = wireDiameterMm;
    }

    /// <summary>인접 발열사 외곽 사이 거리 (mm). 사용자 입력값.</summary>
    public double OuterGapMm { get; }

    /// <summary>적용 전선 직경 (mm). 전선 Case가 결정한다.</summary>
    public double WireDiameterMm { get; }

    /// <summary>레인 중심 피치 (mm) = 외곽 간격 + 전선 직경.</summary>
    public double CenterPitchMm => OuterGapMm + WireDiameterMm;

    public bool IsValid =>
        double.IsFinite(OuterGapMm) && OuterGapMm > 0 &&
        double.IsFinite(WireDiameterMm) && WireDiameterMm >= 0;

    /// <summary>두 발열사 중심 거리에서 두 전선 반경을 뺀 실제 외곽 간격.</summary>
    public double OuterGapFor(double centerDistanceMm) =>
        centerDistanceMm - WireDiameterMm;

    public override string ToString() =>
        OuterGapMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "mm 외곽";
}
