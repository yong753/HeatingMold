using System.Collections.ObjectModel;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Calculation;

namespace HeatingMold.Electrical.Limits;

/// <summary>회로별 실제값을 전압·전류·전력 한도와 반올림 없이 비교합니다.</summary>
public sealed class CircuitLimitEvaluator
{
    /// <summary>실제값과 입력 한도별 판정 결과를 반환합니다.</summary>
    public IReadOnlyList<ElectricalLimitResult> Evaluate(
        Supply supply,
        CurrentAmps? current,
        PowerWatts? power,
        IReadOnlyCollection<string> calculationIssues)
    {
        ArgumentNullException.ThrowIfNull(supply);
        ArgumentNullException.ThrowIfNull(calculationIssues);

        var results = new List<ElectricalLimitResult>
        {
            CheckVoltage(supply, calculationIssues),
            CheckCurrent(supply, current, calculationIssues),
            CheckPower(supply, power, calculationIssues)
        };

        return new ReadOnlyCollection<ElectricalLimitResult>(results);
    }

    private static ElectricalLimitResult CheckVoltage(Supply supply, IReadOnlyCollection<string> issues)
    {
        return CreateLimitResult(
            ElectricalLimitKind.MaximumVoltage,
            supply.ActualVoltageVrms?.Value,
            supply.MaximumVoltageVrms?.Value,
            "V RMS",
            HasAnyIssue(issues, "InvalidActualVoltage", "InvalidMaximumVoltage"));
    }

    private static ElectricalLimitResult CheckCurrent(
        Supply supply,
        CurrentAmps? current,
        IReadOnlyCollection<string> issues)
    {
        return CreateLimitResult(
            ElectricalLimitKind.MaximumCurrent,
            current?.Value,
            supply.MaximumCurrentAmps?.Value,
            "A",
            HasAnyIssue(issues,
                "InvalidActualVoltage", "InvalidResistancePerMeter", "InvalidResistanceLength",
                "ResistanceLengthOutOfRange", "ResistanceOutOfRange", "CurrentOutOfRange",
                "InvalidMaximumCurrent"));
    }

    private static ElectricalLimitResult CheckPower(
        Supply supply,
        PowerWatts? power,
        IReadOnlyCollection<string> issues)
    {
        return CreateLimitResult(
            ElectricalLimitKind.MaximumPower,
            power?.Value,
            supply.MaximumPowerWatts?.Value,
            "W",
            HasAnyIssue(issues,
                "InvalidActualVoltage", "InvalidResistancePerMeter", "InvalidResistanceLength",
                "ResistanceLengthOutOfRange", "ResistanceOutOfRange", "PowerOutOfRange",
                "InvalidMaximumPower"));
    }

    private static ElectricalLimitResult CreateLimitResult(
        ElectricalLimitKind kind,
        double? measured,
        double? allowed,
        string unitSymbol,
        bool invalidInput)
    {
        if (invalidInput)
            return new ElectricalLimitResult(kind, LimitCheckStatus.Invalid, measured, allowed, null, unitSymbol);

        if (!measured.HasValue || !allowed.HasValue)
            return new ElectricalLimitResult(kind, LimitCheckStatus.Missing, measured, allowed, null, unitSymbol);

        if (!IsPositiveFinite(measured.Value) || !IsPositiveFinite(allowed.Value))
            return new ElectricalLimitResult(kind, LimitCheckStatus.Invalid, measured, allowed, null, unitSymbol);

        if (measured.Value <= allowed.Value)
            return new ElectricalLimitResult(kind, LimitCheckStatus.WithinLimit, measured, allowed, 0, unitSymbol);

        double exceedanceRatio = measured.Value / allowed.Value - 1;
        return new ElectricalLimitResult(
            kind,
            LimitCheckStatus.Exceeded,
            measured,
            allowed,
            double.IsFinite(exceedanceRatio) ? exceedanceRatio : null,
            unitSymbol);
    }

    private static bool HasAnyIssue(IReadOnlyCollection<string> issues, params string[] expectedCodes)
    {
        return expectedCodes.Any(issues.Contains);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
