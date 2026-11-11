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

    [Fact]
    public void OverLimitCircuit_StaysValidAndInTotals()
    {
        var inputs = new[]
        {
            new ElectricalCircuitInput("A-1", 20_000), // 2.75 A / 605 W
            new ElectricalCircuitInput("A-2", 1_000)   // 55 A / 12,100 W
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(
            inputs, HeatingWireSpec.Case1, Supply, ElectricalLimits.Default);

        // NG는 계산 오류가 아니므로 두 행 모두 유효하고 합계에도 포함된다.
        Assert.All(table.Rows, row => Assert.True(row.IsValid));
        Assert.True(table.Rows[0].IsWithinLimits);
        Assert.True(table.Rows[1].IsOverLimit);
        Assert.Equal(57.75, table.TotalCurrentA, 9);
        Assert.Equal(12_705.0, table.TotalPowerW, 9);
        Assert.Equal(1, table.WithinLimitCount);
        Assert.Equal(1, table.OverLimitCount);
        Assert.True(table.HasOverLimitCircuits);
        Assert.Contains("전류 초과", table.Rows[1].Note);
    }

    [Fact]
    public void TableWithoutLimits_HasNoAssessment()
    {
        var inputs = new[] { new ElectricalCircuitInput("A-1", 20_000) };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, HeatingWireSpec.Case1, Supply);

        Assert.Null(table.Rows[0].Assessment);
        Assert.False(table.Rows[0].IsWithinLimits);
        Assert.False(table.Rows[0].IsOverLimit);
        Assert.Equal("", table.Rows[0].Note);
    }

    [Fact]
    public void InvalidRow_HasNoAssessmentEvenWithLimits()
    {
        var inputs = new[] { new ElectricalCircuitInput("A-1", 0) };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(
            inputs, HeatingWireSpec.Case1, Supply, ElectricalLimits.Default);

        Assert.False(table.Rows[0].IsValid);
        Assert.Null(table.Rows[0].Assessment);
        Assert.False(table.Rows[0].IsOverLimit);
        Assert.Equal(table.Rows[0].Message, table.Rows[0].Note);
        Assert.Equal(0.0, table.TotalCurrentA, 9);
    }
}
