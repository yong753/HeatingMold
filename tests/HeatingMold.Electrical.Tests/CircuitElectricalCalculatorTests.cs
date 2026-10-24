using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Calculation;

namespace HeatingMold.Electrical.Tests;

/// <summary>회로별 전기 계산과 한도 판정의 모듈 계약을 검증합니다.</summary>
public sealed class CircuitElectricalCalculatorTests
{
    private readonly CircuitElectricalCalculator _calculator = new();

    /// <summary>Case 1의 10m 발열사에서 40Ω, 5.5A, 1210W를 계산합니다.</summary>
    [Fact]
    public void CaseOne_ProducesExpectedResistanceCurrentAndPower()
    {
        ElectricalResult result = Calculate(lengthMm: 10_000, resistanceOhmsPerMeter: 4);

        Assert.Equal(ElectricalStatus.Pass, result.Status);
        Assert.Equal(10, result.ResistanceLengthMeters);
        Assert.Equal(40, result.ResistanceOhms?.Value);
        Assert.Equal(5.5, result.CurrentAmps?.Value);
        Assert.Equal(1_210, result.PowerWatts?.Value);
        Assert.Equal("electrical-v1", result.RuleVersion);
        Assert.Equal(10_000, result.LengthBreakdown.HeatingBodyMm.Value);
        Assert.Equal(0, result.LengthBreakdown.SeparateConnectionWireMm.Value);
    }

    /// <summary>Case 2의 높은 단위 저항에서 기대 전기값을 계산합니다.</summary>
    [Fact]
    public void CaseTwo_ProducesExpectedValues()
    {
        ElectricalResult result = Calculate(lengthMm: 10_000, resistanceOhmsPerMeter: 8);

        Assert.Equal(ElectricalStatus.Pass, result.Status);
        Assert.Equal(80, result.ResistanceOhms?.Value);
        Assert.Equal(2.75, result.CurrentAmps?.Value);
        Assert.Equal(605, result.PowerWatts?.Value);
    }

    /// <summary>발열사 연장을 저항 길이에 포함하고 별도 연결선을 제외합니다.</summary>
    [Fact]
    public void SeparateConnectionWire_IsExcludedFromResistanceLength()
    {
        LengthBreakdown breakdown = CreateBreakdown(
            heatingBodyMm: 9_000,
            heatingExtensionMm: 1_000,
            separateWireMm: 2_000);

        ElectricalResult result = Calculate(breakdown, resistanceOhmsPerMeter: 4);

        Assert.Equal(10, result.ResistanceLengthMeters);
        Assert.Equal(40, result.ResistanceOhms?.Value);
        Assert.Equal(5.5, result.CurrentAmps?.Value);
    }

    /// <summary>발열 여유는 저항 길이에 더하고 별도 연결선 여유는 제외합니다.</summary>
    [Fact]
    public void HeatingAllowance_IsIncludedWithoutCountingLeadAllowance()
    {
        LengthBreakdown breakdown = CreateBreakdown(
            heatingBodyMm: 9_000,
            heatingExtensionMm: 1_000,
            separateWireMm: 2_000,
            heatingAllowanceMm: 200,
            separateWireAllowanceMm: 500);

        ElectricalResult result = Calculate(breakdown, resistanceOhmsPerMeter: 4);

        Assert.InRange(Math.Abs(result.ResistanceLengthMeters!.Value - 10.2), 0, 1e-12);
        Assert.InRange(Math.Abs(result.ResistanceOhms!.Value.Value - 40.8), 0, 1e-12);
    }

    /// <summary>각 회로 한도만 평가하며 서로 다른 회로 전류를 합산하지 않습니다.</summary>
    [Fact]
    public void Circuits_AreEvaluatedIndependently()
    {
        ElectricalResult first = Calculate(lengthMm: 22_000, resistanceOhmsPerMeter: 1);
        ElectricalResult second = Calculate(lengthMm: 22_000, resistanceOhmsPerMeter: 1);

        Assert.Equal(10, first.CurrentAmps?.Value);
        Assert.Equal(10, second.CurrentAmps?.Value);
        Assert.Equal(ElectricalStatus.Pass, first.Status);
        Assert.Equal(ElectricalStatus.Pass, second.Status);
    }

    /// <summary>전압 한도를 넘으면 계산값을 보존하면서 회로를 초과 상태로 표시합니다.</summary>
    [Fact]
    public void MaximumVoltageExceeded_KeepsCalculatedValues()
    {
        ElectricalResult result = Calculate(maximumVoltage: 200);

        Assert.Equal(ElectricalStatus.LimitExceeded, result.Status);
        Assert.Equal(5.5, result.CurrentAmps?.Value);
        Assert.Equal(1_210, result.PowerWatts?.Value);
        Assert.Equal(LimitCheckStatus.Exceeded, FindLimit(result, ElectricalLimitKind.MaximumVoltage).Status);
    }

    /// <summary>한도와 같은 실제값은 통과하고 조금 낮은 한도는 초과로 판정합니다.</summary>
    [Fact]
    public void Limits_AcceptEqualityAndRejectSmallerValues()
    {
        ElectricalResult exact = Calculate(maximumCurrent: 5.5, maximumPower: 1_210);
        ElectricalResult lower = Calculate(maximumCurrent: 5.499, maximumPower: 1_209.9);

        Assert.Equal(ElectricalStatus.Pass, exact.Status);
        Assert.Equal(LimitCheckStatus.WithinLimit, FindLimit(exact, ElectricalLimitKind.MaximumCurrent).Status);
        Assert.Equal(LimitCheckStatus.WithinLimit, FindLimit(exact, ElectricalLimitKind.MaximumPower).Status);
        Assert.Equal(ElectricalStatus.LimitExceeded, lower.Status);
        Assert.Equal(LimitCheckStatus.Exceeded, FindLimit(lower, ElectricalLimitKind.MaximumCurrent).Status);
        Assert.Equal(LimitCheckStatus.Exceeded, FindLimit(lower, ElectricalLimitKind.MaximumPower).Status);
    }

    /// <summary>실제 전압 누락을 최대 전압으로 대체하지 않고 계산값을 비웁니다.</summary>
    [Fact]
    public void MissingActualVoltage_ReturnsIncompleteWithoutInventingCurrent()
    {
        ElectricalResult result = Calculate(omitActualVoltage: true);

        Assert.Equal(ElectricalStatus.Incomplete, result.Status);
        Assert.Null(result.ActualVoltageVrms);
        Assert.Null(result.CurrentAmps);
        Assert.Null(result.PowerWatts);
        Assert.Equal(LimitCheckStatus.Missing, FindLimit(result, ElectricalLimitKind.MaximumVoltage).Status);
        Assert.Contains("Missing:ActualVoltageVrms", result.InputIssues);
    }

    /// <summary>필수 전류 한도가 빠져도 계산 가능한 저항·전류·전력을 보존합니다.</summary>
    [Fact]
    public void MissingRequiredCurrentLimit_KeepsElectricalValues()
    {
        ElectricalResult result = Calculate(maximumCurrent: null);

        Assert.Equal(ElectricalStatus.Incomplete, result.Status);
        Assert.Equal(40, result.ResistanceOhms?.Value);
        Assert.Equal(5.5, result.CurrentAmps?.Value);
        Assert.Equal(1_210, result.PowerWatts?.Value);
        Assert.Equal(LimitCheckStatus.Missing, FindLimit(result, ElectricalLimitKind.MaximumCurrent).Status);
    }

    /// <summary>선택 전력 한도가 없으면 한도 상태만 Missing으로 두고 회로는 통과시킵니다.</summary>
    [Fact]
    public void MissingOptionalPowerLimit_DoesNotMakeCircuitIncomplete()
    {
        ElectricalResult result = Calculate(maximumPower: null);

        Assert.Equal(ElectricalStatus.Pass, result.Status);
        Assert.Equal(LimitCheckStatus.Missing, FindLimit(result, ElectricalLimitKind.MaximumPower).Status);
    }

    /// <summary>0 길이와 0 전압은 유효한 0W 결과가 아니라 잘못된 입력입니다.</summary>
    [Fact]
    public void ZeroLengthOrVoltage_IsInvalidInput()
    {
        ElectricalResult zeroLength = Calculate(lengthMm: 0);
        ElectricalResult zeroVoltage = Calculate(actualVoltage: new VoltageVrms(0));

        Assert.Equal(ElectricalStatus.InvalidInput, zeroLength.Status);
        Assert.Null(zeroLength.ResistanceOhms);
        Assert.Equal(ElectricalStatus.InvalidInput, zeroVoltage.Status);
        Assert.Null(zeroVoltage.PowerWatts);
    }

    /// <summary>유효한 값이 있어도 필수 입력 누락과 함께 숫자 오류가 있으면 무효를 우선합니다.</summary>
    [Fact]
    public void InvalidInput_TakesPrecedenceOverMissingFields()
    {
        ElectricalResult result = Calculate(actualVoltage: new VoltageVrms(0), maximumCurrent: null);

        Assert.Equal(ElectricalStatus.InvalidInput, result.Status);
        Assert.Contains("InvalidActualVoltage", result.InputIssues);
        Assert.Contains("Missing:MaximumCurrentAmps", result.InputIssues);
    }

    /// <summary>표시 반올림에서 같아지는 전류도 원래 double 값이 한도를 넘으면 초과입니다.</summary>
    [Fact]
    public void LimitComparison_UsesUnroundedCurrent()
    {
        ElectricalResult result = Calculate(
            actualVoltage: new VoltageVrms(220.0000000000004),
            maximumCurrent: 5.5);

        Assert.True(result.CurrentAmps!.Value.Value > 5.5);
        Assert.Equal(5.5, Math.Round(result.CurrentAmps.Value.Value, 2));
        Assert.Equal(LimitCheckStatus.Exceeded, FindLimit(result, ElectricalLimitKind.MaximumCurrent).Status);
    }

    /// <summary>저항 계산의 곱셈 overflow와 전력 계산 overflow를 정상 수치로 내보내지 않습니다.</summary>
    [Fact]
    public void ArithmeticOverflow_ReturnsInvalidInput()
    {
        ElectricalResult resistanceOverflow = Calculate(
            lengthMm: 10_000,
            resistanceOhmsPerMeter: 1e308);
        ElectricalResult resistanceUnderflow = Calculate(
            lengthMm: 1,
            resistanceOhmsPerMeter: double.Epsilon);
        ElectricalResult powerOverflow = Calculate(
            lengthMm: 1_000,
            resistanceOhmsPerMeter: 1,
            actualVoltage: new VoltageVrms(1e200));

        Assert.Equal(ElectricalStatus.InvalidInput, resistanceOverflow.Status);
        Assert.Null(resistanceOverflow.ResistanceOhms);
        Assert.Equal(ElectricalStatus.InvalidInput, resistanceUnderflow.Status);
        Assert.Null(resistanceUnderflow.ResistanceOhms);
        Assert.Equal(ElectricalStatus.InvalidInput, powerOverflow.Status);
        Assert.True(double.IsFinite(powerOverflow.CurrentAmps!.Value.Value));
        Assert.Null(powerOverflow.PowerWatts);
    }

    /// <summary>전기식 입력이 같으면 직경 변경만으로 계산값이 바뀌지 않습니다.</summary>
    [Fact]
    public void DiameterChange_DoesNotChangeElectricalFormula()
    {
        ElectricalResult caseOne = Calculate(diameterMm: 0.3, resistanceOhmsPerMeter: 4);
        ElectricalResult differentDiameter = Calculate(diameterMm: 0.15, resistanceOhmsPerMeter: 4);

        Assert.Equal(caseOne.ResistanceOhms, differentDiameter.ResistanceOhms);
        Assert.Equal(caseOne.CurrentAmps, differentDiameter.CurrentAmps);
        Assert.Equal(caseOne.PowerWatts, differentDiameter.PowerWatts);
    }

    private ElectricalResult Calculate(
        double lengthMm = 10_000,
        double resistanceOhmsPerMeter = 4,
        double diameterMm = 0.3,
        VoltageVrms? actualVoltage = null,
        bool omitActualVoltage = false,
        double? maximumVoltage = 220,
        double? maximumCurrent = 16,
        double? maximumPower = null)
    {
        var wire = new WireSpecification(
            "SUS 316L",
            new LengthMm(diameterMm),
            new ResistanceOhmsPerMeter(resistanceOhmsPerMeter));
        var supply = new Supply(
            omitActualVoltage ? null : actualVoltage ?? new VoltageVrms(220),
            maximumVoltage is { } voltageLimit ? new VoltageVrms(voltageLimit) : null,
            maximumCurrent is { } currentLimit ? new CurrentAmps(currentLimit) : null,
            maximumPower is { } powerLimit ? new PowerWatts(powerLimit) : null);
        var input = new CircuitElectricalInput(
            CircuitId.New(),
            wire,
            supply,
            CreateBreakdown(heatingBodyMm: lengthMm),
            CreateStamp());

        return _calculator.Calculate(input);
    }

    private ElectricalResult Calculate(LengthBreakdown breakdown, double resistanceOhmsPerMeter)
    {
        var input = new CircuitElectricalInput(
            CircuitId.New(),
            new WireSpecification("SUS 316L", new LengthMm(0.3), new ResistanceOhmsPerMeter(resistanceOhmsPerMeter)),
            new Supply(new VoltageVrms(220), new VoltageVrms(220), new CurrentAmps(16)),
            breakdown,
            CreateStamp());

        return _calculator.Calculate(input);
    }

    private static LengthBreakdown CreateBreakdown(
        double heatingBodyMm,
        double heatingExtensionMm = 0,
        double separateWireMm = 0,
        double heatingAllowanceMm = 0,
        double separateWireAllowanceMm = 0)
    {
        var entries = new List<LengthLedgerEntry>();
        if (heatingBodyMm > 0 || heatingAllowanceMm > 0)
        {
            entries.Add(new LengthLedgerEntry(
                HeatingPathSegmentId.New(), null,
                LengthContributionKind.HeatingBody, GeometryDomain.OnMold,
                new LengthMm(heatingBodyMm), new LengthMm(heatingAllowanceMm)));
        }

        if (heatingExtensionMm > 0)
        {
            entries.Add(new LengthLedgerEntry(
                null, ConnectionPathSegmentId.New(),
                LengthContributionKind.HeatingWireExtension, GeometryDomain.External,
                new LengthMm(heatingExtensionMm), default));
        }

        if (separateWireMm > 0 || separateWireAllowanceMm > 0)
        {
            entries.Add(new LengthLedgerEntry(
                null, ConnectionPathSegmentId.New(),
                LengthContributionKind.SeparateConnectionWire, GeometryDomain.External,
                new LengthMm(separateWireMm), new LengthMm(separateWireAllowanceMm)));
        }

        return new LengthBreakdown(entries);
    }

    private static DependencyStamp CreateStamp() => new(
        new DesignRevision(1),
        new string('a', 64),
        new string('b', 64),
        new string('c', 64),
        "test-v1");

    private static ElectricalLimitResult FindLimit(ElectricalResult result, ElectricalLimitKind kind) =>
        Assert.Single(result.Limits.Where(limit => limit.Kind == kind));
}
