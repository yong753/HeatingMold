using HeatingMold.Core.Geometry;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;

namespace HeatingMold.Design.HeatingPath;

/// <summary>고정 센터 피치와 명시 방향으로 유한 사행 레인 후보를 열거합니다.</summary>
public sealed class LaneCandidateEnumerator
{
    private readonly BoundaryClassifier _boundaryClassifier;
    private readonly ExitSideResolver _exitSideResolver;

    /// <summary>영역 경계와 출구 앵커를 확인할 서비스를 연결합니다.</summary>
    public LaneCandidateEnumerator(
        BoundaryClassifier? boundaryClassifier = null,
        ExitSideResolver? exitSideResolver = null)
    {
        _boundaryClassifier = boundaryClassifier ?? new BoundaryClassifier();
        _exitSideResolver = exitSideResolver ?? new ExitSideResolver();
    }

    /// <summary>가능한 레인 수, 위상 seed, 순서와 첫 진행 방향을 제한된 수로 평가합니다.</summary>
    public OperationResult<LaneCandidateEnumeration> Enumerate(HeatingPathGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string rule = "heating-path-generation-v1";
        double tolerance = request.Settings.AnchorCoincidenceToleranceMm;
        OperationResult<ResolvedExitSideLocation> startSide = _exitSideResolver.Resolve(
            request.Region, request.PlacementConstraints.StartAnchor.WorkingUv, tolerance, rule);
        OperationResult<ResolvedExitSideLocation> endSide = _exitSideResolver.Resolve(
            request.Region, request.PlacementConstraints.EndAnchor.WorkingUv, tolerance, rule);
        if (!startSide.IsSuccess || !endSide.IsSuccess)
        {
            var issues = new List<ValidationIssue>();
            issues.AddRange(startSide.Issues);
            issues.AddRange(endSide.Issues);
            return OperationResult<LaneCandidateEnumeration>.Failure(issues);
        }

        OperationResult<IReadOnlyList<ClassifiedBoundaryRing>> classified = _boundaryClassifier.Classify(
            request.Region.EffectiveBoundaryLoops.Select(loop => loop.Vertices),
            rule);
        if (!classified.IsSuccess)
            return OperationResult<LaneCandidateEnumeration>.Failure(classified.Issues);

        IReadOnlyList<IReadOnlyList<Vec2>> insetRings = BuildInsetRings(
            classified.GetValueOrThrow(),
            request.PlacementConstraints.EffectiveCenterlineBoundaryMargin(request.WireSpecification.DiameterMm).Value,
            rule,
            out ValidationIssue? insetIssue);
        if (insetIssue is not null)
            return OperationResult<LaneCandidateEnumeration>.Failure([insetIssue]);

        IReadOnlyList<Vec2>[] localRings = insetRings
            .Select(ring => (IReadOnlyList<Vec2>)ring.Select(point => ToLaneLocal(point, request.PlacementConstraints.Direction)).ToArray())
            .ToArray();
        double minimumY = localRings.SelectMany(ring => ring).Min(point => point.Y);
        double maximumY = localRings.SelectMany(ring => ring).Max(point => point.Y);
        double height = maximumY - minimumY;
        double pitch = request.PlacementConstraints.CenterPitch(request.WireSpecification.DiameterMm).Value;
        if (height < 0 || !double.IsFinite(height) || pitch <= 0 || !double.IsFinite(pitch))
        {
            return OperationResult<LaneCandidateEnumeration>.Failure(
            [
                Issue("InvalidLaneSearchBounds", "Inset 영역 또는 센터 피치가 유효한 유한 범위가 아닙니다.", rule, request)]);
        }

        int maximumLaneCount = Math.Max(0, (int)Math.Floor((height + tolerance) / pitch) + 1);
        if (maximumLaneCount == 0)
        {
            return OperationResult<LaneCandidateEnumeration>.Failure(
            [
                Issue("RegionTooNarrowForPitch", "경계 여유를 적용한 영역 높이에 배치할 레인이 없습니다.", rule, request)]);
        }

        Vec2 startLocal = ToLaneLocal(request.PlacementConstraints.StartAnchor.WorkingUv, request.PlacementConstraints.Direction);
        Vec2 endLocal = ToLaneLocal(request.PlacementConstraints.EndAnchor.WorkingUv, request.PlacementConstraints.Direction);
        var candidates = new List<LaneLayoutCandidate>();
        int evaluated = 0;
        bool budgetExhausted = false;
        bool encounteredMultipleIntervals = false;
        int ordinal = 0;

        for (int laneCount = maximumLaneCount; laneCount >= 1 && !budgetExhausted; laneCount--)
        {
            double firstLaneMaximum = maximumY - (laneCount - 1) * pitch;
            if (firstLaneMaximum < minimumY - tolerance)
                continue;

            foreach (double phase in EnumeratePhaseSeeds(minimumY, firstLaneMaximum, pitch, startLocal.Y, endLocal.Y, laneCount))
            {
                foreach (bool reverseLaneOrder in new[] { false, true })
                {
                    foreach (bool firstLaneForward in new[] { true, false })
                    {
                        if (evaluated >= request.Settings.MaximumCandidateCount)
                        {
                            budgetExhausted = true;
                            break;
                        }
                        evaluated++;
                        int[] laneIndices = Enumerable.Range(0, laneCount).ToArray();
                        if (reverseLaneOrder)
                            Array.Reverse(laneIndices);

                        var runs = new List<LaneRun>();
                        bool supported = true;
                        for (int traversalIndex = 0; traversalIndex < laneIndices.Length; traversalIndex++)
                        {
                            int laneIndex = laneIndices[traversalIndex];
                            double y = phase + laneIndex * pitch;
                            List<(double StartX, double EndX)> intervals = FindScanlineIntervals(localRings, y);
                            if (intervals.Count != 1)
                            {
                                // 잘린 레인 사이를 연결해 홀이나 제외 영역을 가로지르지 않습니다.
                                encounteredMultipleIntervals |= intervals.Count > 1;
                                supported = false;
                                break;
                            }

                            (double startX, double endX) = intervals[0];
                            bool forward = firstLaneForward == (traversalIndex % 2 == 0);
                            Vec2 localStart = new(forward ? startX : endX, y);
                            Vec2 localEnd = new(forward ? endX : startX, y);
                            runs.Add(new LaneRun(
                                laneIndex,
                                ToWorkingUv(localStart, request.PlacementConstraints.Direction),
                                ToWorkingUv(localEnd, request.PlacementConstraints.Direction),
                                forward));
                        }

                        if (supported)
                        {
                            double topGap = Math.Max(0, phase - minimumY);
                            double bottomGap = Math.Max(0, maximumY - (phase + (laneCount - 1) * pitch));
                            candidates.Add(new LaneLayoutCandidate(
                                laneCount,
                                phase,
                                reverseLaneOrder,
                                firstLaneForward,
                                runs,
                                Math.Max(topGap, bottomGap),
                                ordinal++));
                        }
                    }

                    if (budgetExhausted)
                        break;
                }

                if (budgetExhausted)
                    break;
            }
        }

        if (candidates.Count == 0)
        {
            string reason = budgetExhausted && encounteredMultipleIntervals
                ? "후보 예산에 도달했고 다중 구간 레인 후보가 포함됐습니다. 수학적 불가능을 뜻하지 않으며, 홀이나 분리 영역을 가로지르지 않는 다중 구간 라우팅은 아직 지원하지 않습니다."
                : budgetExhausted
                    ? "후보 예산 안에서 연속 레인 구조를 찾지 못했습니다. 수학적 불가능을 뜻하지 않습니다."
                    : encounteredMultipleIntervals
                        ? "여러 유효 구간을 가진 레인을 연결하는 안전 경로를 만들지 못했습니다. 홀이나 분리 영역을 가로지르지 않는 다중 구간 라우팅은 아직 지원하지 않습니다."
                        : "모든 평가 레인에 하나의 유효 구간이 있는 사행 후보를 찾지 못했습니다.";
            return OperationResult<LaneCandidateEnumeration>.Failure(
            [
                Issue("NoSupportedLaneLayout", reason, rule, request)
            ]);
        }

        var enumeration = new LaneCandidateEnumeration(candidates, evaluated, budgetExhausted);
        IReadOnlyList<ValidationIssue> warnings = budgetExhausted
            ? [Issue("LaneCandidateBudgetExhausted", "후보 예산에 도달해 최적 후보 여부는 확정하지 못했습니다.", rule, request, ValidationSeverity.Warning)]
            : [];
        return OperationResult<LaneCandidateEnumeration>.Success(enumeration, warnings);
    }

    private static IReadOnlyList<IReadOnlyList<Vec2>> BuildInsetRings(
        IReadOnlyList<ClassifiedBoundaryRing> rings,
        double marginMm,
        string ruleVersion,
        out ValidationIssue? issue)
    {
        var insetRings = new List<IReadOnlyList<Vec2>>();
        foreach (ClassifiedBoundaryRing outer in rings.Where(ring => ring.NestingDepth % 2 == 0))
        {
            IReadOnlyList<IReadOnlyList<Vec2>> holes = rings
                .Where(ring => ring.NestingDepth == outer.NestingDepth + 1 && PointInRing(outer.Vertices, ring.Vertices[0]))
                .Select(ring => ring.Vertices)
                .ToArray();
            List<List<Vec2>> inset = RegionOps.BuildPatternRegion(outer.Vertices, holes, marginMm);
            insetRings.AddRange(inset.Select(ring => (IReadOnlyList<Vec2>)ring));
        }

        if (insetRings.Count == 0)
        {
            issue = new ValidationIssue(
                "EmptyBoundaryInset",
                ValidationSeverity.Error,
                "Design.EmptyBoundaryInset",
                [],
                ruleVersion,
                evidence: ["경계 여유와 도체 반지름을 적용한 뒤 남은 경로 배치 영역이 없습니다."]);
            return [];
        }

        issue = null;
        return RegionOps.DifferencePolygons(insetRings, [], precision: 6)
            .Select(ring => (IReadOnlyList<Vec2>)ring)
            .ToArray();
    }

    private static IEnumerable<double> EnumeratePhaseSeeds(
        double minimumY,
        double maximumFirstLaneY,
        double pitch,
        double startY,
        double endY,
        int laneCount)
    {
        double center = (minimumY + maximumFirstLaneY) / 2;
        double[] seeds =
        [
            center,
            minimumY,
            maximumFirstLaneY,
            startY,
            endY - (laneCount - 1) * pitch
        ];
        var phases = new List<(double Value, double Distance, int SeedOrder)>();
        for (int seedOrder = 0; seedOrder < seeds.Length; seedOrder++)
        {
            double normalized = seeds[seedOrder] % pitch;
            if (normalized < 0)
                normalized += pitch;

            int firstIndex = (int)Math.Ceiling((minimumY - normalized - 1e-9) / pitch);
            for (double phase = normalized + firstIndex * pitch;
                 phase <= maximumFirstLaneY + 1e-9;
                 phase += pitch)
            {
                double clampedPhase = Math.Clamp(phase, minimumY, maximumFirstLaneY);
                if (phases.Any(existing => Math.Abs(existing.Value - clampedPhase) <= 1e-8))
                    continue;
                phases.Add((clampedPhase, Math.Abs(clampedPhase - seeds[seedOrder]), seedOrder));
            }
        }

        return phases
            .OrderBy(candidate => candidate.SeedOrder)
            .ThenBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Value)
            .ToArray();
    }

    private static List<(double StartX, double EndX)> FindScanlineIntervals(
        IReadOnlyList<IReadOnlyList<Vec2>> rings,
        double y)
    {
        var intersections = new List<double>();
        foreach (IReadOnlyList<Vec2> ring in rings)
        {
            for (int index = 0; index < ring.Count; index++)
            {
                Vec2 first = ring[index];
                Vec2 second = ring[(index + 1) % ring.Count];
                if ((first.Y <= y && second.Y > y) || (second.Y <= y && first.Y > y))
                {
                    double parameter = (y - first.Y) / (second.Y - first.Y);
                    intersections.Add(first.X + parameter * (second.X - first.X));
                }
            }
        }

        intersections.Sort();
        var intervals = new List<(double StartX, double EndX)>();
        for (int index = 0; index + 1 < intersections.Count; index += 2)
        {
            if (intersections[index + 1] - intersections[index] > 1e-9)
                intervals.Add((intersections[index], intersections[index + 1]));
        }

        return intervals;
    }

    private static Vec2 ToLaneLocal(Vec2 point, PlacementDirection direction) => direction switch
    {
        PlacementDirection.AlongX => point,
        PlacementDirection.AlongY => new Vec2(point.Y, -point.X),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static Vec2 ToWorkingUv(Vec2 point, PlacementDirection direction) => direction switch
    {
        PlacementDirection.AlongX => point,
        PlacementDirection.AlongY => new Vec2(-point.Y, point.X),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int current = 0, previous = ring.Count - 1; current < ring.Count; previous = current++)
        {
            Vec2 first = ring[current];
            Vec2 second = ring[previous];
            bool crosses = (first.Y > point.Y) != (second.Y > point.Y) &&
                point.X < (second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y) + first.X;
            if (crosses)
                inside = !inside;
        }

        return inside;
    }

    private static ValidationIssue Issue(
        string code,
        string evidence,
        string ruleVersion,
        HeatingPathGenerationRequest request,
        ValidationSeverity severity = ValidationSeverity.Error) => new(
        code,
        severity,
        $"Design.{code}",
        [request.ProjectId.Value, request.Region.Id.Value],
        ruleVersion,
        evidence: [evidence]);
}

/// <summary>정렬, 방향과 레인 실행 순서가 다른 한 개의 2D 사행 배치 후보입니다.</summary>
public sealed class LaneLayoutCandidate
{
    /// <summary>후보가 포함하는 레인 수입니다.</summary>
    public int LaneCount { get; }

    /// <summary>local lane 축에서 첫 레인의 위상 좌표입니다.</summary>
    public double Phase { get; }

    /// <summary>레인을 높은 좌표부터 순회하는지 나타냅니다.</summary>
    public bool ReverseLaneOrder { get; }

    /// <summary>첫 레인이 local X 증가 방향으로 진행하는지 나타냅니다.</summary>
    public bool FirstLaneForward { get; }

    /// <summary>경로 순서대로 배치한 단일 구간 레인 목록입니다.</summary>
    public IReadOnlyList<LaneRun> Runs { get; }

    /// <summary>상·하단에서 가장 가까운 레인까지의 최대 공백(mm)입니다.</summary>
    public double MaximumUncoveredGapMm { get; }

    /// <summary>동일한 품질 점수를 안정 정렬할 탐색 순번입니다.</summary>
    public int CandidateOrdinal { get; }

    /// <summary>검증된 레인 순서와 후보 평가 지표를 보관합니다.</summary>
    internal LaneLayoutCandidate(
        int laneCount,
        double phase,
        bool reverseLaneOrder,
        bool firstLaneForward,
        IEnumerable<LaneRun> runs,
        double maximumUncoveredGapMm,
        int candidateOrdinal)
    {
        LaneCount = laneCount;
        Phase = phase;
        ReverseLaneOrder = reverseLaneOrder;
        FirstLaneForward = firstLaneForward;
        Runs = Array.AsReadOnly(runs.ToArray());
        MaximumUncoveredGapMm = maximumUncoveredGapMm;
        CandidateOrdinal = candidateOrdinal;
    }
}

/// <summary>작업 평면에서 레인 하나의 시작·끝과 실제 순서를 보관합니다.</summary>
public sealed record LaneRun
{
    /// <summary>레인의 진단용 0 기반 인덱스입니다.</summary>
    public int LaneIndex { get; }

    /// <summary>경로 진행 방향 기준 레인 시작 좌표입니다.</summary>
    public Vec2 Start { get; }

    /// <summary>경로 진행 방향 기준 레인 끝 좌표입니다.</summary>
    public Vec2 End { get; }

    /// <summary>local X 증가 방향으로 진행하는지 나타냅니다.</summary>
    public bool IsForward { get; }

    /// <summary>레인 인덱스와 서로 다른 작업 평면 양 끝 좌표를 보관합니다.</summary>
    public LaneRun(int laneIndex, Vec2 start, Vec2 end, bool isForward)
    {
        if (laneIndex < 0 || start == end)
            throw new ArgumentOutOfRangeException(nameof(laneIndex));
        LaneIndex = laneIndex;
        Start = start;
        End = end;
        IsForward = isForward;
    }
}

/// <summary>후보 평가 수와 탐색 예산 상태를 가진 읽기 전용 레인 후보 모음입니다.</summary>
public sealed class LaneCandidateEnumeration
{
    /// <summary>한 개의 연속 끝점 조건을 가진 유효 레인 배치입니다.</summary>
    public IReadOnlyList<LaneLayoutCandidate> Candidates { get; }

    /// <summary>실제로 평가한 위상·순서·방향 조합 수입니다.</summary>
    public int CandidatesEvaluated { get; }

    /// <summary>최대 후보 수에 도달해 나머지 조합을 평가하지 못했는지 나타냅니다.</summary>
    public bool BudgetExhausted { get; }

    /// <summary>후보 수와 budget 상태를 방어 복사해 열거 결과를 만듭니다.</summary>
    internal LaneCandidateEnumeration(
        IEnumerable<LaneLayoutCandidate> candidates,
        int candidatesEvaluated,
        bool budgetExhausted)
    {
        if (candidatesEvaluated < 1)
            throw new ArgumentOutOfRangeException(nameof(candidatesEvaluated));
        Candidates = Array.AsReadOnly(candidates.ToArray());
        CandidatesEvaluated = candidatesEvaluated;
        BudgetExhausted = budgetExhausted;
    }
}
