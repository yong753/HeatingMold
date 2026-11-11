using System;
using System.Globalization;
using HeatingMold.App;
using HeatingMold.Core.Electrical;
using Xunit;

namespace HeatingMold.App.Tests;

public class ElectricalSettingsResolverTests
{
    private static string Text(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    [Fact]
    public void Case1_UsesFixedWireAndConfirmedDefaults()
    {
        Assert.True(ElectricalSettingsResolver.TryResolve(
            0, Text(220), Text(16), Text(3.5), out ElectricalSettings settings, out string error));

        Assert.Null(error);
        Assert.Equal(0.30, settings.Wire.DiameterMm);
        Assert.Equal(4.0, settings.Wire.ResistanceOhmPerMeter);
        Assert.Equal(220.0, settings.Supply.VoltageV);
        Assert.Equal(16.0, settings.Limits.CurrentLimitA);
        Assert.Equal(220.0, settings.Limits.VoltageLimitV);
        Assert.Equal(3_500.0, settings.Limits.PowerLimitW);
    }

    [Fact]
    public void Case2_UsesFixedWire()
    {
        Assert.True(ElectricalSettingsResolver.TryResolve(
            1, Text(220), Text(16), Text(3.5), out ElectricalSettings settings, out string error));

        Assert.Null(error);
        Assert.Equal(0.15, settings.Wire.DiameterMm);
        Assert.Equal(8.0, settings.Wire.ResistanceOhmPerMeter);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DirectInputPreset_IsRejected(int presetIndex)
    {
        Assert.False(ElectricalSettingsResolver.TryResolve(
            presetIndex, Text(220), Text(16), Text(3.5), out ElectricalSettings settings, out string error));

        Assert.Null(settings);
        Assert.Contains("Case 1", error);
        Assert.Contains("Case 2", error);
    }

    [Fact]
    public void Voltage_IsSharedBySupplyAndLimit()
    {
        Assert.True(ElectricalSettingsResolver.TryResolve(
            0, Text(230), Text(16), Text(3.5), out ElectricalSettings settings, out _));

        Assert.Equal(230.0, settings.Supply.VoltageV);
        Assert.Equal(230.0, settings.Limits.VoltageLimitV);
    }

    [Fact]
    public void ZeroVoltage_IsAcceptedConsistently()
    {
        Assert.True(ElectricalSettingsResolver.TryResolve(
            0, Text(0), Text(16), Text(3.5), out ElectricalSettings settings, out _));

        Assert.Equal(0.0, settings.Supply.VoltageV);
        Assert.Equal(0.0, settings.Limits.VoltageLimitV);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    public void InvalidVoltage_IsRejected(string voltageText)
    {
        Assert.False(ElectricalSettingsResolver.TryResolve(
            0, voltageText, Text(16), Text(3.5), out _, out string error));

        Assert.Contains("전압", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidAllowedCurrent_IsRejected(string currentText)
    {
        Assert.False(ElectricalSettingsResolver.TryResolve(
            0, Text(220), currentText, Text(3.5), out _, out string error));

        Assert.Contains("허용 전류", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    public void InvalidAllowedPower_IsRejected(string powerText)
    {
        Assert.False(ElectricalSettingsResolver.TryResolve(
            0, Text(220), Text(16), powerText, out _, out string error));

        Assert.Contains("허용 전력", error);
    }

    [Fact]
    public void AllowedPower_IsConvertedFromKilowattsToWatts()
    {
        Assert.True(ElectricalSettingsResolver.TryResolve(
            0, Text(220), Text(32), Text(7.5), out ElectricalSettings settings, out _));

        Assert.Equal(32.0, settings.Limits.CurrentLimitA);
        Assert.Equal(7_500.0, settings.Limits.PowerLimitW);
    }

    [Fact]
    public void AllowedPowerOverflow_IsRejected()
    {
        string huge = double.MaxValue.ToString("R", CultureInfo.CurrentCulture);

        Assert.False(ElectricalSettingsResolver.TryResolve(
            0, Text(220), Text(16), huge, out _, out string error));

        Assert.Contains("허용 전력", error);
    }
}
