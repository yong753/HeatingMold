using System;
using System.Collections.Generic;
using System.Linq;

namespace HeatingMold.Core.Electrical;

/// <summary>분할 회로 하나에 배분된 목표 발열전력. LengthMm는 그 회로의 실제 길이다.</summary>
public sealed record TargetPowerShare(string CircuitId, double LengthMm, double TargetPowerW);

/// <summary>
/// 총 목표 발열전력을 실제 길이 비율로 회로에 배분한다.
/// 총 목표전력은 분할 이후에도 보존되며, 배분값의 합은 입력 총계와 일치한다(부동소수 오차 범위).
/// 길이 비율 배분에서는 모든 회로의 전류가 √(P / (r·ΣL))로 같아진다.
/// </summary>
public static class TargetPowerAllocation
{
    /// <summary>길이 비율 배분. 회로가 없거나 길이/전력이 유효하지 않으면 실패한다.</summary>
    public static bool TryAllocate(
        IReadOnlyList<(string CircuitId, double LengthMm)> circuits,
        double totalTargetPowerW,
        out IReadOnlyList<TargetPowerShare> shares,
        out string error)
    {
        shares = Array.Empty<TargetPowerShare>();
        if (circuits == null || circuits.Count == 0)
        {
            error = "목표전력을 배분할 회로가 없습니다";
            return false;
        }
        if (!double.IsFinite(totalTargetPowerW) || totalTargetPowerW <= 0)
        {
            error = "총 목표 발열전력은 0보다 커야 합니다";
            return false;
        }

        double totalLength = 0;
        foreach ((string id, double lengthMm) in circuits)
        {
            if (!double.IsFinite(lengthMm) || lengthMm <= 0)
            {
                error = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "회로 {0}의 길이가 유효하지 않습니다", id);
                return false;
            }
            totalLength += lengthMm;
        }
        if (!double.IsFinite(totalLength) || totalLength <= 0)
        {
            error = "회로 길이 합계를 계산할 수 없습니다";
            return false;
        }

        var result = new List<TargetPowerShare>(circuits.Count);
        double allocated = 0;
        for (int i = 0; i < circuits.Count; i++)
        {
            (string id, double lengthMm) = circuits[i];
            // 마지막 회로는 잔여분을 받아 합계가 입력 총계와 정확히 일치하게 한다.
            double power = i == circuits.Count - 1
                ? totalTargetPowerW - allocated
                : totalTargetPowerW * (lengthMm / totalLength);
            if (!double.IsFinite(power) || power <= 0)
            {
                error = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "회로 {0}의 목표전력 배분이 유효하지 않습니다", id);
                return false;
            }
            allocated += power;
            result.Add(new TargetPowerShare(id, lengthMm, power));
        }

        shares = result;
        error = "";
        return true;
    }
}
