using System;
using System.Globalization;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App.Persistence;

/// <summary>
/// 저장된 전기 검토 설정을 현재 제품 정책으로 정규화한다. 발열사 규격은 Case 1/Case 2의
/// 고정값만 쓰므로, 저장 제원이 선택 규격과 다르면 조용히 바꾸지 않고 불러오기를 거부한다.
/// 허용치가 없는 스키마 1 파일에는 기본 허용치를 채운다.
/// </summary>
internal static class ProjectElectricalMigration
{
    private const int LegacyDirectInputPresetIndex = 2;

    public static bool TryNormalize(ProjectDocument document, out string error)
    {
        if (document == null)
        {
            error = "프로젝트 문서가 비어 있습니다.";
            return false;
        }

        ProjectElectricalDocument electrical = document.Electrical;
        if (electrical == null)
            electrical = document.Electrical = new ProjectElectricalDocument();

        if (!TryResolveWire(electrical, out HeatingWireSpec wire, out error))
            return false;

        document.SchemaVersion = ProjectJsonStore.CurrentSchemaVersion;
        electrical.WirePresetIndex = wire.ResistanceOhmPerMeter == HeatingWireSpec.Case2.ResistanceOhmPerMeter
            ? ElectricalSettingsResolver.Case2PresetIndex
            : ElectricalSettingsResolver.Case1PresetIndex;
        electrical.WireDiameterText = Format(wire.DiameterMm);
        electrical.WireResistanceText = Format(wire.ResistanceOhmPerMeter);

        // 사용자가 편집 중 비워 둔 값은 그대로 보존해 입력 오류로 표시한다.
        // 전압 입력은 스키마 2까지 인가·허용 공용이었고, 스키마 3부터 허용 전압 한도다.
        electrical.AllowedVoltageText ??= electrical.AppliedVoltageText
            ?? ElectricalSettingsResolver.DefaultAllowedVoltageText;
        electrical.AppliedVoltageText ??= electrical.AllowedVoltageText;
        electrical.AllowedCurrentText ??= ElectricalSettingsResolver.DefaultCurrentLimitText;
        electrical.AllowedPowerText ??= ElectricalSettingsResolver.DefaultPowerLimitText;

        error = null;
        return true;
    }

    private static bool TryResolveWire(
        ProjectElectricalDocument electrical, out HeatingWireSpec wire, out string error)
    {
        wire = null;
        error = null;

        HeatingWireSpec presetWire = electrical.WirePresetIndex switch
        {
            ElectricalSettingsResolver.Case1PresetIndex => HeatingWireSpec.Case1,
            ElectricalSettingsResolver.Case2PresetIndex => HeatingWireSpec.Case2,
            _ => null
        };

        bool hasDiameter = TryParsePositive(electrical.WireDiameterText, out double diameter);
        bool hasResistance = TryParsePositive(electrical.WireResistanceText, out double resistance);

        if (presetWire != null)
        {
            if (hasDiameter && !NearlyEqual(diameter, presetWire.DiameterMm))
            {
                error = string.Format(CultureInfo.CurrentCulture,
                    "발열사 프리셋과 저장된 직경이 다릅니다: 프리셋 {0:0.###} mm, 저장값 {1:0.###} mm. "
                    + "직접 입력 제원은 현재 제품에서 지원하지 않습니다.",
                    presetWire.DiameterMm, diameter);
                return false;
            }
            if (hasResistance && !NearlyEqual(resistance, presetWire.ResistanceOhmPerMeter))
            {
                error = string.Format(CultureInfo.CurrentCulture,
                    "발열사 프리셋과 저장된 단위 저항이 다릅니다: 프리셋 {0:0.###} Ω/m, 저장값 {1:0.###} Ω/m. "
                    + "직접 입력 제원은 현재 제품에서 지원하지 않습니다.",
                    presetWire.ResistanceOhmPerMeter, resistance);
                return false;
            }
            wire = presetWire;
            return true;
        }

        if (electrical.WirePresetIndex == LegacyDirectInputPresetIndex)
        {
            if (!hasDiameter || !hasResistance)
            {
                error = "저장된 직접 입력 발열사 제원이 완전하지 않습니다.";
                return false;
            }
            if (NearlyEqual(diameter, HeatingWireSpec.Case1.DiameterMm)
                && NearlyEqual(resistance, HeatingWireSpec.Case1.ResistanceOhmPerMeter))
            {
                wire = HeatingWireSpec.Case1;
                return true;
            }
            if (NearlyEqual(diameter, HeatingWireSpec.Case2.DiameterMm)
                && NearlyEqual(resistance, HeatingWireSpec.Case2.ResistanceOhmPerMeter))
            {
                wire = HeatingWireSpec.Case2;
                return true;
            }
            error = string.Format(CultureInfo.CurrentCulture,
                "고정 규격으로 변환할 수 없는 직접 입력 발열사 제원입니다: Ø{0:0.###} mm, {1:0.###} Ω/m. "
                + "Case 1 또는 Case 2 제원과 일치하는 프로젝트만 열 수 있습니다.",
                diameter, resistance);
            return false;
        }

        error = "저장된 발열사 프리셋 인덱스가 올바르지 않습니다: " + electrical.WirePresetIndex;
        return false;
    }

    private static bool TryParsePositive(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && double.IsFinite(value) && value > 0;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1.0, Math.Abs(right));

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);
}
