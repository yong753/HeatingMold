using System;
using System.Globalization;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App;

/// <summary>
/// 발열 전력 배분 방식의 검토 설정. 회로별 필요 전압을 계산하므로 공급 전압은 없다.
/// 허용 전압·전류는 회로별 판정 한도이고, 허용 전력은 확정된 영역 전체가 나누어 쓰는
/// 총 발열전력(공통 한도)이다.
/// </summary>
internal sealed record TargetPowerElectricalSettings(
    HeatingWireSpec Wire,
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
    public static string DefaultAllowedVoltageText => DefaultVoltageText;
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

    /// <summary>
    /// 목표 전력 배분 방식 입력을 해석한다. 전압 입력은 계산에 쓰지 않고
    /// 허용 전압 한도로만 쓰며, 필요 전압은 회로마다 실제 길이로 계산한다.
    /// 전력은 회로별 한도가 아니라 전체가 나누어 쓰는 총 발열전력이다.
    /// </summary>
    public static bool TryResolveLimits(
        int presetIndex,
        string allowedVoltageText,
        string allowedCurrentText,
        string allowedPowerText,
        out TargetPowerElectricalSettings settings,
        out string error)
    {
        settings = null;
        if (!TryGetWire(presetIndex, out HeatingWireSpec wire))
        {
            error = "발열사 규격은 Case 1 또는 Case 2 중 하나를 선택해야 합니다";
            return false;
        }
        if (!TryParse(allowedVoltageText, "허용 전압", allowZero: false, out double voltageLimit, out error))
            return false;
        if (!TryParse(allowedCurrentText, "허용 전류", allowZero: false, out double currentLimit, out error))
            return false;
        if (!TryParse(allowedPowerText, "발열 전력", allowZero: false, out double powerLimitKw, out error))
            return false;

        double powerLimitW = powerLimitKw * 1000.0;
        if (!double.IsFinite(powerLimitW))
        {
            error = "발열 전력이 너무 큽니다";
            return false;
        }

        settings = new TargetPowerElectricalSettings(
            wire,
            new ElectricalLimits
            {
                CurrentLimitA = currentLimit,
                VoltageLimitV = voltageLimit,
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
