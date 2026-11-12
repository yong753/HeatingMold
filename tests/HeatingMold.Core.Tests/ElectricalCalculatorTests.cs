using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class ElectricalCalculatorTests
{
    [Fact]
    public void Case1_20m_Needs220V()
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            20_000, HeatingWireSpec.Case1, 605.0);

        Assert.True(result.IsValid);
        Assert.Equal(20.0, result.LengthM, 9);
        Assert.Equal(80.0, result.ResistanceOhm, 9);
        Assert.Equal(220.0, result.VoltageV, 9);
        Assert.Equal(2.75, result.CurrentA, 9);
        Assert.Equal(605.0, result.PowerW, 9);
    }

    [Fact]
    public void Case2_20m_Needs220V()
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            20_000, HeatingWireSpec.Case2, 302.5);

        Assert.True(result.IsValid);
        Assert.Equal(160.0, result.ResistanceOhm, 9);
        Assert.Equal(220.0, result.VoltageV, 9);
        Assert.Equal(1.375, result.CurrentA, 9);
        Assert.Equal(302.5, result.PowerW, 9);
    }

    [Fact]
    public void LengthInMillimeters_IsConvertedToMeters()
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            1_000, HeatingWireSpec.Case1, 12_100.0);

        Assert.Equal(1.0, result.LengthM, 9);
        Assert.Equal(4.0, result.ResistanceOhm, 9);
        Assert.Equal(220.0, result.VoltageV, 9);
        Assert.Equal(55.0, result.CurrentA, 9);
        Assert.Equal(12_100.0, result.PowerW, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidLength_IsRejected(double lengthMm)
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            lengthMm, HeatingWireSpec.Case1, 100.0);

        Assert.False(result.IsValid);
        Assert.Contains("길이", result.Message);
    }

    [Fact]
    public void ZeroResistance_IsRejected()
    {
        var wire = HeatingWireSpec.Case1 with { ResistanceOhmPerMeter = 0 };

        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(20_000, wire, 100.0);

        Assert.False(result.IsValid);
        Assert.Contains("저항", result.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidTargetPower_IsRejected(double targetPowerW)
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            20_000, HeatingWireSpec.Case1, targetPowerW);

        Assert.False(result.IsValid);
        Assert.Contains("발열 전력", result.Message);
    }
}
