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
        Assert.All(table.Rows, row => Assert.True(row.IsValid));
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
    public void InvalidRow_KeepsMessageAndIsExcludedFromTotals()
    {
        var inputs = new[]
        {
            new ElectricalCircuitInput("A-1", 20_000),
            new ElectricalCircuitInput("A-2", 0)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, Supply);

        Assert.True(table.Rows[0].IsValid);
        Assert.False(table.Rows[1].IsValid);
        Assert.True(table.Rows[1].HasNote);
        Assert.Equal(2.75, table.TotalCurrentA, 9);
        Assert.Equal(605.0, table.TotalPowerW, 9);
    }

    [Fact]
    public void EmptyTable_HasNoRows()
    {
        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(
            Array.Empty<ElectricalCircuitInput>(), HeatingWireSpec.Case1, Supply);

        Assert.Empty(table.Rows);
        Assert.False(table.HasRows);
        Assert.Equal(0.0, table.TotalCurrentA, 9);
        Assert.Equal(0.0, table.TotalPowerW, 9);
    }
}
