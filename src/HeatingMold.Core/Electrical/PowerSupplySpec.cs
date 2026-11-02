namespace HeatingMold.Core.Electrical;

/// <summary>전원 조건. 인가전압은 발열사 양 끝에 실제로 거는 전압(AC RMS)이다.</summary>
public sealed record PowerSupplySpec
{
    public double VoltageV { get; init; } = 220.0;
    public double MaxCurrentA { get; init; } = 16.0;
    public double MaxPowerW { get; init; } = 3520.0;

    public string Describe() =>
        FormattableString.Invariant($"{VoltageV:0.###}V / {MaxCurrentA:0.###}A / {MaxPowerW:0.###}W");
}
