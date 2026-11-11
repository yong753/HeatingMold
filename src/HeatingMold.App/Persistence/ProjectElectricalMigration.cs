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

    /// <summary>발열 전력 하나를 모든 영역이 나누어 쓰는 정책이 적용된 첫 스키마.</summary>
    private const int SharedPowerPolicySchemaVersion = 5;

    /// <summary>
    /// 문서를 현재 정책으로 정규화한다. <paramref name="sharedPowerNotice"/>는 이전 정책 파일을
    /// 읽을 때 계산 결과가 달라지는 경우에만 채워지며, 호출자가 모델을 교체하기 전에 사용자에게
    /// 확인한다.
    /// </summary>
    public static bool TryNormalize(ProjectDocument document, out string error, out string sharedPowerNotice)
    {
        sharedPowerNotice = null;
        if (document == null)
        {
            error = "프로젝트 문서가 비어 있습니다.";
            return false;
        }

        // 정규화가 스키마 번호를 올리므로 원래 버전을 먼저 확보한다.
        int storedSchemaVersion = document.SchemaVersion;

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

        sharedPowerNotice = NormalizeAreaPower(document, storedSchemaVersion, electrical.AllowedPowerText);

        error = null;
        return true;
    }

    /// <summary>
    /// 스키마 4까지의 영역별 목표 발열전력은 현재 정책의 입력이 아니다. 값을 계산에 쓰지 않고 지우되,
    /// 이전 정책의 합계와 현재 발열 전력이 달라 배분 결과가 바뀌는 경우에는 안내 문구를 만든다.
    /// 새 총량이 비어 있거나 잘못된 경우에는 어떤 값으로 배분될지 가장하지 않는다.
    /// </summary>
    private static string NormalizeAreaPower(
        ProjectDocument document, int storedSchemaVersion, string sharedPowerText)
    {
        if (document.Areas == null) return null;

        double legacyTotalW = 0;
        bool hasLegacyTarget = false;
        foreach (ProjectAreaDocument area in document.Areas)
        {
            if (area == null) continue;
            if (TryParsePositive(area.TargetPowerText, out double targetW))
            {
                legacyTotalW += targetW;
                hasLegacyTarget = true;
            }

            // 남겨 두면 불러온 문서와 새로 저장한 문서의 지문이 달라져 저장하지 않았는데도
            // 변경된 것으로 표시된다. 현재 정책에 없는 값이므로 여기서 지운다.
            area.TargetPowerText = null;
        }

        if (storedSchemaVersion >= SharedPowerPolicySchemaVersion || !hasLegacyTarget) return null;

        if (!TryParsePositive(sharedPowerText, out double parsedKilowatts))
        {
            // 새 총량이 비어 있거나 잘못된 값이면 어떤 값으로 배분될지 안내할 수 없다.
            // 없는 값을 기본값으로 가장하지 않고, 입력이 필요하다는 사실만 알린다.
            return string.Format(CultureInfo.CurrentCulture,
                "이 프로젝트는 영역별 목표 발열전력과 회로별 허용 전력을 따로 저장했습니다. "
                + "이제는 발열 전력 입력 하나를 확정된 모든 영역이 실측 길이 비율로 나누어 씁니다. "
                + "저장된 영역별 목표 발열전력 합계는 {0:0.##} kW이지만 발열 전력 입력이 비어 있거나 잘못되어 있어, "
                + "불러온 뒤 검토 탭에서 발열 전력을 입력해야 합니다.",
                legacyTotalW / 1000.0);
        }

        double sharedPowerW = parsedKilowatts * 1000.0;

        if (Math.Abs(legacyTotalW - sharedPowerW) <= 0.5) return null;

        return string.Format(CultureInfo.CurrentCulture,
            "이 프로젝트는 영역별 목표 발열전력과 회로별 허용 전력을 따로 저장했습니다. "
            + "이제는 발열 전력 입력 하나를 확정된 모든 영역이 실측 길이 비율로 나누어 씁니다. "
            + "저장된 영역별 목표 발열전력 합계는 {0:0.##} kW, 발열 전력은 {1:0.##} kW이므로 "
            + "불러오면 배분 결과가 이전과 달라집니다.",
            legacyTotalW / 1000.0, sharedPowerW / 1000.0);
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
