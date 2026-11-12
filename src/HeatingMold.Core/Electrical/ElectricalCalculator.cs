namespace HeatingMold.Core.Electrical;

/// <summary>
/// 회로 하나의 전기 계산. 배분된 발열 전력에서 필요한 전압과 전류를 구한다:
/// R = rL, V = √(PR), I = √(P/R).
/// L은 mm로 받아 m로 변환한다. r은 상온 일정값이고 외부 리드/접속부 저항은 반영하지 않는다.
/// 허용치 판정은 <see cref="CircuitAssessment"/>가 계산 성공 여부와 별도로 수행한다.
/// </summary>
public static class ElectricalCalculator
{
    /// <summary>
    /// 이 회로에 배분된 발열 전력 P에서 필요한 전압과 전류를 계산한다.
    /// R = rL, V = √(PR), I = √(P/R) = P/V. 실제 공급 장치의 전압 제공 가능성은 검토하지 않는다.
    /// </summary>
    public static ElectricalResult ComputeFromTargetPower(
        double lengthMm, HeatingWireSpec wire, double targetPowerW)
    {
        if (wire is null) return ElectricalResult.Invalid("발열사 제원이 없습니다");
        if (!double.IsFinite(lengthMm) || lengthMm <= 0)
            return ElectricalResult.Invalid("발열사 길이는 0보다 커야 합니다");
        if (!double.IsFinite(wire.ResistanceOhmPerMeter) || wire.ResistanceOhmPerMeter <= 0)
            return ElectricalResult.Invalid("단위 길이당 저항은 0보다 커야 합니다");
        // 0 W는 회로를 정의하지 않는다. 0 V/0 A를 정상 계산으로 보고하면 판정이 조용히 OK가 된다.
        if (!double.IsFinite(targetPowerW) || targetPowerW <= 0)
            return ElectricalResult.Invalid("배분된 발열 전력은 0보다 커야 합니다");

        double lengthM = lengthMm / 1000.0;
        double resistance = wire.ResistanceOhmPerMeter * lengthM;
        if (!double.IsFinite(resistance) || resistance <= 0)
            return ElectricalResult.Invalid("저항을 계산할 수 없습니다");

        double voltage = Math.Sqrt(targetPowerW * resistance);
        double current = Math.Sqrt(targetPowerW / resistance);
        if (!double.IsFinite(voltage) || !double.IsFinite(current))
            return ElectricalResult.Invalid("계산된 전압/전류가 유효한 범위를 벗어났습니다");

        return new ElectricalResult
        {
            IsValid = true,
            LengthM = lengthM,
            ResistanceOhm = resistance,
            VoltageV = voltage,
            CurrentA = current,
            PowerW = targetPowerW
        };
    }
}

public sealed class ElectricalResult
{
    public bool IsValid { get; init; }
    public double LengthM { get; init; }
    public double ResistanceOhm { get; init; }

    /// <summary>배분된 발열 전력에서 계산한 이 회로의 필요 전압.</summary>
    public double VoltageV { get; init; }

    public double CurrentA { get; init; }
    public double PowerW { get; init; }

    public string Message { get; init; } = "";

    internal static ElectricalResult Invalid(string message) => new()
    {
        IsValid = false,
        Message = message
    };
}
