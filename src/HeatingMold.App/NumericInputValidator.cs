using System;
using System.Globalization;

namespace HeatingMold.App;

/// <summary>
/// 사용자 숫자 입력의 단일 검증 경로. 잘못된 값을 기본값으로 조용히 바꾸지 않고
/// 호출측이 사용자에게 이유를 표시하도록 실패와 사유를 함께 돌려준다.
/// </summary>
internal static class NumericInputValidator
{
    private const NumberStyles Styles = NumberStyles.Float;

    public static bool TrySpacing(string text, out double value, out string error) =>
        TryFinite(text, "간격", minimum: 0, inclusive: false, out value, out error);

    public static bool TryInsertionDepth(string text, out double value, out string error) =>
        TryFinite(text, "삽입 깊이", minimum: 0, inclusive: true, out value, out error);

    public static bool TryPositive(string text, string name, out double value, out string error) =>
        TryFinite(text, name, minimum: 0, inclusive: false, out value, out error);

    public static bool TryNonNegative(string text, string name, out double value, out string error) =>
        TryFinite(text, name, minimum: 0, inclusive: true, out value, out error);

    private static bool TryFinite(string text, string name, double minimum, bool inclusive,
        out double value, out string error)
    {
        error = "";
        if (!double.TryParse(text, Styles, CultureInfo.CurrentCulture, out value) ||
            !double.IsFinite(value))
        {
            error = name + "은(는) 유한한 숫자여야 하며 "
                + (inclusive ? "0 이상" : "0보다 큰") + " 값이어야 합니다";
            return false;
        }

        if (inclusive ? value < minimum : value <= minimum)
        {
            error = inclusive
                ? name + "은(는) 0 이상의 숫자여야 합니다"
                : name + "은(는) 0보다 큰 숫자여야 합니다";
            return false;
        }

        return true;
    }
}
