using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class ElectricalCalculatorTests
{
    private static readonly PowerSupplySpec Supply = new();

    [Fact]
    public void Case1_20m_MatchesPdfFormula()
    {
        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case1, Supply);

        Assert.True(result.IsValid);
        Assert.Equal(20.0, result.LengthM, 9);
        Assert.Equal(80.0, result.ResistanceOhm, 9);
        Assert.Equal(2.75, result.CurrentA, 9);
        Assert.Equal(605.0, result.PowerW, 9);
    }

    [Fact]
    public void Case2_20m_MatchesPdfFormula()
    {
        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case2, Supply);

        Assert.True(result.IsValid);
        Assert.Equal(160.0, result.ResistanceOhm, 9);
        Assert.Equal(1.375, result.CurrentA, 9);
        Assert.Equal(302.5, result.PowerW, 9);
    }

    [Fact]
    public void LengthInMillimeters_IsConvertedToMeters()
    {
        ElectricalResult result = ElectricalCalculator.Compute(1_000, HeatingWireSpec.Case1, Supply);

        Assert.Equal(1.0, result.LengthM, 9);
        Assert.Equal(4.0, result.ResistanceOhm, 9);
        Assert.Equal(55.0, result.CurrentA, 9);
        Assert.Equal(12_100.0, result.PowerW, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidLength_IsRejected(double lengthMm)
    {
        ElectricalResult result = ElectricalCalculator.Compute(lengthMm, HeatingWireSpec.Case1, Supply);

        Assert.False(result.IsValid);
        Assert.Contains("길이", result.Message);
    }

    [Fact]
    public void ZeroResistance_IsRejected()
    {
        var wire = HeatingWireSpec.Case1 with { ResistanceOhmPerMeter = 0 };

        ElectricalResult result = ElectricalCalculator.Compute(20_000, wire, Supply);

        Assert.False(result.IsValid);
        Assert.Contains("저항", result.Message);
    }

    [Fact]
    public void NegativeVoltage_IsRejected()
    {
        var supply = Supply with { VoltageV = -1 };

        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case1, supply);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ZeroVoltage_ProducesZeroCurrentAndPower()
    {
        var supply = Supply with { VoltageV = 0 };

        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case1, supply);

        Assert.True(result.IsValid);
        Assert.Equal(0.0, result.CurrentA, 9);
        Assert.Equal(0.0, result.PowerW, 9);
    }
}
