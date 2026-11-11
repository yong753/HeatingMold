using System.Linq;
using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class TargetPowerElectricalTests
{
    private static readonly ElectricalLimits Limits = new();

    [Theory]
    [InlineData(10_000, 400.0)]
    [InlineData(2_500, 100.0)]
    public void FromTargetPower_Case1_ComputesRequiredVoltageAndCurrent(double lengthMm, double powerW)
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            lengthMm, HeatingWireSpec.Case1, powerW);

        Assert.True(result.IsValid);
        Assert.True(result.TargetPowerMode);
        Assert.Equal(powerW, result.PowerW, 9);
        double resistance = 4.0 * (lengthMm / 1000.0);
        Assert.Equal(resistance, result.ResistanceOhm, 9);
        Assert.Equal(Math.Sqrt(powerW * resistance), result.VoltageV, 9);
        // V·I = P가 성립한다.
        Assert.Equal(powerW, result.VoltageV * result.CurrentA, 6);
    }

    [Fact]
    public void FromTargetPower_HalfLengthAtHalfPower_KeepsCurrentAndHalvesVoltage()
    {
        ElectricalResult whole = ElectricalCalculator.ComputeFromTargetPower(
            10_000, HeatingWireSpec.Case1, 1000.0);
        ElectricalResult half = ElectricalCalculator.ComputeFromTargetPower(
            5_000, HeatingWireSpec.Case1, 500.0);

        Assert.Equal(whole.CurrentA, half.CurrentA, 9);
        Assert.Equal(whole.VoltageV / 2.0, half.VoltageV, 9);
    }

    [Fact]
    public void FromTargetPower_EqualLengthSplit_KeepsCurrent()
    {
        // 길이 합계가 그대로면 분할 수와 무관하게 전류가 같다.
        ElectricalResult whole = ElectricalCalculator.ComputeFromTargetPower(
            20_000, HeatingWireSpec.Case1, 2000.0);
        ElectricalResult third = ElectricalCalculator.ComputeFromTargetPower(
            20_000 / 3.0, HeatingWireSpec.Case1, 2000.0 / 3.0);

        Assert.Equal(whole.CurrentA, third.CurrentA, 6);
    }

    [Fact]
    public void FromTargetPower_ZeroPower_IsExplicitError()
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            10_000, HeatingWireSpec.Case1, 0.0);

        Assert.False(result.IsValid);
        Assert.Contains("목표 발열전력", result.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FromTargetPower_InvalidLength_IsRejected(double lengthMm)
    {
        ElectricalResult result = ElectricalCalculator.ComputeFromTargetPower(
            lengthMm, HeatingWireSpec.Case1, 1000.0);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void BuildFromTargetPower_KeepsNgRowsAndReportsRequiredVoltage()
    {
        // 2.5 m 회로에 4.84 kW → R=10 Ω, V=220 V, I=22 A(전류/전력 NG).
        var inputs = new[]
        {
            new TargetPowerCircuitInput("A-1", 2_500, 4_840.0)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, Limits);

        Assert.True(table.IsTargetPowerMode);
        Assert.Null(table.Supply);
        Assert.Equal(220.0, table.Rows[0].VoltageV, 6);
        Assert.Equal(22.0, table.Rows[0].CurrentA, 6);
        Assert.False(table.Rows[0].IsValid == false);
        Assert.True(table.Rows[0].IsOverLimit);
        Assert.Equal("NG", table.Rows[0].Assessment!.IsWithinLimits ? "OK" : "NG");
        Assert.Equal(4_840.0, table.TotalPowerW, 6);
        Assert.Equal(4_840.0, table.TotalTargetPowerW, 6);
    }

    [Fact]
    public void BuildFromTargetPower_InvalidTarget_IsCalculationErrorNotNg()
    {
        var inputs = new[] { new TargetPowerCircuitInput("A-1", 2_500, 0.0) };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, Limits);

        Assert.False(table.Rows[0].IsValid);
        Assert.Null(table.Rows[0].Assessment);
        Assert.Equal(0, table.OverLimitCount);
    }

    [Fact]
    public void BuildFromTargetPower_MissingTarget_KeepsRowWithoutComputing()
    {
        var inputs = new[] { new TargetPowerCircuitInput("A-1", 2_500, null) };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, Limits);

        ElectricalSpecRow row = table.Rows[0];
        Assert.True(row.IsTargetPowerMissing);
        Assert.False(row.IsValid);
        Assert.Null(row.TargetPowerW);
        Assert.Null(row.Assessment);
        Assert.Equal(2.5, row.LengthM, 9);
        // 저항은 목표전력과 무관하게 rL로 확정된다.
        Assert.Equal(10.0, row.ResistanceOhm, 9);
        Assert.Contains("미입력", row.Message);
        Assert.Equal(0, table.TotalTargetPowerW);
        Assert.Equal(0, table.TotalPowerW);
        Assert.Equal(0, table.OverLimitCount);
    }

    [Fact]
    public void BuildFromTargetPower_MixedMissingAndReady_KeepsBothRows()
    {
        var inputs = new[]
        {
            new TargetPowerCircuitInput("A-1", 2_500, 4_840.0),
            new TargetPowerCircuitInput("B-1", 2_500, null)
        };

        ElectricalSpecTable table = ElectricalSpecTableBuilder.BuildFromTargetPower(
            inputs, HeatingWireSpec.Case1, Limits);

        Assert.Equal(2, table.Rows.Count);
        Assert.True(table.Rows[0].IsValid);
        Assert.True(table.Rows[1].IsTargetPowerMissing);
        Assert.Equal(4_840.0, table.TotalTargetPowerW, 6);
        Assert.Equal(4_840.0, table.TotalPowerW, 6);
    }

    [Fact]
    public void TryAllocate_EqualLengths_SplitsTotalPowerEvenly()
    {
        var circuits = new (string, double)[] { ("A", 5_000), ("B", 5_000), ("C", 5_000) };

        bool ok = TargetPowerAllocation.TryAllocate(circuits, 3_000.0, out var shares, out string error);

        Assert.True(ok, error);
        Assert.All(shares, share => Assert.Equal(1_000.0, share.TargetPowerW, 9));
        Assert.Equal(3_000.0, shares.Sum(s => s.TargetPowerW), 9);
    }

    [Fact]
    public void TryAllocate_UnequalLengths_UsesLengthRatioAndPreservesTotal()
    {
        var circuits = new (string, double)[] { ("A", 2_000), ("B", 6_000) };

        bool ok = TargetPowerAllocation.TryAllocate(circuits, 4_000.0, out var shares, out string error);

        Assert.True(ok, error);
        Assert.Equal(1_000.0, shares[0].TargetPowerW, 9);
        Assert.Equal(3_000.0, shares[1].TargetPowerW, 9);
        Assert.Equal(4_000.0, shares.Sum(s => s.TargetPowerW), 9);
    }

    [Fact]
    public void TryAllocate_ThenCompute_ProducesEqualCurrents()
    {
        var circuits = new (string, double)[] { ("A", 1_500), ("B", 7_500), ("C", 3_000) };
        Assert.True(TargetPowerAllocation.TryAllocate(circuits, 2_000.0, out var shares, out _));

        var currents = shares
            .Select(s => ElectricalCalculator.ComputeFromTargetPower(
                s.LengthMm, HeatingWireSpec.Case1, s.TargetPowerW).CurrentA)
            .ToList();

        Assert.Equal(currents[0], currents[1], 6);
        Assert.Equal(currents[0], currents[2], 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void TryAllocate_InvalidTotalPower_IsRejected(double total)
    {
        var circuits = new (string, double)[] { ("A", 1_000) };

        Assert.False(TargetPowerAllocation.TryAllocate(circuits, total, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryAllocate_EmptyOrInvalidLength_IsRejected()
    {
        Assert.False(TargetPowerAllocation.TryAllocate(Array.Empty<(string, double)>(), 100.0, out _, out _));
        var circuits = new (string, double)[] { ("A", 1_000), ("B", 0.0) };
        Assert.False(TargetPowerAllocation.TryAllocate(circuits, 100.0, out _, out string error));
        Assert.Contains("길이", error);
    }
}
