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
        Assert.Equal(ElectricalVerdict.Ok, result.Verdict);
    }

    [Fact]
    public void Case2_20m_MatchesPdfFormula()
    {
        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case2, Supply);

        Assert.True(result.IsValid);
        Assert.Equal(160.0, result.ResistanceOhm, 9);
        Assert.Equal(1.375, result.CurrentA, 9);
        Assert.Equal(302.5, result.PowerW, 9);
        Assert.Equal(ElectricalVerdict.Ok, result.Verdict);
    }

    [Fact]
    public void LengthInMillimeters_IsConvertedToMeters()
    {
        ElectricalResult result = ElectricalCalculator.Compute(1_000, HeatingWireSpec.Case1, Supply);

        Assert.Equal(1.0, result.LengthM, 9);
        Assert.Equal(4.0, result.ResistanceOhm, 9);
        Assert.Equal(55.0, result.CurrentA, 9);
        Assert.Equal(12_100.0, result.PowerW, 9);
        Assert.Equal(ElectricalVerdict.OverCurrentAndPower, result.Verdict);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidLength_IsRejected(double lengthMm)
    {
        ElectricalResult result = ElectricalCalculator.Compute(lengthMm, HeatingWireSpec.Case1, Supply);

        Assert.False(result.IsValid);
        Assert.Equal(ElectricalVerdict.InvalidInput, result.Verdict);
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
    public void ExactlyAtCurrentLimit_IsOk()
    {
        // 3.4375 m × 4 Ω/m = 13.75 Ω, 220 V / 13.75 Ω = 16 A, 220 V × 16 A = 3520 W
        ElectricalResult result = ElectricalCalculator.Compute(3_437.5, HeatingWireSpec.Case1, Supply);

        Assert.Equal(16.0, result.CurrentA, 9);
        Assert.Equal(3_520.0, result.PowerW, 9);
        Assert.Equal(ElectricalVerdict.Ok, result.Verdict);
    }

    [Fact]
    public void JustOverCurrentLimit_IsOverCurrent()
    {
        var supply = Supply with { MaxPowerW = 4_000 };

        ElectricalResult result = ElectricalCalculator.Compute(3_400, HeatingWireSpec.Case1, supply);

        Assert.True(result.CurrentA > supply.MaxCurrentA);
        Assert.Equal(ElectricalVerdict.OverCurrent, result.Verdict);
    }

    [Fact]
    public void PowerAboveLimit_IsOverPower()
    {
        var supply = Supply with { MaxPowerW = 500 };

        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case1, supply);

        Assert.Equal(ElectricalVerdict.OverPower, result.Verdict);
    }

    [Fact]
    public void MaxVoltage_KeepsCurrentWithinLimit()
    {
        ElectricalResult result = ElectricalCalculator.Compute(20_000, HeatingWireSpec.Case1, Supply);

        Assert.Equal(1_280.0, result.MaxVoltageV, 9);
        Assert.Equal(Supply.MaxCurrentA, result.MaxVoltageV / result.ResistanceOhm, 9);
    }
}
