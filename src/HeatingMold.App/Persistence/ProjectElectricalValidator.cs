using System;
using System.Globalization;
using System.IO;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App.Persistence
{
    /// <summary>
    /// 저장된 프로젝트(schema 6)의 전기 제원을 네이티브 모델 교체 전에 검증한다.
    /// 구형 스키마 변환은 하지 않고 현재 형식이 자체 저장한 값과 일치하는지만 확인한다.
    /// 허용 전압/전류/전력의 빈 입력은 제품의 입력 오류 상태로 보존해야 하므로
    /// 값을 채우거나 거부하지 않는다.
    /// </summary>
    internal static class ProjectElectricalValidator
    {
        private const double MatchTolerance = 1e-9;

        public static void Validate(ProjectElectricalDocument electrical)
        {
            if (electrical == null)
                throw new InvalidDataException("저장된 전기 설정이 없습니다.");

            HeatingWireSpec wire;
            switch (electrical.WirePresetIndex)
            {
                case ElectricalSettingsResolver.Case1PresetIndex:
                    wire = HeatingWireSpec.Case1;
                    break;
                case ElectricalSettingsResolver.Case2PresetIndex:
                    wire = HeatingWireSpec.Case2;
                    break;
                default:
                    throw new InvalidDataException(
                        "저장된 발열선 프리셋이 올바르지 않습니다: " + electrical.WirePresetIndex);
            }

            ValidateWireValue(electrical.WireDiameterText, wire.DiameterMm, "직경", "mm");
            ValidateWireValue(electrical.WireResistanceText, wire.ResistanceOhmPerMeter, "단위 저항", "Ω/m");
        }

        private static void ValidateWireValue(string text, double expected, string label, string unit)
        {
            if (!TryParsePositive(text, out double actual))
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture,
                    "저장된 발열선 {0}이 올바르지 않습니다: '{1}'", label, text ?? ""));

            if (Math.Abs(actual - expected) > MatchTolerance * Math.Max(1.0, Math.Abs(expected)))
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture,
                    "선택된 발열선 프리셋과 저장된 {0}이 다릅니다: 프리셋 {1:0.###} {2}, 저장값 {3:0.###} {2}",
                    label, expected, unit, actual));
        }

        private static bool TryParsePositive(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && double.IsFinite(value) && value > 0;
    }
}
