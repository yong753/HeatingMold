using System.Numerics;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Geometry;

/// <summary>레인 1개 위의 직선 분절 (Start→End 순서 = 진행 방향).</summary>
public sealed record SerpentineSegment(int LaneIndex, Vec2 Start, Vec2 End)
{
    public double Length => Start.DistanceTo(End);
}

public enum SerpentineRoutePieceKind { HeatingRun, TurnConnector, FixedConnector }

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
    /// <summary>시작 인출점. 인출면이 고정된 생성 결과에만 존재한다.</summary>
    public CircuitExitPoint? StartExit { get; init; }
    /// <summary>종료 인출점. 인출면이 고정된 생성 결과에만 존재한다.</summary>
    public CircuitExitPoint? EndExit { get; init; }
    /// <summary>Identity of the selected route candidate, preserved through circuit conversions.</summary>
    public string? CandidateId { get; init; }
}

/// <summary>스캔라인 기반 사행(serpentine) 발열 패턴 생성기.
/// 영역을 방향축 기준 로컬 프레임으로 회전 → 마진/홀 차집합(Clipper2) → 레인별 교차 구간 분절
/// → 지그재그 연결(U턴 호) → 단일 연속 폴리라인.</summary>
public static class Serpentine
{
    private readonly record struct LaneIntervalNode(int Lane, double Y, double X0, double X1);

    /// <summary>제외영역을 반영한 발열 경로 조각. 발열선은 발열영역(외곽−제외) 안에서만 생성되고,
    /// 턴커넥터는 이전 발열선 끝점에서 다음 발열선 시작점까지의 최단 직선이다.
    /// 제외영역이 영역을 완전히 가로질러 발열영역이 분리되면 경로를 만들지 않고 실패를 반환한다.</summary>
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

    /// <summary>수동 종료점이 시작점의 어느 쪽에 있는지로 레인 진행 방향을 정한다.
    /// 로컬 프레임에서 레인은 항상 로컬 X 방향이고 레인 인덱스는 로컬 Y로 증가하므로,
    /// 종료점의 로컬 Y 부호가 곧 진행 방향이다(가로: 아래/위, 세로: 오른쪽/왼쪽).</summary>
    private static bool TryResolveSweepDirection(
        Vec2 startLocal,
        Vec2 endLocal,
        out int stepSign,
        out AnchoredFailureReason failure)
    {
        failure = AnchoredFailureReason.InvalidInput;
        double delta = endLocal.Y - startLocal.Y;
        // 회전 변환의 부동소수 오차를 고려해 같은 위치로 판정한다.
        if (Math.Abs(delta) <= 1e-9)
        {
            // 가로 배치에서 두 점의 높이가 같거나, 세로 배치에서 두 점의 열이 같으면
            // 사행이 위/아래(또는 좌/우) 어느 쪽으로도 진행할 수 있다.
            // 영역 크기나 간격으로 방향을 추측하지 않고 명시적으로 실패한다.
            stepSign = 0;
            failure = AnchoredFailureReason.IndeterminateDirection;
            return false;
        }
        stepSign = delta > 0 ? 1 : -1;
        return true;
    }

    /// <summary>선택한 시작점을 첫 레인의 기준으로 삼아 사행을 만든다.
    /// 첫 발열선은 시작점에서 배치 축과 평행하게 출발하고, 각 레인은 이미 마진이 적용된
    /// region 경계에서 멈춘다. 시작점 반대편으로 자동 왕복하지 않는다.
    /// 마지막 레인이 종료 인출점의 반대편에서 끝나면 같은 간격으로 레인을 정확히 한 줄만
    /// 더 만들고, 그 발열선 끝에서 종료점으로 돌아가는 연결선은 상위 단계가 붙인다.
    /// 추가 레인 위치에 발열구간이 없으면 일부만 만들지 않고 명시적으로 실패한다.</summary>
    private static bool TryBuildAnchoredLocalRoute(
        List<List<Vec2>> region,
        IReadOnlyList<Vec2> outerLocal,
        PatternParams p,
        Vec2 startLocal,
        Vec2 endLocal,
        int stepSign,
        out LocalRoute? route,
        out AnchoredFailureReason failure)
    {
        route = null;
        failure = AnchoredFailureReason.InvalidInput;
        if (region.Count == 0 || p.Spacing <= 0) return false;

        double minY = region.Min(r => r.Min(v => v.Y));
        double maxY = region.Max(r => r.Max(v => v.Y));
        // 순회 범위는 두 선택점 사이로 제한한다. 종료점이 영역 안쪽이면
        // 경로 폭/높이도 그 범위 안에서만 생성된다.
        double limitY = stepSign > 0 ? Math.Min(endLocal.Y, maxY) : Math.Max(endLocal.Y, minY);
        List<(double X0, double X1)> firstIntervals = ScanlineIntervals(region, startLocal.Y);
        bool syntheticFirstLane = false;
        if (firstIntervals.Count == 0)
        {
            // 시작점은 외곽 경계 위에 있으므로 마진 밴드 밖에 있을 수 있다.
            // 그 경우 원본 외곽 스캔라인에서 진행 방향 반대쪽 끝만 마진만큼 당겨 첫 레인을 만든다.
            // 시작점 자체는 옮기지 않으므로 첫 발열선은 여전히 선택점에서 축과 평행하게 출발한다.
            List<(double X0, double X1)> outerIntervals = ScanlineIntervals(new[] { outerLocal }, startLocal.Y);
            if (outerIntervals.Count == 0)
            {
                failure = AnchoredFailureReason.NoLanes;
                return false;
            }
            int outerChosen = -1;
            double outerDistance = double.PositiveInfinity;
            bool outerLow = true;
            for (int i = 0; i < outerIntervals.Count; i++)
            {
                (double x0, double x1) = outerIntervals[i];
                double low = Math.Abs(startLocal.X - x0);
                double high = Math.Abs(startLocal.X - x1);
                double distance = Math.Min(low, high);
                if (distance + 1e-7 < outerDistance)
                {
                    outerDistance = distance;
                    outerChosen = i;
                    outerLow = low <= high;
                }
            }
            if (outerChosen < 0)
            {
                failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
                return false;
            }
            (double ox0, double ox1) = outerIntervals[outerChosen];
            // 진행 방향 반대쪽 끝에서만 마진을 적용한다. 시작점 쪽은 선택점 그대로다.
            double lowEnd = outerLow ? startLocal.X : ox0 + p.Margin;
            double highEnd = outerLow ? ox1 - p.Margin : startLocal.X;
            if (highEnd - lowEnd <= 1e-9)
            {
                failure = AnchoredFailureReason.NoLanes;
                return false;
            }
            firstIntervals = new() { (lowEnd, highEnd) };
            syntheticFirstLane = true;
        }

        int chosen = -1;
        double chosenDistance = double.PositiveInfinity;
        bool startLow = true;
        for (int i = 0; i < firstIntervals.Count; i++)
        {
            (double x0, double x1) = firstIntervals[i];
            double low = Math.Abs(startLocal.X - x0);
            double high = Math.Abs(startLocal.X - x1);
            double distance = Math.Min(low, high);
            if (distance + 1e-7 < chosenDistance)
            {
                chosenDistance = distance;
                chosen = i;
                startLow = low <= high;
            }
        }
        if (chosen < 0)
        {
            failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
            return false;
        }

        (double chosenX0, double chosenX1) = firstIntervals[chosen];
        double outside = startLow ? chosenX0 - startLocal.X : startLocal.X - chosenX1;
        if (outside < -1e-6)
        {
            // 시작점이 인셋 구간 안쪽에 있어 첫 발열선의 시작 끝을 결정할 수 없다.
            failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
            return false;
        }

        var segments = new List<SerpentineSegment>();
        var routePieces = new List<SerpentineRoutePiece>();
        var pts = new List<Vec2>();
        int traversed = 0;
        Vec2? prevEnd = null;
        Vec2? lastLaneEntry = null;
        double lastLaneY = startLocal.Y;

        // 레인 위치는 실제 배치 범위에서 자연히 끝난다. 진행 거리가 유한하고 간격이
        // 0보다 크므로 별도의 고정 레인 개수 상한을 두지 않는다. 부동소수 정밀도 때문에
        // 더 이상 진행할 수 없을 때만 명시적으로 실패한다(부분 경로를 성공으로 반환하지 않는다).
        double y = startLocal.Y;
        for (int step = 0; ; step++)
        {
            if (stepSign > 0 ? y > limitY + 1e-9 : y < limitY - 1e-9) break;
            if (y < minY - 1e-9 || y > maxY + 1e-9) break;

            List<(double X0, double X1)> intervals = step == 0 && syntheticFirstLane
                ? firstIntervals
                : ScanlineIntervals(region, y);

            if (intervals.Count > 0)
            {
                lastLaneY = y;
                bool ltr = (traversed % 2 == 0) == startLow;

                if (step == 0)
                {
                    // 시작점이 속한 구간이 첫 레인의 첫 구간이어야 첫 발열선이 시작점에서 출발한다.
                    (double firstX0, double firstX1) = intervals[0];
                    if (Math.Abs(firstX0 - chosenX0) > 1e-9 || Math.Abs(firstX1 - chosenX1) > 1e-9)
                    {
                        failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
                        return false;
                    }
                }

                bool firstOnLane = true;
                // 스캔라인 구간은 이미 X0 증가 순서이며 서로 겹치지 않는다. 오른쪽→왼쪽은
                // 역순으로 읽으면 기존 X1 내림차순 정렬 결과와 같다.
                for (int intervalIndex = 0; intervalIndex < intervals.Count; intervalIndex++)
                {
                    (double x0, double x1) = intervals[ltr ? intervalIndex : intervals.Count - 1 - intervalIndex];
                    Vec2 a = ltr ? new Vec2(x0, y) : new Vec2(x1, y);
                    Vec2 b = ltr ? new Vec2(x1, y) : new Vec2(x0, y);
                    if (step == 0 && firstOnLane)
                        a = new Vec2(startLocal.X, y); // 첫 발열선의 시작점 고정
                    if (firstOnLane) lastLaneEntry = a;

                    if (pts.Count > 0 && firstOnLane && prevEnd.HasValue)
                    {
                        routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector, traversed,
                            new[] { prevEnd.Value, a }));
                    }
                    else if (pts.Count > 0 && prevEnd.HasValue)
                    {
                        List<Vec2> connector = RouteAlongBoundary(region, prevEnd.Value, a);
                        pts.AddRange(connector);
                        routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector, traversed,
                            new[] { prevEnd.Value }.Concat(connector).Append(a).ToList()));
                    }

                    pts.Add(a);
                    pts.Add(b);
                    segments.Add(new SerpentineSegment(traversed, a, b));
                    routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, traversed, new[] { a, b }));
                    prevEnd = b;
                    firstOnLane = false;
                }

                traversed++;
            }

            double nextY = y + stepSign * p.Spacing;
            if (nextY == y) { failure = AnchoredFailureReason.NoLanes; return false; }
            y = nextY;
        }

        if (routePieces.Count == 0)
        {
            failure = AnchoredFailureReason.NoLanes;
            return false;
        }

        // 마지막 레인이 종료 인출점의 반대편에서 끝났으면 간격을 유지한 채 다음 레인을
        // 정확히 한 줄만 더 만든다. 이 레인은 선택 종료점보다 더 진행할 수 있지만,
        // 해당 위치에 발열구간이 하나도 없으면 실패한다.
        if (prevEnd.HasValue && lastLaneEntry.HasValue &&
            prevEnd.Value.DistanceTo(endLocal) > lastLaneEntry.Value.DistanceTo(endLocal))
        {
            double extraY = lastLaneY + stepSign * p.Spacing;
            if (extraY == lastLaneY)
            {
                failure = AnchoredFailureReason.NoLanes;
                return false;
            }
            if (!TryAppendAnchoredLane(region, extraY, traversed, startLow,
                    ref prevEnd, pts, segments, routePieces, out failure))
                return false;
            traversed++;
        }

        // RequestedLaneCount=0 → 레인 수를 짝수로 줄이지 않았음을 의미한다.
        route = new LocalRoute(pts, segments, routePieces, traversed, 0);
        failure = AnchoredFailureReason.InvalidInput;
        return true;
    }

    /// <summary>
    /// 두 선택점이 같은 진행 행에 있는 단일 레인 영역(예: 분할로 생긴 좁은 영역)을 직선
    /// 발열선 하나로 만든다. 시작점은 그 행 구간의 한쪽 끝에, 종료점은 같은 행의 반대쪽
    /// 발열선 끝 너머에 있어야 한다. 사행 방향이 없으므로 추가 레인을 만들지 않으며,
    /// 종료점으로 돌아가는 연결선은 상위 단계가 붙인다.
    /// </summary>
    private static bool TryBuildSingleLaneLocalRoute(
        List<List<Vec2>> region,
        IReadOnlyList<Vec2> outerLocal,
        PatternParams p,
        Vec2 startLocal,
        Vec2 endLocal,
        out LocalRoute? route,
        out AnchoredFailureReason failure)
    {
        route = null;
        failure = AnchoredFailureReason.IndeterminateDirection;
        if (Math.Abs(endLocal.Y - startLocal.Y) > 1e-6) return false;

        double minY = region.Min(r => r.Min(v => v.Y));
        double maxY = region.Max(r => r.Max(v => v.Y));
        double y = startLocal.Y;
        if (y < minY - 1e-9 || y > maxY + 1e-9)
        {
            failure = AnchoredFailureReason.NoLanes;
            return false;
        }

        List<(double X0, double X1)> intervals = ScanlineIntervals(region, y);
        if (intervals.Count == 0)
        {
            // 시작점이 마진 밴드 밖이면 기존 사행과 같은 synthetic 첫 레인 규칙을 쓴다.
            List<(double X0, double X1)> outerIntervals = ScanlineIntervals(new[] { outerLocal }, y);
            if (outerIntervals.Count == 0)
            {
                failure = AnchoredFailureReason.NoLanes;
                return false;
            }
            int outerChosen = -1;
            double outerDistance = double.PositiveInfinity;
            bool outerLow = true;
            for (int i = 0; i < outerIntervals.Count; i++)
            {
                (double x0, double x1) = outerIntervals[i];
                double low = Math.Abs(startLocal.X - x0);
                double high = Math.Abs(startLocal.X - x1);
                double distance = Math.Min(low, high);
                if (distance + 1e-7 < outerDistance)
                {
                    outerDistance = distance;
                    outerChosen = i;
                    outerLow = low <= high;
                }
            }
            if (outerChosen < 0)
            {
                failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
                return false;
            }
            (double ox0, double ox1) = outerIntervals[outerChosen];
            double lowEnd = outerLow ? startLocal.X : ox0 + p.Margin;
            double highEnd = outerLow ? ox1 - p.Margin : startLocal.X;
            if (highEnd - lowEnd <= 1e-9)
            {
                failure = AnchoredFailureReason.NoLanes;
                return false;
            }
            intervals = new() { (lowEnd, highEnd) };
        }

        int chosen = -1;
        double chosenDistance = double.PositiveInfinity;
        bool startLow = true;
        for (int i = 0; i < intervals.Count; i++)
        {
            (double x0, double x1) = intervals[i];
            double low = Math.Abs(startLocal.X - x0);
            double high = Math.Abs(startLocal.X - x1);
            double distance = Math.Min(low, high);
            if (distance + 1e-7 < chosenDistance)
            {
                chosenDistance = distance;
                chosen = i;
                startLow = low <= high;
            }
        }
        if (chosen < 0)
        {
            failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
            return false;
        }

        (double chosenX0, double chosenX1) = intervals[chosen];
        double outside = startLow ? chosenX0 - startLocal.X : startLocal.X - chosenX1;
        if (outside < -1e-6)
        {
            failure = AnchoredFailureReason.AnchorNotAtLaneEnd;
            return false;
        }

        // 종료점은 같은 행에서 발열선 끝 너머, 시작점의 반대쪽에 있어야 한다. 그렇지
        // 않으면 발열선 위를 되짚는 연결선이 생기므로 방향 미정으로 실패한다.
        if (startLow ? endLocal.X < chosenX1 - 1e-6 : endLocal.X > chosenX0 + 1e-6)
            return false;

        Vec2 a = new(startLocal.X, y);
        Vec2 b = startLow ? new Vec2(chosenX1, y) : new Vec2(chosenX0, y);
        var pts = new List<Vec2> { a, b };
        var segments = new List<SerpentineSegment> { new(0, a, b) };
        var pieces = new List<SerpentineRoutePiece>
        {
            new(SerpentineRoutePieceKind.HeatingRun, 0, new[] { a, b })
        };
        route = new LocalRoute(pts, segments, pieces, 1, 0);
        failure = AnchoredFailureReason.InvalidInput;
        return true;
    }

    /// <summary>종료점 연결을 위한 마지막 레인 한 줄을 추가한다. 해당 위치에 발열구간이
    /// 하나라도 있으면 길이와 무관하게 그 구간을 모두 사용하고, 없으면 명시적으로 실패한다.</summary>
    private static bool TryAppendAnchoredLane(
        List<List<Vec2>> region,
        double y,
        int laneIndex,
        bool startLow,
        ref Vec2? prevEnd,
        List<Vec2> pts,
        List<SerpentineSegment> segments,
        List<SerpentineRoutePiece> routePieces,
        out AnchoredFailureReason failure)
    {
        failure = AnchoredFailureReason.InvalidInput;
        List<(double X0, double X1)> intervals = ScanlineIntervals(region, y);
        if (intervals.Count == 0)
        {
            failure = AnchoredFailureReason.RequiredExtraLaneOutsideRegion;
            return false;
        }

        bool ltr = (laneIndex % 2 == 0) == startLow;

        bool firstOnLane = true;
        for (int intervalIndex = 0; intervalIndex < intervals.Count; intervalIndex++)
        {
            (double x0, double x1) = intervals[ltr ? intervalIndex : intervals.Count - 1 - intervalIndex];
            Vec2 a = ltr ? new Vec2(x0, y) : new Vec2(x1, y);
            Vec2 b = ltr ? new Vec2(x1, y) : new Vec2(x0, y);

            if (prevEnd.HasValue)
            {
                if (firstOnLane)
                {
                    routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector,
                        laneIndex, new[] { prevEnd.Value, a }));
                }
                else
                {
                    List<Vec2> connector = RouteAlongBoundary(region, prevEnd.Value, a);
                    pts.AddRange(connector);
                    routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector,
                        laneIndex, new[] { prevEnd.Value }.Concat(connector).Append(a).ToList()));
                }
            }

            pts.Add(a);
            pts.Add(b);
            segments.Add(new SerpentineSegment(laneIndex, a, b));
            routePieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun,
                laneIndex, new[] { a, b }));
            prevEnd = b;
            firstOnLane = false;
        }

        return true;
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
    private static bool IsFinite(Vec2 point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    /// <summary>사용자가 직접 고른 시작점에서 사행 전체를 생성한다.
    /// 첫 발열선은 시작점에서 배치 축과 평행하게 출발하고, 레인은 수동 종료점 쪽으로 진행하며,
    /// 반대편은 마진이 적용된 영역 경계에서 멈춘다. 마지막 레인이 종료점의 반대편에서 끝나면
    /// 같은 간격으로 한 줄만 더 만든 뒤, 마지막 발열선의 정상 끝에서 종료점으로 돌아가는
    /// 별도 연결선을 붙인다. 발열선 자체는 축과 평행하게 유지된다.</summary>
    public static AnchoredResult GenerateWithSelectedExits(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        PatternDirection direction,
        ExitBoundaryPoint start,
        ExitBoundaryPoint end,
        IExitBoundary2D? boundary = null)
    {
        if (outer.Count < 3 || p.Spacing <= 0) return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (!IsFinite(start.Point) || !IsFinite(end.Point)
            || start.Point.DistanceTo(end.Point) <= AnchorMatchTolerance)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (!TryValidateSelectedExit(boundary, start, out ExitBoundaryPoint savedStart, out var failure)
            || !TryValidateSelectedExit(boundary, end, out ExitBoundaryPoint savedEnd, out failure))
            return AnchoredResult.Fail(AnchoredFailureReason.ExitBoundaryConflict, failure);

        double angleDeg = ResolveAngleDeg(outer, direction);
        double rad = angleDeg * Math.PI / 180.0;
        List<Vec2> outerLocal = Rotate(outer, -rad);
        List<List<Vec2>>? holesLocal = holes?.Select(h => Rotate(h, -rad)).ToList();
        List<List<Vec2>> region = RegionOps.BuildPatternRegion(outerLocal, holesLocal, p.Margin);
        if (region.Count == 0) return AnchoredResult.Fail(AnchoredFailureReason.NoLanes);
        if (region.Count != 1) return AnchoredResult.Fail(AnchoredFailureReason.RegionSplit);

        Vec2 startLocal = savedStart.Point.Rotate(-rad);
        Vec2 endLocal = savedEnd.Point.Rotate(-rad);
        LocalRoute? route;
        AnchoredFailureReason routeFailure;
        if (TryResolveSweepDirection(startLocal, endLocal,
                out int stepSign, out AnchoredFailureReason directionFailure))
        {
            if (!TryBuildAnchoredLocalRoute(region, outerLocal, p, startLocal, endLocal, stepSign,
                    out route, out routeFailure))
                return AnchoredResult.Fail(routeFailure);
        }
        // 두 선택점이 같은 진행 행에 있는 단일 레인 영역(분할로 생긴 좁은 영역)은 사행
        // 방향이 없으므로 직선 한 레인으로 생성한다. 시작·종료가 같은 행의 서로 반대쪽
        // 끝에 있을 때만 허용하고, 그 외에는 기존처럼 명시적으로 실패한다.
        else if (directionFailure == AnchoredFailureReason.IndeterminateDirection)
        {
            if (!TryBuildSingleLaneLocalRoute(region, outerLocal, p, startLocal, endLocal,
                    out route, out routeFailure))
                return AnchoredResult.Fail(routeFailure);
        }
        else
        {
            return AnchoredResult.Fail(directionFailure);
        }
        if (route == null || route.Pieces.Count == 0) return AnchoredResult.Fail(AnchoredFailureReason.NoLanes);

        SerpentineResult world = ToWorld(route, rad, angleDeg);
        return FinalizeSelectedExits(world, savedStart, savedEnd, boundary);
    }

    public static AnchoredResult GenerateComponentsWithSelectedExits(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParams p,
        PatternDirection direction,
        ExitBoundaryPoint start,
        ExitBoundaryPoint end,
        IExitBoundary2D? boundary = null)
    {
        if (outer.Count < 3 || p.Spacing <= 0) return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (!IsFinite(start.Point) || !IsFinite(end.Point)
            || start.Point.DistanceTo(end.Point) <= AnchorMatchTolerance)
            return AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        if (!TryValidateSelectedExit(boundary, start, out ExitBoundaryPoint savedStart, out var failure)
            || !TryValidateSelectedExit(boundary, end, out ExitBoundaryPoint savedEnd, out failure))
            return AnchoredResult.Fail(AnchoredFailureReason.ExitBoundaryConflict, failure);
        ArgumentNullException.ThrowIfNull(exclusions);
        var exclusionList = exclusions.ToList();

        double angleDeg = ResolveAngleDeg(outer, direction);
        double rad = angleDeg * Math.PI / 180.0;
        List<Vec2> localOuter = Rotate(outer, -rad);
        List<(double MinX, double MinY, double MaxX, double MaxY)> rotatedExclusions =
            RotateExclusions(exclusionList, -rad);
        List<List<Vec2>> region = RegionOps.BuildGenerationRegion(
            localOuter, rotatedExclusions, p.Margin, out List<List<Vec2>> cadRegion);
        if (region.Count == 0) return AnchoredResult.Fail(AnchoredFailureReason.NoLanes);

        Vec2 startLocal = savedStart.Point.Rotate(-rad);
        Vec2 endLocal = savedEnd.Point.Rotate(-rad);
        if (!TryResolveSweepDirection(startLocal, endLocal,
                out int stepSign, out AnchoredFailureReason directionFailure))
            return AnchoredResult.Fail(directionFailure);

        SerpentineResult world;
        if (rotatedExclusions.Count == 0)
        {
            if (!TryBuildAnchoredLocalRoute(region, localOuter, p, startLocal, endLocal, stepSign,
                    out LocalRoute? route, out AnchoredFailureReason routeFailure))
                return AnchoredResult.Fail(routeFailure);
            if (route == null || route.Pieces.Count == 0) return AnchoredResult.Fail(AnchoredFailureReason.NoLanes);
            world = ToWorld(route, rad, angleDeg);
        }
        else
        {
            // 제외영역이 있어도 공통 순차 생성 규칙을 쓴다. 발열선만 마진 확장
            // 제외영역을 피하고, 레인·분할 구간을 잇는 연결선은 제외영역을 관통할 수
            // 있으므로 모든 구간을 한 번씩 방문하는 전역 순서 탐색이 필요하지 않다.
            // 원래 CAD 경계(오목/홀) 때문에 갈린 구간만 기존 경계 추종 연결을 유지한다.
            // cadRegion은 위의 영역 생성에서 같은 마진 오프셋 결과를 그대로 받는다.
            if (!TryBuildExcludedSequentialPieces(region, cadRegion, rotatedExclusions, p,
                    startLocal, endLocal, stepSign,
                    out List<SerpentineRoutePiece> pieces, out AnchoredFailureReason routeFailure))
                return AnchoredResult.Fail(routeFailure);

            List<SerpentineRoutePiece> worldPieces = pieces.Select(piece => piece with
            {
                Points = piece.Points.Select(point => point.Rotate(rad)).ToList(),
            }).ToList();
            int laneCount = pieces.Select(piece => piece.LaneIndex).Distinct().Count();
            world = BuildPatternFromPieces(worldPieces, angleDeg, laneCount);
        }

        return FinalizeSelectedExits(world, savedStart, savedEnd, boundary);
    }

    /// <summary>제외영역이 있는 영역의 레인 구간을 진행 순서대로 한 번씩 이어 단일 사슬을 만든다.
    /// 각 레인은 이전 끝점에 가까운 쪽으로 진입하며, 마지막 레인도 방향을 뒤집지 않는다.
    /// 마지막 레인이 종료점의 반대편에서 끝나면 같은 간격으로 한 줄만 더 만들고, 그 위치에
    /// 발열구간이 없으면 실패한다. 레인 사이 연결선과 소프트웨어 제외영역이 만든 같은 레인
    /// 틈은 직선이며, 그 직선이 마진 확장 제외영역을 관통하는 것은 허용한다. 발열선만 제외영역을 피한다.</summary>
    private static bool TryBuildExcludedSequentialPieces(
        IReadOnlyList<IReadOnlyList<Vec2>> region,
        IReadOnlyList<IReadOnlyList<Vec2>> cadRegion,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParams p,
        Vec2 startLocal,
        Vec2 endLocal,
        int stepSign,
        out List<SerpentineRoutePiece> pieces,
        out AnchoredFailureReason failure)
    {
        pieces = new();
        failure = AnchoredFailureReason.InvalidInput;
        if (region.Count == 0 || p.Spacing <= 0) return false;

        double minY = region.Min(r => r.Min(v => v.Y));
        double maxY = region.Max(r => r.Max(v => v.Y));
        double limitY = stepSign > 0 ? Math.Min(endLocal.Y, maxY) : Math.Max(endLocal.Y, minY);

        // 시작점에서 종료점 방향으로만 레인을 전개한다. 간격이 유한하고 0보다 크므로
        // 별도의 레인 개수 상한 없이 실제 영역 범위에서 자연히 종료된다.
        var lanes = new List<LaneScan>();
        double y = startLocal.Y;
        while (true)
        {
            if (stepSign > 0 ? y > limitY + 1e-9 : y < limitY - 1e-9) break;
            if (y < minY - 1e-9 || y > maxY + 1e-9) break;
            lanes.Add(new LaneScan(y,
                ScanlineIntervals(region, y),
                ScanlineIntervals(cadRegion, y)));
            double next = y + stepSign * p.Spacing;
            if (next == y) { failure = AnchoredFailureReason.NoLanes; return false; }
            y = next;
        }
        if (lanes.Count == 0) { failure = AnchoredFailureReason.NoLanes; return false; }

        int firstPopulated = -1;
        for (int i = 0; i < lanes.Count; i++)
            if (lanes[i].Intervals.Count > 0) { firstPopulated = i; break; }
        if (firstPopulated < 0) { failure = AnchoredFailureReason.NoLanes; return false; }

        bool ltr = true;
        if (firstPopulated == 0)
        {
            // 선택한 시작점은 첫 레인의 실제 발열구간 끝(진행 방향의 첫 구간)이어야 한다.
            LaneScan first = lanes[0];
            int chosen = -1;
            bool chosenLow = true;
            double chosenDistance = double.PositiveInfinity;
            for (int i = 0; i < first.Intervals.Count; i++)
            {
                (double x0, double x1) = first.Intervals[i];
                double low = Math.Abs(startLocal.X - x0);
                double high = Math.Abs(startLocal.X - x1);
                double distance = Math.Min(low, high);
                if (distance + 1e-7 < chosenDistance)
                {
                    chosenDistance = distance;
                    chosen = i;
                    chosenLow = low <= high;
                }
            }
            if (chosen < 0) { failure = AnchoredFailureReason.AnchorNotAtLaneEnd; return false; }
            (double cx0, double cx1) = first.Intervals[chosen];
            double outside = chosenLow ? cx0 - startLocal.X : startLocal.X - cx1;
            if (outside < -1e-6) { failure = AnchoredFailureReason.AnchorNotAtLaneEnd; return false; }
            if (first.Intervals.Count > 1)
            {
                double extreme = chosenLow
                    ? first.Intervals.Min(i => i.X0)
                    : first.Intervals.Max(i => i.X1);
                double anchorEnd = chosenLow ? cx0 : cx1;
                if (Math.Abs(extreme - anchorEnd) > 1e-9)
                { failure = AnchoredFailureReason.AnchorNotAtLaneEnd; return false; }
            }
            ltr = chosenLow;
        }
        else
        {
            // 시작점이 마진 밴드 안이라 시작 레인에 발열구간이 없다. 가짜 레인을 만들지 않고
            // 시작점에서 첫 허용 발열구간까지를 연결선 하나로 잇는다.
            LaneScan first = lanes[firstPopulated];
            double leftEntry = first.Intervals.Min(i => i.X0);
            double rightEntry = first.Intervals.Max(i => i.X1);
            ltr = Math.Abs(startLocal.X - leftEntry) <= Math.Abs(startLocal.X - rightEntry);
        }

        var built = new List<SerpentineRoutePiece>();
        Vec2? previousEnd = null;
        int previousCad = -1;
        int traversed = 0;
        bool firstLane = true;
        Vec2? lastLaneEntry = null;
        double lastLaneY = startLocal.Y;

        void EmitLane(LaneScan lane, bool laneLtr)
        {
            lastLaneY = lane.Y;
            // 스캔라인 구간은 이미 X0 증가 순서이며 서로 겹치지 않는다. 왼쪽→오른쪽은
            // 그대로, 오른쪽→왼쪽은 역순으로 읽으면 기존 정렬 결과와 같다.
            bool firstOnLane = true;
            for (int intervalIndex = 0; intervalIndex < lane.Intervals.Count; intervalIndex++)
            {
                (double x0, double x1) = lane.Intervals[laneLtr ? intervalIndex : lane.Intervals.Count - 1 - intervalIndex];
                Vec2 a = laneLtr ? new Vec2(x0, lane.Y) : new Vec2(x1, lane.Y);
                Vec2 b = laneLtr ? new Vec2(x1, lane.Y) : new Vec2(x0, lane.Y);
                if (firstOnLane) lastLaneEntry = a;
                int cad = FindContainingInterval(lane.CadIntervals, x0, x1);

                if (previousEnd.HasValue)
                {
                    if (firstOnLane)
                    {
                        // 레인 사이 연결선: 두 끝점 직선. 제외영역을 관통해도 된다.
                        built.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector,
                            traversed, new[] { previousEnd.Value, a }));
                    }
                    else if (cad >= 0 && cad == previousCad)
                    {
                        // 소프트웨어 제외영역이 같은 레인을 갈라놓은 틈: 직선 연결선.
                        built.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector,
                            traversed, new[] { previousEnd.Value, a }));
                    }
                    else
                    {
                        // 원래 CAD 경계(오목/홀) 때문에 갈린 구간: 기존 경계 추종 연결 유지.
                        List<Vec2> connector = RouteAlongBoundary(cadRegion, previousEnd.Value, a);
                        built.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector,
                            traversed, new[] { previousEnd.Value }.Concat(connector).Append(a).ToList()));
                    }
                }

                built.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun,
                    traversed, new[] { a, b }));
                previousEnd = b;
                previousCad = cad;
                firstOnLane = false;
            }

            traversed++;
            firstLane = false;
        }

        for (int laneIndex = firstPopulated; laneIndex < lanes.Count; laneIndex++)
        {
            LaneScan lane = lanes[laneIndex];
            if (lane.Intervals.Count == 0) continue;

            // 중간 레인과 마지막 레인 모두 이전 끝점에서 가까운 쪽으로 진입한다.
            // 종료점에 맞추기 위해 마지막 레인만 방향을 뒤집지 않는다.
            if (!firstLane && previousEnd.HasValue)
                ltr = ChooseEntryLow(lane, previousEnd.Value);

            EmitLane(lane, ltr);
        }

        if (built.Count == 0) { failure = AnchoredFailureReason.NoLanes; return false; }

        // 마지막 레인이 종료 인출점의 반대편에서 끝났으면 간격을 유지한 채 다음 레인을
        // 정확히 한 줄만 더 만든다. 이 레인은 선택 종료점보다 더 진행할 수 있지만,
        // 해당 위치에 발열구간이 하나도 없으면 실패한다.
        if (previousEnd.HasValue && lastLaneEntry.HasValue &&
            previousEnd.Value.DistanceTo(endLocal) > lastLaneEntry.Value.DistanceTo(endLocal))
        {
            double extraY = lastLaneY + stepSign * p.Spacing;
            if (extraY == lastLaneY) { failure = AnchoredFailureReason.NoLanes; return false; }
            LaneScan extra = new(extraY,
                ScanlineIntervals(region, extraY),
                ScanlineIntervals(cadRegion, extraY));
            if (extra.Intervals.Count == 0)
            {
                failure = AnchoredFailureReason.RequiredExtraLaneOutsideRegion;
                return false;
            }
            EmitLane(extra, ChooseEntryLow(extra, previousEnd.Value));
        }

        Vec2 firstRunStart = built[0].Points[0];
        if (firstRunStart.DistanceTo(startLocal) > 1e-9)
        {
            // 시작 인출점에서 첫 발열구간까지의 리드. 시작 레인 밖(마진 밴드)이거나
            // 리드가 마진 확장 제외영역을 지나면 발열선으로 만들지 않고 연결선으로 둔다.
            bool outsideFirstLane = firstPopulated > 0;
            if (outsideFirstLane ||
                SegmentCrossesExpandedExclusions(startLocal, firstRunStart, exclusions, p.Margin))
            {
                built.Insert(0, new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector,
                    0, new[] { startLocal, firstRunStart }));
            }
            else
            {
                List<Vec2> points = built[0].Points.ToList();
                points[0] = new Vec2(startLocal.X, points[0].Y);
                built[0] = built[0] with { Points = points };
            }
        }

        // 종료 인출점까지의 리드는 제외영역 통과 여부와 무관하게 상위 단계가 항상
        // 별도 연결선으로 붙인다. 여기서는 발열선을 종료점으로 늘리지 않는다.

        pieces = built;
        failure = AnchoredFailureReason.InvalidInput;
        return true;
    }

    private sealed record LaneScan(
        double Y,
        List<(double X0, double X1)> Intervals,
        List<(double X0, double X1)> CadIntervals);

    /// <summary>다음 레인의 두 진입 끝 중 이전 끝점에 가까운 쪽을 고른다. 동률이면 왼쪽→오른쪽.</summary>
    private static bool ChooseEntryLow(LaneScan lane, Vec2 from)
    {
        double left = Math.Abs(from.X - lane.Intervals.Min(i => i.X0));
        double right = Math.Abs(from.X - lane.Intervals.Max(i => i.X1));
        return left <= right;
    }

    private static int FindContainingInterval(
        IReadOnlyList<(double X0, double X1)> intervals, double x0, double x1)
    {
        for (int i = 0; i < intervals.Count; i++)
            if (x0 >= intervals[i].X0 - 1e-7 && x1 <= intervals[i].X1 + 1e-7) return i;
        return -1;
    }

    /// <summary>선분이 마진만큼 확장된 제외 사각형과 교차하는지. 발열선이 아니라
    /// 인출 리드를 발열선으로 만들지 판정하는 데만 쓴다.</summary>
    private static bool SegmentCrossesExpandedExclusions(Vec2 a, Vec2 b,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions, double margin)
    {
        foreach (var r in exclusions)
            if (SegmentIntersectsBox(a, b, r.MinX - margin, r.MinY - margin,
                    r.MaxX + margin, r.MaxY + margin)) return true;
        return false;
    }

    private static bool SegmentIntersectsBox(Vec2 a, Vec2 b,
        double minX, double minY, double maxX, double maxY)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t0 = 0.0, t1 = 1.0;
        Span<double> p = stackalloc double[4] { -dx, dx, -dy, dy };
        Span<double> q = stackalloc double[4] { a.X - minX, maxX - a.X, a.Y - minY, maxY - a.Y };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) <= 1e-15)
            {
                if (q[i] < 0) return false;
                continue;
            }
            double r = q[i] / p[i];
            if (p[i] < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
        }
        return true;
    }

    private static bool TryValidateSelectedExit(IExitBoundary2D? boundary, ExitBoundaryPoint point,
        out ExitBoundaryPoint saved, out ExitBoundaryFailureReason? failure)
    {
        saved = point;
        failure = null;
        if (boundary == null) return true;
        ExitBoundaryResolution result = ExitBoundaryConstraint.EvaluateSaved(boundary, point);
        if (!result.Succeeded)
        {
            failure = result.Failure;
            return false;
        }
        saved = result.Point!.Value;
        return true;
    }

    /// <summary>생성된 마지막 발열선을 그대로 두고, 그 끝에서 선택 종료점까지의 리드를
    /// 별도 연결선으로 붙인다. 발열선 끝이 이미 종료점과 같으면 연결선을 만들지 않는다.</summary>
    private static AnchoredResult FinalizeSelectedExits(SerpentineResult world, ExitBoundaryPoint start,
        ExitBoundaryPoint end, IExitBoundary2D? boundary)
    {
        if (world.RoutePieces.Count == 0) return AnchoredResult.Fail(AnchoredFailureReason.NoLanes);
        List<SerpentineRoutePiece> pieces = world.RoutePieces.ToList();
        Vec2 lastPoint = pieces[^1].Points[^1];
        if (lastPoint.DistanceTo(end.Point) > ExitBoundaryConstraint.Tolerance)
        {
            pieces.Add(new SerpentineRoutePiece(SerpentineRoutePieceKind.FixedConnector,
                pieces[^1].LaneIndex, new[] { lastPoint, end.Point }));
        }
        if (!CircuitRouteBuilder.TryTrimToExit(pieces, start, end,
                world.AngleDeg, world.LaneCount, world.NominalLaneCount,
                out CircuitRoute? trimmed, out _))
            return boundary != null
                ? AnchoredResult.Fail(AnchoredFailureReason.ExitBoundaryConflict,
                    ExitBoundaryFailureReason.GeometryFailure)
                : AnchoredResult.Fail(AnchoredFailureReason.InvalidInput);
        return AnchoredResult.Ok(trimmed!.ToResult());
    }

    private static Vec2 PointOnNode(LaneIntervalNode node, int endpoint) =>
        new(endpoint == 0 ? node.X0 : node.X1, node.Y);

    private static double EndpointLeadCost(
        LaneIntervalNode node, int endpoint, IReadOnlyList<Vec2> exitChain, PatternParams p, double rad,
        IExitBoundary2D? boundary, out ExitBoundaryFailureReason? failure)
    {
        Vec2 world = PointOnNode(node, endpoint).Rotate(rad);
        if (!TryBoundaryDistance(world, exitChain, boundary, ChainReach(p), out double distance, out failure))
            return double.PositiveInfinity;
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

    /// <summary>사행 경로 양 끝의 바깥 연장선과 인출 경계의 유일한 교차점을 계산한다.
    /// 교차점이 없거나 모호하면 후보를 거부한다.</summary>
    private static bool TryBoundaryDistance(Vec2 point, IReadOnlyList<Vec2> exitChain,
        IExitBoundary2D? boundary, double projectionTolerance, out double distance,
        out ExitBoundaryFailureReason? failure)
    {
        failure = null;
        if (boundary == null)
        {
            distance = DistanceToChain(exitChain, point);
            return true;
        }
        // 근접 스냅 반경은 호출자가 별도로 잡는 거리 기준(reach)을 따른다.
        // 최소 정밀도 공차를 그대로 쓰면 마진만큼 떨어진 레인 끝점이 무조건 Miss가 된다.
        ExitBoundaryResolution projection = ExitBoundaryConstraint.RequireProvenance(
            boundary.Project(point, projectionTolerance));
        if (!projection.Succeeded)
        {
            distance = double.PositiveInfinity;
            failure = projection.Failure;
            return false;
        }
        distance = point.DistanceTo(projection.Point!.Value.Point);
        return true;
    }

    private static SerpentineResult BuildPatternFromPieces(
        IReadOnlyList<SerpentineRoutePiece> pieces, double angleDeg, int laneCount) => new()
    {
        Path = CircuitRouteGeometry.PathFromPieces(pieces),
        Segments = CircuitRouteGeometry.SegmentsFromPieces(pieces),
        LaneCount = laneCount,
        AngleDeg = angleDeg,
        TotalLength = CircuitRouteGeometry.TotalLength(pieces),
        RoutePieces = pieces,
    };

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

public enum AnchoredFailureReason
{
    InvalidInput,
    NoLanes,
    RegionSplit,
    ExitChainNotSpanning,
    AnchorNotOnExitChain,
    AnchorNotAtLaneEnd,
    ParityConflict,
    ExitBoundaryConflict,
    NoCompletePath,
    IndeterminateDirection,
    /// <summary>종료 인출점 반대편에서 끝난 마지막 레인 뒤에 같은 간격으로 한 줄을 더
    /// 만들어야 하지만, 그 위치에 발열구간이 하나도 없다.</summary>
    RequiredExtraLaneOutsideRegion,
}

public sealed record AnchoredResult(SerpentineResult? Pattern, AnchoredFailureReason? Failure)
{
    public bool Succeeded => Pattern != null;
    public ExitBoundaryFailureReason? BoundaryFailure { get; init; }
    public static AnchoredResult Ok(SerpentineResult pattern) => new(pattern, null);
    public static AnchoredResult Fail(AnchoredFailureReason reason, ExitBoundaryFailureReason? boundaryFailure = null) =>
        new(null, reason) { BoundaryFailure = boundaryFailure };
}
