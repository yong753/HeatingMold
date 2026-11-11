using System.Globalization;
using HeatingMold.App.Persistence;
using Xunit;

namespace HeatingMold.App.Tests;

public class ProjectElectricalMigrationTests
{
    private static string Text(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static ProjectDocument Document(
        int presetIndex, string diameter, string resistance,
        string voltage = "220", string allowedCurrent = null, string allowedPower = null)
    {
        return new ProjectDocument
        {
            SchemaVersion = 1,
            Electrical = new ProjectElectricalDocument
            {
                WirePresetIndex = presetIndex,
                WireDiameterText = diameter,
                WireResistanceText = resistance,
                AppliedVoltageText = voltage,
                AllowedCurrentText = allowedCurrent,
                AllowedPowerText = allowedPower
            }
        };
    }

    [Fact]
    public void Case1Document_IsNormalizedAndKeepsLimits()
    {
        ProjectDocument document = Document(0, "0.3", "4", "220", "20", "4.2");

        Assert.True(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Null(error);
        Assert.Equal(ProjectJsonStore.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(0, document.Electrical.WirePresetIndex);
        Assert.Equal("20", document.Electrical.AllowedCurrentText);
        Assert.Equal("4.2", document.Electrical.AllowedPowerText);
    }

    [Fact]
    public void MissingLimitFields_GetConfirmedDefaults()
    {
        ProjectDocument document = Document(0, "0.3", "4");

        Assert.True(ProjectElectricalMigration.TryNormalize(document, out _));

        Assert.Equal(Text(16), document.Electrical.AllowedCurrentText);
        Assert.Equal(Text(3.5), document.Electrical.AllowedPowerText);
        Assert.Equal("220", document.Electrical.AppliedVoltageText);
    }

    [Fact]
    public void ClearedVoltageText_IsPreservedForExplicitInputError()
    {
        ProjectDocument document = Document(0, "0.3", "4", voltage: "");

        Assert.True(ProjectElectricalMigration.TryNormalize(document, out _));

        Assert.Equal("", document.Electrical.AppliedVoltageText);
    }

    [Fact]
    public void LegacyDirectInputMatchingCase1_IsConverted()
    {
        ProjectDocument document = Document(2, "0.3", "4");

        Assert.True(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Null(error);
        Assert.Equal(0, document.Electrical.WirePresetIndex);
    }

    [Fact]
    public void LegacyDirectInputMatchingCase2_IsConverted()
    {
        ProjectDocument document = Document(2, "0.15", "8");

        Assert.True(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Null(error);
        Assert.Equal(1, document.Electrical.WirePresetIndex);
    }

    [Fact]
    public void NonStandardDirectInput_IsRejectedWithoutChangingSpec()
    {
        ProjectDocument document = Document(2, "1.0", "0.1");

        Assert.False(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Contains("고정 규격", error);
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(2, document.Electrical.WirePresetIndex);
    }

    [Fact]
    public void PresetWithMismatchedWire_IsRejected()
    {
        ProjectDocument document = Document(0, "1.0", "0.1");

        Assert.False(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Contains("직경", error);
    }

    [Fact]
    public void InvalidPresetIndex_IsRejected()
    {
        ProjectDocument document = Document(-1, "0.3", "4");

        Assert.False(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Contains("프리셋", error);
    }

    [Fact]
    public void MissingElectricalSection_GetsDefaults()
    {
        var document = new ProjectDocument { SchemaVersion = 1, Electrical = null };

        Assert.True(ProjectElectricalMigration.TryNormalize(document, out string error));

        Assert.Null(error);
        Assert.NotNull(document.Electrical);
        Assert.Equal(0, document.Electrical.WirePresetIndex);
        Assert.Equal(Text(16), document.Electrical.AllowedCurrentText);
    }
}
