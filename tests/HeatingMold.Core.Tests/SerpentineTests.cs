using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class SerpentineTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    // T1: 직사각형 - 레인 수·길이·간격 일치 (배치 마진 없음)
    [Fact]
    public void T1_Rectangle_LaneCount_Length_Spacing()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Spacing = 10 });

        Assert.Equal(0.0, r.AngleDeg, 9);
        Assert.Equal(6, r.LaneCount);
        Assert.Equal(6, r.Segments.Count);

        for (int i = 0; i < r.Segments.Count; i++)
        {
            SerpentineSegment s = r.Segments[i];
            Assert.Equal(5.0 + 10.0 * i, s.Start.Y, 9);   // 영역 높이 60에 10 간격으로 균등 배치
            if (i > 0) Assert.Equal(10.0, s.Start.Y - r.Segments[i - 1].Start.Y, 9);
            Assert.Equal(100.0, s.Length, 9);              // 마진이 없으므로 영역 폭 전체
        }
    }

    // T2: ?� ?�함 ?�리�????�인 분절 (MVP ?�용)
    [Fact]
    public void T2_HoleSplitsLaneIntoSegments()
    {
        var outer = Rect(0, 0, 100, 60);
        var holes = new List<IReadOnlyList<Vec2>> { Rect(40, 13, 60, 17) };
        var r = Serpentine.Generate(outer, holes, new PatternParams { Spacing = 10 });

        Assert.Equal(6, r.LaneCount);
        Assert.Equal(7, r.Segments.Count);   // 레인 6개 중 y=15 하나가 구멍 때문에 2분절

        var split = r.Segments.Where(s => Math.Abs(s.Start.Y - 15.0) < 1e-9).ToList();
        Assert.Equal(2, split.Count);

        var spans = split
            .Select(s => (Min: Math.Min(s.Start.X, s.End.X), Max: Math.Max(s.Start.X, s.End.X)))
            .OrderBy(t => t.Min)
            .ToArray();
        Assert.Equal(0.0, spans[0].Min, 9);    // 영역 좌측 경계
        Assert.Equal(40.0, spans[0].Max, 9);   // 구멍 좌측 x=40 (마진 확장 없음)
        Assert.Equal(60.0, spans[1].Min, 9);   // 구멍 우측 x=60
        Assert.Equal(100.0, spans[1].Max, 9);  // 영역 우측 경계
    }

    // T3: ?�???�인 U??반전 ???�일 ?�속 ?�?�어
    [Fact]
    public void T3_AlternatesDirection_SingleContinuousPath()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Spacing = 10 });

        Assert.Equal(6, r.LaneCount);   // 레인 6개

        // 진행 방향 교�?: ?�인 0 LTR, 1 RTL, 2 LTR, ...
        for (int i = 0; i < r.Segments.Count; i++)
        {
            SerpentineSegment s = r.Segments[i];
            bool ltr = s.Start.X < s.End.X;
            Assert.Equal(i % 2 == 0, ltr);
        }

        // ?�일 ?�속 ?�리?�인: ?�그먼트?�이 경로???�서?��??�함
        Assert.Equal(r.Segments.First().Start, r.Path[0]);
        Assert.Equal(r.Segments.Last().End, r.Path[^1]);

        int idx = 0;
        foreach (SerpentineSegment s in r.Segments)
        {
            while (idx < r.Path.Count && r.Path[idx] != s.Start) idx++;
            Assert.True(idx < r.Path.Count, $"?�그먼트 ?�작???�락 (lane {s.LaneIndex})");
            Assert.Equal(s.End, r.Path[idx + 1]);
            idx += 2;
        }
    }

    // T4: 방향�??�전 ??UV ?�축 ?�동 ?�출 + ?�전 불�???
    [Fact]
    public void T4_AutoLongAxisRotation()
    {
        var wide = Serpentine.Generate(Rect(0, 0, 100, 60), null, new PatternParams { Spacing = 10 });
        var tall = Serpentine.Generate(Rect(0, 0, 60, 100), null, new PatternParams { Spacing = 10 });

        Assert.Equal(0.0, wide.AngleDeg, 9);    // ?�축 = X
        Assert.Equal(90.0, tall.AngleDeg, 9);   // ?�축 = Y

        // ?�일 ?�역(90×50)?�로 ?�규?????�인 ?�·총 길이 ?�일
        Assert.Equal(wide.LaneCount, tall.LaneCount);
        Assert.Equal(wide.TotalLength, tall.TotalLength, 6);

        // tall???�인?� ?�드 Y�?방향
        SerpentineSegment s0 = tall.Segments[0];
        Assert.True(Math.Abs(s0.End.Y - s0.Start.Y) > 80.0);
        Assert.True(Math.Abs(s0.End.X - s0.Start.X) < 1e-6);
    }

    // T5: �?길이 sanity ???�인 ??+ U??반원 길이
    [Fact]
    public void T5_TotalLengthSanity()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Spacing = 10 });

        double straight = 6 * 100.0;           // 레인 6개 × 길이 100
        double turns = 5 * 10.0;     // 레인 사이 직선 턴 5개

        Assert.True(r.TotalLength >= straight);
        Assert.InRange(r.TotalLength, straight + turns * 0.98, straight + turns * 1.02);
    }

    // T5b: Turn connector is a straight 2-point segment (no arc interior points).
    [Fact]
    public void TurnConnector_IsStraightTwoPointSegment()
    {
        var r = Serpentine.Generate(Rect(0, 0, 100, 60), null,
            new PatternParams { Spacing = 10 });

        var turns = r.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
        Assert.Equal(5, turns.Count);
        Assert.All(turns, t => Assert.Equal(2, t.Points.Count));
        Assert.All(turns, t => Assert.Equal(10.0, t.Points[0].DistanceTo(t.Points[1]), 6));
    }

    // T6: 비볼�??�기) �????�접 ?�인 극단?�이 ?�게 ?�긋?�도 커넥?��? �?밖으�??�탈?��? ?�아????(#597 ?��?)
    [Fact]
    public void T6_NonConvexWedge_ConnectorStaysInsideFace()
    {
        // ?�단???�만?�게 기울?�진 ?�기 ???�인 극단?�이 ?�인??~130mm???�동
        var outer = new List<Vec2> { new(0, 0), new(400, 0), new(400, 120), new(0, 90) };
        double spacing = 10;
        var r = Serpentine.Generate(outer, null, new PatternParams { Spacing = spacing });

        // ?�리�?존재 ?�인: ?�접 ?�인 ?�↔?�작 직선거리가 간격보다 ?�씬 ??
        double maxJump = 0;
        for (int i = 0; i + 1 < r.Segments.Count; i++)
        {
            if (r.Segments[i].LaneIndex == r.Segments[i + 1].LaneIndex) continue;
            maxJump = Math.Max(maxJump, r.Segments[i].End.DistanceTo(r.Segments[i + 1].Start));
        }
        Assert.True(maxJump > 3 * spacing, $"?�리�??�음 (maxJump={maxJump:F1})");

        foreach (Vec2 p in r.Path)
            Assert.True(InsideOrOn(outer, p), $"�?�?경로??{p}");
    }

    // T7: 볼록 직사각형?�라??margin < Spacing/2 ?�면 반원??�?밖으�??��?므�?경계 ?�우?�되?�야 ??
    [Fact]
    public void T7_ConvexButTightMargin_StaysInsideFace()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null, new PatternParams { Spacing = 10 });

        foreach (Vec2 p in r.Path)
            Assert.True(InsideOrOn(outer, p), $"�?�?경로??{p}");
    }

    [Fact]
    public void RoutePieces_ReconstructPathAndClassifyConnectors()
    {
        var result = Serpentine.Generate(Rect(0, 0, 100, 60),
            new List<IReadOnlyList<Vec2>> { Rect(40, 13, 60, 17) },
            new PatternParams { Spacing = 10 });
        Assert.Equal(result.LaneCount - 1, result.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Single(result.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        var rebuilt = result.RoutePieces.SelectMany((p, i) => i == 0 ? p.Points : p.Points.Skip(1)).ToList();
        Assert.Equal(result.Path, rebuilt);
        Assert.Equal(result.TotalLength, result.RoutePieces.Sum(p => p.Length), 8);
    }

    // T10: 배치 방향 강제 ??FrameY???�축�?무�??�게 90°
    [Fact]
    public void T10_DirectionOverride_ForcesAngle()
    {
        var outer = Rect(0, 0, 100, 60);
        var r = Serpentine.Generate(outer, null,
            new PatternParams { Spacing = 10, Direction = PatternDirection.FrameY });

        Assert.Equal(90.0, r.AngleDeg, 9);
        Assert.Equal(10, r.LaneCount);                       // 로컬 폭 100 / 간격 10
        Assert.All(r.Segments, s => Assert.Equal(60.0, s.Length, 9));
        Assert.All(r.Segments, s => Assert.Equal(s.Start.X, s.End.X, 9));   // ?�드?�서 ?�로 ?�인

        var tall = Rect(0, 0, 60, 100);
        var rx = Serpentine.Generate(tall, null,
            new PatternParams { Spacing = 10, Direction = PatternDirection.FrameX });
        Assert.Equal(0.0, rx.AngleDeg, 9);
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
