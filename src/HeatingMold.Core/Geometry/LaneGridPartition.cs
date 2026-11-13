namespace HeatingMold.Core.Geometry;

/// <summary>연속된 레인 행 구간. 분할 자식 하나가 가져갈 행 범위다.</summary>
public readonly record struct LaneRowRange(int FirstIndex, int LastIndex)
{
    public int Count => LastIndex - FirstIndex + 1;
}

/// <summary>
/// 공통 레인 격자의 행을 연속 구간으로 나눈다. 각 구간은 비어 있지 않고,
/// 최소 한 행은 실제 발열 구간을 가져야 한다(시작·종료 인출점을 놓을 수 있어야 함).
/// 목표는 구간별 발열 길이(행별 구간 길이 합)의 최대/최소 비율을 최소화하는 것이다.
/// </summary>
public static class LaneGridPartition
{
    public const string Version = "lane-partition-v1";

    public static bool TrySplit(
        IReadOnlyList<LaneGridRow>? rows,
        int parts,
        out List<LaneRowRange> ranges,
        out string failure)
    {
        ranges = new List<LaneRowRange>();
        failure = string.Empty;

        if (rows == null || rows.Count == 0)
        {
            failure = "분할할 레인 행이 없습니다.";
            return false;
        }
        if (parts < 2)
        {
            failure = "분할 영역 수가 올바르지 않습니다.";
            return false;
        }
        if (parts > rows.Count)
        {
            failure = "레인 행 수보다 분할 영역 수가 많습니다.";
            return false;
        }

        int count = rows.Count;
        var prefix = new double[count + 1];
        for (int i = 0; i < count; ++i) prefix[i + 1] = prefix[i] + rows[i].CoveredLengthMm;

        // cost[i, k] = 앞 i개 행을 k개 구간으로 나눌 때의 최소 (최대 구간 길이).
        // choice는 그때 마지막 구간의 시작 인덱스다.
        var cost = new double[parts + 1, count + 1];
        var choice = new int[parts + 1, count + 1];
        for (int k = 0; k <= parts; ++k)
            for (int i = 0; i <= count; ++i)
            {
                cost[k, i] = double.PositiveInfinity;
                choice[k, i] = -1;
            }
        cost[0, 0] = 0;

        for (int k = 1; k <= parts; ++k)
        {
            for (int i = k; i <= count; ++i)
            {
                for (int start = k - 1; start < i; ++start)
                {
                    if (double.IsPositiveInfinity(cost[k - 1, start])) continue;
                    if (!IsUsable(rows, start, i - 1)) continue;

                    double segment = prefix[i] - prefix[start];
                    double candidate = Math.Max(cost[k - 1, start], segment);
                    if (candidate < cost[k, i])
                    {
                        cost[k, i] = candidate;
                        choice[k, i] = start;
                    }
                }
            }
        }

        if (double.IsPositiveInfinity(cost[parts, count]))
        {
            failure = "모든 구간이 발열 구간을 갖도록 레인 행을 나눌 수 없습니다.";
            return false;
        }

        var reversed = new List<LaneRowRange>(parts);
        int end = count;
        for (int k = parts; k >= 1; --k)
        {
            int start = choice[k, end];
            if (start < 0)
            {
                failure = "레인 행 분할 결과를 복원할 수 없습니다.";
                return false;
            }
            reversed.Add(new LaneRowRange(rows[start].Index, rows[end - 1].Index));
            end = start;
        }

        reversed.Reverse();
        ranges = reversed;
        return true;
    }

    private static bool IsUsable(IReadOnlyList<LaneGridRow> rows, int first, int last)
    {
        for (int i = first; i <= last; ++i)
            if (rows[i].HasIntervals) return true;
        return false;
    }
}
