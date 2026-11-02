namespace HeatingMold.Core.Electrical;

/// <summary>사양표 한 행의 입력. CircuitId는 영역 접두어를 포함한 표시용 ID다(예: A-1).</summary>
public sealed record ElectricalCircuitInput(string CircuitId, double LengthMm);

public sealed class ElectricalSpecRow
{
    public string CircuitId { get; init; } = "";
    public double LengthM { get; init; }
    public double ResistanceOhm { get; init; }
    public double VoltageV { get; init; }
    public double CurrentA { get; init; }
    public double PowerW { get; init; }
    public double MaxVoltageV { get; init; }
    public ElectricalVerdict Verdict { get; init; } = ElectricalVerdict.InvalidInput;
    public string Message { get; init; } = "";

    public bool IsValid => Verdict != ElectricalVerdict.InvalidInput;
    public string VerdictText => ElectricalVerdictText.ToText(Verdict);
    public string VerdictShortText => ElectricalVerdictText.ToShortText(Verdict);
}

public sealed class ElectricalSpecTable
{
    public HeatingWireSpec Wire { get; init; } = null!;
    public PowerSupplySpec Supply { get; init; } = null!;
    public IReadOnlyList<ElectricalSpecRow> Rows { get; init; } = Array.Empty<ElectricalSpecRow>();
    public double TotalCurrentA { get; init; }
    public double TotalPowerW { get; init; }
    public ElectricalVerdict TotalVerdict { get; init; } = ElectricalVerdict.Ok;
    public string TotalMessage { get; init; } = "";

    /// <summary>인가전압과 허용전류에서 회로 하나가 가져야 하는 최소 길이(m).</summary>
    public double RecommendedMinLengthM { get; init; }

    public bool HasRows => Rows.Count > 0;
    public string TotalVerdictText => ElectricalVerdictText.ToText(TotalVerdict);
}

public static class ElectricalSpecTableBuilder
{
    private const double Epsilon = 1e-9;

    public static ElectricalSpecTable Build(
        IEnumerable<ElectricalCircuitInput> inputs,
        HeatingWireSpec wire,
        PowerSupplySpec supply)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(supply);

        var rows = new List<ElectricalSpecRow>();
        foreach (ElectricalCircuitInput input in inputs ?? Enumerable.Empty<ElectricalCircuitInput>())
        {
            ElectricalResult result = ElectricalCalculator.Compute(input.LengthMm, wire, supply);
            rows.Add(new ElectricalSpecRow
            {
                CircuitId = input.CircuitId,
                LengthM = result.LengthM,
                ResistanceOhm = result.ResistanceOhm,
                VoltageV = result.VoltageV,
                CurrentA = result.CurrentA,
                PowerW = result.PowerW,
                MaxVoltageV = result.MaxVoltageV,
                Verdict = result.Verdict,
                Message = result.Message
            });
        }

        bool anyInvalid = rows.Any(r => !r.IsValid);
        double totalCurrent = rows.Where(r => r.IsValid).Sum(r => r.CurrentA);
        double totalPower = rows.Where(r => r.IsValid).Sum(r => r.PowerW);
        bool overCurrent = totalCurrent - supply.MaxCurrentA > Epsilon;
        bool overPower = totalPower - supply.MaxPowerW > Epsilon;

        ElectricalVerdict totalVerdict = anyInvalid
            ? ElectricalVerdict.InvalidInput
            : (overCurrent, overPower) switch
            {
                (true, true) => ElectricalVerdict.OverCurrentAndPower,
                (true, false) => ElectricalVerdict.OverCurrent,
                (false, true) => ElectricalVerdict.OverPower,
                _ => ElectricalVerdict.Ok
            };

        string totalMessage = totalVerdict switch
        {
            ElectricalVerdict.InvalidInput => "일부 회로의 입력이 올바르지 않습니다",
            ElectricalVerdict.Ok => "허용 범위 이내",
            ElectricalVerdict.OverCurrent =>
                FormattableString.Invariant($"합계 전류 {totalCurrent:F2}A > 허용 {supply.MaxCurrentA:F2}A"),
            ElectricalVerdict.OverPower =>
                FormattableString.Invariant($"합계 전력 {totalPower:F1}W > 허용 {supply.MaxPowerW:F1}W"),
            _ => FormattableString.Invariant(
                $"합계 전류 {totalCurrent:F2}A > {supply.MaxCurrentA:F2}A, 합계 전력 {totalPower:F1}W > {supply.MaxPowerW:F1}W")
        };

        double minLength = wire.ResistanceOhmPerMeter > 0 && supply.MaxCurrentA > 0
            ? supply.VoltageV / (wire.ResistanceOhmPerMeter * supply.MaxCurrentA)
            : 0;

        return new ElectricalSpecTable
        {
            Wire = wire,
            Supply = supply,
            Rows = rows,
            TotalCurrentA = totalCurrent,
            TotalPowerW = totalPower,
            TotalVerdict = totalVerdict,
            TotalMessage = totalMessage,
            RecommendedMinLengthM = double.IsFinite(minLength) ? minLength : 0
        };
    }
}
