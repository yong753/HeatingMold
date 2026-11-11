using System;
using System.Globalization;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App;

/// <summary>화면 입력에서 결정한 전기 검토 설정. 전압은 계산과 허용 판정에 함께 쓴다.</summary>
internal sealed record ElectricalSettings(
    HeatingWireSpec Wire,
    PowerSupplySpec Supply,
    ElectricalLimits Limits);

/// <summary>
/// 화면 입력을 전기 검토 설정으로 해석한다. 발열사 직경·단위저항은 선택한 Case의
/// 고정값만 쓰고, 직접 입력 규격은 지원하지 않는다.
/// </summary>
internal static class ElectricalSettingsResolver
{
    public const int Case1PresetIndex = 0;
    public const int Case2PresetIndex = 1;

    public static string DefaultVoltageText =>
        220.0.ToString("0.###", CultureInfo.CurrentCulture);
    public static string DefaultCurrentLimitText =>
        ElectricalLimits.DefaultCurrentLimitA.ToString("0.###", CultureInfo.CurrentCulture);
    public static string DefaultPowerLimitText =>
        (ElectricalLimits.DefaultPowerLimitW / 1000.0).ToString("0.###", CultureInfo.CurrentCulture);

    public static bool TryGetWire(int presetIndex, out HeatingWireSpec wire)
    {
        switch (presetIndex)
        {
            case Case1PresetIndex:
                wire = HeatingWireSpec.Case1;
                return true;
            case Case2PresetIndex:
                wire = HeatingWireSpec.Case2;
                return true;
            default:
                wire = null;
                return false;
        }
    }

    public static bool TryResolve(
        int presetIndex,
        string voltageText,
        string allowedCurrentText,
        string allowedPowerText,
        out ElectricalSettings settings,
        out string error)
    {
        settings = null;
        if (!TryGetWire(presetIndex, out HeatingWireSpec wire))
        {
            error = "발열사 규격은 Case 1 또는 Case 2 중 하나를 선택해야 합니다";
            return false;
        }
        if (!TryParse(voltageText, "전압", allowZero: true, out double voltage, out error)) return false;
        if (!TryParse(allowedCurrentText, "허용 전류", allowZero: false, out double currentLimit, out error))
            return false;
        if (!TryParse(allowedPowerText, "허용 전력", allowZero: false, out double powerLimitKw, out error))
            return false;

        double powerLimitW = powerLimitKw * 1000.0;
        if (!double.IsFinite(powerLimitW))
        {
            error = "허용 전력이 너무 큽니다";
            return false;
        }

        settings = new ElectricalSettings(
            wire,
            new PowerSupplySpec { VoltageV = voltage },
            new ElectricalLimits
            {
                CurrentLimitA = currentLimit,
                VoltageLimitV = voltage,
                PowerLimitW = powerLimitW
            });
        error = null;
        return true;
    }

    private static bool TryParse(
        string text, string name, bool allowZero, out double value, out string error)
    {
        error = "";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || !double.IsFinite(value)
            || (allowZero ? value < 0 : value <= 0))
        {
            error = name + (allowZero
                ? "은(는) 0 이상의 숫자여야 합니다"
                : "은(는) 0보다 큰 숫자여야 합니다");
            return false;
        }
        return true;
    }
}
