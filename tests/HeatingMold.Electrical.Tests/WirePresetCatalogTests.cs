using HeatingMold.Electrical.Presets;

namespace HeatingMold.Electrical.Tests;

/// <summary>도체 초기 프리셋이 합의된 값과 독립적인 사양 편집을 제공하는지 검증합니다.</summary>
public sealed class WirePresetCatalogTests
{
    /// <summary>두 사례의 지름과 단위 저항이 별개 값으로 보존됩니다.</summary>
    [Fact]
    public void Catalog_ContainsBothAgreedCases()
    {
        IReadOnlyList<WirePreset> presets = WirePresetCatalog.GetAll();

        Assert.Equal(2, presets.Count);
        Assert.True(WirePresetCatalog.TryGet("CASE-1", out WirePreset? first));
        Assert.True(WirePresetCatalog.TryGet("case-2", out WirePreset? second));
        Assert.Equal(0.3, first!.WireSpecification.DiameterMm.Value);
        Assert.Equal(4, first.WireSpecification.ResistanceOhmsPerMeter.Value);
        Assert.Equal(0.15, second!.WireSpecification.DiameterMm.Value);
        Assert.Equal(8, second.WireSpecification.ResistanceOhmsPerMeter.Value);
        Assert.Equal(220, first.DefaultSupply.ActualVoltageVrms?.Value);
        Assert.Equal(16, second.DefaultSupply.MaximumCurrentAmps?.Value);
    }

    /// <summary>없는 키와 공백 키는 예외 대신 찾지 못한 결과로 반환됩니다.</summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData(" ")]
    public void TryGet_ReturnsFalseForUnknownKeys(string key)
    {
        Assert.False(WirePresetCatalog.TryGet(key, out WirePreset? preset));
        Assert.Null(preset);
    }
}
