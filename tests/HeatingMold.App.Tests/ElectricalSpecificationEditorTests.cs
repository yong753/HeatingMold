using HeatingMold.App;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>회로별 전원 입력의 문자열 변환과 누락값 처리를 검사합니다.</summary>
public sealed class ElectricalSpecificationEditorTests
{
    /// <summary>공백 입력은 자동 추정 없이 nullable 전원값으로 남습니다.</summary>
    [Fact]
    public void TryCreateSupply_LeavesBlankValuesUnspecified()
    {
        ElectricalSupplyEditRow row = CreateRow();

        Assert.True(row.TryCreateSupply(out Supply supply));

        Assert.Null(supply.ActualVoltageVrms);
        Assert.Null(supply.MaximumVoltageVrms);
        Assert.Null(supply.MaximumCurrentAmps);
        Assert.Null(supply.MaximumPowerWatts);
    }

    /// <summary>입력된 전원값은 회로별로 보존하고 0·NaN은 거부합니다.</summary>
    [Fact]
    public void TryCreateSupply_ParsesIndependentCircuitValuesAndRejectsInvalidNumbers()
    {
        ElectricalSupplyEditRow row = CreateRow();
        row.ActualVoltage = "230";
        row.MaximumVoltage = "240";
        row.MaximumCurrent = "12.5";
        row.MaximumPower = "1500";

        Assert.True(row.TryCreateSupply(out Supply supply));
        Assert.Equal(new VoltageVrms(230), supply.ActualVoltageVrms);
        Assert.Equal(new VoltageVrms(240), supply.MaximumVoltageVrms);
        Assert.Equal(new CurrentAmps(12.5), supply.MaximumCurrentAmps);
        Assert.Equal(new PowerWatts(1500), supply.MaximumPowerWatts);

        row.MaximumCurrent = "0";
        Assert.False(row.TryCreateSupply(out _));
        row.MaximumCurrent = "NaN";
        Assert.False(row.TryCreateSupply(out _));
    }

    private static ElectricalSupplyEditRow CreateRow()
    {
        var circuit = new Circuit(
            CircuitId.New(),
            0,
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), 0, false)],
            ConnectionPointId.New(),
            ConnectionPointId.New(),
            new Supply(null, null, null));
        return new ElectricalSupplyEditRow(circuit);
    }
}
