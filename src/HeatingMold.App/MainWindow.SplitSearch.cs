using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 전기 검토에서 분할이 필요할 때, <b>이미 확정된 발열레인을 연속 묶음으로 나누는</b> 방식으로
/// 자식 영역을 만든다. 레인을 새로 생성하지 않으므로 추가 레인·피치 변경·마지막 레인 뒤집기가
/// 발생하지 않는다.
///
/// 묶음은 다음 조건을 모두 만족해야 후보가 된다.
/// - 각 묶음의 실제 첫 발열선 진행 방향이 부모 시작 인출점과 같은 측면이다.
/// - 각 묶음의 실제 마지막 발열선 진행 방향이 부모 종료 인출점과 같은 측면이다.
/// - 절단선은 이웃한 두 레인 사이에만 둔다. 발열선을 가르는 절단은 만들지 않는다.
///
/// 후보의 길이는 확정 조각 길이의 합(자식 사이 연결선 제외, 인출 연결선 포함)이며, 최대/최소
/// 비를 최소화하는 묶음을 고른다. 길이 편차가 남아도 그 수치를 표시하고 확정한다. 유효한
/// 묶음이 하나도 없으면 부모 영역을 그대로 유지하고 사유를 돌려준다.
/// </summary>
public partial class MainWindow
{
    /// <summary>절단 위치를 실제로 구성해 검증하는 후보 묶음 수 상한. 레인 배치 수 상한이 아니다.</summary>
    private const int SplitSearchMaxEvaluations = 192;

    /// <summary>길이 순으로 보관하는 후보 묶음 수. 초과분은 더 나쁜 후보부터 버린다.</summary>
    private const int SplitLanePartitionPool = 512;

    /// <summary>묶음 열거 방문 노드 상한. 넘으면 전역 불가능으로 단정하지 않고 탐색 범위를 밝힌다.</summary>
    private const int SplitLanePartitionVisitLimit = 200000;

    /// <summary>레인 묶음 하나의 배치. Starts는 묶음별 첫 레인 인덱스다.</summary>
    private sealed class LanePartitionCandidate
    {
        public int[] Starts { get; init; } = Array.Empty<int>();
        public double[] Lengths { get; init; } = Array.Empty<double>();
        public double Ratio { get; init; }
    }

    /// <summary>
    /// 부모 영역의 확정 발열레인을 partCount개의 연속 묶음으로 나누고, 각 묶음의 경계·인출점·
    /// 2D 경로를 준비한다. 성공하면 자식 영역과 준비된 패턴을 함께 돌려주므로 호출자는 같은
    /// 패턴을 그대로 확정 경로로 쓴다.
    /// </summary>
    internal bool TryPrepareBalancedSplit(
        WorkingArea parent,
        AreaSplitResult initial,
        int partCount,
        LanePitchSpec pitch,
        out AreaSplitResult chosen,
        out List<WorkingArea> children,
        out Dictionary<WorkingArea, SerpentineResult> patterns,
        out double balanceRatio,
        out string failure)
    {
        // 면적 근사 분할 결과는 더 이상 절단 후보의 기준이 아니다. 호출 흐름 유지를 위해 받기만 한다.
        _ = initial;
        chosen = null!;
        children = new List<WorkingArea>();
        patterns = new Dictionary<WorkingArea, SerpentineResult>();
        balanceRatio = double.PositiveInfinity;
        failure = string.Empty;

        if (!pitch.IsValid)
        {
            failure = "분할 영역의 레인 피치가 올바르지 않습니다.";
            return false;
        }
        if (parent == null || parent.Region.Count != 1 || parent.Region[0].Count < 3)
        {
            failure = "분할할 부모 영역이 단일 폐합 영역이 아닙니다.";
            return false;
        }
        if (partCount < 2)
        {
            failure = "분할 영역 수가 올바르지 않습니다.";
            return false;
        }
        if (partCount > TargetPowerSplitPlanner.MaxParts)
        {
            failure = string.Format(CultureInfo.CurrentCulture,
                "자동분할은 최대 {0}개 영역까지 지원합니다 — 요청 {1}개.",
                TargetPowerSplitPlanner.MaxParts, partCount);
            return false;
        }
        if (!SplitExitPointPlanner.HasAssignablePair(parent))
        {
            failure = "부모 영역의 시작/종료 인출점이 준비되지 않았습니다.";
            return false;
        }
        if (parent.Pattern == null || parent.Pattern.RoutePieces == null
            || parent.Pattern.RoutePieces.Count == 0)
        {
            failure = "확정된 발열경로가 없어 기존 레인을 나눌 수 없습니다 — 먼저 경로를 생성하세요.";
            return false;
        }

        double radians = parent.Direction == PatternDirection.FrameY ? Math.PI / 2.0 : 0.0;
        if (!CommittedRouteLayoutAnalyzer.TryAnalyze(parent.Pattern, radians, out CommittedRouteLayout layout,
                out string analyzeFailure))
        {
            failure = "확정 발열경로에서 레인을 읽지 못했습니다 — " + analyzeFailure;
            return false;
        }
        if (layout.Lanes.Count < partCount)
        {
            failure = string.Format(CultureInfo.CurrentCulture,
                "확정 레인이 {0}개뿐이라 {1}개 영역으로 나눌 수 없습니다.", layout.Lanes.Count, partCount);
            return false;
        }
        if (!SplitExitSidePolicy.TryResolve(parent, radians, out bool startLow, out bool endLow,
                out string sideFailure))
        {
            failure = sideFailure;
            return false;
        }
        if (layout.Lanes[0].StartsLow != startLow)
        {
            failure = "부모 시작 인출점의 측면과 첫 발열레인의 진행 방향이 달라 그대로 나눌 수 없습니다.";
            return false;
        }
        if (layout.Lanes[^1].EndsLow != endLow)
        {
            failure = "부모 종료 인출점의 측면과 마지막 발열레인의 진행 방향이 달라 그대로 나눌 수 없습니다.";
            return false;
        }

        if (!EnumerateLanePartitions(layout.Lanes, partCount, startLow, endLow,
                out List<LanePartitionCandidate> partitions, out string enumerateFailure))
        {
            failure = enumerateFailure;
            return false;
        }
        if (partitions.Count == 0)
        {
            failure = string.Format(CultureInfo.CurrentCulture,
                "기존 발열레인 {0}개를 {1}개 묶음으로 나눌 때 시작/종료 인출 측면을 유지하는 배치가 없습니다.",
                layout.Lanes.Count, partCount);
            return false;
        }

        string lastFailure = string.Empty;
        int evaluated = 0;
        foreach (LanePartitionCandidate partition in partitions)
        {
            if (evaluated >= SplitSearchMaxEvaluations)
            {
                lastFailure = string.Format(CultureInfo.CurrentCulture,
                    "묶음 후보 {0}개를 실제 2D 경로로 확인했지만 유효한 배치를 찾지 못했습니다.",
                    evaluated);
                break;
            }
            evaluated++;

            bool cutAxisIsWorldY = parent.Direction != PatternDirection.FrameY;
            if (!TryBuildCutCoordinates(layout.Lanes, partition.Starts, cutAxisIsWorldY,
                    out double[] cuts, out List<(int First, int Last)> bands))
            {
                lastFailure = "레인 사이 절단 좌표를 계산할 수 없습니다.";
                continue;
            }
            AreaSplitResult split = AreaSplitter.TryBuildAtCuts(parent.Region[0], parent.Exclusions,
                parent.Direction, pitch.CenterPitchMm, cuts);
            if (!split.Succeeded)
            {
                lastFailure = split.Failure;
                continue;
            }
            if (split.Bands.Count != partCount)
            {
                lastFailure = "절단 위치로 요청한 영역 수만큼 나뉘지 않았습니다.";
                continue;
            }

            if (!TryEvaluateSplit2D(parent, split, pitch, bands,
                    out List<WorkingArea> candidates, out Dictionary<WorkingArea, SerpentineResult> candidatePatterns,
                    out double[] lengths, out double ratio, out string evaluateFailure))
            {
                lastFailure = evaluateFailure;
                continue;
            }

            chosen = split;
            children = candidates;
            patterns = candidatePatterns;
            balanceRatio = ratio;
            return true;
        }

        if (string.IsNullOrEmpty(lastFailure))
            lastFailure = "기존 레인을 나누는 배치를 찾지 못했습니다.";
        failure = "분할에 실패했습니다 — 기존 발열레인을 그대로 나누는 배치를 확정하지 못했습니다. "
            + lastFailure;
        return false;
    }

    /// <summary>
    /// 주어진 레인 묶음 배치로 자식 영역·인출점·2D 경로를 실제로 준비하고 길이를 잰다.
    /// 자식 경로는 부모 확정 조각을 복사해 만들며 생성기를 다시 호출하지 않는다.
    /// </summary>
    internal bool TryEvaluateSplit2D(
        WorkingArea parent,
        AreaSplitResult split,
        LanePitchSpec pitch,
        IReadOnlyList<(int First, int Last)> bands,
        out List<WorkingArea> children,
        out Dictionary<WorkingArea, SerpentineResult> patterns,
        out double[] lengths,
        out double ratio,
        out string failure)
    {
        children = new List<WorkingArea>();
        patterns = new Dictionary<WorkingArea, SerpentineResult>();
        lengths = Array.Empty<double>();
        ratio = double.PositiveInfinity;
        failure = string.Empty;

        if (!pitch.IsValid)
        {
            failure = "분할 영역의 레인 피치가 올바르지 않습니다.";
            return false;
        }
        if (bands == null || bands.Count < 2)
        {
            failure = "레인 묶음 배치가 올바르지 않습니다.";
            return false;
        }
        if (parent == null || parent.Pattern == null
            || parent.SelectedStartExit is not ExitBoundaryPoint parentStart
            || parent.SelectedEndExit is not ExitBoundaryPoint parentEnd)
        {
            failure = "부모 영역의 확정 경로 또는 선택 인출점이 준비되지 않았습니다.";
            return false;
        }

        double radians = parent.Direction == PatternDirection.FrameY ? Math.PI / 2.0 : 0.0;
        if (!CommittedRouteLayoutAnalyzer.TryAnalyze(parent.Pattern, radians, out CommittedRouteLayout layout,
                out string analyzeFailure))
        {
            failure = "확정 발열경로에서 레인을 읽지 못했습니다 — " + analyzeFailure;
            return false;
        }
        if (!SplitExitSidePolicy.TryResolve(parent, radians, out bool startLow, out bool endLow,
                out string sideFailure))
        {
            failure = sideFailure;
            return false;
        }
        if (bands.Count != split.Bands.Count)
        {
            failure = "레인 묶음 수와 분할 영역 수가 다릅니다.";
            return false;
        }

        double parentLowY = Math.Min(parentStart.Point.Rotate(-radians).Y, parentEnd.Point.Rotate(-radians).Y);
        double parentHighY = Math.Max(parentStart.Point.Rotate(-radians).Y, parentEnd.Point.Rotate(-radians).Y);
        double rowMatchTolerance = Math.Min(1.0, pitch.CenterPitchMm * 0.5);

        if (!TryBuildSplitChildren(parent, split, out children, out string childFailure))
        {
            failure = childFailure;
            return false;
        }
        if (children.Count != bands.Count)
        {
            failure = "분할 영역 수가 레인 묶음 수와 다릅니다.";
            return false;
        }

        lengths = new double[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            WorkingArea child = children[i];
            (int firstLane, int lastLane) = bands[i];
            if (firstLane < 0 || lastLane >= layout.Lanes.Count || firstLane > lastLane)
            {
                failure = child.Name + ": 레인 묶음 범위가 올바르지 않습니다.";
                return false;
            }
            if (layout.Lanes[firstLane].StartsLow != startLow)
            {
                failure = child.Name + ": 첫 발열레인이 분할 전 시작 인출점과 반대 측면에서 시작합니다.";
                return false;
            }
            if (layout.Lanes[lastLane].EndsLow != endLow)
            {
                failure = child.Name + ": 마지막 발열레인이 분할 전 종료 인출점과 반대 측면에서 끝납니다.";
                return false;
            }

            bool ownsParentStart = firstLane == 0;
            bool ownsParentEnd = lastLane == layout.Lanes.Count - 1;
            ExitBoundaryPoint start = ownsParentStart ? parentStart : default;
            ExitBoundaryPoint end = ownsParentEnd ? parentEnd : default;

            if (!ownsParentStart)
            {
                if (!TryResolveChildExit(child, radians, layout.Lanes[firstLane], startLow,
                        layout.Lanes[firstLane].StartCrossCoordinate, true, rowMatchTolerance,
                        parentLowY, parentHighY, out start, out string startFailure))
                {
                    failure = startFailure;
                    return false;
                }
            }
            if (!ownsParentEnd)
            {
                if (!TryResolveChildExit(child, radians, layout.Lanes[lastLane], endLow,
                        layout.Lanes[lastLane].EndCrossCoordinate, false, rowMatchTolerance,
                        parentLowY, parentHighY, out end, out string endFailure))
                {
                    failure = endFailure;
                    return false;
                }
            }

            child.SelectedStartExit = start;
            child.SelectedEndExit = end;
            child.StartAnchor = start.Point;
            child.EndAnchor = end.Point;

            if (!CommittedLanePartition.TryBuildChildRoute(parent.Pattern, layout, firstLane, lastLane,
                    start, end, out SerpentineResult pattern, out string routeFailure))
            {
                failure = child.Name + ": " + routeFailure;
                return false;
            }
            patterns[child] = pattern;
            double length = 0;
            for (int p = 0; p < pattern.RoutePieces.Count; p++) length += pattern.RoutePieces[p].Length;
            if (!double.IsFinite(length) || length <= 0)
            {
                failure = child.Name + ": 분할 영역의 2D 경로 길이를 계산할 수 없습니다.";
                return false;
            }
            lengths[i] = length;
        }

        double min = lengths.Min();
        double max = lengths.Max();
        if (!(min > 0) || !(max > 0))
        {
            failure = "분할 영역의 2D 경로 길이를 계산할 수 없습니다.";
            return false;
        }
        ratio = max / min;
        return true;
    }

    /// <summary>자식 영역의 실제 첫/마지막 레인 행에서 부모와 같은 측면의 정확 경계점을 구한다.</summary>
    private static bool TryResolveChildExit(
        WorkingArea child,
        double radians,
        CommittedLane lane,
        bool lowSide,
        double targetLocalX,
        bool atStart,
        double rowMatchTolerance,
        double parentLowY,
        double parentHighY,
        out ExitBoundaryPoint point,
        out string failure)
    {
        point = default;
        failure = string.Empty;
        string role = atStart ? "시작" : "종료";
        if (child.RegionExitBoundary is not SplitRegionBoundary2D boundary)
        {
            failure = child.Name + ": 분할 영역 경계가 준비되지 않았습니다.";
            return false;
        }
        // 인출점은 부모가 선택한 진행 구간 안에 있어야 한다. 마지막 추가 레인처럼 그 구간을
        // 벗어난 행을 자식의 인출 행으로 쓰지 않는다.
        if (lane.SweepCoordinate < parentLowY - rowMatchTolerance
            || lane.SweepCoordinate > parentHighY + rowMatchTolerance)
        {
            failure = child.Name + ": " + role
                + " 인출 행이 분할 전 시작/종료 구간을 벗어났습니다.";
            return false;
        }
        if (!SplitExitPointPlanner.TryResolveLaneRowPoint(boundary, radians, lane.SweepCoordinate,
                lowSide, targetLocalX, rowMatchTolerance, out point, out string resolveFailure))
        {
            failure = child.Name + ": " + role + " — " + resolveFailure;
            return false;
        }
        Vec2 local = point.Point.Rotate(-radians);
        double deviation = Math.Abs(local.X - targetLocalX);
        if (deviation > CommittedLanePartition.EndpointSnapTolerance)
        {
            failure = string.Format(CultureInfo.CurrentCulture,
                "{0}: {1} 인출점이 기존 레인 끝에서 {2:F3} mm 떨어져 있어 그대로 배정할 수 없습니다.",
                child.Name, role, deviation);
            return false;
        }
        if (Math.Abs(local.Y - lane.SweepCoordinate) > CommittedRouteLayoutAnalyzer.ContinuityTolerance)
        {
            failure = child.Name + ": " + role + " 인출점이 기존 레인 행 위에 있지 않습니다.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 레인 사이 중간 좌표를 절단 위치로 만든다. 절단은 발열선을 가르지 않는다.
    /// 레인 좌표는 회전된 지역 좌표이므로 절단 축의 월드 좌표로 되돌리고,
    /// 밴드 순서(오름차순)에 맞춰 레인 묶음 경계도 함께 되돌린다.
    /// </summary>
    private static bool TryBuildCutCoordinates(IReadOnlyList<CommittedLane> lanes,
        IReadOnlyList<int> starts, bool cutAxisIsWorldY, out double[] cuts,
        out List<(int First, int Last)> bands)
    {
        cuts = Array.Empty<double>();
        bands = new List<(int First, int Last)>();
        int parts = starts.Count;
        int laneCount = lanes.Count;
        if (parts < 2 || laneCount < 2) return false;
        if (starts[0] != 0) return false;
        for (int i = 0; i < parts; i++)
        {
            if (starts[i] < 0 || starts[i] >= laneCount) return false;
            if (i > 0 && starts[i] <= starts[i - 1]) return false;
        }

        double ToCut(double sweepLocalY) => cutAxisIsWorldY ? sweepLocalY : -sweepLocalY;

        double[] rawCuts = new double[parts - 1];
        for (int i = 1; i < parts; i++)
        {
            int previous = starts[i] - 1;
            int next = starts[i];
            if (previous < 0 || next >= laneCount) return false;
            double low = lanes[previous].SweepCoordinate;
            double high = lanes[next].SweepCoordinate;
            if (Math.Abs(high - low) <= CommittedRouteLayoutAnalyzer.ContinuityTolerance)
                return false;
            double middle = ToCut(low + (high - low) * 0.5);
            if (!double.IsFinite(middle)) return false;
            rawCuts[i - 1] = middle;
        }

        bool ascending = true;
        for (int i = 1; i < rawCuts.Length; i++)
        {
            if (!(rawCuts[i] > rawCuts[i - 1])) ascending = false;
        }

        if (ascending)
        {
            cuts = rawCuts;
            for (int i = 0; i < parts; i++)
            {
                int end = (i + 1 < parts ? starts[i + 1] : laneCount) - 1;
                bands.Add((starts[i], end));
            }
            return true;
        }

        // 절단 축은 밴드 순서(오름차순)로 넘겨야 한다. 레인 진행 순서가 내림차순이면
        // 절단 좌표를 뒤집고, 각 밴드에 대응하는 레인 묶음도 같은 순서로 뒤집는다.
        cuts = new double[rawCuts.Length];
        for (int i = 0; i < rawCuts.Length; i++) cuts[i] = rawCuts[rawCuts.Length - 1 - i];
        for (int i = 1; i < cuts.Length; i++)
        {
            if (!(cuts[i] > cuts[i - 1])) return false;
        }
        for (int i = 0; i < parts; i++)
        {
            int routeIndex = parts - 1 - i;
            int end = (routeIndex + 1 < parts ? starts[routeIndex + 1] : laneCount) - 1;
            bands.Add((starts[routeIndex], end));
        }
        return bands.Count == parts;
    }

    /// <summary>
    /// 시작/종료 측면 조건을 만족하는 레인 연속 묶음 배치를 길이 비 순으로 열거한다.
    /// 각 묶음의 길이는 확정 조각 길이 합이므로, 이 순서는 실제 2D 경로 길이 비와 같다.
    /// </summary>
    private static bool EnumerateLanePartitions(
        IReadOnlyList<CommittedLane> lanes,
        int partCount,
        bool startLow,
        bool endLow,
        out List<LanePartitionCandidate> ordered,
        out string failure)
    {
        ordered = new List<LanePartitionCandidate>();
        failure = string.Empty;
        if (partCount < 2 || partCount > lanes.Count)
        {
            failure = "레인 수보다 많은 영역으로 나눌 수 없습니다.";
            return false;
        }

        var prefix = new double[lanes.Count + 1];
        for (int i = 0; i < lanes.Count; i++) prefix[i + 1] = prefix[i] + lanes[i].LengthMm;
        double total = prefix[lanes.Count];
        if (!(total > 0))
        {
            failure = "확정 레인의 길이가 0이라 분할 길이를 비교할 수 없습니다.";
            return false;
        }

        var pool = new List<LanePartitionCandidate>(SplitLanePartitionPool + 1);
        var starts = new int[partCount];
        int visited = 0;
        bool limitReached = false;

        bool Compatible(int first, int last) =>
            lanes[first].StartsLow == startLow && lanes[last].EndsLow == endLow;

        void Visit(int group, int firstLane)
        {
            if (limitReached) return;
            if (++visited > SplitLanePartitionVisitLimit) { limitReached = true; return; }

            int remaining = partCount - 1 - group;
            if (group == partCount - 1)
            {
                if (!Compatible(firstLane, lanes.Count - 1)) return;
                starts[group] = firstLane;
                var lengths = new double[partCount];
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                for (int i = 0; i < partCount; i++)
                {
                    int end = i + 1 < partCount ? starts[i + 1] - 1 : lanes.Count - 1;
                    double length = prefix[end + 1] - prefix[starts[i]];
                    lengths[i] = length;
                    if (length < min) min = length;
                    if (length > max) max = length;
                }
                if (!(min > 0)) return;
                var candidate = new LanePartitionCandidate
                {
                    Starts = (int[])starts.Clone(),
                    Lengths = lengths,
                    Ratio = max / min,
                };
                InsertCandidate(pool, candidate);
                return;
            }

            int lastStart = lanes.Count - 1 - remaining;
            for (int last = firstLane; last <= lastStart; last++)
            {
                if (!Compatible(firstLane, last)) continue;
                starts[group] = firstLane;
                Visit(group + 1, last + 1);
                if (limitReached) return;
            }
        }

        Visit(0, 0);
        if (limitReached)
        {
            failure = string.Format(CultureInfo.CurrentCulture,
                "레인 {0}개를 {1}개 묶음으로 나누는 배치가 너무 많아 {2}개까지만 확인했습니다 — "
                + "자동분할로는 이 영역을 나눌 수 없습니다.",
                lanes.Count, partCount, SplitLanePartitionVisitLimit);
            return false;
        }

        ordered = pool.Take(SplitSearchMaxEvaluations).ToList();
        return true;
    }

    private static void InsertCandidate(List<LanePartitionCandidate> pool, LanePartitionCandidate candidate)
    {
        int index = pool.Count;
        while (index > 0 && CompareCandidates(candidate, pool[index - 1]) < 0) index--;
        if (index >= SplitLanePartitionPool) return;
        pool.Insert(index, candidate);
        if (pool.Count > SplitLanePartitionPool) pool.RemoveAt(pool.Count - 1);
    }

    private static int CompareCandidates(LanePartitionCandidate a, LanePartitionCandidate b)
    {
        int byRatio = a.Ratio.CompareTo(b.Ratio);
        if (byRatio != 0) return byRatio;
        for (int i = 0; i < Math.Min(a.Lengths.Length, b.Lengths.Length); i++)
        {
            int byLength = a.Lengths[i].CompareTo(b.Lengths[i]);
            if (byLength != 0) return byLength;
        }
        for (int i = 0; i < Math.Min(a.Starts.Length, b.Starts.Length); i++)
        {
            int byStart = a.Starts[i].CompareTo(b.Starts[i]);
            if (byStart != 0) return byStart;
        }
        return 0;
    }
}
