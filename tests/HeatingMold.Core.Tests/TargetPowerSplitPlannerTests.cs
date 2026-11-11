using HeatingMold.Core.Electrical;

namespace HeatingMold.Core.Tests;

public class TargetPowerSplitPlannerTests
{
    private static ElectricalLimits Limits() => new();

    [Fact]
    public void VoltageOverLimit_DrivesPartCount()
    {
        // 20 m, 5 kW, Case1: R=80Ω, V=632.46V, I=7.906A. 전압 한도 220V로 3분할이 필요하다.
        Assert.True(TargetPowerSplitPlanner.TryPlan(
            5000, 20_000, HeatingWireSpec.Case1, Limits(), out TargetPowerSplitPlan plan, out string error));

        Assert.True(string.IsNullOrEmpty(error));
        Assert.Equal(3, plan.Parts);
        Assert.Equal(1666.6667, plan.PerCircuitTargetPowerW, 3);
        Assert.Equal(210.8185, plan.PerCircuitVoltageV, 4);
        Assert.Equal(7.9057, plan.PerCircuitCurrentA, 3);
        Assert.Equal(20.0 / 3.0, plan.PerCircuitLengthMm / 1000.0, 6);
        Assert.False(plan.CurrentExceedsLimit);
        Assert.True(plan.RequiresSplit);
    }

    [Fact]
    public void PowerOverCircuitLimit_DoesNotAddParts()
    {
        // 1 m, 7 kW, Case1: R=4Ω, V=167.33V로 허용 전압 이하다. 회로별 전력이 3.5 kW를 넘어도
        // 분할은 전력을 늘리지 않으므로 분할 수를 늘리지 않는다.
        Assert.True(TargetPowerSplitPlanner.TryPlan(
            7000, 1_000, HeatingWireSpec.Case1, Limits(), out TargetPowerSplitPlan plan, out string error));

        Assert.True(string.IsNullOrEmpty(error));
        Assert.Equal(1, plan.Parts);
        Assert.False(plan.RequiresSplit);
        Assert.Equal(167.3320, plan.PerCircuitVoltageV, 3);
    }

    [Fact]
    public void WithinAllLimits_PlansSinglePart()
    {
        // 20 m, 500 W, Case1: R=80Ω, V=200V, I=2.5A → 분할 불필요.
        Assert.True(TargetPowerSplitPlanner.TryPlan(
            500, 20_000, HeatingWireSpec.Case1, Limits(), out TargetPowerSplitPlan plan, out string error));

        Assert.True(string.IsNullOrEmpty(error));
        Assert.Equal(1, plan.Parts);
        Assert.False(plan.RequiresSplit);
        Assert.Equal(200.0, plan.PerCircuitVoltageV, 6);
        Assert.Equal(2.5, plan.PerCircuitCurrentA, 6);
    }

    [Fact]
    public void CurrentOverLimit_SurvivesSplitAndIsReported()
    {
        // 2 m, 10 kW, Case1: R=8Ω, I=35.36A. 전압/전력 분할로도 전류는 그대로다.
        Assert.True(TargetPowerSplitPlanner.TryPlan(
            10_000, 2_000, HeatingWireSpec.Case1, Limits(), out TargetPowerSplitPlan plan, out string error));

        Assert.True(string.IsNullOrEmpty(error));
        Assert.True(plan.CurrentExceedsLimit);
        Assert.True(plan.Parts > 1);
        Assert.Equal(35.3553, plan.PerCircuitCurrentA, 3);
        Assert.Contains("분할", plan.Note);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTargetPower_IsRejected(double power)
    {
        Assert.False(TargetPowerSplitPlanner.TryPlan(
            power, 20_000, HeatingWireSpec.Case1, Limits(), out _, out string error));
        Assert.Contains("발열 전력", error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void InvalidLength_IsRejected(double lengthMm)
    {
        Assert.False(TargetPowerSplitPlanner.TryPlan(
            1000, lengthMm, HeatingWireSpec.Case1, Limits(), out _, out string error));
        Assert.Contains("길이", error);
    }

    [Fact]
    public void MissingWireOrLimits_IsRejected()
    {
        Assert.False(TargetPowerSplitPlanner.TryPlan(
            1000, 20_000, null, Limits(), out _, out string wireError));
        Assert.Contains("발열사", wireError);

        Assert.False(TargetPowerSplitPlanner.TryPlan(
            1000, 20_000, HeatingWireSpec.Case1, null, out _, out string limitError));
        Assert.Contains("허용치", limitError);
    }

    [Fact]
    public void ZeroLimit_IsRejected()
    {
        var limits = new ElectricalLimits { CurrentLimitA = 0 };

        Assert.False(TargetPowerSplitPlanner.TryPlan(
            1000, 20_000, HeatingWireSpec.Case1, limits, out _, out string error));
        Assert.Contains("허용", error);
    }

    [Fact]
    public void PlannedSharesMatchCurrentStrength()
    {
        // 길이비 배분이면 모든 분할 회로의 전류는 단일 회로 전류와 같다.
        Assert.True(TargetPowerSplitPlanner.TryPlan(
            4000, 12_000, HeatingWireSpec.Case2, Limits(), out TargetPowerSplitPlan plan, out _));

        double resistance = HeatingWireSpec.Case2.ResistanceOhmPerMeter * 12.0;
        double expected = Math.Sqrt(4000 / resistance);
        Assert.Equal(expected, plan.PerCircuitCurrentA, 9);
        Assert.Equal(Math.Sqrt(4000 * resistance) / plan.Parts, plan.PerCircuitVoltageV, 6);
        Assert.Equal(4000.0 / plan.Parts, plan.PerCircuitTargetPowerW, 6);
    }
}
