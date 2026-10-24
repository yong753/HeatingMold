using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Limits;

namespace HeatingMold.Electrical.Calculation;

/// <summary>한 회로의 유효한 길이와 전기 사양으로 저항·전류·전력을 계산합니다.</summary>
public sealed class CircuitElectricalCalculator
{
    private const string RuleVersion = "electrical-v1";
    private readonly CircuitLimitEvaluator _limitEvaluator;

    /// <summary>전기 한도 평가기를 받아 계산기를 구성합니다.</summary>
    public CircuitElectricalCalculator(CircuitLimitEvaluator? limitEvaluator = null)
    {
        _limitEvaluator = limitEvaluator ?? new CircuitLimitEvaluator();
    }

    /// <summary>입력 지문을 유지하며 한 회로의 계산 결과를 반환합니다.</summary>
    public ElectricalResult Calculate(CircuitElectricalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        ElectricalInputValidation validation = ElectricalInputValidator.Validate(input);
        var issues = validation.Issues.ToList();
        var diagnostics = issues.Concat(validation.MissingFields.Select(field => $"Missing:{field}")).ToArray();
        LengthMm resistanceLength = input.LengthBreakdown.ResistanceLengthMm;
        double? lengthMeters = null;
        ResistanceOhms? resistance = null;
        CurrentAmps? current = null;
        PowerWatts? power = null;

        if (IsPositiveFinite(resistanceLength.Value))
        {
            double convertedLengthMeters = resistanceLength.ToMeters();
            if (IsPositiveFinite(convertedLengthMeters))
            {
                lengthMeters = convertedLengthMeters;
                double resistanceValue = input.WireSpecification.ResistanceOhmsPerMeter.Value * convertedLengthMeters;
                if (IsPositiveFinite(resistanceValue))
                {
                    resistance = new ResistanceOhms(resistanceValue);
                }
                else
                {
                    issues.Add("ResistanceOutOfRange");
                }
            }
            else
            {
                issues.Add("ResistanceLengthOutOfRange");
            }
        }

        if (resistance.HasValue && input.Supply.ActualVoltageVrms is { } voltage && IsPositiveFinite(voltage.Value))
        {
            double currentValue = voltage.Value / resistance.Value.Value;
            if (IsPositiveFinite(currentValue))
                current = new CurrentAmps(currentValue);
            else
                issues.Add("CurrentOutOfRange");

            double powerValue = voltage.Value * voltage.Value / resistance.Value.Value;
            if (IsPositiveFinite(powerValue))
                power = new PowerWatts(powerValue);
            else
                issues.Add("PowerOutOfRange");
        }

        IReadOnlyList<ElectricalLimitResult> limits = _limitEvaluator.Evaluate(
            input.Supply,
            current,
            power,
            issues);
        ElectricalStatus status = DetermineStatus(validation.MissingFields, issues, limits);
        string? failureCode = status switch
        {
            ElectricalStatus.InvalidInput => "InvalidInput",
            ElectricalStatus.Incomplete => "IncompleteInput",
            _ => null
        };

        return new ElectricalResult(
            input.CircuitId,
            input.DependencyStamp,
            input.LengthBreakdown,
            RuleVersion,
            resistanceLength,
            lengthMeters,
            input.Supply.ActualVoltageVrms,
            input.WireSpecification.ResistanceOhmsPerMeter,
            resistance,
            current,
            power,
            status,
            limits,
            failureCode,
            diagnostics.Concat(issues.Except(validation.Issues)).Distinct(StringComparer.Ordinal));
    }

    private static ElectricalStatus DetermineStatus(
        IReadOnlyCollection<string> missingFields,
        IReadOnlyCollection<string> issues,
        IReadOnlyList<ElectricalLimitResult> limits)
    {
        if (issues.Count > 0)
            return ElectricalStatus.InvalidInput;

        if (limits.Any(limit => limit.Status == LimitCheckStatus.Exceeded))
            return ElectricalStatus.LimitExceeded;

        bool requiredLimitMissing = limits.Any(limit =>
            limit.Status == LimitCheckStatus.Missing && limit.Kind != ElectricalLimitKind.MaximumPower);
        if (missingFields.Count > 0 || requiredLimitMissing)
            return ElectricalStatus.Incomplete;

        return ElectricalStatus.Pass;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
