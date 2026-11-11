using System.Globalization;

namespace HeatingMold.Core.Electrical;

/// <summary>
/// 전기 검토 허용치. 회로 하나의 계산값을 항목별 상한과 비교하며, 세 항목이 모두
/// 이하일 때만 OK다. 전압은 계산에 사용한 인가전압과 같은 값을 허용전압으로 쓴다.
/// </summary>
public sealed record ElectricalLimits
{
    public const double DefaultCurrentLimitA = 16.0;
    public const double DefaultVoltageLimitV = 220.0;
    public const double DefaultPowerLimitW = 3_500.0;

    public static ElectricalLimits Default { get; } = new();

    public double CurrentLimitA { get; init; } = DefaultCurrentLimitA;
    public double VoltageLimitV { get; init; } = DefaultVoltageLimitV;
    public double PowerLimitW { get; init; } = DefaultPowerLimitW;

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "허용 {0:0.###}A / {1:0.###}V / {2:0.###}W",
        CurrentLimitA, VoltageLimitV, PowerLimitW);
}
