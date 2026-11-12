namespace HeatingMold.Core.Electrical;

/// <summary>
/// 같은 예상 길이로 n개 영역에 나눌 때의 회로별 계산값. 실제 확정 판정은 생성 후 실측 길이로
/// 다시 계산하며, 이 값은 분할 수를 정하는 계획 단계의 예상치다.
/// </summary>
public sealed record TargetPowerSplitPlan(
    int Parts,
    double TotalLengthMm,
    double TotalTargetPowerW,
    double PerCircuitTargetPowerW,
    double PerCircuitLengthMm,
    double PerCircuitResistanceOhm,
    double PerCircuitVoltageV,
    double PerCircuitCurrentA)
{
    public bool RequiresSplit => Parts > 1;

    /// <summary>계획 단계 예상 전류가 이미 허용 전류를 넘는가. 길이비 배분에서는 분할 수를 바꿔도 그대로다.</summary>
    public bool CurrentExceedsLimit { get; init; }

    /// <summary>사용자에게 함께 보여줄 계획 사유.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// 배분된 발열전력과 예상 길이로 필요한 분할 수를 정한다. 총 발열전력은 분할해도 늘지 않으므로
/// 전력 한도는 분할 수를 늘리는 기준이 아니고, 필요 전압만 분할 수를 정한다. 길이비 배분이면
/// 회로 전류는 I = √(P/(rL))로 분할 수와 무관하므로 전류 초과는 분할로 해결되지 않는다.
/// CurrentExceedsLimit은 그 사실을 숨기지 않고 알리는 계획 단계 표시이며, 호출자는 분할을
/// 시작하지 않는 근거로 쓴다. 최종 판정은 분할 후 실측 길이로 다시 계산한다.
/// 자동분할은 한 번에 최대 <see cref="MaxParts"/>개 영역까지 지원하며, 그보다 많이 필요하면
/// 일부만 분할하지 않고 계획 단계에서 명시적으로 거부한다.
/// </summary>
public static class TargetPowerSplitPlanner
{
    /// <summary>한 번의 자동분할이 만들 수 있는 영역 수 상한(사용자 확정 2026-11-12).</summary>
    public const int MaxParts = 4;

    public static bool TryPlan(
        double totalTargetPowerW,
        double estimatedLengthMm,
        HeatingWireSpec? wire,
        ElectricalLimits? limits,
        out TargetPowerSplitPlan plan,
        out string error)
    {
        plan = null!;
        if (wire is null) { error = "발열사 제원이 없습니다"; return false; }
        if (limits is null) { error = "허용치가 없습니다"; return false; }
        if (!double.IsFinite(totalTargetPowerW) || totalTargetPowerW <= 0)
        {
            error = "배분된 발열 전력은 0보다 커야 합니다";
            return false;
        }
        if (!double.IsFinite(estimatedLengthMm) || estimatedLengthMm <= 0)
        {
            error = "예상 발열 길이는 0보다 커야 합니다";
            return false;
        }
        if (!double.IsFinite(wire.ResistanceOhmPerMeter) || wire.ResistanceOhmPerMeter <= 0)
        {
            error = "단위 길이당 저항은 0보다 커야 합니다";
            return false;
        }
        if (!double.IsFinite(limits.CurrentLimitA) || limits.CurrentLimitA <= 0
            || !double.IsFinite(limits.VoltageLimitV) || limits.VoltageLimitV <= 0
            || !double.IsFinite(limits.PowerLimitW) || limits.PowerLimitW <= 0)
        {
            error = "허용 전압·전류·전력은 모두 0보다 커야 합니다";
            return false;
        }

        double lengthM = estimatedLengthMm / 1000.0;
        double resistance = wire.ResistanceOhmPerMeter * lengthM;
        if (!double.IsFinite(resistance) || resistance <= 0)
        {
            error = "저항을 계산할 수 없습니다";
            return false;
        }

        // 단일 회로 값. n등분하면 전압은 1/n, 전력은 1/n, 전류는 그대로다.
        double singleVoltage = Math.Sqrt(totalTargetPowerW * resistance);
        double sharedCurrent = Math.Sqrt(totalTargetPowerW / resistance);
        if (!double.IsFinite(singleVoltage) || !double.IsFinite(sharedCurrent))
        {
            error = "계획 계산값이 유효한 범위를 벗어났습니다";
            return false;
        }

        // 전력은 분할해도 늘지 않는다. 총 발열전력을 나누어 쓰므로 분할 수를 바꾸는 기준은
        // 회로별 필요 전압뿐이다. (전류는 길이비 배분에서 분할 수와 무관하다.)
        double partsByVoltage = Math.Ceiling(singleVoltage / limits.VoltageLimitV);
        double parts = Math.Max(1.0, partsByVoltage);
        if (!double.IsFinite(parts) || parts > int.MaxValue)
        {
            error = "필요한 분할 수가 유효한 범위를 벗어났습니다";
            return false;
        }

        int count = (int)parts;
        // 상한을 넘으면 일부만 분할하거나 4개로 맞추지 않는다. 계산된 필요 개수를 그대로
        // 알리고 계획을 실패로 돌려, 호출자가 기존 영역·경로를 유지하게 한다.
        if (count > MaxParts)
        {
            error = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "현재 조건에서는 {0}개 분할이 필요하지만 자동분할은 최대 {1}개까지 지원합니다",
                count, MaxParts);
            return false;
        }

        double perPower = totalTargetPowerW / count;
        double perLengthMm = estimatedLengthMm / count;
        double perResistance = resistance / count;
        double perVoltage = singleVoltage / count;
        if (!double.IsFinite(perPower) || !double.IsFinite(perResistance) || !double.IsFinite(perVoltage))
        {
            error = "분할 계산값이 유효한 범위를 벗어났습니다";
            return false;
        }

        bool currentExceeds = sharedCurrent > limits.CurrentLimitA;
        string note = count <= 1
            ? ""
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0}개 영역으로 분할 · 예상 길이 {1:0.#} mm/영역 · 회로별 예상 {2:0.#} W, 필요 {3:0.#} V, {4:0.###} A",
                count, perLengthMm, perPower, perVoltage, sharedCurrent);
        if (currentExceeds)
        {
            note = (note.Length == 0 ? "" : note + " · ")
                + string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "예상 길이 기준 전류 {0:0.###} A가 허용 {1:0.###} A를 넘습니다. 길이비 배분에서는 분할 수를 늘려도 회로 전류가 줄지 않으므로, 실제 도체 길이가 늘어나지 않으면 분할로 해결되지 않습니다.",
                    sharedCurrent, limits.CurrentLimitA);
        }

        plan = new TargetPowerSplitPlan(
            count,
            estimatedLengthMm,
            totalTargetPowerW,
            perPower,
            perLengthMm,
            perResistance,
            perVoltage,
            sharedCurrent)
        {
            CurrentExceedsLimit = currentExceeds,
            Note = note
        };
        error = "";
        return true;
    }
}
