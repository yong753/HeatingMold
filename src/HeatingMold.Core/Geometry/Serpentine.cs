using HeatingMold.Core.Model;

namespace HeatingMold.Core.Geometry;

/// <summary>레인 1개 위의 직선 분절 (Start→End 순서 = 진행 방향).</summary>
public sealed record SerpentineSegment(int LaneIndex, Vec2 Start, Vec2 End)
{
    public double Length => Start.DistanceTo(End);
}

public enum SerpentineRoutePieceKind { HeatingRun, TurnConnector, FixedConnector, ExitPiece }

/// <summary>An ordered, world-frame portion of a generated serpentine route.</summary>
public sealed record SerpentineRoutePiece(SerpentineRoutePieceKind Kind, int LaneIndex, IReadOnlyList<Vec2> Points)
{
    public double Length
    {
        get { double length = 0; for (int i = 1; i < Points.Count; i++) length += Points[i - 1].DistanceTo(Points[i]); return length; }
    }
}

/// <summary>사행 생성 결과 — 단일 연속 폴리라인.</summary>
public sealed class SerpentineResult
{
    public required IReadOnlyList<Vec2> Path { get; init; }
    public required IReadOnlyList<SerpentineSegment> Segments { get; init; }
    public required int LaneCount { get; init; }
    public required double AngleDeg { get; init; }
    public required double TotalLength { get; init; }
    /// <summary>간격/마진만으로 계산한 레인 수. 같은 면 인출을 위해 짝수로 줄인 경우에만 값이 있고, 아니면 0이다.</summary>
    public int NominalLaneCount { get; init; }
    /// <summary>같은 면 인출을 위해 실제 레인 수를 줄였는지 여부.</summary>
    public bool LaneCountAdjusted => NominalLaneCount > 0;
    public IReadOnlyList<SerpentineRoutePiece> RoutePieces { get; init; } = Array.Empty<SerpentineRoutePiece>();
}

/// <summary>제외영역 경로 계산이 실패한 이유. 분리된 발열영역을 억지로 잇지 않고 호출부가
/// 명시적으로 처리하도록 결과에 담는다.</summary>
public enum SerpentineComponentsFailureReason
{
    InvalidInput,
    NoLanes,
    /// <summary>제외영역이 작업영역을 완전히 가로질러 발열영역이 둘 이상으로 분리됨.</summary>
    SplitByExclusion,
    /// <summary>레인 구간 노드가 정확해 탐색 한계(20개)를 넘음.</summary>
    TooComplex,
    /// <summary>레인 구간 노드가 폴백 한계(4000개)를 넘음.</summary>
    TooManyFragments,
    /// <summary>모든 레인 구간을 한 번씩 잇는 완전 경로가 존재하지 않음.</summary>
    NoCompletePath,
}

public sealed record SerpentineComponentsResult(
    IReadOnlyList<IReadOnlyList<SerpentineRoutePiece>> Components,
    SerpentineComponentsFailureReason? Failure)
{
    public bool Succeeded => Failure == null;
    public static SerpentineComponentsResult Ok(IReadOnlyList<IReadOnlyList<SerpentineRoutePiece>> components) =>
        new(components, null);
    public static SerpentineComponentsResult Fail(SerpentineComponentsFailureReason reason) =>
        new(Array.Empty<IReadOnlyList<SerpentineRoutePiece>>(), reason);
}

/// <summary>Pure topology projection of route pieces. Disabling a turn separates the route;</summary>
/// <summary>fixed same-lane connectors are never editable and remain in their component.</summary>
public static class SerpentineTopology
{
    public static IReadOnlyList<IReadOnlyList<Vec2>> Compute(
        IReadOnlyList<SerpentineRoutePiece> pieces, IReadOnlyList<bool>? turnEnabled = null)
    {
        if (turnEnabled != null && turnEnabled.Count != pieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector))
            throw new ArgumentException("One enabled state is required per turn connector", nameof(turnEnabled));
        var components = new List<IReadOnlyList<Vec2>>();
        var current = new List<Vec2>();
        int turn = 0;
        foreach (var piece in pieces)
        {
            bool enabled = piece.Kind != SerpentineRoutePieceKind.TurnConnector || turnEnabled == null || turnEnabled[turn++];
            if (!enabled)
            {
                if (current.Count > 0) { components.Add(current); current = new List<Vec2>(); }
                continue;
            }
            if (piece.Points.Count == 0) continue;
            if (current.Count == 0) current.AddRange(piece.Points);
            else current.AddRange(piece.Points.Skip(current[^1] == piece.Points[0] ? 1 : 0));
        }
        if (current.Count > 0) components.Add(current);
        return components;
    }
}

/// <summary>스캔라인 기반 사행(serpentine) 발열 패턴 생성기.
/// 영역을 방향축 기준 로컬 프레임으로 회전 → 마진/홀 차집합(Clipper2) → 레인별 교차 구간 분절
/// → 지그재그 연결(U턴 호) → 단일 연속 폴리라인.</summary>
public static class Serpentine
{
    /// <summary>제외영역을 반영한 발열 경로 조각. 발열선은 발열영역(외곽−제외) 안에서만 생성되고,
    /// 턴커넥터는 이전 발열선 끝점에서 다음 발열선 시작점까지의 최단 직선이다.
    /// 제외영역이 영역을 완전히 가로질러 발열영역이 분리되면 경로를 만들지 않고 실패를 반환한다.</summary>
    public static SerpentineComponentsResult GenerateComponents(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParams p)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));
        if (p.Spacing <= 0) throw new ArgumentOutOfRangeException(nameof(p), "Spacing > 0 필요");
        double angle = ResolveAngleDeg(outer, p.Direction), rad = angle * Math.PI / 180.0;
        List<Vec2> local = Rotate(outer, -rad);
        var rotatedExclusions = exclusions.Select(r =>
        {
            Vec2[] corners = { new(r.MinX, r.MinY), new(r.MaxX, r.MinY), new(r.MaxX, r.MaxY), new(r.MinX, r.MaxY) };
            Vec2[] q = corners.Select(v => v.Rotate(-rad)).ToArray();
            return (q.Min(v => v.X), q.Min(v => v.Y), q.Max(v => v.X), q.Max(v => v.Y));
        }).ToList();

        // 발열영역 = 외곽(마진) − 제외 사각형(정확 차감, 팽창·AABB 근사 없음).
        List<List<Vec2>> region = RegionOps.SubtractRectangles(
            RegionOps.BuildPatternRegion(local, null, p.Margin), rotatedExclusions);
        if (region.Count == 0) return SerpentineComponentsResult.Ok(Array.Empty<IReadOnlyList<SerpentineRoutePiece>>());

        // 제외가 없으면 일반 생성과 완전히 같은 경로를 만든다(기존 동작 보존).
        if (rotatedExclusions.Count == 0)
        {
            var plain = BuildLocalRoute(region, p, true);
            return SerpentineComponentsResult.Ok(new IReadOnlyList<SerpentineRoutePiece>[]
            {
                plain.Pieces.Select(piece => piece with
                {
                    Points = piece.Points.Select(v => v.Rotate(rad)).ToList()
                }).ToList()
            });
        }

        // 외곽 링이 둘 이상이면 제외가 영역을 완전히 가로지른 것이다. 내부 홀은 외곽 링 1개 + CW 홀이라
        // 여기에 해당하지 않는다. 분리된 영역을 각각 독립 경로로 생성하지 않는다.
        int outerRingCount = region.Count(r => r.Count >= 3 && RegionOps.SignedArea(r) > 0);
        if (outerRingCount >= 2)
            return SerpentineComponentsResult.Fail(SerpentineComponentsFailureReason.SplitByExclusion);

        double minY = region.Min(r => r.Min(v => v.Y)), maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int lanes = Math.Max(1, (int)Math.Floor((height + 1e-9) / p.Spacing));
        double y0 = minY + (height - (lanes - 1) * p.Spacing) / 2.0;
        var laneY = new double[lanes];
        for (int k = 0; k < lanes; k++) laneY[k] = y0 + k * p.Spacing;

        var components = GroupComponents(region);
        if (components.Count > 1)
            return SerpentineComponentsResult.Fail(SerpentineComponentsFailureReason.SplitByExclusion);

        var result = new List<IReadOnlyList<SerpentineRoutePiece>>();
        foreach (List<List<Vec2>> component in components)
        {
            var byLane = new List<LaneIntervalNode>[lanes];
            for (int k = 0; k < lanes; k++)
            {
                byLane[k] = new List<LaneIntervalNode>();
                foreach ((double X0, double X1) interval in ScanlineIntervals(component, laneY[k]))
                    byLane[k].Add(new LaneIntervalNode(k, laneY[k], interval.X0, interval.X1));
            }
            var trace = TraceLaneChains(byLane);
            if (trace.Failure is { } failure) return SerpentineComponentsResult.Fail(failure);
            foreach (List<SerpentineRoutePiece> chain in trace.Chains)
                result.Add(chain.Select(piece => piece with { Points = piece.Points.Select(v => v.Rotate(rad)).ToList() }).ToList());
        }
        return SerpentineComponentsResult.Ok(result);
    }

    private const int MaxLaneChainNodes = 4000;
    /// <summary>비트마스크 DP가 정확히 탐색할 수 있는 최대 레인 구간 노드 수.</summary>
    private const int MaxExactLaneChainNodes = 20;
    /// <summary>DP 상태 폭주 방지 상한. 넘으면 TooComplex로 실패한다.</summary>
    private const int MaxLaneChainDpStates = 2_000_000;

    private readonly record struct LaneIntervalNode(int Lane, double Y, double X0, double X1);
    private readonly record struct LaneChainState(ulong Mask, int Node, int EntryEnd);
    private sealed class LaneChainStateValue
    {
        public double Cost;
        public LaneChainState Parent;
        public bool HasParent;
    }
    private readonly record struct LaneChainSolution(List<int> Order, List<bool> EnterLow, double Cost);
    private readonly record struct LaneChainTrace(
        List<List<SerpentineRoutePiece>> Chains,
        SerpentineComponentsFailureReason? Failure);

    /// <summary>CCW 외곽 링마다 그 안에 들어 있는 CW 홀 링을 묶어 연결 성분을 만든다.
    /// 홀의 대표점은 정점 평균(링 내부)을 쓴다.</summary>
    private static List<List<List<Vec2>>> GroupComponents(IReadOnlyList<List<Vec2>> rings)
    {
        var outers = new List<List<Vec2>>();
        var holes = new List<List<Vec2>>();
        foreach (List<Vec2> ring in rings)
        {
            if (ring.Count < 3) continue;
            if (RegionOps.SignedArea(ring) > 0) outers.Add(ring);
            else holes.Add(ring);
        }
        if (outers.Count == 0) return new();
        var components = outers.Select(o => new List<List<Vec2>> { o }).ToList();
        foreach (List<Vec2> hole in holes)
        {
            Vec2 probe = new(hole.Average(v => v.X), hole.Average(v => v.Y));
            int best = -1;
            double bestArea = double.MaxValue;
            for (int i = 0; i < components.Count; i++)
            {
                double area = Math.Abs(RegionOps.SignedArea(components[i][0]));
                if (area < bestArea && PointInRing(components[i][0], probe)) { best = i; bestArea = area; }
            }
            if (best >= 0) components[best].Add(hole);
        }
        return components;
    }

    /// <summary>연결 성분 하나의 레인 구간 노드들을 최단 커넥터 사슬로 이어 경로로 만든다.
    /// 간선은 인접 레인의 모든 노드 쌍(기하 제약 없음 — 턴커넥터는 어디든 지날 수 있다).
    /// 같은 레인의 노드끼리는 잇지 않는다(제외로 갈라진 구간은 같은 레인에서 이어지지 않는다).
    /// 완전 사슬이 없으면 가장 많은 노드를 덮는 사슬을 먼저 만들고 남은 노드로 반복한다.</summary>
    private static LaneChainTrace TraceLaneChains(IReadOnlyList<List<LaneIntervalNode>> byLane)
    {
        var nodes = new List<LaneIntervalNode>();
        var laneNodes = new List<int>[byLane.Count];
        for (int k = 0; k < byLane.Count; k++)
        {
            laneNodes[k] = new List<int>(byLane[k].Count);
            foreach (LaneIntervalNode node in byLane[k]) { laneNodes[k].Add(nodes.Count); nodes.Add(node); }
        }
        var chains = new List<List<SerpentineRoutePiece>>();
        if (nodes.Count == 0) return new(chains, null);

        if (nodes.Count > MaxLaneChainNodes)
        {
            // 기존 4000개 초과 폴백은 유지한다 — 조각 하나씩 표시하고 경로 연결은 하지 않는다.
            foreach (LaneIntervalNode node in nodes)
                chains.Add(new List<SerpentineRoutePiece>
                {
                    new(SerpentineRoutePieceKind.HeatingRun, node.Lane,
                        new Vec2[] { new(node.X0, node.Y), new(node.X1, node.Y) }),
                });
            return new(chains, null);
        }
        if (nodes.Count > MaxExactLaneChainNodes)
            return new(chains, SerpentineComponentsFailureReason.TooComplex);

        var adjacency = new List<int>[nodes.Count];
        for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new();
        for (int k = 0; k + 1 < byLane.Count; k++)
            foreach (int a in laneNodes[k])
                foreach (int b in laneNodes[k + 1])
                {
                    adjacency[a].Add(b);
                    adjacency[b].Add(a);
                }

        var remaining = new bool[nodes.Count];
        Array.Fill(remaining, true);
        int left = nodes.Count;
        while (left > 0)
        {
            ulong target = 0;
            for (int i = 0; i < nodes.Count; i++) if (remaining[i]) target |= 1UL << i;
            var solutions = SolveLaneChainDp(nodes, adjacency, target,
                static (_, _) => 0.0, static (_, _) => 0.0,
                collectAll: false, requireComplete: false, out var failure);
            if (failure is { } reason) return new(chains, reason);
            if (solutions.Count == 0) return new(chains, SerpentineComponentsFailureReason.TooComplex);
            LaneChainSolution solution = solutions[0];
            if (solution.Order.Count == 0) return new(chains, SerpentineComponentsFailureReason.TooComplex);
            chains.Add(BuildLaneChainPath(nodes, solution.Order, solution.EnterLow));
            foreach (int index in solution.Order)
                if (remaining[index]) { remaining[index] = false; left--; }
        }
        return new(chains, null);
    }

    /// <summary>레인 구간 노드를 각 1회씩 방문하는 최단 커넥터 경로를 비트마스크 DP로 찾는다.
    /// 상태 = (방문 마스크, 마지막 노드, 마지막 노드의 진입 끝). 값 = 시작 리드 + 누적 커넥터 길이.
    /// 시작/종료 비용을 0이 아닌 값으로 주면 인출면 근처 끝점만 후보가 된다.
    /// 완전 경로가 없으면(requireComplete=false) 가장 많은 노드를 덮는 부분 경로를 반환한다.</summary>
    private static List<LaneChainSolution> SolveLaneChainDp(
        IReadOnlyList<LaneIntervalNode> nodes,
        IReadOnlyList<List<int>> adjacency,
        ulong targetMask,
        Func<int, int, double> startCost,
        Func<int, int, double> endCost,
        bool collectAll,
        bool requireComplete,
        out SerpentineComponentsFailureReason? failure)
    {
        failure = null;
        int count = nodes.Count;
        var allowed = new bool[count];
        int targetCount = 0;
        for (int i = 0; i < count; i++)
            if ((targetMask & (1UL << i)) != 0) { allowed[i] = true; targetCount++; }
        if (targetCount == 0) return new();

        var values = new Dictionary<LaneChainState, LaneChainStateValue>();
        var buckets = new List<LaneChainState>[targetCount + 1];
        for (int i = 0; i <= targetCount; i++) buckets[i] = new List<LaneChainState>();

        for (int start = 0; start < count; start++)
        {
            if (!allowed[start]) continue;
            for (int entry = 0; entry < 2; entry++)
            {
                double cost = startCost(start, entry);
                if (!double.IsFinite(cost)) continue;
                var state = new LaneChainState(1UL << start, start, entry);
                values[state] = new LaneChainStateValue { Cost = cost };
                buckets[1].Add(state);
            }
        }
        if (buckets[1].Count == 0) return new();

        for (int visited = 1; visited < targetCount; visited++)
        {
            List<LaneChainState> bucket = buckets[visited];
            for (int b = 0; b < bucket.Count; b++)
            {
                LaneChainState state = bucket[b];
                LaneChainStateValue value = values[state];
                LaneIntervalNode last = nodes[state.Node];
                Vec2 exit = new(state.EntryEnd == 0 ? last.X1 : last.X0, last.Y);
                foreach (int next in adjacency[state.Node])
                {
                    if (!allowed[next] || (state.Mask & (1UL << next)) != 0) continue;
                    LaneIntervalNode target = nodes[next];
                    for (int entry = 0; entry < 2; entry++)
                    {
                        Vec2 enter = new(entry == 0 ? target.X0 : target.X1, target.Y);
                        double cost = value.Cost + exit.DistanceTo(enter);
                        var key = new LaneChainState(state.Mask | (1UL << next), next, entry);
                        if (values.TryGetValue(key, out LaneChainStateValue? existing))
                        {
                            if (cost + 1e-12 < existing.Cost)
                            {
                                existing.Cost = cost;
                                existing.Parent = state;
                                existing.HasParent = true;
                            }
                        }
                        else
                        {
                            values[key] = new LaneChainStateValue { Cost = cost, Parent = state, HasParent = true };
                            buckets[visited + 1].Add(key);
                            if (values.Count > MaxLaneChainDpStates)
                            {
                                failure = SerpentineComponentsFailureReason.TooComplex;
                                return new();
                            }
                        }
                    }
                }
            }
        }

        var completed = new List<(LaneChainState State, double Total, int Order)>();
        List<LaneChainState> fullBucket = buckets[targetCount];
        for (int i = 0; i < fullBucket.Count; i++)
        {
            LaneChainState state = fullBucket[i];
            double end = endCost(state.Node, 1 - state.EntryEnd);
            if (!double.IsFinite(end)) continue;
            completed.Add((state, values[state].Cost + end, i));
        }

        if (completed.Count > 0)
        {
            var ordered = collectAll
                ? completed.OrderBy(x => x.Total).ThenBy(x => x.Order).ToList()
                : completed.OrderBy(x => x.Total).ThenBy(x => x.Order).Take(1).ToList();
            return ordered.Select(x => ReconstructLaneChain(x.State, x.Total, values)).ToList();
        }
        if (requireComplete) return new();

        // 완전 경로가 없다 — 가장 많은 노드를 덮고, 같은 커버리지면 비용이 최소인 부분 경로.
        LaneChainState? best = null;
        double bestCost = double.PositiveInfinity;
        for (int visited = targetCount; visited >= 1 && best == null; visited--)
        {
            List<LaneChainState> bucket = buckets[visited];
            for (int i = 0; i < bucket.Count; i++)
            {
                LaneChainState state = bucket[i];
                double end = endCost(state.Node, 1 - state.EntryEnd);
                if (!double.IsFinite(end)) continue;
                double total = values[state].Cost + end;
                if (best == null || total + 1e-12 < bestCost)
                {
                    best = state;
                    bestCost = total;
                }
            }
        }
        if (best == null) return new();
        return new List<LaneChainSolution> { ReconstructLaneChain(best.Value, bestCost, values) };
    }

    private static LaneChainSolution ReconstructLaneChain(
        LaneChainState state, double total,
        IReadOnlyDictionary<LaneChainState, LaneChainStateValue> values)
    {
        var order = new List<int>();
        var enterLow = new List<bool>();
        LaneChainState current = state;
        while (true)
        {
            order.Add(current.Node);
            enterLow.Add(current.EntryEnd == 0);
            LaneChainStateValue value = values[current];
            if (!value.HasParent) break;
            current = value.Parent;
        }
        order.Reverse();
        enterLow.Reverse();
        return new LaneChainSolution(order, enterLow, total);
    }

    /// <summary>사슬의 노드 순서·진입 끝으로 조각 목록을 만든다. 노드마다 HeatingRun 1개,
    /// 노드 사이에는 이전 출구에서 다음 진입구까지의 직선 2점 TurnConnector가 들어간다.</summary>
    private static List<SerpentineRoutePiece> BuildLaneChainPath(
        IReadOnlyList<LaneIntervalNode> nodes,
        IReadOnlyList<int> order,
        IReadOnlyList<bool> enterLow)
    {
        var pieces = new List<SerpentineRoutePiece>(Math.Max(0, order.Count * 2 - 1));
        Vec2? previousExit = null;
        for (int i = 0; i < order.Count; i++)
        {
            LaneIntervalNode node = nodes[order[i]];
            Vec2 enter = new(enterLow[i] ? node.X0 : node.X1, node.Y);
            Vec2 exit = new(enterLow[i] ? node.X1 : node.X0, node.Y);
            if (previousExit.HasValue)
                pieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector, node.Lane,
                    new[] { previousExit.Value, enter }));
            pieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, node.Lane,
                new[] { enter, exit }));
            previousExit = exit;
        }
        return pieces;
    }

    public static SerpentineResult Generate(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        bool constrainConnectors = false)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));
        if (p.Spacing <= 0) throw new ArgumentOutOfRangeException(nameof(p), "Spacing > 0 필요");

        double angleDeg = ResolveAngleDeg(outer, p.Direction);
        double rad = angleDeg * Math.PI / 180.0;

        // 로컬 프레임(방향축 = 로컬 X)으로 회전
        List<Vec2> outerLocal = Rotate(outer, -rad);
        List<List<Vec2>>? holesLocal = holes?.Select(h => Rotate(h, -rad)).ToList();

        // 마진·홀 차집합
        List<List<Vec2>> region = RegionOps.BuildPatternRegion(outerLocal, holesLocal, p.Margin);
        if (region.Count == 0) return EmptyPattern(angleDeg);
        if (constrainConnectors && region.Count != 1)
            throw new InvalidOperationException("마진 적용 후 영역이 분리되어 단일 연속 경로를 구성할 수 없습니다");

        LocalRoute route = BuildLocalRoute(region, p, true);
        return ToWorld(route, rad, angleDeg);
    }

    /// <summary>배치 방향 설정 → 실제 회전각. Auto는 외곽 bbox 장축.</summary>
    public static double ResolveAngleDeg(IReadOnlyList<Vec2> outer, PatternDirection direction) => direction switch
    {
        PatternDirection.FrameX => 0.0,
        PatternDirection.FrameY => 90.0,
        _ => AutoAngleDeg(outer),
    };

    private sealed record LocalRoute(
        List<Vec2> Path,
        List<SerpentineSegment> Segments,
        List<SerpentineRoutePiece> Pieces,
        int Traversed,
        int RequestedLaneCount);

    /// <summary>로컬 프레임에서 레인 스캔·연결. startLtr이 첫 레인의 진행 방향을 결정한다.
    /// requireEvenRuns면 양 끝이 같은 인출면에 놓이도록 레인 수를 짝수로 줄인다(간격/마진은 유지).</summary>
    private static LocalRoute BuildLocalRoute(
        List<List<Vec2>> region,
        PatternParams p,
        bool startLtr,
        bool requireEvenRuns = false,
        int? laneCountOverride = null,
        int requestedLaneCount = 0)
    {
        // 레인 배치: 로컬 Y 범위에 Spacing 간격으로 균등 배치
        double minY = region.Min(r => r.Min(v => v.Y));
        double maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int nominalLaneCount = Math.Max(1, (int)Math.Floor((height + 1e-9) / p.Spacing));
        int laneCount = laneCountOverride.HasValue
            ? Math.Max(1, Math.Min(nominalLaneCount, laneCountOverride.Value))
            : nominalLaneCount;
        double y0 = minY + (height - (laneCount - 1) * p.Spacing) / 2.0;

        var segments = new List<SerpentineSegment>();
        var routePieces = new List<SerpentineRoutePiece>();
        var pts = new List<Vec2>();
        int traversed = 0;
        Vec2? prevEnd = null;

        for (int k = 0; k < laneCount; k++)
        {
            double y = y0 + k * p.Spacing;
            List<(double X0, double X1)> intervals = ScanlineIntervals(region, y);
            if (intervals.Count == 0) continue;

            bool ltr = (traversed % 2 == 0) == startLtr;
            List<(double X0, double X1)> ordered = ltr
                ? intervals.OrderBy(i => i.X0).ToList()
                : intervals.OrderByDescending(i => i.X1).ToList();

            bool firstOnLane = true;
            foreach ((double x0, double x1) in ordered)
            {
                Vec2 a = ltr ? new Vec2(x0, y) : new Vec2(x1, y);
                Vec2 b = ltr ? new Vec2(x1, y) : new Vec2(x0, y);

                if (pts.Count > 0 && firstOnLane && prevEnd.HasValue)
                {
                    // 레인 간 연결은 항상 두 끝점을 잇는 직선 2점이다. 영역 경계 판정을 하지 않는다.
                    // 직선이 실제 모델 표면을 벗어나면 확정 단계의 표면 매핑이 거부한다(fail-closed).
                    routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector, traversed,
                        new[] { prevEnd.Value, a }));
                }
                else if (pts.Count > 0 && prevEnd.HasValue)
                {
                    // A concave connected outline can have multiple intervals on
                    // one lane. Follow its inset boundary, never cut across the
                    // unselected notch between those intervals.
                    List<Vec2> connector = RouteAlongBoundary(region, prevEnd.Value, a);
                    pts.AddRange(connector);
                    routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector, traversed, new[] { prevEnd.Value }.Concat(connector).Append(a).ToList()));
                }
                // 같은 레인 내부 분절(홀)은 직선 연결 — MVP 허용 (홀 자동회피 제외 항목)

                pts.Add(a);
                pts.Add(b);
                segments.Add(new SerpentineSegment(traversed, a, b));
                routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, traversed, new[] { a, b }));
                prevEnd = b;
                firstOnLane = false;
            }

            traversed++;
        }

        // 같은 면 인출은 경로 양 끝이 같은 쪽 모서리에 있어야 하고, 그 조건은 레인(구간) 수가 짝수일 때 성립한다.
        // 사용자가 간격을 바꾸지 않아도 되도록 시스템이 가능한 최대 짝수 개수로 한 개 줄여 다시 배치한다.
        if (requireEvenRuns && !laneCountOverride.HasValue && traversed % 2 != 0 && laneCount >= 2)
            return BuildLocalRoute(region, p, startLtr,
                requireEvenRuns, laneCount - 1, laneCount);

        return new LocalRoute(pts, segments, routePieces, traversed, requestedLaneCount);
    }

    private static SerpentineResult ToWorld(LocalRoute route, double rad, double angleDeg)
    {
        // 원래 프레임으로 복원
        List<Vec2> pathWorld = Rotate(route.Path, +rad);
        List<SerpentineSegment> segmentsWorld = route.Segments
            .Select(s => new SerpentineSegment(s.LaneIndex, s.Start.Rotate(rad), s.End.Rotate(rad)))
            .ToList();
        List<SerpentineRoutePiece> piecesWorld = route.Pieces.Select(piece => piece with
        { Points = piece.Points.Select(point => point.Rotate(rad)).ToList() }).ToList();

        double total = 0;
        for (int i = 1; i < pathWorld.Count; i++) total += pathWorld[i].DistanceTo(pathWorld[i - 1]);

        return new SerpentineResult
        {
            Path = pathWorld,
            Segments = segmentsWorld,
            LaneCount = route.Traversed,
            NominalLaneCount = route.RequestedLaneCount,
            AngleDeg = angleDeg,
            TotalLength = total,
            RoutePieces = piecesWorld,
        };
    }

    private static SerpentineResult EmptyPattern(double angleDeg) => new()
    {
        Path = Array.Empty<Vec2>(),
        Segments = Array.Empty<SerpentineSegment>(),
        LaneCount = 0,
        AngleDeg = angleDeg,
        TotalLength = 0,
    };

    /// <summary>UV 외곽 bbox 장축 → 사행 방향각 (0 또는 90).</summary>
    public static double AutoAngleDeg(IReadOnlyList<Vec2> outer)
    {
        double w = outer.Max(v => v.X) - outer.Min(v => v.X);
        double h = outer.Max(v => v.Y) - outer.Min(v => v.Y);
        return w >= h ? 0.0 : 90.0;
    }

    // ---- 인출면 고정 경로 (시작·종료점이 같은 인출면 위에 있어야 하는 요구) ----

    /// <summary>ExitChain(인출면 경계) 위에서 양 끝이 모두 그 면에 놓이는 후보 쌍을 계산한다.
    /// 후보가 비면 해당 방향으로는 같은 면 인출이 불가능하다.</summary>
    public static IReadOnlyList<AnchoredCandidate> ComputeCandidates(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        IReadOnlyList<Vec2> exitChain,
        PatternDirection direction)
    {
        var candidates = new List<AnchoredCandidate>();
        if (outer.Count < 3 || p.Spacing <= 0 || exitChain.Count < 2) return candidates;

        foreach (double angleDeg in AngleCandidates(outer, direction))
        {
            foreach (bool startLtr in new[] { true, false })
            {
                (int regionCount, double rad, LocalRoute? route) =
                    BuildAtAngle(outer, holes, p, angleDeg, startLtr, requireEvenRuns: true);
                if (regionCount != 1 || route == null || route.Path.Count < 2) continue;
                Vec2 start = route.Path[0].Rotate(rad);
                Vec2 end = route.Path[^1].Rotate(rad);
                double reach = ChainReach(p);
                if (DistanceToChain(exitChain, start) > reach) continue;
                if (DistanceToChain(exitChain, end) > reach) continue;
                // 마진 인셋 때문에 경로 끝점은 인출면 경계보다 margin 만큼 안쪽에 있다.
                // 경계 위로 직교 투영한 점을 앵커로 쓰고, 그 연결 구간은 ExitPiece가 된다.
                Vec2 startAnchor = SnapOnChain(exitChain, start);
                Vec2 endAnchor = SnapOnChain(exitChain, end);
                if (startAnchor.DistanceTo(endAnchor) <= AnchorMatchTolerance) continue;
                if (candidates.Any(c => c.Start.DistanceTo(startAnchor) <= AnchorMatchTolerance
                        && c.End.DistanceTo(endAnchor) <= AnchorMatchTolerance)) continue;
                candidates.Add(new AnchoredCandidate(startAnchor, endAnchor, angleDeg));
            }
        }
        return candidates;
    }

    /// <summary>시작·종료점이 주어진 인출면 위에 있고 실제 경로의 양 끝이 되도록 생성한다.
    /// 조건을 만족하지 못하면 다른 위치로 옮기지 않고 명시적 사유를 반환한다.</summary>
    public static AnchoredResult GenerateAnchored(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        IReadOnlyList<Vec2> exitChain,
        Vec2 startAnchor,
        Vec2 endAnchor,
        PatternDirection direction)
    {
        if (outer.Count < 3 || p.Spacing <= 0 || exitChain.Count < 2)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (startAnchor.DistanceTo(endAnchor) <= AnchorMatchTolerance)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (DistanceToChain(exitChain, startAnchor) > ChainTolerance
            || DistanceToChain(exitChain, endAnchor) > ChainTolerance)
            return AnchoredResult.Fail(AnchoredFailureReason.AnchorNotOnExitChain);

        foreach (double angleDeg in AngleCandidates(outer, direction))
        {
            foreach (bool startLtr in new[] { true, false })
            {
                (int regionCount, double rad, LocalRoute? route) =
                    BuildAtAngle(outer, holes, p, angleDeg, startLtr, requireEvenRuns: true);
                if (regionCount != 1 || route == null || route.Path.Count < 2) continue;
                Vec2 start = route.Path[0].Rotate(rad);
                Vec2 end = route.Path[^1].Rotate(rad);
                double reach = ChainReach(p);
                if (DistanceToChain(exitChain, start) > reach) continue;
                if (DistanceToChain(exitChain, end) > reach) continue;
                if (!Near(SnapOnChain(exitChain, start), startAnchor)) continue;
                if (!Near(SnapOnChain(exitChain, end), endAnchor)) continue;
                return AnchoredResult.Ok(WithExitPieces(ToWorld(route, rad, angleDeg),
                    startAnchor, endAnchor));
            }
        }

        return AnchoredResult.Fail(ClassifyFailure(outer, holes, p, exitChain, direction, startAnchor, endAnchor));
    }

    /// <summary>제외영역이 있는 영역에서 인출면 양 끝을 고정할 수 있는 후보를 계산한다.
    /// 각 후보는 실제 생성에 그대로 재사용할 수 있는 레인 노드 순서/진입 끝/각도를 담는다.
    /// 후보가 하나도 없으면 분리/복잡도/레인 없음 중 실제 사유를 반환한다.</summary>
    public static ExcludedCandidatesResult ComputeCandidatesComponents(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParams p,
        IReadOnlyList<Vec2> exitChain,
        PatternDirection direction)
    {
        if (outer.Count < 3 || p.Spacing <= 0 || exitChain.Count < 2)
            return new ExcludedCandidatesResult(Array.Empty<ExcludedAnchorCandidate>(),
                SerpentineComponentsFailureReason.InvalidInput);
        ArgumentNullException.ThrowIfNull(exclusions);
        var exclusionList = exclusions.ToList();

        var candidates = new List<ExcludedAnchorCandidate>();
        bool anySplit = false, anyTooComplex = false, anyTooMany = false, anyNoPath = false;
        foreach (double angleDeg in AngleCandidates(outer, direction))
        {
            double rad = angleDeg * Math.PI / 180.0;
            List<Vec2> localOuter = Rotate(outer, -rad);
            List<(double MinX, double MinY, double MaxX, double MaxY)> rotatedExclusions =
                RotateExclusions(exclusionList, -rad);
            List<List<Vec2>> region = RegionOps.SubtractRectangles(
                RegionOps.BuildPatternRegion(localOuter, null, p.Margin), rotatedExclusions);
            if (region.Count == 0) continue;
            int outerRingCount = region.Count(r => r.Count >= 3 && RegionOps.SignedArea(r) > 0);
            if (outerRingCount >= 2) { anySplit = true; continue; }

            List<LaneIntervalNode>[] byLane = BuildLaneNodes(region, p.Spacing, out int nodeCount);
            if (nodeCount == 0) continue;
            if (nodeCount > MaxLaneChainNodes) { anyTooMany = true; continue; }
            if (nodeCount > MaxExactLaneChainNodes) { anyTooComplex = true; continue; }

            List<LaneIntervalNode> nodes = FlattenLaneNodes(byLane);
            List<int>[] adjacency = BuildLaneAdjacency(byLane, nodes.Count);
            ulong mask = (1UL << nodeCount) - 1;
            var solutions = SolveLaneChainDp(nodes, adjacency, mask,
                (node, endpoint) => EndpointLeadCost(nodes[node], endpoint, exitChain, p, rad),
                (node, endpoint) => EndpointLeadCost(nodes[node], endpoint, exitChain, p, rad),
                collectAll: true, requireComplete: true, out var failure);
            if (failure == SerpentineComponentsFailureReason.TooComplex) { anyTooComplex = true; continue; }
            if (solutions.Count == 0) { anyNoPath = true; continue; }

            var laneNodes = nodes.Select(n => new SerpentineLaneNode(n.Lane, n.Y, n.X0, n.X1)).ToList();
            foreach (LaneChainSolution solution in solutions)
            {
                if (solution.Order.Count == 0) continue;
                Vec2 startWorld = PointOnNode(nodes[solution.Order[0]], solution.EnterLow[0] ? 0 : 1).Rotate(rad);
                Vec2 endWorld = PointOnNode(nodes[solution.Order[^1]], solution.EnterLow[^1] ? 1 : 0).Rotate(rad);
                Vec2 startAnchor = SnapOnChain(exitChain, startWorld);
                Vec2 endAnchor = SnapOnChain(exitChain, endWorld);
                if (startAnchor.DistanceTo(endAnchor) <= AnchorMatchTolerance) continue;
                if (candidates.Any(c => c.Start.DistanceTo(startAnchor) <= AnchorMatchTolerance
                        && c.End.DistanceTo(endAnchor) <= AnchorMatchTolerance)) continue;
                candidates.Add(new ExcludedAnchorCandidate(
                    startAnchor, endAnchor, angleDeg, laneNodes,
                    solution.Order.ToList(), solution.EnterLow.ToList(), solution.Cost));
            }
        }

        candidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        if (candidates.Count > 32) candidates.RemoveRange(32, candidates.Count - 32);
        if (candidates.Count > 0) return new ExcludedCandidatesResult(candidates, null);
        if (anySplit) return new ExcludedCandidatesResult(candidates, SerpentineComponentsFailureReason.SplitByExclusion);
        if (anyNoPath) return new ExcludedCandidatesResult(candidates, SerpentineComponentsFailureReason.NoCompletePath);
        if (anyTooMany) return new ExcludedCandidatesResult(candidates, SerpentineComponentsFailureReason.TooManyFragments);
        if (anyTooComplex) return new ExcludedCandidatesResult(candidates, SerpentineComponentsFailureReason.TooComplex);
        return new ExcludedCandidatesResult(candidates, SerpentineComponentsFailureReason.NoLanes);
    }

    /// <summary>후보의 레인 순서/진입 끝을 그대로 사용해 인출면 고정 경로를 만든다.
    /// 후보에 저장된 시작/종료 앵커를 경로 양 끝으로 쓰고, 레인 끝과의 간극은 ExitPiece로 남긴다.</summary>
    public static AnchoredResult GenerateAnchoredComponents(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParams p,
        IReadOnlyList<Vec2> exitChain,
        ExcludedAnchorCandidate candidate,
        PatternDirection direction)
    {
        if (outer.Count < 3 || p.Spacing <= 0 || exitChain.Count < 2 || candidate == null)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (candidate.Nodes == null || candidate.Order == null || candidate.EnterLow == null)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (candidate.Order.Count == 0 || candidate.Order.Count != candidate.EnterLow.Count)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (candidate.Start.DistanceTo(candidate.End) <= AnchorMatchTolerance)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (DistanceToChain(exitChain, candidate.Start) > ChainTolerance
            || DistanceToChain(exitChain, candidate.End) > ChainTolerance)
            return AnchoredResult.Fail(AnchoredFailureReason.AnchorNotOnExitChain);
        if (!AngleCandidates(outer, direction).Any(a => Math.Abs(a - candidate.AngleDeg) <= 1e-9))
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);

        int nodeCount = candidate.Nodes.Count;
        if (nodeCount == 0 || nodeCount > MaxExactLaneChainNodes)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        var seen = new bool[nodeCount];
        foreach (int index in candidate.Order)
        {
            if (index < 0 || index >= nodeCount || seen[index])
                return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
            seen[index] = true;
        }
        if (seen.Any(value => !value))
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);

        var nodes = candidate.Nodes
            .Select(n => new LaneIntervalNode(n.Lane, n.Y, n.X0, n.X1)).ToList();
        List<SerpentineRoutePiece> localPieces = BuildLaneChainPath(nodes, candidate.Order, candidate.EnterLow);
        List<Vec2> localPath = PathFromPieces(localPieces);
        if (localPath.Count < 2) return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);

        double rad = candidate.AngleDeg * Math.PI / 180.0;
        Vec2 firstWorld = localPath[0].Rotate(rad);
        Vec2 lastWorld = localPath[^1].Rotate(rad);
        if (SnapOnChain(exitChain, firstWorld).DistanceTo(candidate.Start) > 1e-3
            || SnapOnChain(exitChain, lastWorld).DistanceTo(candidate.End) > 1e-3)
            return AnchoredResult.Fail(AnchoredFailureReason.AnchorNotAtLaneEnd);

        List<SerpentineRoutePiece> worldPieces = localPieces.Select(piece => piece with
        {
            Points = piece.Points.Select(point => point.Rotate(rad)).ToList(),
        }).ToList();
        List<Vec2> worldPath = localPath.Select(point => point.Rotate(rad)).ToList();
        List<SerpentineSegment> segments = worldPieces
            .Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Select(piece => new SerpentineSegment(piece.LaneIndex, piece.Points[0], piece.Points[1]))
            .ToList();
        var basePattern = new SerpentineResult
        {
            Path = worldPath,
            Segments = segments,
            LaneCount = nodes.Select(node => node.Lane).Distinct().Count(),
            AngleDeg = candidate.AngleDeg,
            TotalLength = worldPieces.Sum(piece => piece.Length),
            RoutePieces = worldPieces,
        };
        return AnchoredResult.Ok(WithExitPieces(basePattern, candidate.Start, candidate.End));
    }

    private static Vec2 PointOnNode(LaneIntervalNode node, int endpoint) =>
        new(endpoint == 0 ? node.X0 : node.X1, node.Y);

    private static double EndpointLeadCost(
        LaneIntervalNode node, int endpoint, IReadOnlyList<Vec2> exitChain, PatternParams p, double rad)
    {
        Vec2 world = PointOnNode(node, endpoint).Rotate(rad);
        double distance = DistanceToChain(exitChain, world);
        return distance <= ChainReach(p) ? distance : double.PositiveInfinity;
    }

    private static List<(double MinX, double MinY, double MaxX, double MaxY)> RotateExclusions(
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions, double rad)
    {
        var rotated = new List<(double MinX, double MinY, double MaxX, double MaxY)>(exclusions.Count);
        foreach (var r in exclusions)
        {
            Vec2[] corners = { new(r.MinX, r.MinY), new(r.MaxX, r.MinY), new(r.MaxX, r.MaxY), new(r.MinX, r.MaxY) };
            Vec2[] q = corners.Select(v => v.Rotate(rad)).ToArray();
            rotated.Add((q.Min(v => v.X), q.Min(v => v.Y), q.Max(v => v.X), q.Max(v => v.Y)));
        }
        return rotated;
    }

    private static List<LaneIntervalNode>[] BuildLaneNodes(
        IReadOnlyList<IReadOnlyList<Vec2>> region, double spacing, out int nodeCount)
    {
        double minY = region.Min(r => r.Min(v => v.Y)), maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int lanes = Math.Max(1, (int)Math.Floor((height + 1e-9) / spacing));
        double y0 = minY + (height - (lanes - 1) * spacing) / 2.0;
        var byLane = new List<LaneIntervalNode>[lanes];
        nodeCount = 0;
        for (int k = 0; k < lanes; k++)
        {
            double y = y0 + k * spacing;
            byLane[k] = new List<LaneIntervalNode>();
            foreach ((double X0, double X1) interval in ScanlineIntervals(region, y))
            {
                byLane[k].Add(new LaneIntervalNode(k, y, interval.X0, interval.X1));
                nodeCount++;
            }
        }
        return byLane;
    }

    private static List<LaneIntervalNode> FlattenLaneNodes(IReadOnlyList<List<LaneIntervalNode>> byLane)
    {
        var nodes = new List<LaneIntervalNode>();
        foreach (List<LaneIntervalNode> lane in byLane) nodes.AddRange(lane);
        return nodes;
    }

    private static List<int>[] BuildLaneAdjacency(IReadOnlyList<List<LaneIntervalNode>> byLane, int nodeCount)
    {
        var adjacency = new List<int>[nodeCount];
        for (int i = 0; i < nodeCount; i++) adjacency[i] = new();
        var starts = new int[byLane.Count];
        int offset = 0;
        for (int k = 0; k < byLane.Count; k++) { starts[k] = offset; offset += byLane[k].Count; }
        for (int k = 0; k + 1 < byLane.Count; k++)
            for (int a = 0; a < byLane[k].Count; a++)
                for (int b = 0; b < byLane[k + 1].Count; b++)
                {
                    int ia = starts[k] + a, ib = starts[k + 1] + b;
                    adjacency[ia].Add(ib);
                    adjacency[ib].Add(ia);
                }
        return adjacency;
    }

    private static List<Vec2> PathFromPieces(IReadOnlyList<SerpentineRoutePiece> pieces)
    {
        var path = new List<Vec2>();
        foreach (SerpentineRoutePiece piece in pieces)
        {
            int start = path.Count > 0 && piece.Points.Count > 0
                && path[^1].DistanceTo(piece.Points[0]) <= 1e-9 ? 1 : 0;
            for (int i = start; i < piece.Points.Count; i++) path.Add(piece.Points[i]);
        }
        return path;
    }

    private const double ChainTolerance = 0.05;
    private const double AnchorMatchTolerance = 1e-4;

    /// <summary>경로 끝점이 인출면 경계에 닿는 것으로 인정하는 최대 거리.
    /// 사행 경로는 마진만큼 안쪽으로 인셋되므로 그 거리까지 허용하고, 경계 위로 투영해 앵커로 쓴다.</summary>
    private static double ChainReach(PatternParams p) => Math.Max(0.0, p.Margin) + ChainTolerance;

    public static Vec2 SnapOnChain(IReadOnlyList<Vec2> chain, Vec2 p)
    {
        Vec2 best = chain[0];
        double bestDistance = double.MaxValue;
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            Vec2 candidate = ClosestOnSegment(chain[i], chain[i + 1], p);
            double distance = candidate.DistanceTo(p);
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        return best;
    }

    private static Vec2 ClosestOnSegment(Vec2 a, Vec2 b, Vec2 p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-18) return a;
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0.0, 1.0);
        return new Vec2(a.X + t * dx, a.Y + t * dy);
    }

    /// <summary>인출면 경계의 앵커와 사행 경로 끝점 사이를 ExitPiece로 연결한다.
    /// 두 점이 이미 같으면 조각을 만들지 않는다.</summary>
    private static SerpentineResult WithExitPieces(SerpentineResult pattern, Vec2 startAnchor, Vec2 endAnchor)
    {
        var path = new List<Vec2>(pattern.Path);
        var pieces = new List<SerpentineRoutePiece>();
        double extra = 0;
        Vec2 first = path[0];
        Vec2 last = path[^1];
        if (startAnchor.DistanceTo(first) > AnchorMatchTolerance)
        {
            pieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.ExitPiece, -1,
                new[] { startAnchor, first }));
            path.Insert(0, startAnchor);
            extra += startAnchor.DistanceTo(first);
        }
        if (endAnchor.DistanceTo(last) > AnchorMatchTolerance)
        {
            path.Add(endAnchor);
        }
        pieces.AddRange(pattern.RoutePieces);
        if (endAnchor.DistanceTo(last) > AnchorMatchTolerance)
        {
            pieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.ExitPiece, -1,
                new[] { last, endAnchor }));
            extra += endAnchor.DistanceTo(last);
        }
        return new SerpentineResult
        {
            Path = path,
            Segments = pattern.Segments,
            LaneCount = pattern.LaneCount,
            NominalLaneCount = pattern.NominalLaneCount,
            AngleDeg = pattern.AngleDeg,
            TotalLength = pattern.TotalLength + extra,
            RoutePieces = pieces,
        };
    }

    private static IEnumerable<double> AngleCandidates(IReadOnlyList<Vec2> outer, PatternDirection direction)
    {
        if (direction == PatternDirection.FrameX) { yield return 0.0; yield break; }
        if (direction == PatternDirection.FrameY) { yield return 90.0; yield break; }
        double auto = AutoAngleDeg(outer);
        yield return auto;
        yield return auto == 0.0 ? 90.0 : 0.0;
    }

    private static (int RegionCount, double Rad, LocalRoute? Route) BuildAtAngle(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        double angleDeg,
        bool startLtr,
        bool requireEvenRuns = false)
    {
        double rad = angleDeg * Math.PI / 180.0;
        List<Vec2> outerLocal = Rotate(outer, -rad);
        List<List<Vec2>>? holesLocal = holes?.Select(h => Rotate(h, -rad)).ToList();
        List<List<Vec2>> region = RegionOps.BuildPatternRegion(outerLocal, holesLocal, p.Margin);
        if (region.Count != 1) return (region.Count, rad, null);
        return (1, rad, BuildLocalRoute(region, p, startLtr, requireEvenRuns));
    }

    private static AnchoredFailureReason ClassifyFailure(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        IReadOnlyList<Vec2> exitChain,
        PatternDirection direction,
        Vec2 startAnchor,
        Vec2 endAnchor)
    {
        bool anyRegion = false, anySplit = false, anyRoute = false, anyChainPair = false;
        bool anyEndpointOnChain = false, parityLike = false, anyParallelChain = false;
        double primaryAngle = AngleCandidates(outer, direction).First();
        bool lanesAlongX = primaryAngle == 0.0;
        double chainDx = exitChain[0].X, chainDy = exitChain[0].Y;
        double chainSpanX = 0, chainSpanY = 0;
        foreach (Vec2 point in exitChain)
        {
            chainSpanX = Math.Max(chainSpanX, Math.Abs(point.X - chainDx));
            chainSpanY = Math.Max(chainSpanY, Math.Abs(point.Y - chainDy));
        }
        // 레인 진행축과 인출면 경계가 나란하면 양 끝이 같은 면에 놓일 수 없다.
        bool chainParallelToLanes = lanesAlongX ? chainSpanX >= chainSpanY : chainSpanY >= chainSpanX;

        foreach (bool startLtr in new[] { true, false })
        {
            {
                (int regionCount, double rad, LocalRoute? route) =
                    BuildAtAngle(outer, holes, p, primaryAngle, startLtr, requireEvenRuns: true);
                if (regionCount == 0) continue;
                anyRegion = true;
                if (regionCount != 1) { anySplit = true; continue; }
                if (route == null || route.Path.Count < 2) continue;
                anyRoute = true;
                Vec2 start = route.Path[0].Rotate(rad);
                Vec2 end = route.Path[^1].Rotate(rad);
                double reach = ChainReach(p);
                bool startOn = DistanceToChain(exitChain, start) <= reach;
                bool endOn = DistanceToChain(exitChain, end) <= reach;
                if (startOn || endOn)
                {
                    anyEndpointOnChain = true;
                    if (chainParallelToLanes) anyParallelChain = true;
                }
                if (startOn && endOn) anyChainPair = true;
                Vec2 projectedStart = SnapOnChain(exitChain, start);
                Vec2 projectedEnd = SnapOnChain(exitChain, end);
                if ((Near(projectedStart, startAnchor) && Near(projectedEnd, endAnchor))
                    || (Near(projectedStart, endAnchor) && Near(projectedEnd, startAnchor)))
                    parityLike = true;
            }
        }

        if (!anyRegion) return AnchoredFailureReason.NoLanes;
        if (!anyRoute) return anySplit ? AnchoredFailureReason.RegionSplit : AnchoredFailureReason.NoLanes;
        if (!anyChainPair)
            return !anyEndpointOnChain || anyParallelChain
                ? AnchoredFailureReason.ExitChainNotSpanning
                : AnchoredFailureReason.ParityConflict;
        if (parityLike) return AnchoredFailureReason.ParityConflict;
        return AnchoredFailureReason.AnchorNotAtLaneEnd;
    }

    private static bool Near(Vec2 a, Vec2 b) => a.DistanceTo(b) <= 1e-3;

    public static double DistanceToChain(IReadOnlyList<Vec2> chain, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < chain.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, chain[i], chain[i + 1]));
        return best;
    }

    /// <summary>수평선 y와 영역(외곽+홀)의 교차 → 짝지은 x 구간 목록 (even-odd).
    /// 반개구간 규칙([y0,y1))으로 꼭짓점 중복 계수를 방지한다.</summary>
    internal static List<(double X0, double X1)> ScanlineIntervals(
        IReadOnlyList<IReadOnlyList<Vec2>> region, double y)
    {
        var xs = new List<double>();
        foreach (List<Vec2> ring in region)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vec2 a = ring[i];
                Vec2 b = ring[(i + 1) % ring.Count];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                {
                    double t = (y - a.Y) / (b.Y - a.Y);
                    xs.Add(a.X + t * (b.X - a.X));
                }
            }
        }

        xs.Sort();
        var result = new List<(double, double)>(xs.Count / 2);
        for (int i = 0; i + 1 < xs.Count; i += 2)
        {
            if (xs[i + 1] - xs[i] > 1e-9)
                result.Add((xs[i], xs[i + 1]));
        }
        return result;
    }

    private static List<Vec2> Rotate(IReadOnlyList<Vec2> pts, double rad)
    {
        var res = new List<Vec2>(pts.Count);
        foreach (Vec2 v in pts) res.Add(v.Rotate(rad));
        return res;
    }

    // ---- 면 내부 구속 커넥터 (모서리·끝단에서 직선이 면 밖으로 이탈하는 문제 방지) ----

    /// <summary>영역 경계를 따라 P→Q로 가는 내부 점들(끝점 제외). 두 경계 호 중 짧은 쪽을 따른다.
    /// P·Q가 서로 다른 링이면 빈 목록(호출부가 직선 연결).</summary>
    private static List<Vec2> RouteAlongBoundary(IReadOnlyList<IReadOnlyList<Vec2>> region, Vec2 p, Vec2 q)
    {
        // P·Q가 모두 경계 위에 있는 링을 찾는다 (외곽 우선 = 면적 큰 순)
        IReadOnlyList<Vec2>? ring = null;
        foreach (var r in region.OrderByDescending(r => Math.Abs(RegionOps.SignedArea(r))))
        {
            if (r.Count < 3) continue;
            if (TryProject(r, p, out _, out _) && TryProject(r, q, out _, out _)) { ring = r; break; }
        }
        var result = new List<Vec2>();
        if (ring == null) return result;

        int n = ring.Count;
        TryProject(ring, p, out int pe, out double tp);
        TryProject(ring, q, out int qe, out double tq);

        var seg = new double[n];
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            seg[i] = ring[i].DistanceTo(ring[(i + 1) % n]);
            cum[i + 1] = cum[i] + seg[i];
        }
        double length = cum[n];
        double sp = cum[pe] + tp * seg[pe];
        double sq = cum[qe] + tq * seg[qe];
        double forward = sq - sp;
        if (forward < 0) forward += length;

        if (pe != qe)
        {
            if (forward <= length - forward)
            {
                // 증가 방향: V[pe+1] … V[qe]
                for (int i = (pe + 1) % n; ; i = (i + 1) % n)
                {
                    result.Add(ring[i]);
                    if (i == qe) break;
                }
            }
            else
            {
                // 감소 방향: V[pe] … V[qe+1]
                for (int i = pe; ; i = (i - 1 + n) % n)
                {
                    result.Add(ring[i]);
                    if (i == (qe + 1) % n) break;
                }
            }
        }
        // 같은 에지 위(pe==qe)면 P→Q 직선이므로 내부 정점 없음

        result.RemoveAll(v => v.DistanceTo(p) <= 1e-9 || v.DistanceTo(q) <= 1e-9);
        return result;
    }

    /// <summary>점을 링 위로 투영 — 가장 가까운 에지 인덱스·파라미터. 경계 밖(1e-3 초과)이면 false.</summary>
    private static bool TryProject(IReadOnlyList<Vec2> ring, Vec2 p, out int edge, out double t)
    {
        int n = ring.Count;
        double best = double.MaxValue;
        edge = 0; t = 0;
        for (int i = 0; i < n; i++)
        {
            Vec2 a = ring[i], b = ring[(i + 1) % n];
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len2 = abx * abx + aby * aby;
            double ti = len2 < 1e-18 ? 0.0
                : Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
            double dx = p.X - (a.X + ti * abx), dy = p.Y - (a.Y + ti * aby);
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < best) { best = d; edge = i; t = ti; }
        }
        return best <= 1e-3;
    }

    internal static double DistanceToRing(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Count]));
        return best;
    }

    internal static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        if (len2 < 1e-18) return p.DistanceTo(a);
        double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return p.DistanceTo(new Vec2(a.X + t * abx, a.Y + t * aby));
    }

    internal static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if (((ring[i].Y > p.Y) != (ring[j].Y > p.Y)) &&
                (p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X))
                inside = !inside;
        }
        return inside;
    }
}

// ---- 인출면 고정 경로 공개 타입 ----

/// <summary>인출면 위에서 고를 수 있는 시작/종료 쌍. 두 점 모두 ExitChain 위에 있다.</summary>
public sealed record AnchoredCandidate(Vec2 Start, Vec2 End, double AngleDeg);

public enum AnchoredFailureReason
{
    InvalidInput,
    NoLanes,
    RegionSplit,
    ExitChainNotSpanning,
    AnchorNotOnExitChain,
    AnchorNotAtLaneEnd,
    ParityConflict,
}

public sealed record AnchoredResult(SerpentineResult? Pattern, AnchoredFailureReason? Failure)
{
    public bool Succeeded => Pattern != null;
    public static AnchoredResult Ok(SerpentineResult pattern) => new(pattern, null);
    public static AnchoredResult Fail(AnchoredFailureReason reason) => new(null, reason);
}

/// <summary>제외영역 경로 후보를 만들 때 쓰는 로컬 프레임 레인 구간.</summary>
public readonly record struct SerpentineLaneNode(int Lane, double Y, double X0, double X1);

/// <summary>제외영역 + 인출면 고정 경로 후보. Nodes/Order/EnterLow는 로컬 프레임 기준이며
/// 생성 시 그대로 재사용한다. Start/End는 작업영역 좌표의 실제 인출면 앵커다.</summary>
public sealed record ExcludedAnchorCandidate(
    Vec2 Start,
    Vec2 End,
    double AngleDeg,
    IReadOnlyList<SerpentineLaneNode> Nodes,
    IReadOnlyList<int> Order,
    IReadOnlyList<bool> EnterLow,
    double Cost);

public sealed record ExcludedCandidatesResult(
    IReadOnlyList<ExcludedAnchorCandidate> Candidates,
    SerpentineComponentsFailureReason? Failure)
{
    public bool Succeeded => Failure == null;
}
