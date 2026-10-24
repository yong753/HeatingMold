using HeatingMold.Core.Geometry;
using HeatingMold.Design.Regions;

namespace HeatingMold.Design.Legacy;

/// <summary>레인 1개 위의 직선 분절 (Start→End 순서 = 진행 방향).</summary>
public sealed record HeatingRunSegment(int LaneIndex, Vec2 Start, Vec2 End)
{
    public double Length => Start.DistanceTo(End);
}

public enum HeatingPathSegmentKind { HeatingRunSegment, TurnSegment, FixedJoinSegment }

/// <summary>생성된 발열 경로를 순서대로 구성하는 조각입니다.</summary>
public sealed record HeatingPathSegment(HeatingPathSegmentKind Kind, int LaneIndex, IReadOnlyList<Vec2> Points)
{
    public double Length
    {
        get { double length = 0; for (int i = 1; i < Points.Count; i++) length += Points[i - 1].DistanceTo(Points[i]); return length; }
    }
}

/// <summary>사행 생성 결과 — 단일 연속 폴리라인.</summary>
public sealed class HeatingPath
{
    public required IReadOnlyList<Vec2> Path { get; init; }
    public required IReadOnlyList<HeatingRunSegment> HeatingRuns { get; init; }
    public required int LaneCount { get; init; }
    public required double AngleDeg { get; init; }
    public required double TotalLength { get; init; }
    public IReadOnlyList<HeatingPathSegment> PathSegments { get; init; } = Array.Empty<HeatingPathSegment>();
}

/// <summary>회전 조각의 활성 여부에 따라 경로 성분을 나눕니다. 같은 레인의 고정 연결 조각은 분할하지 않습니다.</summary>
public static class CircuitPathPartitioner
{
    public static IReadOnlyList<IReadOnlyList<Vec2>> Compute(
        IReadOnlyList<HeatingPathSegment> pathSegments, IReadOnlyList<bool>? turnSegmentEnabled = null)
    {
        if (turnSegmentEnabled != null && turnSegmentEnabled.Count != pathSegments.Count(p => p.Kind == HeatingPathSegmentKind.TurnSegment))
            throw new ArgumentException("One enabled state is required per turn segment", nameof(turnSegmentEnabled));
        var components = new List<IReadOnlyList<Vec2>>();
        var current = new List<Vec2>();
        int turn = 0;
        foreach (var piece in pathSegments)
        {
            bool enabled = piece.Kind != HeatingPathSegmentKind.TurnSegment || turnSegmentEnabled == null || turnSegmentEnabled[turn++];
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
/// 영역을 방향축 기준 로컬 투영 좌표로 회전 → 경계 여유/홀 차집합(Clipper2) → 레인별 교차 구간 분절
/// → 지그재그 연결(U턴 호) → 단일 연속 폴리라인.</summary>
public static class HeatingPathGenerator
{
    /// <summary>직사각형 제외 영역을 건너지 않도록 레인별 독립 경로 성분을 생성합니다.</summary>
    public static IReadOnlyList<IReadOnlyList<Vec2>> GenerateComponents(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternParameters p)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));
        if (p.CenterPitchMm <= 0) throw new ArgumentOutOfRangeException(nameof(p), "CenterPitchMm must be positive");
        double angle = AutoAngleDeg(outer), rad = angle * Math.PI / 180.0;
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
            RegionOps.BuildPatternRegion(local, null, p.BoundaryMarginMm), rotatedExclusions);
        if (region.Count == 0) return Array.Empty<IReadOnlyList<Vec2>>();
        double minY = region.Min(r => r.Min(v => v.Y)), maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int lanes = Math.Max(1, (int)Math.Floor((height + 1e-9) / p.CenterPitchMm));
        double y0 = minY + (height - (lanes - 1) * p.CenterPitchMm) / 2.0;
        var result = new List<IReadOnlyList<Vec2>>();
        for (int k = 0; k < lanes; k++)
        {
            double y = y0 + k * p.CenterPitchMm;
            foreach (var interval in ScanlineIntervals(region, y))
                result.Add(new[] { new Vec2(interval.X0, y).Rotate(rad), new Vec2(interval.X1, y).Rotate(rad) });
        }
        return result;
    }

    public static HeatingPath Generate(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParameters p,
        int arcPoints = 12,
        bool requireContinuousPath = false)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));
        if (p.CenterPitchMm <= 0) throw new ArgumentOutOfRangeException(nameof(p), "CenterPitchMm must be positive");

        double angleDeg = AutoAngleDeg(outer);
        double rad = angleDeg * Math.PI / 180.0;

        // 로컬 프레임(방향축 = 로컬 X)으로 회전
        List<Vec2> outerLocal = Rotate(outer, -rad);
        List<List<Vec2>>? holesLocal = holes?.Select(h => Rotate(h, -rad)).ToList();

        // 경계 여유·홀 차집합
        List<List<Vec2>> region = RegionOps.BuildPatternRegion(outerLocal, holesLocal, p.BoundaryMarginMm);
        if (region.Count == 0)
        {
            return new HeatingPath
            {
                Path = Array.Empty<Vec2>(),
                HeatingRuns = Array.Empty<HeatingRunSegment>(),
                LaneCount = 0,
                AngleDeg = angleDeg,
                TotalLength = 0,
            };
        }
        if (requireContinuousPath && region.Count != 1)
            throw new InvalidOperationException("경계 여유 적용 후 발열 영역이 분리되어 단일 연속 발열사 경로를 구성할 수 없습니다");

        // 레인 배치: 로컬 Y 범위에 CenterPitchMm 간격으로 균등 배치
        double minY = region.Min(r => r.Min(v => v.Y));
        double maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int laneCount = Math.Max(1, (int)Math.Floor((height + 1e-9) / p.CenterPitchMm));
        double y0 = minY + (height - (laneCount - 1) * p.CenterPitchMm) / 2.0;

        var heatingRuns = new List<HeatingRunSegment>();
        var pathSegments = new List<HeatingPathSegment>();
        var pts = new List<Vec2>();
        int traversed = 0;
        Vec2? prevEnd = null;
        bool prevLtr = true;

        for (int k = 0; k < laneCount; k++)
        {
            double y = y0 + k * p.CenterPitchMm;
            List<(double X0, double X1)> intervals = ScanlineIntervals(region, y);
            if (intervals.Count == 0) continue;

            bool ltr = traversed % 2 == 0;
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
                    List<Vec2> turnSegment;
                    if (requireContinuousPath
                        ? ArcStaysInsideFace(region[0], null, arc, 1e-6)
                        : ArcStaysInsideFace(outerLocal, holesLocal, arc, 1e-6))
                        turnSegment = arc;
                    else
                        turnSegment = FollowBoundary(region, prevEnd.Value, a);
                    pts.AddRange(turnSegment);
                    pathSegments.Add(new HeatingPathSegment(HeatingPathSegmentKind.TurnSegment, traversed, new[] { prevEnd.Value }.Concat(turnSegment).Append(a).ToList()));
                }
                else if (pts.Count > 0 && prevEnd.HasValue)
                {
                    // A concave connected outline can have multiple intervals on
                    // one lane. Follow its inset boundary, never cut across the
                    // unselected notch between those intervals.
                    List<Vec2> turnSegment = FollowBoundary(region, prevEnd.Value, a);
                    pts.AddRange(turnSegment);
                    pathSegments.Add(new HeatingPathSegment(HeatingPathSegmentKind.FixedJoinSegment, traversed, new[] { prevEnd.Value }.Concat(turnSegment).Append(a).ToList()));
                }
                // 같은 레인 내부 분절(홀)은 직선 연결 — MVP 허용 (홀 자동회피 제외 항목)

                pts.Add(a);
                pts.Add(b);
                heatingRuns.Add(new HeatingRunSegment(traversed, a, b));
                pathSegments.Add(new HeatingPathSegment(HeatingPathSegmentKind.HeatingRunSegment, traversed, new[] { a, b }));
                prevEnd = b;
                firstOnLane = false;
            }

            prevLtr = ltr;
            traversed++;
        }

        // 원래 프레임으로 복원
        List<Vec2> pathWorld = Rotate(pts, +rad);
        List<HeatingRunSegment> heatingRunsWorld = heatingRuns
            .Select(s => new HeatingRunSegment(s.LaneIndex, s.Start.Rotate(rad), s.End.Rotate(rad)))
            .ToList();
        List<HeatingPathSegment> piecesWorld = pathSegments.Select(piece => piece with
        { Points = piece.Points.Select(point => point.Rotate(rad)).ToList() }).ToList();

        double total = 0;
        for (int i = 1; i < pathWorld.Count; i++) total += pathWorld[i].DistanceTo(pathWorld[i - 1]);

        return new HeatingPath
        {
            Path = pathWorld,
            HeatingRuns = heatingRunsWorld,
            LaneCount = traversed,
            AngleDeg = angleDeg,
            TotalLength = total,
            PathSegments = piecesWorld,
        };
    }

    /// <summary>UV 외곽 bbox 장축 → 사행 방향각 (0 또는 90).</summary>
    public static double AutoAngleDeg(IReadOnlyList<Vec2> outer)
    {
        double w = outer.Max(v => v.X) - outer.Min(v => v.X);
        double h = outer.Max(v => v.Y) - outer.Min(v => v.Y);
        return w >= h ? 0.0 : 90.0;
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
    private static List<Vec2> FollowBoundary(IReadOnlyList<IReadOnlyList<Vec2>> region, Vec2 p, Vec2 q)
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
