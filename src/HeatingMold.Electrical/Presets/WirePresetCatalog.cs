using System.Collections.ObjectModel;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Electrical.Presets;

/// <summary>선택해 편집할 수 있는 도체 사양과 기본 전원 값을 묶습니다.</summary>
public sealed record WirePreset(
    string Key,
    string DisplayName,
    WireSpecification WireSpecification,
    Supply DefaultSupply);

/// <summary>공통 합의의 SUS 316L 사례를 초기값으로 제공합니다.</summary>
public static class WirePresetCatalog
{
    private static readonly IReadOnlyList<WirePreset> Presets = new ReadOnlyCollection<WirePreset>(
    [
        new WirePreset(
            "case-1",
            "Case 1",
            new WireSpecification("SUS 316L", new LengthMm(0.3), new ResistanceOhmsPerMeter(4)),
            new Supply(new VoltageVrms(220), new VoltageVrms(220), new CurrentAmps(16))),
        new WirePreset(
            "case-2",
            "Case 2",
            new WireSpecification("SUS 316L", new LengthMm(0.15), new ResistanceOhmsPerMeter(8)),
            new Supply(new VoltageVrms(220), new VoltageVrms(220), new CurrentAmps(16)))
    ]);

    /// <summary>편집 가능한 프리셋 목록을 읽기 전용으로 반환합니다.</summary>
    public static IReadOnlyList<WirePreset> GetAll() => Presets;

    /// <summary>키가 일치하는 프리셋을 찾아 반환합니다.</summary>
    public static bool TryGet(string key, out WirePreset? preset)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            preset = null;
            return false;
        }

        preset = Presets.FirstOrDefault(item =>
            string.Equals(item.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return preset is not null;
    }
}
