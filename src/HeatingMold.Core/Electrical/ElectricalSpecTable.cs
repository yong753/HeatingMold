namespace HeatingMold.Core.Electrical;

/// <summary>사양표 한 줄의 입력. CircuitId는 회로 구분자를 그대로 표시용 ID로 쓴다(예: A-1).</summary>
public sealed record ElectricalCircuitInput(string CircuitId, double LengthMm);

public sealed class ElectricalSpecRow
{
    public string CircuitId { get; init; } = "";
    public double LengthM { get; init; }
    public double ResistanceOhm { get; init; }
    public double VoltageV { get; init; }
    public double CurrentA { get; init; }
    public double PowerW { get; init; }

    /// <summary>입력이 유효하지 않을 때의 사유. 정상 계산된 행에서는 빈 문자열.</summary>
    public string Message { get; init; } = "";

    public bool IsValid => string.IsNullOrEmpty(Message);
    public bool HasNote => !string.IsNullOrEmpty(Message);
}

public sealed class ElectricalSpecTable
{
    public HeatingWireSpec Wire { get; init; } = null!;
    public PowerSupplySpec Supply { get; init; } = null!;
    public IReadOnlyList<ElectricalSpecRow> Rows { get; init; } = Array.Empty<ElectricalSpecRow>();
    public double TotalCurrentA { get; init; }
    public double TotalPowerW { get; init; }

    public bool HasRows => Rows.Count > 0;
}

public static class ElectricalSpecTableBuilder
{
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
                Message = result.Message
            });
        }

        double totalCurrent = rows.Where(r => r.IsValid).Sum(r => r.CurrentA);
        double totalPower = rows.Where(r => r.IsValid).Sum(r => r.PowerW);

        return new ElectricalSpecTable
        {
            Wire = wire,
            Supply = supply,
            Rows = rows,
            TotalCurrentA = totalCurrent,
            TotalPowerW = totalPower
        };
    }
}
