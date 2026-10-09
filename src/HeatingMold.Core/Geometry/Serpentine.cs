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

        double angleDeg = p.AngleDeg ?? AutoAngleDeg(outer);
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
                    // 레인 간 연결: U턴 호 (진행 방향 쪽으로 볼록)
                    pts.AddRange(ArcInterior(prevEnd.Value, a, prevLtr ? +1 : -1, arcPoints));
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
}
