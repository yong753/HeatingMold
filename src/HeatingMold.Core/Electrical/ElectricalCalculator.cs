namespace HeatingMold.Core.Electrical;

/// <summary>
/// PDF 계산식 P = V²/(rL)과 정의 R = rL, 옴의 법칙 I = V/R로 회로 하나를 계산한다.
/// L은 mm로 받아 m로 변환한다. r은 상온 일정값이고 외부 리드/접속부 저항은 반영하지 않는다.
/// </summary>
public static class ElectricalCalculator
{
    private const double Epsilon = 1e-9;

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
        if (!double.IsFinite(supply.MaxCurrentA) || supply.MaxCurrentA <= 0)
            return ElectricalResult.Invalid("허용 전류는 0보다 커야 합니다");
        if (!double.IsFinite(supply.MaxPowerW) || supply.MaxPowerW <= 0)
            return ElectricalResult.Invalid("허용 전력은 0보다 커야 합니다");

        double lengthM = lengthMm / 1000.0;
        double resistance = wire.ResistanceOhmPerMeter * lengthM;
        if (!double.IsFinite(resistance) || resistance <= 0)
            return ElectricalResult.Invalid("저항을 계산할 수 없습니다");

        double current = supply.VoltageV / resistance;
        double power = supply.VoltageV * current;
        double maxVoltage = supply.MaxCurrentA * resistance;

        bool overCurrent = current - supply.MaxCurrentA > Epsilon;
        bool overPower = power - supply.MaxPowerW > Epsilon;
        ElectricalVerdict verdict = (overCurrent, overPower) switch
        {
            (true, true) => ElectricalVerdict.OverCurrentAndPower,
            (true, false) => ElectricalVerdict.OverCurrent,
            (false, true) => ElectricalVerdict.OverPower,
            _ => ElectricalVerdict.Ok
        };

        string message = verdict switch
        {
            ElectricalVerdict.Ok => "허용 범위 이내",
            ElectricalVerdict.OverCurrent =>
                FormattableString.Invariant($"전류 {current:F2}A > 허용 {supply.MaxCurrentA:F2}A"),
            ElectricalVerdict.OverPower =>
                FormattableString.Invariant($"전력 {power:F1}W > 허용 {supply.MaxPowerW:F1}W"),
            _ => FormattableString.Invariant(
                $"전류 {current:F2}A > {supply.MaxCurrentA:F2}A, 전력 {power:F1}W > {supply.MaxPowerW:F1}W")
        };

        return new ElectricalResult
        {
            IsValid = true,
            LengthM = lengthM,
            ResistanceOhm = resistance,
            VoltageV = supply.VoltageV,
            CurrentA = current,
            PowerW = power,
            MaxVoltageV = maxVoltage,
            Verdict = verdict,
            Message = message
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

    /// <summary>이 회로의 전류를 허용치 이하로 유지하는 최대 인가전압(V = Imax·R).</summary>
    public double MaxVoltageV { get; init; }

    public ElectricalVerdict Verdict { get; init; } = ElectricalVerdict.InvalidInput;
    public string Message { get; init; } = "";

    internal static ElectricalResult Invalid(string message) => new()
    {
        IsValid = false,
        Verdict = ElectricalVerdict.InvalidInput,
        Message = message
    };
}
