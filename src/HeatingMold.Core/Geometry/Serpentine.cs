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
    /// <summary>Generate independent lane components for a face with rectangular exclusions.
    /// Each scanline interval is emitted separately, so no path bridges an excluded gap.</summary>
    public static IReadOnlyList<IReadOnlyList<Vec2>> GenerateComponents(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParams p)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));
        if (p.Spacing <= 0) throw new ArgumentOutOfRangeException(nameof(p), "Spacing > 0 필요");
        double angle = ResolveAngleDeg(outer, p.Direction), rad = angle * Math.PI / 180.0;
        List<Vec2> local = Rotate(outer, -rad);
        var rotatedExclusions = exclusions.Select(r =>
        {
            // Rectangles are axis-aligned in the supplied face coordinates; rotate their corners
            // and use the enclosing local rectangle for robust scanline clipping.
            Vec2[] corners = { new(r.MinX,r.MinY), new(r.MaxX,r.MinY), new(r.MaxX,r.MaxY), new(r.MinX,r.MaxY) };
            Vec2[] q = corners.Select(v => v.Rotate(-rad)).ToArray();
            return (q.Min(v => v.X), q.Min(v => v.Y), q.Max(v => v.X), q.Max(v => v.Y));
        }).ToList();
        List<List<Vec2>> region = RegionOps.SubtractRectangles(
            RegionOps.BuildPatternRegion(local, null, p.Margin), rotatedExclusions);
        if (region.Count == 0) return Array.Empty<IReadOnlyList<Vec2>>();
        double minY = region.Min(r => r.Min(v => v.Y)), maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int lanes = Math.Max(1, (int)Math.Floor((height + 1e-9) / p.Spacing));
        double y0 = minY + (height - (lanes - 1) * p.Spacing) / 2.0;
        var result = new List<IReadOnlyList<Vec2>>();
        for (int k = 0; k < lanes; k++)
        {
            double y = y0 + k * p.Spacing;
            foreach (var interval in ScanlineIntervals(region, y))
                result.Add(new[] { new Vec2(interval.X0, y).Rotate(rad), new Vec2(interval.X1, y).Rotate(rad) });
        }
        return result;
    }

    public static SerpentineResult Generate(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        int arcPoints = 12,
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

        LocalRoute route = BuildLocalRoute(outerLocal, holesLocal, region, p, constrainConnectors, true, arcPoints);
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
        List<Vec2> outerLocal,
        List<List<Vec2>>? holesLocal,
        List<List<Vec2>> region,
        PatternParams p,
        bool constrainConnectors,
        bool startLtr,
        int arcPoints,
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
        bool prevLtr = true;

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
                    // 레인 간 연결: 반원 U턴이 원본 면(외곽−홀) 안에 머물면 그대로 사용하고,
                    // 면 밖으로 튀어나오면(모서리·끝단의 완만한 경계) 영역 경계를 따라 라우팅한다.
                    List<Vec2> arc = ArcInterior(prevEnd.Value, a, prevLtr ? +1 : -1, arcPoints).ToList();
                    List<Vec2> connector;
                    if (constrainConnectors
                        ? ArcStaysInsideFace(region[0], null, arc, 1e-6)
                        : ArcStaysInsideFace(outerLocal, holesLocal, arc, 1e-6))
                        connector = arc;
                    else
                        connector = RouteAlongBoundary(region, prevEnd.Value, a);
                    pts.AddRange(connector);
                    routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector, traversed, new[] { prevEnd.Value }.Concat(connector).Append(a).ToList()));
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

            prevLtr = ltr;
            traversed++;
        }

        // 같은 면 인출은 경로 양 끝이 같은 쪽 모서리에 있어야 하고, 그 조건은 레인(구간) 수가 짝수일 때 성립한다.
        // 사용자가 간격을 바꾸지 않아도 되도록 시스템이 가능한 최대 짝수 개수로 한 개 줄여 다시 배치한다.
        if (requireEvenRuns && !laneCountOverride.HasValue && traversed % 2 != 0 && laneCount >= 2)
            return BuildLocalRoute(outerLocal, holesLocal, region, p, constrainConnectors, startLtr,
                arcPoints, requireEvenRuns, laneCount - 1, laneCount);

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
                    BuildAtAngle(outer, holes, p, angleDeg, startLtr, 12, requireEvenRuns: true);
                if (regionCount != 1 || route == null || route.Path.Count < 2) continue;
                Vec2 start = route.Path[0].Rotate(rad);
                Vec2 end = route.Path[^1].Rotate(rad);
                double reach = ChainReach(p);
                if (DistanceToChain(exitChain, start) > reach) continue;
                if (DistanceToChain(exitChain, end) > reach) continue;
                // 마진 인셋 때문에 경로 끝점은 인출면 경계보다 margin 만큼 안쪽에 있다.
                // 경계 위로 직교 투영한 점을 앵커로 쓰고, 그 연결 구간은 ExitPiece가 된다.
                Vec2 startAnchor = NearestOnChain(exitChain, start);
                Vec2 endAnchor = NearestOnChain(exitChain, end);
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
        PatternDirection direction,
        int arcPoints = 12)
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
                    BuildAtAngle(outer, holes, p, angleDeg, startLtr, arcPoints, requireEvenRuns: true);
                if (regionCount != 1 || route == null || route.Path.Count < 2) continue;
                Vec2 start = route.Path[0].Rotate(rad);
                Vec2 end = route.Path[^1].Rotate(rad);
                double reach = ChainReach(p);
                if (DistanceToChain(exitChain, start) > reach) continue;
                if (DistanceToChain(exitChain, end) > reach) continue;
                if (!Near(NearestOnChain(exitChain, start), startAnchor)) continue;
                if (!Near(NearestOnChain(exitChain, end), endAnchor)) continue;
                return AnchoredResult.Ok(WithExitPieces(ToWorld(route, rad, angleDeg),
                    startAnchor, endAnchor));
            }
        }

        return AnchoredResult.Fail(ClassifyFailure(outer, holes, p, exitChain, direction, startAnchor, endAnchor));
    }

    private const double ChainTolerance = 0.05;
    private const double AnchorMatchTolerance = 1e-4;

    /// <summary>경로 끝점이 인출면 경계에 닿는 것으로 인정하는 최대 거리.
    /// 사행 경로는 마진만큼 안쪽으로 인셋되므로 그 거리까지 허용하고, 경계 위로 투영해 앵커로 쓴다.</summary>
    private static double ChainReach(PatternParams p) => Math.Max(0.0, p.Margin) + ChainTolerance;

    private static Vec2 NearestOnChain(IReadOnlyList<Vec2> chain, Vec2 p)
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
        int arcPoints,
        bool requireEvenRuns = false)
    {
        double rad = angleDeg * Math.PI / 180.0;
        List<Vec2> outerLocal = Rotate(outer, -rad);
        List<List<Vec2>>? holesLocal = holes?.Select(h => Rotate(h, -rad)).ToList();
        List<List<Vec2>> region = RegionOps.BuildPatternRegion(outerLocal, holesLocal, p.Margin);
        if (region.Count != 1) return (region.Count, rad, null);
        return (1, rad, BuildLocalRoute(outerLocal, holesLocal, region, p, true, startLtr, arcPoints,
            requireEvenRuns));
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
                    BuildAtAngle(outer, holes, p, primaryAngle, startLtr, 12, requireEvenRuns: true);
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
                Vec2 projectedStart = NearestOnChain(exitChain, start);
                Vec2 projectedEnd = NearestOnChain(exitChain, end);
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

    private static double DistanceToChain(IReadOnlyList<Vec2> chain, Vec2 p)
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

    /// <summary>a→b U턴 반원의 내부 점들 (끝점 제외). bulgeSign: +1 → +X 쪽 볼록.</summary>
    private static IEnumerable<Vec2> ArcInterior(Vec2 a, Vec2 b, int bulgeSign, int n)
    {
        Vec2 mid = (a + b) * 0.5;
        double r = a.DistanceTo(b) / 2.0;
        if (r < 1e-9 || n < 2) yield break;

        double thA = Math.Atan2(a.Y - mid.Y, a.X - mid.X);
        double thB = Math.Atan2(b.Y - mid.Y, b.X - mid.X);
        double thM = bulgeSign > 0 ? 0.0 : Math.PI;

        static double Norm(double t) => ((t % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);

        double ccwSweep = Norm(thB - thA);
        bool ccw = Norm(thM - thA) <= ccwSweep;
        double sweep = ccw ? ccwSweep : 2 * Math.PI - ccwSweep;
        double dir = ccw ? +1 : -1;

        for (int i = 1; i < n; i++)
        {
            double t = thA + dir * sweep * i / n;
            yield return new Vec2(mid.X + r * Math.Cos(t), mid.Y + r * Math.Sin(t));
        }
    }

    private static List<Vec2> Rotate(IReadOnlyList<Vec2> pts, double rad)
    {
        var res = new List<Vec2>(pts.Count);
        foreach (Vec2 v in pts) res.Add(v.Rotate(rad));
        return res;
    }

    // ---- 면 내부 구속 커넥터 (모서리·끝단에서 반원이 면 밖으로 이탈하는 문제 방지) ----

    /// <summary>반원 U턴 내부 점들이 원본 면(외곽 − 홀) 안에 머무는지 검사.
    /// 경계 위(tol 이내)는 면 안으로 간주한다.</summary>
    private static bool ArcStaysInsideFace(
        IReadOnlyList<Vec2> outer, IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        IEnumerable<Vec2> arc, double tol)
    {
        foreach (Vec2 p in arc)
        {
            if (!PointInRing(outer, p) && DistanceToRing(outer, p) > tol)
                return false;
            if (holes == null) continue;
            foreach (IReadOnlyList<Vec2> h in holes)
            {
                if (h.Count < 3 || DistanceToRing(h, p) <= tol) continue;
                if (PointInRing(h, p)) return false;
            }
        }
        return true;
    }

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

    private static double DistanceToRing(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Count]));
        return best;
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        if (len2 < 1e-18) return p.DistanceTo(a);
        double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return p.DistanceTo(new Vec2(a.X + t * abx, a.Y + t * aby));
    }

    private static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 p)
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
