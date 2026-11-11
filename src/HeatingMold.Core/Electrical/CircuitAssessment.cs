using System.Globalization;

namespace HeatingMold.Core.Electrical;

/// <summary>
/// 한 회로의 허용치 판정. 계산 성공 여부(IsValid/Message)와 분리해 보관하므로
/// 허용치를 초과한 정상 계산 회로도 표와 합계에서 빠지지 않는다.
/// </summary>
public sealed class CircuitAssessment
{
    private CircuitAssessment(bool currentWithinLimit, bool voltageWithinLimit, bool powerWithinLimit, string note)
    {
        IsCurrentWithinLimit = currentWithinLimit;
        IsVoltageWithinLimit = voltageWithinLimit;
        IsPowerWithinLimit = powerWithinLimit;
        Note = note;
    }

    public bool IsCurrentWithinLimit { get; }
    public bool IsVoltageWithinLimit { get; }
    public bool IsPowerWithinLimit { get; }
    public bool IsWithinLimits => IsCurrentWithinLimit && IsVoltageWithinLimit && IsPowerWithinLimit;

    /// <summary>OK면 빈 문자열, NG면 초과 항목과 비교값을 나열한다.</summary>
    public string Note { get; }

    public static CircuitAssessment Assess(
        double currentA, double voltageV, double powerW, ElectricalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        // 등호는 허용치 이내로 본다. 반올림 전 계산값으로 그대로 비교한다.
        bool currentWithin = currentA <= limits.CurrentLimitA;
        bool voltageWithin = voltageV <= limits.VoltageLimitV;
        bool powerWithin = powerW <= limits.PowerLimitW;

        var exceeded = new List<string>();
        if (!currentWithin)
            exceeded.Add(string.Format(CultureInfo.InvariantCulture,
                "전류 초과: {0:0.###} A > {1:0.###} A", currentA, limits.CurrentLimitA));
        if (!voltageWithin)
            exceeded.Add(string.Format(CultureInfo.InvariantCulture,
                "전압 초과: {0:0.###} V > {1:0.###} V", voltageV, limits.VoltageLimitV));
        if (!powerWithin)
            exceeded.Add(string.Format(CultureInfo.InvariantCulture,
                "전력 초과: {0:0.###} W > {1:0.###} W", powerW, limits.PowerLimitW));

        return new CircuitAssessment(currentWithin, voltageWithin, powerWithin, string.Join(" / ", exceeded));
    }
}
