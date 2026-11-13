namespace HeatingMold.Core.Geometry;

/// <summary>발열선 한 지점에서 가장 가까운 다른 발열선까지의 실제 외곽 간격 표본입니다.</summary>
public readonly record struct RouteSpacingSample(
    int RunIndex,
    int OtherRunIndex,
    Vec2 At,
    double CenterDistanceMm,
    double OuterGapMm);

/// <summary>분석 입력으로 전달하는 확정 발열선 하나의 정체성과 확정 설정입니다.
/// 좌표는 확정 작업평면의 2D 좌표이고, 간격/직경은 <b>확정 당시</b> 값이어야 한다.</summary>
public readonly record struct RouteSpacingRunInput(
    string AreaId,
    int PieceIndex,
    IReadOnlyList<Vec2> Points,
    double ConfiguredOuterGapMm,
    double WireDiameterMm,
    double DirectionDeg);

/// <summary>분석 결과가 보존하는 발열선 메타데이터입니다. Index는 입력 목록에서의 위치이고,
/// AreaId/PieceIndex는 영역·조각 identity이다.</summary>
public sealed class RouteSpacingRunInfo
{
    public int Index { get; init; }
    public string AreaId { get; init; } = string.Empty;
    public int PieceIndex { get; init; }
    public double ConfiguredOuterGapMm { get; init; }
    public double WireDiameterMm { get; init; }
    public double DirectionDeg { get; init; }
}

/// <summary>같은 생성 방향 안에서 비교된 레인 쌍 하나의 요약입니다.
/// 최소 외곽 간격을 만든 표본 위치(At)와 두 레인의 확정 설정을 함께 보존한다.</summary>
public readonly record struct RouteSpacingPairResult(
    int RunIndex,
    int OtherRunIndex,
    int AtRunIndex,
    Vec2 At,
    double CenterDistanceMm,
    double MinOuterGapMm,
    double MaxOuterGapMm,
    double ConfiguredOuterGapMm,
    double OtherConfiguredOuterGapMm,
    double WireDiameterMm,
    double OtherWireDiameterMm);

/// <summary>생성 방향이 같은(평행한) 발열선 묶음과 그 안에서만 비교된 쌍들입니다.
/// 방향이 다른 묶음끼리는 서로 비교하지 않으며 같은 피치의 정상/비정상을 판정하지 않는다.</summary>
public sealed class RouteSpacingDirectionGroup
{
    public double DirectionDeg { get; init; }
    public IReadOnlyList<int> RunIndices { get; init; } = Array.Empty<int>();
    public IReadOnlyList<RouteSpacingPairResult> Pairs { get; init; } = Array.Empty<RouteSpacingPairResult>();
    public double MinOuterGapMm { get; init; }
    public double MaxOuterGapMm { get; init; }
}

public enum RouteSpacingStatus
{
    Completed,
    Empty,
    InvalidInput,

    /// <summary>발열선은 있지만 생성 방향이 서로 달라 비교 가능한 쌍이 하나도 없습니다.</summary>
    DirectionSeparated,
}

/// <summary>발열사 간격 분석 결과입니다. 이 분석은 보고 전용이며 경로·길이·전기 판정을 바꾸지 않습니다.</summary>
public sealed class RouteSpacingResult
{
    public RouteSpacingStatus Status { get; init; }
    public IReadOnlyList<RouteSpacingSample> Samples { get; init; } = Array.Empty<RouteSpacingSample>();
    public IReadOnlyList<RouteSpacingRunInfo> Runs { get; init; } = Array.Empty<RouteSpacingRunInfo>();
    public IReadOnlyList<RouteSpacingDirectionGroup> DirectionGroups { get; init; } = Array.Empty<RouteSpacingDirectionGroup>();

    /// <summary>단일 설정 입력(레거시 4-인자 오버로드)에서만 채워집니다. 영역별 설정을 받는
    /// 오버로드에서는 0이며, 실제 설정은 <see cref="Runs"/>에 보존됩니다.</summary>
    public double ConfiguredOuterGapMm { get; init; }

    /// <summary>단일 직경 입력(레거시 4-인자 오버로드)에서만 채워집니다.</summary>
    public double WireDiameterMm { get; init; }

    public double MinOuterGapMm { get; init; }
    public double MaxOuterGapMm { get; init; }

    /// <summary>같은 방향에 이웃이 없어 비교 대상에서 빠진 발열선 수입니다.</summary>
    public int UncomparedRunCount { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>확정 발열선 사이의 실제 외곽 간격을 작업평면 2D에서 측정합니다.
/// 외곽 간격 = 발열선 중심선 사이 거리 − (두 발열선 직경의 합) / 2 이므로,
/// 직경이 다른 두 영역 사이에서도 같은 정의를 쓴다. 생성 방향이 평행한 발열선끼리만 비교한다.</summary>
public static class RouteSpacingAnalyzer
{
    public const string Version = "route-spacing-v2";

    /// <summary>두 발열선의 생성 방향을 같은 방향으로 볼 최대 각도 차이입니다(180° 모듈로).</summary>
    public const double ParallelDirectionToleranceDeg = 0.1;

    private const double MinimumSegmentLength = 1e-6;

    /// <summary>레거시 단일 설정 오버로드입니다. 모든 발열선이 같은 설정·같은 직경·같은 방향이라고 보고
    /// 하나의 방향 그룹으로 분석한다. 샘플 인덱스는 입력 목록 위치를 유지한다.</summary>
    public static RouteSpacingResult Analyze(
        IReadOnlyList<IReadOnlyList<Vec2>>? runs,
        double configuredOuterGapMm,
        double wireDiameterMm,
        double sampleStepMm = 1.0)
    {
        if (runs == null) return Invalid("발열선 목록이 없습니다.");
        if (!IsFinite(configuredOuterGapMm) || configuredOuterGapMm <= 0)
            return Invalid("설정된 발열사 외곽 간격이 올바르지 않습니다.");
        if (!IsFinite(wireDiameterMm) || wireDiameterMm < 0)
            return Invalid("전선 직경이 올바르지 않습니다.");
        if (!IsFinite(sampleStepMm) || sampleStepMm <= 0)
            return Invalid("간격 표본 간격이 올바르지 않습니다.");

        var inputs = new List<RouteSpacingRunInput>(runs.Count);
        for (int index = 0; index < runs.Count; index++)
        {
            inputs.Add(new RouteSpacingRunInput(
                string.Empty,
                index,
                runs[index] ?? Array.Empty<Vec2>(),
                configuredOuterGapMm,
                wireDiameterMm,
                0.0));
        }

        RouteSpacingResult result = Analyze(inputs, sampleStepMm);
        return new RouteSpacingResult
        {
            Status = result.Status,
            Samples = result.Samples,
            Runs = result.Runs,
            DirectionGroups = result.DirectionGroups,
            ConfiguredOuterGapMm = configuredOuterGapMm,
            WireDiameterMm = wireDiameterMm,
            MinOuterGapMm = result.MinOuterGapMm,
            MaxOuterGapMm = result.MaxOuterGapMm,
            UncomparedRunCount = result.UncomparedRunCount,
            Message = result.Message,
        };
    }

    /// <summary>영역·조각 identity, 확정 당시 간격·직경, 생성 방향을 보존한 채 간격을 분석합니다.
    /// 방향이 다른 묶음은 서로 비교하지 않고 <see cref="RouteSpacingStatus.DirectionSeparated"/>로
    /// 보고하며, 숫자 정상/비정상 판정을 만들지 않는다.</summary>
    public static RouteSpacingResult Analyze(
        IReadOnlyList<RouteSpacingRunInput>? runs,
        double sampleStepMm = 1.0,
        CancellationToken cancellationToken = default)
    {
        if (runs == null) return Invalid("발열선 목록이 없습니다.");
        if (!IsFinite(sampleStepMm) || sampleStepMm <= 0)
            return Invalid("간격 표본 간격이 올바르지 않습니다.");

        var infos = new List<RouteSpacingRunInfo>(runs.Count);
        var validIndices = new List<int>(runs.Count);
        var pointsByIndex = new Dictionary<int, IReadOnlyList<Vec2>>(runs.Count);

        for (int index = 0; index < runs.Count; index++)
        {
            RouteSpacingRunInput run = runs[index];
            IReadOnlyList<Vec2>? points = run.Points;
            if (points == null) return Invalid("발열선 좌표가 올바르지 않습니다.");
            if (!IsFinite(run.ConfiguredOuterGapMm) || run.ConfiguredOuterGapMm <= 0)
                return Invalid("설정된 발열사 외곽 간격이 올바르지 않습니다.");
            if (!IsFinite(run.WireDiameterMm) || run.WireDiameterMm < 0)
                return Invalid("전선 직경이 올바르지 않습니다.");
            if (!IsFinite(run.DirectionDeg))
                return Invalid("발열선 생성 방향이 올바르지 않습니다.");

            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                Vec2 point = points[pointIndex];
                if (!IsFinite(point.X) || !IsFinite(point.Y))
                    return Invalid("발열선 좌표가 올바르지 않습니다.");
            }

            infos.Add(new RouteSpacingRunInfo
            {
                Index = index,
                AreaId = run.AreaId ?? string.Empty,
                PieceIndex = run.PieceIndex,
                ConfiguredOuterGapMm = run.ConfiguredOuterGapMm,
                WireDiameterMm = run.WireDiameterMm,
                DirectionDeg = run.DirectionDeg,
            });
            pointsByIndex[index] = points;
            if (points.Count >= 2) validIndices.Add(index);
        }

        if (validIndices.Count == 0)
        {
            return new RouteSpacingResult
            {
                Status = RouteSpacingStatus.Empty,
                Runs = infos,
                Message = "확정된 발열선이 없습니다.",
            };
        }

        if (validIndices.Count < 2)
        {
            return new RouteSpacingResult
            {
                Status = RouteSpacingStatus.Empty,
                Runs = infos,
                UncomparedRunCount = validIndices.Count,
                Message = "이웃 발열선이 없어 간격을 측정할 수 없습니다.",
            };
        }

        // 생성 방향별 묶음(입력 순서를 유지한 첫 등장 순서, 저장된 각도가 대표값).
        var groupDirections = new List<double>();
        var groupMembers = new List<List<int>>();
        foreach (int index in validIndices)
        {
            double direction = infos[index].DirectionDeg;
            int group = -1;
            for (int existing = 0; existing < groupMembers.Count; existing++)
            {
                bool fits = true;
                foreach (int member in groupMembers[existing])
                {
                    if (!IsParallelDirection(infos[member].DirectionDeg, direction))
                    {
                        fits = false;
                        break;
                    }
                }

                if (fits)
                {
                    group = existing;
                    break;
                }
            }

            if (group < 0)
            {
                groupDirections.Add(direction);
                groupMembers.Add(new List<int>());
                group = groupMembers.Count - 1;
            }

            groupMembers[group].Add(index);
        }

        var samples = new List<RouteSpacingSample>();
        var pairStats = new Dictionary<(int Low, int High), PairAccumulator>();
        double minOuterGap = double.PositiveInfinity;
        double maxOuterGap = double.NegativeInfinity;

        for (int groupIndex = 0; groupIndex < groupMembers.Count; groupIndex++)
        {
            List<int> members = groupMembers[groupIndex];
            if (members.Count < 2) continue;

            foreach (int runIndex in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<Vec2> run = pointsByIndex[runIndex];
                for (int segment = 1; segment < run.Count; segment++)
                {
                    Vec2 start = run[segment - 1];
                    Vec2 end = run[segment];
                    double length = start.DistanceTo(end);
                    if (!IsFinite(length) || length <= MinimumSegmentLength) continue;

                    int steps = Math.Max(1, (int)Math.Ceiling(length / sampleStepMm));
                    for (int step = 0; step <= steps; step++)
                    {
                        Vec2 point = step == steps
                            ? end
                            : start + (end - start) * ((double)step / steps);

                        double bestOuterGap = double.PositiveInfinity;
                        double bestCenterDistance = 0.0;
                        int bestIndex = -1;
                        foreach (int otherIndex in members)
                        {
                            if (otherIndex == runIndex) continue;
                            if (!TryNearestOnRun(pointsByIndex[otherIndex], point, out double centerDistance)) continue;

                            // 직경이 다른 영역 사이에서도 외곽 간격의 정의가 유지되도록
                            // 중심선 거리에서 두 직경의 절반 합을 뺀 값을 비교 기준으로 쓴다.
                            double outerGap = centerDistance
                                - (infos[runIndex].WireDiameterMm + infos[otherIndex].WireDiameterMm) * 0.5;
                            if (outerGap < bestOuterGap)
                            {
                                bestOuterGap = outerGap;
                                bestCenterDistance = centerDistance;
                                bestIndex = otherIndex;
                            }
                        }

                        if (bestIndex < 0) continue;

                        samples.Add(new RouteSpacingSample(
                            runIndex,
                            bestIndex,
                            point,
                            bestCenterDistance,
                            bestOuterGap));

                        if (bestOuterGap < minOuterGap) minOuterGap = bestOuterGap;
                        if (bestOuterGap > maxOuterGap) maxOuterGap = bestOuterGap;

                        int low = Math.Min(runIndex, bestIndex);
                        int high = Math.Max(runIndex, bestIndex);
                        if (!pairStats.TryGetValue((low, high), out PairAccumulator? accumulator))
                        {
                            accumulator = new PairAccumulator();
                            pairStats[(low, high)] = accumulator;
                        }

                        accumulator.Add(bestOuterGap, point, bestCenterDistance, runIndex);
                    }
                }
            }
        }

        var groups = new List<RouteSpacingDirectionGroup>(groupMembers.Count);
        for (int groupIndex = 0; groupIndex < groupMembers.Count; groupIndex++)
        {
            List<int> members = groupMembers[groupIndex];
            var membership = new HashSet<int>(members);
            var pairs = new List<RouteSpacingPairResult>();
            double groupMin = double.PositiveInfinity;
            double groupMax = double.NegativeInfinity;

            foreach (KeyValuePair<(int Low, int High), PairAccumulator> entry in pairStats)
            {
                if (!membership.Contains(entry.Key.Low) || !membership.Contains(entry.Key.High)) continue;
                PairAccumulator accumulator = entry.Value;
                pairs.Add(new RouteSpacingPairResult(
                    entry.Key.Low,
                    entry.Key.High,
                    accumulator.AtRunIndex,
                    accumulator.At,
                    accumulator.CenterDistanceMm,
                    accumulator.MinOuterGapMm,
                    accumulator.MaxOuterGapMm,
                    infos[entry.Key.Low].ConfiguredOuterGapMm,
                    infos[entry.Key.High].ConfiguredOuterGapMm,
                    infos[entry.Key.Low].WireDiameterMm,
                    infos[entry.Key.High].WireDiameterMm));
                if (accumulator.MinOuterGapMm < groupMin) groupMin = accumulator.MinOuterGapMm;
                if (accumulator.MaxOuterGapMm > groupMax) groupMax = accumulator.MaxOuterGapMm;
            }

            pairs.Sort(static (left, right) =>
            {
                int compare = left.RunIndex.CompareTo(right.RunIndex);
                return compare != 0 ? compare : left.OtherRunIndex.CompareTo(right.OtherRunIndex);
            });

            groups.Add(new RouteSpacingDirectionGroup
            {
                DirectionDeg = groupDirections[groupIndex],
                RunIndices = members,
                Pairs = pairs,
                MinOuterGapMm = pairs.Count == 0 ? 0.0 : groupMin,
                MaxOuterGapMm = pairs.Count == 0 ? 0.0 : groupMax,
            });
        }

        int uncomparedRunCount = 0;
        foreach (List<int> members in groupMembers)
        {
            if (members.Count < 2) uncomparedRunCount += members.Count;
        }

        if (samples.Count == 0)
        {
            bool onlySeparatedDirections = groupMembers.Count >= 2 && uncomparedRunCount == validIndices.Count;
            return new RouteSpacingResult
            {
                Status = onlySeparatedDirections
                    ? RouteSpacingStatus.DirectionSeparated
                    : RouteSpacingStatus.Empty,
                Runs = infos,
                DirectionGroups = groups,
                UncomparedRunCount = uncomparedRunCount,
                Message = onlySeparatedDirections
                    ? "방향이 다른 발열선만 있어 간격을 비교하지 않았습니다."
                    : "간격을 측정할 수 있는 표본이 없습니다.",
            };
        }

        return new RouteSpacingResult
        {
            Status = RouteSpacingStatus.Completed,
            Samples = samples,
            Runs = infos,
            DirectionGroups = groups,
            MinOuterGapMm = minOuterGap,
            MaxOuterGapMm = maxOuterGap,
            UncomparedRunCount = uncomparedRunCount,
            Message = "발열사 간격을 분석했습니다.",
        };
    }

    private static bool IsParallelDirection(double leftDeg, double rightDeg)
    {
        double difference = leftDeg - rightDeg;
        double normalized = difference
            - 180.0 * Math.Round(difference / 180.0, MidpointRounding.AwayFromZero);
        return Math.Abs(normalized) <= ParallelDirectionToleranceDeg;
    }

    private static bool TryNearestOnRun(IReadOnlyList<Vec2>? run, Vec2 point, out double distance)
    {
        distance = double.PositiveInfinity;
        if (run == null || run.Count == 0) return false;

        bool found = false;
        if (run.Count == 1)
        {
            distance = point.DistanceTo(run[0]);
            return IsFinite(distance);
        }

        for (int index = 1; index < run.Count; index++)
        {
            double candidate = DistanceToSegment(point, run[index - 1], run[index]);
            if (!IsFinite(candidate)) continue;
            if (!found || candidate < distance)
            {
                distance = candidate;
                found = true;
            }
        }

        return found && IsFinite(distance);
    }

    private static double DistanceToSegment(Vec2 point, Vec2 a, Vec2 b)
    {
        Vec2 ab = b - a;
        double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared <= MinimumSegmentLength * MinimumSegmentLength)
            return point.DistanceTo(a);

        double t = ((point.X - a.X) * ab.X + (point.Y - a.Y) * ab.Y) / lengthSquared;
        if (t < 0.0) t = 0.0;
        else if (t > 1.0) t = 1.0;

        return point.DistanceTo(new Vec2(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static RouteSpacingResult Invalid(string message) => new()
    {
        Status = RouteSpacingStatus.InvalidInput,
        Message = message,
    };

    private sealed class PairAccumulator
    {
        internal double MinOuterGapMm { get; private set; } = double.PositiveInfinity;
        internal double MaxOuterGapMm { get; private set; } = double.NegativeInfinity;
        internal int AtRunIndex { get; private set; } = -1;
        internal Vec2 At { get; private set; }
        internal double CenterDistanceMm { get; private set; }

        internal void Add(double outerGapMm, Vec2 at, double centerDistanceMm, int atRunIndex)
        {
            if (outerGapMm < MinOuterGapMm)
            {
                MinOuterGapMm = outerGapMm;
                At = at;
                CenterDistanceMm = centerDistanceMm;
                AtRunIndex = atRunIndex;
            }

            if (outerGapMm > MaxOuterGapMm) MaxOuterGapMm = outerGapMm;
        }
    }
}
