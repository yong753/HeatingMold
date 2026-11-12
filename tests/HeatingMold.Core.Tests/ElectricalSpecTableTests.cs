using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class ElectricalSpecTableTests
{
    [Fact]
    public void TwoCircuits_SumCurrentAndPower()
    {
        var inputs = new[]
        {
            new TargetPowerCircuitInput("A-1", 20_000, 605.0),
            new TargetPowerCircuitInput("A-2", 20_000, 605.0)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, null);

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
            new TargetPowerCircuitInput("B-2", 10_000, 302.5),
            new TargetPowerCircuitInput("A-1", 20_000, 605.0)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, null);

        Assert.Equal(new[] { "B-2", "A-1" }, table.Rows.Select(r => r.CircuitId));
    }

    [Fact]
    public void InvalidRow_KeepsMessageAndIsExcludedFromTotals()
    {
        var inputs = new[]
        {
            new TargetPowerCircuitInput("A-1", 20_000, 605.0),
            new TargetPowerCircuitInput("A-2", 0, 605.0)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, null);

        Assert.True(table.Rows[0].IsValid);
        Assert.False(table.Rows[1].IsValid);
        Assert.True(table.Rows[1].HasNote);
        Assert.Equal(2.75, table.TotalCurrentA, 9);
        Assert.Equal(605.0, table.TotalPowerW, 9);
    }

    [Fact]
    public void EmptyTable_HasNoRows()
    {
        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            Array.Empty<TargetPowerCircuitInput>(), HeatingWireSpec.Case1, null);

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
            new TargetPowerCircuitInput("A-1", 20_000, 605.0),   // 2.75 A
            new TargetPowerCircuitInput("A-2", 1_000, 12_100.0)  // 55 A
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, ElectricalLimits.Default);

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
        var inputs = new[] { new TargetPowerCircuitInput("A-1", 20_000, 605.0) };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, null);

        Assert.Null(table.Rows[0].Assessment);
        Assert.False(table.Rows[0].IsWithinLimits);
        Assert.False(table.Rows[0].IsOverLimit);
        Assert.Equal("", table.Rows[0].Note);
    }

    [Fact]
    public void InvalidRow_HasNoAssessmentEvenWithLimits()
    {
        var inputs = new[] { new TargetPowerCircuitInput("A-1", 0, 605.0) };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, ElectricalLimits.Default);

        Assert.False(table.Rows[0].IsValid);
        Assert.Null(table.Rows[0].Assessment);
        Assert.False(table.Rows[0].IsOverLimit);
        Assert.Equal(table.Rows[0].Message, table.Rows[0].Note);
        Assert.Equal(0.0, table.TotalCurrentA, 9);
    }
}
