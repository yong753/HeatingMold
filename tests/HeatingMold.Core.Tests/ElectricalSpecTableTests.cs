using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class ElectricalSpecTableTests
{
    private static readonly PowerSupplySpec Supply = new();

    [Fact]
    public void TwoCircuits_SumCurrentAndPower()
    {
        var inputs = new[]
        {
            new ElectricalCircuitInput("A-1", 20_000),
            new ElectricalCircuitInput("A-2", 20_000)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, Supply);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(5.5, table.TotalCurrentA, 9);
        Assert.Equal(1_210.0, table.TotalPowerW, 9);
        Assert.Equal(ElectricalVerdict.Ok, table.TotalVerdict);
        Assert.Equal(3.4375, table.RecommendedMinLengthM, 9);
    }

    [Fact]
    public void TotalCurrentOverLimit_IsFlaggedEvenWhenEachCircuitIsOk()
    {
        var supply = Supply with { MaxCurrentA = 4.0, MaxPowerW = 2_000 };
        var inputs = new[]
        {
            new ElectricalCircuitInput("A-1", 20_000),
            new ElectricalCircuitInput("A-2", 20_000)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, supply);

        Assert.All(table.Rows, row => Assert.Equal(ElectricalVerdict.Ok, row.Verdict));
        Assert.Equal(ElectricalVerdict.OverCurrent, table.TotalVerdict);
        Assert.Contains("합계 전류", table.TotalMessage);
    }

    [Fact]
    public void TotalPowerOverLimit_IsFlagged()
    {
        var supply = Supply with { MaxPowerW = 1_000 };
        var inputs = new[]
        {
            new ElectricalCircuitInput("A-1", 20_000),
            new ElectricalCircuitInput("A-2", 20_000)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, supply);

        Assert.Equal(ElectricalVerdict.OverPower, table.TotalVerdict);
        Assert.Contains("합계 전력", table.TotalMessage);
    }

    [Fact]
    public void CircuitOrderAndIds_ArePreserved()
    {
        var inputs = new[]
        {
            new ElectricalCircuitInput("B-2", 10_000),
            new ElectricalCircuitInput("A-1", 20_000)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, Supply);

        Assert.Equal(new[] { "B-2", "A-1" }, table.Rows.Select(r => r.CircuitId));
    }

    [Fact]
    public void InvalidRow_MakesTotalInvalid()
    {
        var inputs = new[]
        {
            new ElectricalCircuitInput("A-1", 20_000),
            new ElectricalCircuitInput("A-2", 0)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, Supply);

        Assert.False(table.Rows[1].IsValid);
        Assert.Equal(ElectricalVerdict.InvalidInput, table.TotalVerdict);
    }

    [Fact]
    public void EmptyTable_IsOk()
    {
        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(
            Array.Empty<ElectricalCircuitInput>(), HeatingWireSpec.Case1, Supply);

        Assert.Empty(table.Rows);
        Assert.Equal(0.0, table.TotalCurrentA, 9);
        Assert.Equal(0.0, table.TotalPowerW, 9);
        Assert.Equal(ElectricalVerdict.Ok, table.TotalVerdict);
    }
}
