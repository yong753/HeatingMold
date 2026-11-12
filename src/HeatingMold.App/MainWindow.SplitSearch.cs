using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 전기 검토에서 분할이 필요할 때, 면적 근사가 아니라 실제 선택-인출점 2D 생성기로 후보
/// 절단 위치를 평가한다. 각 후보마다 자식 경계·자동 인출점·2D 경로를 실제로 만들고,
/// 모든 경로 조각(발열선 + 턴 연결선 + 기타 연결선)의 2D 길이 합을 비교한다.
///
/// 목표는 가장 긴 영역과 가장 짧은 영역의 2D 길이 비를 1에 가깝게 만드는 것이다.
/// 탐색은 결정적이며 전역 최적을 보장하지 않는다. 탐색 한도 안에서 허용 비율을
/// 만족하지 못하면 일부만 확정하지 않고 명시적으로 거부한다.
/// </summary>
public partial class MainWindow
{
    /// <summary>분할 후보 2D 평가의 전체 상한. 영역 수가 많으면 이보다 작아진다.</summary>
    private const int SplitSearchMaxEvaluations = 192;

    /// <summary>인출점 배치 후보까지 평가할 상위 절단 후보 수.</summary>
    private const int SplitSearchMaxVariantSeeds = 4;

    /// <summary>인출점 배치 후보를 포함해 평가할 절단 위치 후보 수의 상한.</summary>
    private const int SplitSearchMaxVariantCutSets = 10;

    /// <summary>인출점 배치 후보를 실제 2D로 생성하는 횟수 상한(탐색 폭주 방지).</summary>
    private const int SplitSearchMaxVariantGenerations = 2400;

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

    /// <summary>절단 위치 하나에서 인출점 배치 후보를 실제 2D로 생성해 고른 조합.</summary>
    private sealed class SplitVariantCandidate
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
        double margin,
        double spacing,
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

        // 후보 하나를 평가할 때마다 자식 수만큼 경계·인출점·2D 경로를 만든다. 영역 수가
        // 많을수록 후보 수를 줄여, 큰 분할이 탐색 때문에 폭주하지 않게 한다. 이 값은
        // 생성기의 레인/조각 상한이 아니라 후보 평가 횟수 상한이다.
        int budget = partCount <= 4
            ? Math.Min(SplitSearchMaxEvaluations, Math.Max(96, 24 * partCount))
            : partCount <= 8 ? 48 : 8;
        int evaluations = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var starts = new List<double[]>();
        var startKeys = new HashSet<string>(StringComparer.Ordinal);
        var evaluatedCuts = new List<(double[] Cuts, double Ratio, double Spread)>();
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
                    margin, spacing, cuts);
                if (!split.Succeeded)
                {
                    candidateFailure = split.Failure;
                    return false;
                }
                if (!TryEvaluateSplit2D(parent, split, margin, spacing,
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
                evaluatedCuts.Add((candidate.Cuts, candidate.Ratio, candidate.Spread));
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
                    spacing * 0.5,
                    spacing * 0.25,
                    spacing * 0.125,
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

        // 3단계: 절단 위치만으로는 고정 레인 간격의 위상 때문에 길이가 계단처럼 뛴다.
        // 길이 비가 목표보다 나쁘면, 상위 절단 후보에서 시작 측면·종료 측면·시작 레인 위상을
        // 바꾼 인출점 배치를 실제 2D로 생성해 조합을 고른다. 인출점은 모두 자식 영역의 실제
        // 경계 provenance를 가지며 부모 선택 구간과 마진 안에 있다.
        SplitVariantCandidate bestVariant = null;
        int variantGenerations = 0;
        int variantCutSets = 0;
        string lastVariantFailure = string.Empty;
        if (best.Ratio > AreaSplitter.LengthBalanceTolerance)
        {
            List<(double[] Cuts, double Ratio, double Spread)> variantSeeds = evaluatedCuts
                .OrderBy(entry => entry.Ratio)
                .ThenBy(entry => entry.Spread)
                .Take(SplitSearchMaxVariantSeeds)
                .ToList();
            foreach ((double[] seedCuts, _, _) in variantSeeds)
            {
                if (TryEvaluateVariantCutSet(parent, seedCuts, margin, spacing,
                        ref variantGenerations, ref variantCutSets, ref bestVariant,
                        out SplitVariantCandidate seedVariant, out string seedFailure))
                {
                    if (seedVariant != null
                        && seedVariant.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
                }
                else
                {
                    lastVariantFailure = seedFailure;
                }
            }

            // 4단계: 가장 좋은 인출점 조합의 절단 위치 주변을 좁혀 본다. 절단 좌표는
            // 인출점 배치와 달리 연속적이므로, 레인 수가 바뀌는 경계 근처만 국소 탐색한다.
            if (bestVariant != null
                && bestVariant.Ratio > AreaSplitter.LengthBalanceTolerance)
            {
                double[] localCuts = bestVariant.Cuts;
                SplitVariantCandidate localBest = bestVariant;
                double[] steps = { spacing * 0.5, spacing * 0.25, spacing * 0.125 };
                foreach (double step in steps)
                {
                    if (!(step > 1e-6)) continue;
                    const int maxPasses = 2;
                    for (int pass = 0; pass < maxPasses; pass++)
                    {
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
                                    if (!TryEvaluateVariantCutSet(parent, moved, margin, spacing,
                                            ref variantGenerations, ref variantCutSets, ref bestVariant,
                                            out SplitVariantCandidate trial, out string trialFailure))
                                    {
                                        lastVariantFailure = trialFailure;
                                        continue;
                                    }
                                    if (trial == null) continue;
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
                        if (!improved) break;
                        if (localBest.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
                    }
                    if (localBest.Ratio <= AreaSplitter.LengthBalanceTolerance) break;
                }
            }
        }

        bool preferVariant = bestVariant != null
            && (bestVariant.Ratio < best.Ratio - 1e-9
                || (Math.Abs(bestVariant.Ratio - best.Ratio) <= 1e-9
                    && bestVariant.Spread < best.Spread - 1e-9));
        if (preferVariant)
        {
            chosen = bestVariant!.Split;
            children = bestVariant.Children;
            patterns = bestVariant.Patterns;
            balanceRatio = bestVariant.Ratio;
        }
        else
        {
            chosen = best.Split;
            children = best.Children;
            patterns = best.Patterns;
            balanceRatio = best.Ratio;
        }

        if (!double.IsFinite(balanceRatio) || balanceRatio > AreaSplitter.HardLengthBalanceLimit)
        {
            double[] finalLengths = preferVariant ? bestVariant!.Lengths : best.Lengths;
            double min = finalLengths.Min();
            double max = finalLengths.Max();
            failure = string.Format(CultureInfo.CurrentCulture,
                "분할 영역의 실제 2D 발열경로 길이를 허용 범위 안에서 균등하게 만들 수 없습니다 — 최소 {0:F1} mm, 최대 {1:F1} mm, 편차 {2:F3}배 (허용 {3:F2}배). 절단 위치 {4}개, 인출점 배치 {5}개 후보를 실제 2D로 생성해 비교했지만 허용 비율을 만족하지 못했습니다. 이 분할은 확정하지 않습니다.",
                min, max, balanceRatio, AreaSplitter.HardLengthBalanceLimit,
                evaluatedCuts.Count, variantGenerations);
            if (!string.IsNullOrEmpty(lastVariantFailure) &&
                variantGenerations == 0)
            {
                failure += " 인출점 배치 후보 평가 실패: " + lastVariantFailure;
            }
            return false;
        }

        return true;
    }

    /// <summary>
    /// 절단 후보 하나에서 인출점 배치 후보를 실제 2D로 생성·평가한다. 성공하면 고른 조합을
    /// <paramref name="best"/>와 비교해 더 좋은 것만 남긴다.
    /// </summary>
    private bool TryEvaluateVariantCutSet(
        WorkingArea parent,
        double[] cuts,
        double margin,
        double spacing,
        ref int generations,
        ref int cutSets,
        ref SplitVariantCandidate best,
        out SplitVariantCandidate evaluated,
        out string failure)
    {
        evaluated = null;
        failure = string.Empty;
        if (cutSets >= SplitSearchMaxVariantCutSets)
        {
            failure = "인출점 배치 후보를 평가할 절단 후보 한도에 도달했습니다.";
            return false;
        }
        AreaSplitResult split = AreaSplitter.TryBuildAtCuts(parent.Region[0], parent.Exclusions,
            parent.Direction, margin, spacing, cuts);
        if (!split.Succeeded)
        {
            failure = split.Failure;
            return false;
        }
        cutSets++;
        if (!TryEvaluateSplitVariants(parent, split, margin, spacing, ref generations,
                out SplitVariantCandidate variant, out failure))
            return false;
        evaluated = variant;
        if (IsBetterVariant(variant, best)) best = variant;
        return true;
    }

    /// <summary>
    /// 자식마다 시작 측면·종료 측면·시작 레인 위상을 바꾼 인출점 후보를 실제 2D로 생성하고,
    /// 영역별로 하나씩 골라 길이 비가 가장 좋은 조합을 게시한다. 생성한 패턴을 그대로
    /// 돌려주므로 호출자는 다시 생성하지 않는다.
    /// </summary>
    private bool TryEvaluateSplitVariants(
        WorkingArea parent,
        AreaSplitResult split,
        double margin,
        double spacing,
        ref int generations,
        out SplitVariantCandidate candidate,
        out string failure)
    {
        candidate = null!;
        failure = string.Empty;
        if (!split.Succeeded || split.Bands.Count < 2)
        {
            failure = split.Succeeded ? "분할 영역 수가 2개보다 적습니다." : split.Failure;
            return false;
        }
        if (!TryBuildSplitChildren(parent, split, out List<WorkingArea> children, out failure))
            return false;
        if (!SplitExitPointPlanner.TryBuildCandidates(parent, children, margin, spacing, true,
                out List<List<SplitExitPointPlanner.SplitExitCandidate>> candidateLists,
                out failure))
            return false;

        var lengthsByChild = new List<IReadOnlyList<double>>(children.Count);
        var patternsByChild = new List<List<SerpentineResult>>(children.Count);
        var exitsByChild = new List<List<SplitExitPointPlanner.SplitExitCandidate>>(children.Count);
        string firstGenerationFailure = string.Empty;

        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            WorkingArea child = children[childIndex];
            var lengths = new List<double>();
            var childPatterns = new List<SerpentineResult>();
            var childExits = new List<SplitExitPointPlanner.SplitExitCandidate>();
            foreach (SplitExitPointPlanner.SplitExitCandidate exitCandidate in candidateLists[childIndex])
            {
                if (generations >= SplitSearchMaxVariantGenerations) break;
                child.SelectedStartExit = exitCandidate.Start;
                child.SelectedEndExit = exitCandidate.End;
                child.StartAnchor = exitCandidate.Start.Point;
                child.EndAnchor = exitCandidate.End.Point;
                generations++;
                if (!TryGenerateSelectedExitPattern(child, margin, spacing,
                        out SerpentineResult pattern, out string generationFailure))
                {
                    if (firstGenerationFailure.Length == 0)
                        firstGenerationFailure = child.Name + ": " + generationFailure;
                    continue;
                }
                double length = pattern!.RoutePieces.Sum(piece => piece.Length);
                if (!double.IsFinite(length) || length <= 0) continue;
                lengths.Add(length);
                childPatterns.Add(pattern);
                childExits.Add(exitCandidate);
            }
            if (lengths.Count == 0)
            {
                failure = child.Name + " 영역의 2D 경로 후보를 만들지 못했습니다 — "
                    + (firstGenerationFailure.Length > 0
                        ? firstGenerationFailure
                        : "인출점 배치 후보가 없습니다.");
                return false;
            }
            lengthsByChild.Add(lengths);
            patternsByChild.Add(childPatterns);
            exitsByChild.Add(childExits);
        }

        if (!SplitLengthCombinationSolver.TrySolve(lengthsByChild,
                out int[] indices, out double ratio, out _, out _))
        {
            failure = "분할 영역의 2D 경로 길이 조합을 계산하지 못했습니다.";
            return false;
        }

        var patterns = new Dictionary<WorkingArea, SerpentineResult>();
        var chosenLengths = new double[children.Count];
        double mean = 0;
        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            WorkingArea child = children[childIndex];
            int chosenIndex = indices[childIndex];
            SplitExitPointPlanner.SplitExitCandidate chosen = exitsByChild[childIndex][chosenIndex];
            child.SelectedStartExit = chosen.Start;
            child.SelectedEndExit = chosen.End;
            child.StartAnchor = chosen.Start.Point;
            child.EndAnchor = chosen.End.Point;
            patterns[child] = patternsByChild[childIndex][chosenIndex];
            chosenLengths[childIndex] = lengthsByChild[childIndex][chosenIndex];
            mean += chosenLengths[childIndex];
        }
        mean /= children.Count;
        double spread = chosenLengths.Sum(length => (length - mean) * (length - mean));

        candidate = new SplitVariantCandidate
        {
            Cuts = split.Cuts.ToArray(),
            Split = split,
            Children = children,
            Patterns = patterns,
            Lengths = chosenLengths,
            Ratio = ratio,
            Spread = spread,
        };
        return true;
    }

    /// <summary>인출점 배치 조합도 길이 비, 편차 제곱합, 절단 좌표 사전순으로 결정적으로 고른다.</summary>
    private static bool IsBetterVariant(SplitVariantCandidate candidate, SplitVariantCandidate current)
    {
        if (candidate == null) return false;
        if (current == null) return true;
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

    /// <summary>분할 후보의 자식 경계·인출점·2D 경로를 실제로 만들고 길이를 잰다.</summary>
    internal bool TryEvaluateSplit2D(
        WorkingArea parent,
        AreaSplitResult split,
        double margin,
        double spacing,
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

        if (!split.Succeeded || split.Bands.Count < 2)
        {
            failure = split.Succeeded ? "분할 영역 수가 2개보다 적습니다." : split.Failure;
            return false;
        }
        if (!TryBuildSplitChildren(parent, split, out children, out failure))
            return false;
        if (!SplitExitPointPlanner.TryAssign(parent, children, margin, spacing, out failure))
            return false;

        lengths = new double[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            WorkingArea child = children[i];
            if (!TryGenerateSelectedExitPattern(child, margin, spacing,
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
