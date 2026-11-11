namespace HeatingMold.Core.Electrical;

/// <summary>
/// PDF 계산식 P = V²/(rL)과 정의 R = rL, 옴의 법칙 I = V/R로 회로 하나를 계산한다.
/// L은 mm로 받아 m로 변환한다. r은 상온 일정값이고 외부 리드/접속부 저항은 반영하지 않는다.
/// 허용치 판정은 <see cref="CircuitAssessment"/>가 계산 성공 여부와 별도로 수행한다.
/// </summary>
public static class ElectricalCalculator
{
    public static ElectricalResult Compute(double lengthMm, HeatingWireSpec wire, PowerSupplySpec supply)
    {
        if (wire is null) return ElectricalResult.Invalid("발열사 제원이 없습니다");
        if (supply is null) return ElectricalResult.Invalid("전원 사양이 없습니다");
        if (!double.IsFinite(lengthMm) || lengthMm <= 0)
            return ElectricalResult.Invalid("발열사 길이는 0보다 커야 합니다");
        if (!double.IsFinite(wire.ResistanceOhmPerMeter) || wire.ResistanceOhmPerMeter <= 0)
            return ElectricalResult.Invalid("단위 길이당 저항은 0보다 커야 합니다");
        if (!double.IsFinite(supply.VoltageV) || supply.VoltageV < 0)
            return ElectricalResult.Invalid("인가전압은 0 이상이어야 합니다");

        double lengthM = lengthMm / 1000.0;
        double resistance = wire.ResistanceOhmPerMeter * lengthM;
        if (!double.IsFinite(resistance) || resistance <= 0)
            return ElectricalResult.Invalid("저항을 계산할 수 없습니다");

        double current = supply.VoltageV / resistance;
        double power = supply.VoltageV * current;
        if (!double.IsFinite(current) || !double.IsFinite(power))
            return ElectricalResult.Invalid("계산된 전류/전력이 유효한 범위를 벗어났습니다");

        return new ElectricalResult
        {
            IsValid = true,
            LengthM = lengthM,
            ResistanceOhm = resistance,
            VoltageV = supply.VoltageV,
            CurrentA = current,
            PowerW = power
        };
    }
}

public sealed class ElectricalResult
{
    public bool IsValid { get; init; }
    public double LengthM { get; init; }
    public double ResistanceOhm { get; init; }
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
