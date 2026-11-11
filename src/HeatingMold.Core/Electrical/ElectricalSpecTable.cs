namespace HeatingMold.Core.Electrical;

/// <summary>사양표 한 줄의 입력. CircuitId는 회로 구분자를 그대로 표시용 ID로 쓴다(예: A-1).</summary>
public sealed record ElectricalCircuitInput(string CircuitId, double LengthMm);

/// <summary>
/// 목표전력 방식 사양표 한 줄의 입력. TargetPowerW는 이 회로에 배분된 발열 전력이며,
/// null이면 아직 배분되지 않은 상태다(0 W 계산 오류와 구분한다).
/// </summary>
public sealed record TargetPowerCircuitInput(string CircuitId, double LengthMm, double? TargetPowerW);

/// <summary>사양표 계산 방식. 전압 열의 의미가 다르므로 표와 내보내기가 구분해 표기한다.</summary>
public enum ElectricalCalculationMode
{
    /// <summary>공통 인가전압을 입력으로 쓰는 기존 방식.</summary>
    AppliedVoltage = 0,

    /// <summary>회로별 목표 발열전력을 입력으로 쓰고 필요 전압을 계산하는 방식.</summary>
    TargetPower = 1
}

public sealed class ElectricalSpecRow
{
    public string CircuitId { get; init; } = "";
    public double LengthM { get; init; }
    public double ResistanceOhm { get; init; }

    /// <summary>인가전압 방식이면 공급 전압, 목표전력 방식이면 필요한 전압이다.</summary>
    public double VoltageV { get; init; }

    public double CurrentA { get; init; }
    public double PowerW { get; init; }

    /// <summary>이 회로에 배분된 목표 발열전력. 인가전압 방식에서는 계산된 전력과 같다.</summary>
    public double? TargetPowerW { get; init; }

    /// <summary>목표 발열전력이 아직 입력되지 않은 행. 계산 오류(0 W 등)와 구분한다.</summary>
    public bool IsTargetPowerMissing { get; init; }

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

    /// <summary>인가전압 방식의 공급 전압. 목표전력 방식에서는 null이다.</summary>
    public PowerSupplySpec? Supply { get; init; }

    public ElectricalLimits? Limits { get; init; }
    public ElectricalCalculationMode Mode { get; init; } = ElectricalCalculationMode.AppliedVoltage;
    public IReadOnlyList<ElectricalSpecRow> Rows { get; init; } = Array.Empty<ElectricalSpecRow>();
    public double TotalCurrentA { get; init; }
    public double TotalPowerW { get; init; }

    /// <summary>모든 회로에 배분된 목표 발열전력의 합계.</summary>
    public double TotalTargetPowerW { get; init; }

    /// <summary>
    /// 배분 전력이 허용치를 넘었는지. 배분은 허용 전력을 나누므로 정상 경로에서는 같거나 작고,
    /// 저장된 옛 계산값처럼 한도를 넘는 입력이 들어올 때만 참이 된다.
    /// </summary>
    public bool IsSharedPowerOverLimit =>
        Limits != null && TotalTargetPowerW > Limits.PowerLimitW;

    /// <summary>배분 합계가 공통 전력 한도 이내인지. 한도가 없으면 판정하지 않는다.</summary>
    public bool IsSharedPowerWithinLimit => Limits != null && !IsSharedPowerOverLimit;

    public bool HasRows => Rows.Count > 0;
    public bool IsTargetPowerMode => Mode == ElectricalCalculationMode.TargetPower;
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
            Mode = ElectricalCalculationMode.AppliedVoltage,
            Rows = rows,
            TotalCurrentA = totalCurrent,
            TotalPowerW = totalPower,
            TotalTargetPowerW = totalPower
        };
    }

    /// <summary>
    /// 회로별로 배분된 목표 발열전력에서 필요 전압·전류를 계산한다.
    /// 목표전력이 0 이하이거나 길이가 유효하지 않은 회로는 계산 오류 행으로 남는다.
    /// </summary>
    public static ElectricalSpecTable BuildFromTargetPower(
        IEnumerable<TargetPowerCircuitInput> inputs,
        HeatingWireSpec wire,
        ElectricalLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(wire);

        var rows = new List<ElectricalSpecRow>();
        double requestedTarget = 0;
        foreach (TargetPowerCircuitInput input in inputs ?? Enumerable.Empty<TargetPowerCircuitInput>())
        {
            if (input.TargetPowerW is not double targetPowerW)
            {
                // 미입력은 계산 오류가 아니다. 길이·저항은 그대로 보여 주고 전압/전류/전력만 비운다.
                double? knownLength = double.IsFinite(input.LengthMm) && input.LengthMm > 0
                    ? input.LengthMm / 1000.0
                    : null;
                double? knownResistance = knownLength is double length
                    ? wire.ResistanceOhmPerMeter * length
                    : null;
                if (knownResistance is double resistance
                    && (!double.IsFinite(resistance) || resistance <= 0))
                {
                    knownResistance = null;
                }
                rows.Add(new ElectricalSpecRow
                {
                    CircuitId = input.CircuitId,
                    LengthM = knownLength ?? 0.0,
                    ResistanceOhm = knownResistance ?? 0.0,
                    TargetPowerW = null,
                    IsTargetPowerMissing = true,
                    Message = "발열 전력이 배분되지 않았습니다"
                });
                continue;
            }

            ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
                input.LengthMm, wire, targetPowerW);
            CircuitAssessment? assessment = result.IsValid && limits != null
                ? CircuitAssessment.Assess(result.CurrentA, result.VoltageV, result.PowerW, limits)
                : null;
            if (double.IsFinite(targetPowerW) && targetPowerW > 0)
                requestedTarget += targetPowerW;
            rows.Add(new ElectricalSpecRow
            {
                CircuitId = input.CircuitId,
                LengthM = result.LengthM,
                ResistanceOhm = result.ResistanceOhm,
                VoltageV = result.VoltageV,
                CurrentA = result.CurrentA,
                PowerW = result.PowerW,
                TargetPowerW = targetPowerW,
                Message = result.Message,
                Assessment = assessment
            });
        }

        double totalCurrent = rows.Where(r => r.IsValid).Sum(r => r.CurrentA);
        double totalPower = rows.Where(r => r.IsValid).Sum(r => r.PowerW);

        return new ElectricalSpecTable
        {
            Wire = wire,
            Supply = null,
            Limits = limits,
            Mode = ElectricalCalculationMode.TargetPower,
            Rows = rows,
            TotalCurrentA = totalCurrent,
            TotalPowerW = totalPower,
            TotalTargetPowerW = requestedTarget
        };
    }
}
