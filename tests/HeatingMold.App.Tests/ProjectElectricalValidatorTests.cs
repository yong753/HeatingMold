using System.Globalization;
using System.IO;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App.Tests;

public sealed class ProjectElectricalValidatorTests
{
    [Theory]
    [InlineData(0, 0.30, 4.0)]
    [InlineData(1, 0.15, 8.0)]
    public void Validate_AcceptsFixedCaseWire(int presetIndex, double diameter, double resistance)
    {
        ProjectElectricalDocument electrical = CreateElectrical(presetIndex, diameter, resistance);

        ProjectElectricalValidator.Validate(electrical);
    }

    [Fact]
    public void Validate_RejectsMissingElectrical()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ProjectElectricalValidator.Validate(null));

        Assert.Contains("전기 설정", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void Validate_RejectsUnknownPreset(int presetIndex)
    {
        ProjectElectricalDocument electrical = CreateElectrical(presetIndex, 0.30, 4.0);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ProjectElectricalValidator.Validate(electrical));

        Assert.Contains("프리셋", error.Message);
    }

    [Fact]
    public void Validate_RejectsMismatchedDiameter()
    {
        ProjectElectricalDocument electrical = CreateElectrical(0, HeatingWireSpec.Case2.DiameterMm, 4.0);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ProjectElectricalValidator.Validate(electrical));

        Assert.Contains("직경", error.Message);
    }

    [Fact]
    public void Validate_RejectsMismatchedResistance()
    {
        ProjectElectricalDocument electrical = CreateElectrical(1, 0.15, HeatingWireSpec.Case1.ResistanceOhmPerMeter);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ProjectElectricalValidator.Validate(electrical));

        Assert.Contains("단위 저항", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("NaN")]
    public void Validate_RejectsInvalidWireValue(string text)
    {
        ProjectElectricalDocument electrical = CreateElectrical(0, 0.30, 4.0);
        electrical.WireDiameterText = text;

        Assert.Throws<InvalidDataException>(() => ProjectElectricalValidator.Validate(electrical));
    }

    [Fact]
    public void Validate_PreservesClearedLimitInputs()
    {
        // 빈 허용 전압/전류/전력은 제품의 입력 오류 상태이며, 불러오기에서
        // 기본값으로 채우거나 프로젝트를 거부하지 않는다.
        ProjectElectricalDocument electrical = CreateElectrical(0, 0.30, 4.0);
        electrical.AllowedVoltageText = "";
        electrical.AllowedCurrentText = null;
        electrical.AllowedPowerText = "  ";

        ProjectElectricalValidator.Validate(electrical);
    }

    [Fact]
    public void Validate_DoesNotInterpretLimitValues()
    {
        // 허용 한도 문자열의 해석·검증은 전기 검토 입력 검증의 책임이다.
        ProjectElectricalDocument electrical = CreateElectrical(0, 0.30, 4.0);
        electrical.AllowedVoltageText = "abc";
        electrical.AllowedCurrentText = "-1";
        electrical.AllowedPowerText = "0";

        ProjectElectricalValidator.Validate(electrical);
    }

    private static ProjectElectricalDocument CreateElectrical(
        int presetIndex, double diameter, double resistance)
    {
        return new ProjectElectricalDocument
        {
            WirePresetIndex = presetIndex,
            WireDiameterText = diameter.ToString("0.###", CultureInfo.CurrentCulture),
            WireResistanceText = resistance.ToString("0.###", CultureInfo.CurrentCulture),
            AllowedVoltageText = "220",
            AllowedCurrentText = "16",
            AllowedPowerText = "3.5"
        };
    }
}
