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

    /// <summary>입력이 유효하지 않을 때의 계산 오류 사유. 정상 계산된 행에서는 빈 문자열.</summary>
    public string Message { get; init; } = "";

    /// <summary>허용치 판정. 허용치 없이 계산한 표에서는 null이다.</summary>
    public CircuitAssessment? Assessment { get; init; }

    /// <summary>계산 자체가 유효한지. 허용치 초과(NG)는 계산 오류이므로 이 값은 true로 유지된다.</summary>
    public bool IsValid => string.IsNullOrEmpty(Message);
    public bool HasNote => !string.IsNullOrEmpty(Message);

    public bool IsWithinLimits => IsValid && Assessment != null && Assessment.IsWithinLimits;
    public bool IsOverLimit => IsValid && Assessment != null && !Assessment.IsWithinLimits;

    /// <summary>표에 표시할 비고. 계산 오류가 우선하고, 없으면 허용치 초과 사유다.</summary>
    public string Note => HasNote ? Message : (Assessment?.Note ?? "");
}

public sealed class ElectricalSpecTable
{
    public HeatingWireSpec Wire { get; init; } = null!;
    public PowerSupplySpec Supply { get; init; } = null!;
    public ElectricalLimits? Limits { get; init; }
    public IReadOnlyList<ElectricalSpecRow> Rows { get; init; } = Array.Empty<ElectricalSpecRow>();
    public double TotalCurrentA { get; init; }
    public double TotalPowerW { get; init; }

    public bool HasRows => Rows.Count > 0;
    public int WithinLimitCount => Rows.Count(r => r.IsWithinLimits);
    public int OverLimitCount => Rows.Count(r => r.IsOverLimit);
    public bool HasOverLimitCircuits => OverLimitCount > 0;
}

public static class ElectricalSpecTableBuilder
{
    public static ElectricalSpecTable Build(
        IEnumerable<ElectricalCircuitInput> inputs,
        HeatingWireSpec wire,
        PowerSupplySpec supply) => Build(inputs, wire, supply, null);

    public static ElectricalSpecTable Build(
        IEnumerable<ElectricalCircuitInput> inputs,
        HeatingWireSpec wire,
        PowerSupplySpec supply,
        ElectricalLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(supply);

        var rows = new List<ElectricalSpecRow>();
        foreach (ElectricalCircuitInput input in inputs ?? Enumerable.Empty<ElectricalCircuitInput>())
        {
            ElectricalResult result = ElectricalCalculator.Compute(input.LengthMm, wire, supply);
            // 계산 오류 행에는 판정을 만들지 않는다. 정상 계산된 NG 행은 판정만 NG로 남는다.
            CircuitAssessment? assessment = result.IsValid && limits != null
                ? CircuitAssessment.Assess(result.CurrentA, result.VoltageV, result.PowerW, limits)
                : null;
            rows.Add(new ElectricalSpecRow
            {
                CircuitId = input.CircuitId,
                LengthM = result.LengthM,
                ResistanceOhm = result.ResistanceOhm,
                VoltageV = result.VoltageV,
                CurrentA = result.CurrentA,
                PowerW = result.PowerW,
                Message = result.Message,
                Assessment = assessment
            });
        }

        // 합계는 계산이 유효한 모든 회로를 포함한다. 허용치 초과(NG)는 계산 오류가 아니다.
        double totalCurrent = rows.Where(r => r.IsValid).Sum(r => r.CurrentA);
        double totalPower = rows.Where(r => r.IsValid).Sum(r => r.PowerW);

        return new ElectricalSpecTable
        {
            Wire = wire,
            Supply = supply,
            Limits = limits,
            Rows = rows,
            TotalCurrentA = totalCurrent,
            TotalPowerW = totalPower
        };
    }
}
