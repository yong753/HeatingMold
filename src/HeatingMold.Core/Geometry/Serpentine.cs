using HeatingMold.Core.Model;

namespace HeatingMold.Core.Geometry;

/// <summary>레인 1개 위의 직선 분절 (Start→End 순서 = 진행 방향).</summary>
public sealed record SerpentineSegment(int LaneIndex, Vec2 Start, Vec2 End)
{
    public double Length => Start.DistanceTo(End);
}

/// <summary>사행 생성 결과 — 단일 연속 폴리라인.</summary>
public sealed class SerpentineResult
{
    public required IReadOnlyList<Vec2> Path { get; init; }
    public required IReadOnlyList<SerpentineSegment> Segments { get; init; }
    public required int LaneCount { get; init; }
    public required double AngleDeg { get; init; }
    public required double TotalLength { get; init; }
}

/// <summary>스캔라인 기반 사행(serpentine) 발열 패턴 생성기.
/// 영역을 방향축 기준 로컬 프레임으로 회전 → 마진/홀 차집합(Clipper2) → 레인별 교차 구간 분절
/// → 지그재그 연결(U턴 호) → 단일 연속 폴리라인.</summary>
public static class Serpentine
{
    public static SerpentineResult Generate(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        PatternParams p,
        int arcPoints = 12)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));
        if (p.Spacing <= 0) throw new ArgumentOutOfRangeException(nameof(p), "Spacing > 0 필요");

        double angleDeg = AutoAngleDeg(outer);
        double rad = angleDeg * Math.PI / 180.0;

        // 로컬 프레임(방향축 = 로컬 X)으로 회전
        List<Vec2> outerLocal = Rotate(outer, -rad);
        List<List<Vec2>>? holesLocal = holes?.Select(h => Rotate(h, -rad)).ToList();

        // 마진·홀 차집합
        List<List<Vec2>> region = RegionOps.BuildPatternRegion(outerLocal, holesLocal, p.Margin);
        if (region.Count == 0)
        {
            return new SerpentineResult
            {
                Path = Array.Empty<Vec2>(),
                Segments = Array.Empty<SerpentineSegment>(),
                LaneCount = 0,
                AngleDeg = angleDeg,
                TotalLength = 0,
            };
        }

        // 레인 배치: 로컬 Y 범위에 Spacing 간격으로 균등 배치
        double minY = region.Min(r => r.Min(v => v.Y));
        double maxY = region.Max(r => r.Max(v => v.Y));
        double height = maxY - minY;
        int laneCount = Math.Max(1, (int)Math.Floor((height + 1e-9) / p.Spacing));
        double y0 = minY + (height - (laneCount - 1) * p.Spacing) / 2.0;

        var segments = new List<SerpentineSegment>();
        var pts = new List<Vec2>();
        int traversed = 0;
        Vec2? prevEnd = null;
        bool prevLtr = true;

        for (int k = 0; k < laneCount; k++)
        {
            double y = y0 + k * p.Spacing;
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
                    if (ArcStaysInsideFace(outerLocal, holesLocal, arc, 1e-6))
                        pts.AddRange(arc);
                    else
                        pts.AddRange(RouteAlongBoundary(region, prevEnd.Value, a));
                }
                // 같은 레인 내부 분절(홀)은 직선 연결 — MVP 허용 (홀 자동회피 제외 항목)

                pts.Add(a);
                pts.Add(b);
                segments.Add(new SerpentineSegment(traversed, a, b));
                prevEnd = b;
                firstOnLane = false;
            }

            prevLtr = ltr;
            traversed++;
        }

        // 원래 프레임으로 복원
        List<Vec2> pathWorld = Rotate(pts, +rad);
        List<SerpentineSegment> segmentsWorld = segments
            .Select(s => new SerpentineSegment(s.LaneIndex, s.Start.Rotate(rad), s.End.Rotate(rad)))
            .ToList();

        double total = 0;
        for (int i = 1; i < pathWorld.Count; i++) total += pathWorld[i].DistanceTo(pathWorld[i - 1]);

        return new SerpentineResult
        {
            Path = pathWorld,
            Segments = segmentsWorld,
            LaneCount = traversed,
            AngleDeg = angleDeg,
            TotalLength = total,
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
