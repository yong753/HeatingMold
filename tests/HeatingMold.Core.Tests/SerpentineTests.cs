using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class SerpentineTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    // T1: 직사각형 + 마진 — 레인 수·길이·간격 일치
    [Fact]
    public void T1_Rectangle_Margin_LaneCount_Length_Spacing()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(0.0, r.AngleDeg, 9);
        Assert.Equal(5, r.LaneCount);
        Assert.Equal(5, r.Segments.Count);

        for (int i = 0; i < r.Segments.Count; i++)
        {
            SerpentineSegment s = r.Segments[i];
            Assert.Equal(10.0 + 10.0 * i, s.Start.Y, 9);   // 레인 y = 10,20,30,40,50
            if (i > 0) Assert.Equal(10.0, s.Start.Y - r.Segments[i - 1].Start.Y, 9);
            Assert.Equal(90.0, s.Length, 9);               // 마진 5 → 100 − 2×5
        }
    }

    // T2: 홀 포함 폴리곤 — 레인 분절 (MVP 허용)
    [Fact]
    public void T2_HoleSplitsLaneIntoSegments()
    {
        var outer = Rect(0, 0, 100, 60);
        var holes = new List<IReadOnlyList<Vec2>> { Rect(40, 18, 60, 22) };
        var r = Serpentine.Generate(outer, holes, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(5, r.LaneCount);
        Assert.Equal(6, r.Segments.Count);   // 레인 5개 중 y=20 하나가 홀에 의해 2분절

        var split = r.Segments.Where(s => Math.Abs(s.Start.Y - 20.0) < 1e-9).ToList();
        Assert.Equal(2, split.Count);

        var spans = split
            .Select(s => (Min: Math.Min(s.Start.X, s.End.X), Max: Math.Max(s.Start.X, s.End.X)))
            .OrderBy(t => t.Min)
            .ToArray();
        Assert.Equal(5.0, spans[0].Min, 9);    // 영역 좌측 (마진 오프셋)
        Assert.Equal(35.0, spans[0].Max, 9);   // 홀 x=40 − 마진 5
        Assert.Equal(65.0, spans[1].Min, 9);   // 홀 x=60 + 마진 5
        Assert.Equal(95.0, spans[1].Max, 9);   // 영역 우측
    }

    // T3: 홀수 레인 U턴 반전 — 단일 연속 와이어
    [Fact]
    public void T3_OddLaneCount_AlternatesDirection_SingleContinuousPath()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(5, r.LaneCount);   // 홀수 레인

        // 진행 방향 교대: 레인 0 LTR, 1 RTL, 2 LTR, ...
        for (int i = 0; i < r.Segments.Count; i++)
        {
            SerpentineSegment s = r.Segments[i];
            bool ltr = s.Start.X < s.End.X;
            Assert.Equal(i % 2 == 0, ltr);
        }

        // 단일 연속 폴리라인: 세그먼트들이 경로에 순서대로 포함
        Assert.Equal(r.Segments.First().Start, r.Path[0]);
        Assert.Equal(r.Segments.Last().End, r.Path[^1]);

        int idx = 0;
        foreach (SerpentineSegment s in r.Segments)
        {
            while (idx < r.Path.Count && r.Path[idx] != s.Start) idx++;
            Assert.True(idx < r.Path.Count, $"세그먼트 시작점 누락 (lane {s.LaneIndex})");
            Assert.Equal(s.End, r.Path[idx + 1]);
            idx += 2;
        }
    }

    // T4: 방향축 회전 — UV 장축 자동 산출 + 회전 불변성
    [Fact]
    public void T4_AutoLongAxisRotation()
    {
        var wide = Serpentine.Generate(Rect(0, 0, 100, 60), null, new PatternParams { Margin = 5, Spacing = 10 });
        var tall = Serpentine.Generate(Rect(0, 0, 60, 100), null, new PatternParams { Margin = 5, Spacing = 10 });

        Assert.Equal(0.0, wide.AngleDeg, 9);    // 장축 = X
        Assert.Equal(90.0, tall.AngleDeg, 9);   // 장축 = Y

        // 동일 영역(90×50)으로 정규화 → 레인 수·총 길이 동일
        Assert.Equal(wide.LaneCount, tall.LaneCount);
        Assert.Equal(wide.TotalLength, tall.TotalLength, 6);

        // tall의 레인은 월드 Y축 방향
        SerpentineSegment s0 = tall.Segments[0];
        Assert.True(Math.Abs(s0.End.Y - s0.Start.Y) > 80.0);
        Assert.True(Math.Abs(s0.End.X - s0.Start.X) < 1e-6);
    }

    // T5: 총 길이 sanity — 레인 합 + U턴 반원 길이
    [Fact]
    public void T5_TotalLengthSanity()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 5, Spacing = 10 });

        double straight = 5 * 90.0;           // 레인 5개 × 길이 90
        double turns = 4 * Math.PI * 5.0;     // U턴 반원 4개 (r = Spacing/2 = 5)

        Assert.True(r.TotalLength > straight);
        Assert.InRange(r.TotalLength, straight + turns * 0.98, straight + turns * 1.02);
    }

    // T6: 비볼록(쐐기) 면 — 인접 레인 극단점이 크게 어긋나도 커넥터가 면 밖으로 이탈하지 않아야 함 (#597 회귀)
    [Fact]
    public void T6_NonConvexWedge_ConnectorStaysInsideFace()
    {
        // 상단이 완만하게 기울어진 쐐기 — 레인 극단점이 레인당 ~130mm씩 이동
        var outer = new List<Vec2> { new(0, 0), new(400, 0), new(400, 120), new(0, 90) };
        double margin = 5, spacing = 10;
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = margin, Spacing = spacing });

        // 트리거 존재 확인: 인접 레인 끝↔시작 직선거리가 간격보다 훨씬 큼
        double maxJump = 0;
        for (int i = 0; i + 1 < r.Segments.Count; i++)
        {
            if (r.Segments[i].LaneIndex == r.Segments[i + 1].LaneIndex) continue;
            maxJump = Math.Max(maxJump, r.Segments[i].End.DistanceTo(r.Segments[i + 1].Start));
        }
        Assert.True(maxJump > 3 * spacing, $"트리거 없음 (maxJump={maxJump:F1})");

        foreach (Vec2 p in r.Path)
            Assert.True(InsideOrOn(outer, p), $"면 밖 경로점 {p}");
    }

    // T7: 볼록 직사각형이라도 margin < Spacing/2 이면 반원이 면 밖으로 나가므로 경계 라우팅되어야 함
    [Fact]
    public void T7_ConvexButTightMargin_StaysInsideFace()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Margin = 2, Spacing = 10 });

        foreach (Vec2 p in r.Path)
            Assert.True(InsideOrOn(outer, p), $"면 밖 경로점 {p}");
    }

    private static bool InsideOrOn(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        for (int i = 0; i < ring.Count; i++)
            if (DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Count]) <= 1e-6) return true;

        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if (((ring[i].Y > p.Y) != (ring[j].Y > p.Y)) &&
                (p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X))
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, len2 = abx * abx + aby * aby;
        if (len2 < 1e-18) return p.DistanceTo(a);
        double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return p.DistanceTo(new Vec2(a.X + t * abx, a.Y + t * aby));
    }
}
