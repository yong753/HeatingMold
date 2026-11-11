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

    /// <summary>길이비 배분에서 분할 수와 무관하게 유지되는 회로 전류(계획 단계 예상).</summary>
    public bool CurrentExceedsLimit { get; init; }

    /// <summary>사용자에게 함께 보여줄 계획 사유.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// 목표 발열전력과 예상 길이로 필요한 분할 수를 정한다. 길이비 배분이면 회로 전류는
/// I = √(P/(rL))로 분할 수와 무관하므로, 전류 초과는 분할로 해결되지 않는다는 사실을
/// 숨기지 않고 함께 보고한다. 실제 도체 길이는 분할 후 달라질 수 있으므로 이 판단을
/// 최종 불가능 판정으로 쓰지 않는다.
/// </summary>
public static class TargetPowerSplitPlanner
{
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
            error = "목표 발열전력은 0보다 커야 합니다";
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

        double partsByPower = Math.Ceiling(totalTargetPowerW / limits.PowerLimitW);
        double partsByVoltage = Math.Ceiling(singleVoltage / limits.VoltageLimitV);
        double parts = Math.Max(1.0, Math.Max(partsByPower, partsByVoltage));
        if (!double.IsFinite(parts) || parts > int.MaxValue)
        {
            error = "필요한 분할 수가 유효한 범위를 벗어났습니다";
            return false;
        }

        int count = (int)parts;
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
