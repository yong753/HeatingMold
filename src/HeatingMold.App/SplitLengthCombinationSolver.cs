using System;
using System.Collections.Generic;

namespace HeatingMold.App;

/// <summary>
/// 자식 영역마다 준비된 실제 2D 경로 길이 후보 중 하나씩 골라, 가장 긴 영역과 가장 짧은
/// 영역의 길이 비를 최소로 만드는 조합을 결정적으로 고른다.
///
/// 후보 조합을 모두 펼치지 않는다. 최적 조합의 최소 길이는 반드시 어떤 후보의 길이이므로,
/// 후보 길이를 하나씩 하한으로 잡고 각 영역에서 그 하한 이상인 가장 짧은 후보를 고르면
/// 그 하한에 대한 최적 상한을 얻는다. 하한 후보 수 × 영역 수 × 후보 수로 계산이 끝난다.
/// 같은 비율이면 최대 길이가 짧은 쪽, 그다음 후보 인덱스 사전순으로 고른다(기본 후보 우선).
/// </summary>
internal static class SplitLengthCombinationSolver
{
    /// <summary>
    /// <paramref name="lengthsByChild"/>의 각 목록에서 하나씩 골라 길이 비를 최소화한다.
    /// 성공하면 <paramref name="indices"/>는 영역별로 고른 후보 인덱스다.
    /// </summary>
    internal static bool TrySolve(
        IReadOnlyList<IReadOnlyList<double>> lengthsByChild,
        out int[] indices,
        out double ratio,
        out double minLength,
        out double maxLength)
    {
        indices = Array.Empty<int>();
        ratio = double.PositiveInfinity;
        minLength = 0;
        maxLength = 0;
        if (lengthsByChild == null || lengthsByChild.Count < 2) return false;

        var lowerBounds = new List<double>();
        foreach (IReadOnlyList<double> lengths in lengthsByChild)
        {
            if (lengths == null || lengths.Count == 0) return false;
            foreach (double length in lengths)
                if (double.IsFinite(length) && length > 0) lowerBounds.Add(length);
        }
        if (lowerBounds.Count == 0) return false;
        lowerBounds.Sort();

        double bestRatio = double.PositiveInfinity;
        double bestMin = 0;
        double bestMax = 0;
        int[] bestIndices = Array.Empty<int>();

        foreach (double lower in lowerBounds)
        {
            var choice = new int[lengthsByChild.Count];
            double chosenMin = double.PositiveInfinity;
            double chosenMax = 0;
            bool usable = true;
            for (int child = 0; child < lengthsByChild.Count && usable; child++)
            {
                IReadOnlyList<double> lengths = lengthsByChild[child];
                int found = -1;
                double foundLength = 0;
                for (int candidate = 0; candidate < lengths.Count; candidate++)
                {
                    double value = lengths[candidate];
                    if (!double.IsFinite(value) || value <= 0) continue;
                    if (value < lower - 1e-9) continue;
                    if (found < 0 || value < foundLength - 1e-12 ||
                        (Math.Abs(value - foundLength) <= 1e-12 && candidate < found))
                    {
                        found = candidate;
                        foundLength = value;
                    }
                }
                if (found < 0)
                {
                    usable = false;
                    break;
                }
                choice[child] = found;
                if (foundLength < chosenMin) chosenMin = foundLength;
                if (foundLength > chosenMax) chosenMax = foundLength;
            }
            if (!usable || !(chosenMin > 0) || !double.IsFinite(chosenMax)) continue;

            double candidateRatio = chosenMax / chosenMin;
            bool better = candidateRatio < bestRatio - 1e-12
                || (Math.Abs(candidateRatio - bestRatio) <= 1e-12
                    && (chosenMax < bestMax - 1e-9
                        || (Math.Abs(chosenMax - bestMax) <= 1e-9
                            && IsLexicographicallySmaller(choice, bestIndices))));
            if (!better) continue;
            bestRatio = candidateRatio;
            bestMin = chosenMin;
            bestMax = chosenMax;
            bestIndices = choice;
        }

        if (!double.IsFinite(bestRatio) || bestIndices.Length == 0) return false;
        indices = bestIndices;
        ratio = bestRatio;
        minLength = bestMin;
        maxLength = bestMax;
        return true;
    }

    private static bool IsLexicographicallySmaller(int[] candidate, int[] current)
    {
        if (current.Length == 0) return true;
        int shared = Math.Min(candidate.Length, current.Length);
        for (int i = 0; i < shared; i++)
        {
            if (candidate[i] == current[i]) continue;
            return candidate[i] < current[i];
        }
        return candidate.Length < current.Length;
    }
}
