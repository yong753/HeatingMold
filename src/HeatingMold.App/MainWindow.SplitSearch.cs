using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 전기 검토에서 분할이 필요할 때, 면적 근사가 아니라 실제 선택-인출점 2D 생성기로 후보
/// 절단 위치를 평가한다. 각 후보마다 자식 경계·자동 인출점·2D 경로를 실제로 만들고,
/// 모든 경로 조각(발열선 + 턴 연결선 + 기타 연결선)의 2D 길이 합을 비교한다.
///
/// 목표는 가장 긴 영역과 가장 짧은 영역의 2D 길이 비를 1에 가깝게 만드는 것이다.
/// 인출점의 시작/종료 측면은 분할 전 방향을 그대로 유지하므로, 길이 균등화를 위해
/// 인출점을 옮기지 않고 절단 위치만 탐색한다. 탐색은 결정적이며 전역 최적을 보장하지
/// 않는다. 탐색 범위에서 가장 균등한 절단을 그대로 쓰고, 남는 편차는 결과에 표시한다.
/// 유효한 경로를 만들 수 없는 절단은 후보에서 제외하며, 어떤 후보도 없을 때만 실패한다.
/// </summary>
public partial class MainWindow
{
    /// <summary>분할 후보 2D 평가의 전체 상한. 자동분할 상한 4개에서는 96회를 쓴다.</summary>
    private const int SplitSearchMaxEvaluations = 192;

    /// <summary>실제 2D 생성 결과로 평가한 분할 후보 하나.</summary>
    private sealed class Split2DCandidate
    {
        public double[] Cuts { get; init; } = Array.Empty<double>();
        public AreaSplitResult Split { get; init; } = null!;
        public List<WorkingArea> Children { get; init; } = null!;
        public Dictionary<WorkingArea, SerpentineResult> Patterns { get; init; } = null!;
        public double[] Lengths { get; init; } = Array.Empty<double>();
        public double Ratio { get; init; }
        public double Spread { get; init; }
    }

    /// <summary>
    /// 부모 영역과 필요한 영역 수로 균등한 2D 길이 분할을 찾는다. 성공하면 자식 영역과
    /// 미리 생성한 2D 패턴을 함께 돌려주므로, 호출자는 같은 패턴을 그대로 확정 경로로 쓴다.
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
        chosen = initial;
        children = new List<WorkingArea>();
        patterns = new Dictionary<WorkingArea, SerpentineResult>();
        balanceRatio = double.PositiveInfinity;
        failure = string.Empty;

        if (!pitch.IsValid)
        {
            failure = "분할 영역의 레인 피치가 올바르지 않습니다.";
            return false;
        }
        // 피치의 단일 기준은 LanePitchSpec이다. 영역 분할·레인 격자·인출점 행은
        // 중심 피치를 쓰고, 실제 생성만 외곽 간격과 직경을 따로 받는다.
        double centerPitch = pitch.CenterPitchMm;

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
        // 제품 상한은 계산 단계에서 이미 거부되지만, 내부 탐색을 직접 호출하는 경로도
        // 같은 상한을 지킨다. 상한을 넘는 요청은 후보를 만들기 전에 실패한다.
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
        if (!TryGetSplitDomain(parent, out double axisMin, out double axisMax))
        {
            failure = "분할 축의 영역 범위를 계산할 수 없습니다.";
            return false;
        }

        // 자동분할 상한이 4개이므로 2~4분할 모두 같은 평가 예산(96회)을 쓴다. 이 값은
        // 생성기의 레인/조각 상한이 아니라 후보 평가 횟수 상한이다.
        int budget = Math.Min(SplitSearchMaxEvaluations, Math.Max(96, 24 * partCount));
        int evaluations = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var starts = new List<double[]>();
        var startKeys = new HashSet<string>(StringComparer.Ordinal);
        Split2DCandidate best = null;
        string lastFailure = string.Empty;

        bool TryEvaluate(double[] cuts, out Split2DCandidate candidate, out string candidateFailure)
        {
            candidate = null!;
            candidateFailure = string.Empty;
            if (evaluations >= budget)
            {
                candidateFailure = "탐색 평가 한도에 도달했습니다.";
                return false;
            }
            if (!TryValidateCuts(cuts, axisMin, axisMax))
            {
                candidateFailure = "절단 위치가 분할 가능한 범위를 벗어났습니다.";
                return false;
            }
            string key = string.Join("|",
                cuts.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            if (!visited.Add(key))
            {
                candidateFailure = "이미 평가한 절단 위치입니다.";
                return false;
            }
            evaluations++;
            try
            {
                AreaSplitResult split = AreaSplitter.TryBuildAtCuts(
                    parent.Region[0], parent.Exclusions, parent.Direction,
                    centerPitch, cuts);
                if (!split.Succeeded)
                {
                    candidateFailure = split.Failure;
                    return false;
                }
                if (!TryEvaluateSplit2D(parent, split, pitch,
                        out List<WorkingArea> candidateChildren,
                        out Dictionary<WorkingArea, SerpentineResult> candidatePatterns,
                        out double[] lengths, out double ratio, out candidateFailure))
                    return false;

                double mean = lengths.Average();
                double spread = lengths.Sum(length => (length - mean) * (length - mean));
                candidate = new Split2DCandidate
                {
                    Cuts = cuts.ToArray(),
                    Split = split,
                    Children = candidateChildren,
                    Patterns = candidatePatterns,
                    Lengths = lengths,
                    Ratio = ratio,
                    Spread = spread,
                };
                return true;
            }
            catch (Exception ex)
            {
                candidateFailure = ex.Message;
                return false;
            }
        }

        void AddStart(double[] cuts)
        {
            if (cuts == null || cuts.Length != partCount - 1) return;
            if (!TryValidateCuts(cuts, axisMin, axisMax)) return;
            string key = string.Join("|",
                cuts.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            if (!startKeys.Add(key)) return;
            starts.Add(cuts);
        }

        // 1) 면적 근사로 만든 기존 후보를 그대로 첫 후보로 쓴다.
        if (initial != null && initial.Succeeded && initial.Cuts.Count == partCount - 1)
            AddStart(initial.Cuts.ToArray());

        // 1-1) 공통 레인 격자를 연속 레인 묶음으로 나눈 후보. 절단선은 이웃한 두 레인 사이에 둔다.
        //      마진이 없어졌으므로 영역마다 레인 위상을 다시 시작하지 않고 같은 중심 피치를 이어받는다.
        if (double.IsFinite(centerPitch) && centerPitch > 0)
        {
            double lanePitch = centerPitch;
            bool sweepAlongX = parent.Direction == PatternDirection.FrameY;
            double Sweep(Vec2 point) => sweepAlongX ? point.X : point.Y;
            double originSweep = parent.SelectedStartExit.HasValue
                ? Sweep(parent.SelectedStartExit.Value.Point)
                : axisMin;
            int firstIndex = (int)Math.Floor((axisMin - originSweep) / lanePitch) - 1;
            int lastIndex = (int)Math.Ceiling((axisMax - originSweep) / lanePitch) + 1;
            if (lastIndex > firstIndex &&
                LaneGridSpec.TryBuild(parent.Region[0], parent.Exclusions, parent.Direction,
                    originSweep, lanePitch, firstIndex, lastIndex, out LaneGridSpec grid, out _) &&
                LaneGridPartition.TrySplit(grid.Rows, partCount, out List<LaneRowRange> ranges, out _))
            {
                var gridCuts = new double[ranges.Count - 1];
                for (int i = 0; i + 1 < ranges.Count; i++)
                {
                    int left = ranges[i].LastIndex - firstIndex;
                    int right = ranges[i + 1].FirstIndex - firstIndex;
                    gridCuts[i] = (grid.Rows[left].SweepCoordinateMm + grid.Rows[right].SweepCoordinateMm) * 0.5;
                }
                AddStart(gridCuts);
            }
        }

        // 2) 분할 축 전체를 균등하게 나눈 후보. 면적 근사가 실패한 경우의 대체 출발점이다.
        AddStart(EqualCuts(axisMin, axisMax, partCount));

        // 3) 사용자가 지정한 시작/종료 진행 구간을 균등하게 나눈 후보. 인출점은 이 구간
        //    안에 있어야 하므로, 형상이 좌우/상하로 다른 경우 좋은 출발점이 된다.
        if (TryGetSelectedSweepRange(parent, out double selectedLow, out double selectedHigh))
            AddStart(EqualCuts(selectedLow, selectedHigh, partCount));

        // 4) 2분할은 절단선이 하나뿐이므로 전체 범위를 촘촘히 훑는다. 이렇게 하면 초기
        //    후보가 모두 유효하지 않은 형상에서도 실제 생성이 되는 절단을 찾을 수 있다.
        if (partCount == 2)
        {
            const int samples = 16;
            for (int i = 1; i <= samples; i++)
            {
                double cut = axisMin + (axisMax - axisMin) * i / (samples + 1);
                AddStart(new[] { cut });
            }
        }

        void Consider(Split2DCandidate candidate)
        {
            if (best == null || IsBetterCandidate(candidate, best)) best = candidate;
        }

        // 1단계: 모든 출발 후보를 먼저 평가한다. 좋은 출발점이 여러 개면 그중 상위 몇 개에서
        // 지역 탐색을 시작해, 한 출발점의 지역 최소에 갇히지 않게 한다.
        var evaluatedStarts = new List<Split2DCandidate>();
        foreach (double[] start in starts)
        {
            if (evaluations >= budget) break;
            if (!TryEvaluate(start, out Split2DCandidate startCandidate, out string startFailure))
            {
                lastFailure = startFailure;
                continue;
            }
            evaluatedStarts.Add(startCandidate);
            Consider(startCandidate);
            if (best != null && best.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
        }

        // 2단계: 평가된 출발점 중 길이 비가 좋은 순서로 지역 탐색한다. 탐색 예산이 남아
        // 있는 동안만 수행하며, 예산 소진은 형상 불가능과 구분해 호출자에게 보고한다.
        if (best == null || best.Ratio > AreaSplitter.LengthBalanceTolerance)
        {
            List<Split2DCandidate> seeds = evaluatedStarts
                .OrderBy(candidate => candidate.Ratio)
                .ThenBy(candidate => candidate.Spread)
                .Take(3)
                .ToList();
            foreach (Split2DCandidate seed in seeds)
            {
                if (evaluations >= budget) break;
                double[] localCuts = seed.Cuts;
                Split2DCandidate localBest = seed;
                double[] steps =
                {
                    centerPitch * 0.5,
                    centerPitch * 0.25,
                    centerPitch * 0.125,
                };
                foreach (double step in steps)
                {
                    if (!(step > 1e-6)) continue;
                    const int maxPasses = 4;
                    for (int pass = 0; pass < maxPasses; pass++)
                    {
                        if (evaluations >= budget) break;
                        bool improved = false;
                        for (int index = 0; index < localCuts.Length && !improved; index++)
                        {
                            for (int magnitude = 4; magnitude >= 1 && !improved; magnitude--)
                            {
                                for (int side = 0; side < 2; side++)
                                {
                                    int sign = side == 0 ? 1 : -1;
                                    if (!TryOffsetCut(localCuts, index, sign * magnitude * step,
                                            axisMin, axisMax, out double[] moved))
                                        continue;
                                    if (!TryEvaluate(moved, out Split2DCandidate trial, out string trialFailure))
                                    {
                                        lastFailure = trialFailure;
                                        continue;
                                    }
                                    if (trial.Ratio < localBest.Ratio - 1e-9)
                                    {
                                        localCuts = moved;
                                        localBest = trial;
                                        improved = true;
                                        break;
                                    }
                                }
                            }
                        }
                        Consider(localBest);
                        if (best != null && best.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
                        if (!improved) break;
                    }
                    if (best != null && best.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
                }
                if (best != null && best.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
            }
        }

        if (best == null)
        {
            failure = string.IsNullOrEmpty(lastFailure)
                ? "분할 영역의 실제 2D 경로를 만들 수 있는 절단 위치를 찾지 못했습니다."
                : "분할 영역의 실제 2D 경로를 만들 수 있는 절단 위치를 찾지 못했습니다 — " + lastFailure;
            return false;
        }

        // 3단계: 평가한 후보 중 길이 비가 가장 좋은 절단을 그대로 쓴다. 인출점의 시작/종료
        // 측면은 분할 전 방향을 유지하므로, 길이를 맞추기 위해 인출점을 다시 배치하지 않는다.
        chosen = best.Split;
        children = best.Children;
        patterns = best.Patterns;
        balanceRatio = best.Ratio;
        return true;
    }

    /// <summary>분할 후보의 자식 경계·인출점·2D 경로를 실제로 만들고 길이를 잰다.</summary>
    internal bool TryEvaluateSplit2D(
        WorkingArea parent,
        AreaSplitResult split,
        LanePitchSpec pitch,
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
        if (!split.Succeeded || split.Bands.Count < 2)
        {
            failure = split.Succeeded ? "분할 영역 수가 2개보다 적습니다." : split.Failure;
            return false;
        }
        if (!TryBuildSplitChildren(parent, split, out children, out failure))
            return false;
        if (!SplitExitPointPlanner.TryAssign(parent, children, pitch.CenterPitchMm, out failure))
            return false;

        lengths = new double[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            WorkingArea child = children[i];
            if (!TryGenerateSelectedExitPattern(child, pitch.OuterGapMm, pitch.WireDiameterMm,
                    out SerpentineResult pattern, out string generationFailure))
            {
                failure = child.Name + ": " + generationFailure;
                return false;
            }
            double length = pattern!.RoutePieces.Sum(piece => piece.Length);
            if (!double.IsFinite(length) || length <= 0)
            {
                failure = child.Name + ": 실제 2D 경로 길이를 계산할 수 없습니다.";
                return false;
            }
            lengths[i] = length;
            patterns[child] = pattern;
        }

        double min = lengths.Min();
        double max = lengths.Max();
        if (!(min > 0) || !double.IsFinite(max))
        {
            failure = "분할 영역의 실제 2D 경로 길이 비를 계산할 수 없습니다.";
            return false;
        }
        ratio = max / min;
        return true;
    }

    /// <summary>작업평면의 분할 축에서 유효영역 전체 범위를 구한다.</summary>
    private static bool TryGetSplitDomain(WorkingArea parent, out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        bool axisIsY = AreaSplitter.ResolveSplitAxisIsY(parent.Direction, parent.Region[0]);
        List<List<Vec2>> effective = RegionOps.SubtractRectangles(
            new List<IReadOnlyList<Vec2>> { parent.Region[0] }, parent.Exclusions);
        IEnumerable<Vec2> points = effective.Count > 0
            ? effective.SelectMany(ring => ring)
            : parent.Region[0];
        foreach (Vec2 point in points)
        {
            double value = axisIsY ? point.Y : point.X;
            if (value < min) min = value;
            if (value > max) max = value;
        }
        return double.IsFinite(min) && double.IsFinite(max) && max - min > 0;
    }

    /// <summary>부모에서 사용자가 고른 시작/종료점의 진행 방향 구간을 구한다.</summary>
    private static bool TryGetSelectedSweepRange(WorkingArea parent, out double low, out double high)
    {
        low = 0;
        high = 0;
        if (parent.SelectedStartExit is not ExitBoundaryPoint start
            || parent.SelectedEndExit is not ExitBoundaryPoint end)
            return false;
        bool axisIsY = AreaSplitter.ResolveSplitAxisIsY(parent.Direction, parent.Region[0]);
        double a = axisIsY ? start.Point.Y : start.Point.X;
        double b = axisIsY ? end.Point.Y : end.Point.X;
        low = Math.Min(a, b);
        high = Math.Max(a, b);
        return high - low > 1e-9;
    }

    private static double[] EqualCuts(double low, double high, int count)
    {
        var cuts = new double[count - 1];
        for (int i = 1; i < count; i++)
            cuts[i - 1] = low + (high - low) * i / count;
        return cuts;
    }

    private static bool TryValidateCuts(double[] cuts, double min, double max)
    {
        if (cuts == null || cuts.Length == 0) return false;
        for (int i = 0; i < cuts.Length; i++)
        {
            if (!double.IsFinite(cuts[i])) return false;
            if (cuts[i] <= min || cuts[i] >= max) return false;
            if (i > 0 && cuts[i] <= cuts[i - 1]) return false;
        }
        return true;
    }

    /// <summary>절단 하나를 옆으로 옮기되 이웃 절단과 순서를 유지한다.</summary>
    private static bool TryOffsetCut(double[] cuts, int index, double offset,
        double min, double max, out double[] moved)
    {
        moved = Array.Empty<double>();
        if (cuts == null || index < 0 || index >= cuts.Length) return false;
        double lower = index == 0 ? min : cuts[index - 1];
        double upper = index == cuts.Length - 1 ? max : cuts[index + 1];
        double gap = Math.Max(1e-3, (max - min) * 1e-6);
        if (upper - lower <= 2.0 * gap) return false;
        double value = Math.Clamp(cuts[index] + offset, lower + gap, upper - gap);
        if (Math.Abs(value - cuts[index]) <= 1e-12) return false;
        moved = (double[])cuts.Clone();
        moved[index] = value;
        return true;
    }

    /// <summary>길이 비가 같으면 편차 제곱합, 그다음 절단 좌표 사전순으로 결정적으로 고른다.</summary>
    private static bool IsBetterCandidate(Split2DCandidate candidate, Split2DCandidate current)
    {
        if (candidate.Ratio < current.Ratio - 1e-9) return true;
        if (candidate.Ratio > current.Ratio + 1e-9) return false;
        if (candidate.Spread < current.Spread - 1e-9) return true;
        if (candidate.Spread > current.Spread + 1e-9) return false;
        int shared = Math.Min(candidate.Cuts.Length, current.Cuts.Length);
        for (int i = 0; i < shared; i++)
        {
            int comparison = candidate.Cuts[i].CompareTo(current.Cuts[i]);
            if (comparison != 0) return comparison < 0;
        }
        return candidate.Cuts.Length < current.Cuts.Length;
    }
}
