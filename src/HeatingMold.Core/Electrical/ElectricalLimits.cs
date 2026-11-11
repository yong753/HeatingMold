using System.Globalization;

namespace HeatingMold.Core.Electrical;

/// <summary>
/// 전기 검토 허용치. 전압·전류는 회로 하나의 계산값을 상한과 비교한다.
/// <see cref="PowerLimitW"/>는 회로별 상한이 아니라 확정된 영역 전체가 나누어 쓰는
/// 총 발열전력이며, 사용자는 이 한 가지 전력만 입력한다. 회로별 배분값의 합이
/// 이 값을 넘으면 영역을 어떻게 나누었든 NG다.
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
