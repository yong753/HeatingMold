using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Electrical.Calculation;

/// <summary>전기 계산에 필요한 Core 값을 한 회로 단위로 묶습니다.</summary>
public sealed class CircuitElectricalInput
{
    /// <summary>계산할 회로 ID입니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>재료와 도체 제원입니다.</summary>
    public WireSpecification WireSpecification { get; }

    /// <summary>회로별 실제 전압과 허용 한도입니다.</summary>
    public Supply Supply { get; }

    /// <summary>기하 구간별 길이 원장입니다.</summary>
    public LengthBreakdown LengthBreakdown { get; }

    /// <summary>계산 결과의 최신 여부를 판정할 입력 지문입니다.</summary>
    public DependencyStamp DependencyStamp { get; }

    /// <summary>회로별 Core 값을 참조해 변경 불가능한 계산 입력을 만듭니다.</summary>
    public CircuitElectricalInput(
        CircuitId circuitId,
        WireSpecification wireSpecification,
        Supply supply,
        LengthBreakdown lengthBreakdown,
        DependencyStamp dependencyStamp)
    {
        CircuitId = circuitId;
        WireSpecification = wireSpecification ?? throw new ArgumentNullException(nameof(wireSpecification));
        Supply = supply ?? throw new ArgumentNullException(nameof(supply));
        LengthBreakdown = lengthBreakdown ?? throw new ArgumentNullException(nameof(lengthBreakdown));
        DependencyStamp = dependencyStamp ?? throw new ArgumentNullException(nameof(dependencyStamp));
    }
}

/// <summary>전기 입력의 무효 값과 필수 누락을 분리해 보관합니다.</summary>
public sealed class ElectricalInputValidation
{
    /// <summary>계산을 차단하는 무효 입력 코드입니다.</summary>
    public IReadOnlyList<string> Issues { get; }

    /// <summary>입력되어야 하지만 빠진 필드 이름입니다.</summary>
    public IReadOnlyList<string> MissingFields { get; }

    /// <summary>진단 목록을 방어 복사해 입력 검증 결과를 만듭니다.</summary>
    public ElectricalInputValidation(IEnumerable<string> issues, IEnumerable<string> missingFields)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(missingFields);
        Issues = new ReadOnlyCollection<string>(issues.ToArray());
        MissingFields = new ReadOnlyCollection<string>(missingFields.ToArray());
    }
}

/// <summary>전기 계산 전에 수치 무효와 필수 입력 누락을 구분합니다.</summary>
public static class ElectricalInputValidator
{
    /// <summary>입력 진단을 표준 코드와 필드명으로 반환합니다.</summary>
    public static ElectricalInputValidation Validate(CircuitElectricalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = new List<string>();
        var missingFields = new List<string>();

        if (input.CircuitId.IsEmpty)
            issues.Add("InvalidCircuitId");
        if (string.IsNullOrWhiteSpace(input.WireSpecification.MaterialName))
            issues.Add("InvalidMaterialName");
        if (!IsPositiveFinite(input.WireSpecification.DiameterMm.Value))
            issues.Add("InvalidDiameter");
        if (!IsPositiveFinite(input.WireSpecification.ResistanceOhmsPerMeter.Value))
            issues.Add("InvalidResistancePerMeter");
        if (!IsPositiveFinite(input.LengthBreakdown.ResistanceLengthMm.Value))
            issues.Add("InvalidResistanceLength");

        ValidateOptionalPositive(input.Supply.ActualVoltageVrms?.Value,
            "InvalidActualVoltage", "ActualVoltageVrms", issues, missingFields);
        ValidateOptionalPositive(input.Supply.MaximumVoltageVrms?.Value,
            "InvalidMaximumVoltage", "MaximumVoltageVrms", issues, missingFields);
        ValidateOptionalPositive(input.Supply.MaximumCurrentAmps?.Value,
            "InvalidMaximumCurrent", "MaximumCurrentAmps", issues, missingFields);
        ValidateOptionalPositive(input.Supply.MaximumPowerWatts?.Value,
            "InvalidMaximumPower", requiredField: null, issues, missingFields);

        return new ElectricalInputValidation(issues, missingFields);
    }

    private static void ValidateOptionalPositive(
        double? value,
        string issue,
        string? requiredField,
        ICollection<string> issues,
        ICollection<string> missingFields)
    {
        if (!value.HasValue)
        {
            if (requiredField is not null)
                missingFields.Add(requiredField);
            return;
        }

        if (!IsPositiveFinite(value.Value))
            issues.Add(issue);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
