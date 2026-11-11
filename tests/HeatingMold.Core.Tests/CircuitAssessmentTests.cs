using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class CircuitAssessmentTests
{
    [Fact]
    public void DefaultLimits_MatchConfirmedValues()
    {
        ElectricalLimits limits = ElectricalLimits.Default;

        Assert.Equal(16.0, limits.CurrentLimitA);
        Assert.Equal(220.0, limits.VoltageLimitV);
        Assert.Equal(3_500.0, limits.PowerLimitW);
    }

    [Fact]
    public void EqualValues_AreWithinLimits()
    {
        CircuitAssessment assessment = CircuitAssessment.Assess(16.0, 220.0, 3_500.0, ElectricalLimits.Default);

        Assert.True(assessment.IsCurrentWithinLimit);
        Assert.True(assessment.IsVoltageWithinLimit);
        Assert.True(assessment.IsPowerWithinLimit);
        Assert.True(assessment.IsWithinLimits);
        Assert.Equal("", assessment.Note);
    }

    [Fact]
    public void CurrentAboveLimit_IsNg()
    {
        CircuitAssessment assessment = CircuitAssessment.Assess(16.001, 220.0, 100.0, ElectricalLimits.Default);

        Assert.False(assessment.IsCurrentWithinLimit);
        Assert.True(assessment.IsVoltageWithinLimit);
        Assert.True(assessment.IsPowerWithinLimit);
        Assert.False(assessment.IsWithinLimits);
        Assert.Contains("전류 초과", assessment.Note);
        Assert.DoesNotContain("전압 초과", assessment.Note);
        Assert.DoesNotContain("전력 초과", assessment.Note);
    }

    [Fact]
    public void VoltageAboveLimit_IsNg()
    {
        CircuitAssessment assessment = CircuitAssessment.Assess(1.0, 230.0, 230.0, ElectricalLimits.Default);

        Assert.False(assessment.IsVoltageWithinLimit);
        Assert.False(assessment.IsWithinLimits);
        Assert.Contains("전압 초과", assessment.Note);
    }

    [Fact]
    public void PowerAboveLimit_IsNg()
    {
        CircuitAssessment assessment = CircuitAssessment.Assess(1.0, 220.0, 3_500.001, ElectricalLimits.Default);

        Assert.False(assessment.IsPowerWithinLimit);
        Assert.False(assessment.IsWithinLimits);
        Assert.Contains("전력 초과", assessment.Note);
    }

    [Fact]
    public void DefaultLimits_ExactCurrentLimitAt220V_ExceedsPowerLimit()
    {
        // 220 V / 16 A = 3,520 W이므로 전류는 경계값이지만 기본 허용 전력 3,500 W를 넘는다.
        ElectricalResult result = ElectricalCalculator.Compute(
            13_750, new HeatingWireSpec { ResistanceOhmPerMeter = 1.0 }, new PowerSupplySpec { VoltageV = 220 });

        CircuitAssessment assessment = CircuitAssessment.Assess(
            result.CurrentA, result.VoltageV, result.PowerW, ElectricalLimits.Default);

        Assert.Equal(16.0, result.CurrentA, 9);
        Assert.Equal(3_520.0, result.PowerW, 9);
        Assert.True(assessment.IsCurrentWithinLimit);
        Assert.False(assessment.IsPowerWithinLimit);
        Assert.False(assessment.IsWithinLimits);
        Assert.Contains("전력 초과", assessment.Note);
    }

    [Fact]
    public void MultipleExceededItems_AreAllReported()
    {
        CircuitAssessment assessment = CircuitAssessment.Assess(55.0, 230.0, 12_100.0, ElectricalLimits.Default);

        Assert.False(assessment.IsWithinLimits);
        Assert.Contains("전류 초과", assessment.Note);
        Assert.Contains("전압 초과", assessment.Note);
        Assert.Contains("전력 초과", assessment.Note);
    }
}
