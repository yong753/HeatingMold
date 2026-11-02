using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class RegionOpsTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0,y0), new(x1,y0), new(x1,y1), new(x0,y1) };

    [Fact]
    public void UnionPolygonsMergesOverlappingFaces()
    {
        var union = RegionOps.UnionPolygons(new IReadOnlyList<Vec2>[] { Rect(0,0,10,10), Rect(5,0,15,10) });
        Assert.Single(union);
        Assert.Equal(150, Math.Abs(RegionOps.SignedArea(union[0])), 6);
    }

    [Fact]
    public void SubtractRectanglesRemovesExclusion()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0,0,20,10) },
            new[] { (8d, 2d, 12d, 8d) });
        Assert.Equal(176, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesSplitsRegionIntoTwoRings()
    {
        // 제외가 영역 높이를 완전히 가로지르면 남은 발열영역은 두 조각으로 분리된다.
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (8d, -1d, 12d, 11d) });
        Assert.Equal(2, remaining.Count);
        Assert.Equal(160, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesUnionsOverlappingExclusions()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (2d, 2d, 8d, 8d), (6d, 2d, 12d, 8d) });
        Assert.Equal(140, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesOutsideRegionKeepsArea()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (30d, 30d, 40d, 40d) });
        Assert.Equal(200, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
    }

    [Fact]
    public void SubtractRectanglesCoveringRegionReturnsEmpty()
    {
        var remaining = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) },
            new[] { (-1d, -1d, 21d, 11d) });
        Assert.Empty(remaining);
    }

    // ---- 제외영역 정책: 발열선은 발열영역(외곽−제외) 안에서만 생성되고, 턴커넥터는 이전 발열선
    // 끝점에서 다음 발열선 시작점까지의 최단 직선(2점)이다. 제외영역이 영역을 가로질러 발열영역이
    // 분리되면 연결 성분마다 독립 경로가 된다. 갈라진 구간은 같은 레인에서 이어지지 않는다.

    /// <summary>100×40 / Margin 0 / Spacing 10 → 레인 y=5,15,25,35 (각 런 0~100).</summary>
    private static IReadOnlyList<IReadOnlyList<SerpentineRoutePiece>> ExcludedComponents(
        (double MinX, double MinY, double MaxX, double MaxY)[] exclusions,
        PatternDirection direction = PatternDirection.FrameX) =>
        Serpentine.GenerateComponents(Rect(0, 0, 100, 40), exclusions,
            new PatternParams { Margin = 0, Spacing = 10, Direction = direction });

    [Fact]
    public void InteriorExclusionSpiralsWithStraightConnectors()
    {
        // 제외 (40,10)-(60,30)는 레인 y=15/25만 (0,40)+(60,100)으로 갈라지므로 레인 구간은
        // 1+2+2+1 = 6개다. 한 사슬로 모두 이어져 발열선 6 + 턴커넥터 5가 된다.
        var components = ExcludedComponents(new[] { (40d, 10d, 60d, 30d) });
        Assert.Single(components);
        var pieces = components[0];

        Assert.Equal(6, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(5, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector));

        // 가로 길이: 100 + 40 + 40 + 40 + 40 + 100 = 360 (레인 4개 합 400 − 제외 폭 20 × 2레인)
        Assert.Equal(360, pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Sum(piece => piece.Length), 6);

        // 커넥터는 인접 레인만 잇는다(간격 10): 5개 커넥터의 |dy| 합 = 5 × 10 = 50.
        var connectors = pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
        Assert.Equal(50, connectors.Sum(c => Math.Abs(c.Points[0].Y - c.Points[1].Y)), 6);

        var runs = pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun).ToList();
        for (int i = 1; i < runs.Count; i++)
            Assert.NotEqual(runs[i - 1].LaneIndex, runs[i].LaneIndex);

        Assert.All(connectors, connector => Assert.Equal(2, connector.Points.Count));
        // 최단 직선 정책(가장 가까운 이웃 우선): 커넥터는 전부 한 레인 수직(길이 10)이어야 하고,
        // 5개 합계가 정확히 50이어야 한다. (나선: lane0→lane1-right→lane2-right→lane3→lane2-left→lane1-left)
        Assert.All(connectors, connector => Assert.True(connector.Length <= 10 + 1e-6,
            "커넥터가 한 레인 수직(길이 10)이 아닙니다: " + connector.Length));
        Assert.Equal(50, connectors.Sum(c => c.Length), 6);
        AssertRunsAvoidRect(pieces, (40d, 10d, 60d, 30d));
    }

    [Fact]
    public void FullSpanExclusionYieldsTwoIndependentPaths()
    {
        // 제외 (40,0)-(60,40)가 영역을 완전히 가로질러 발열영역이 둘로 나뉘면 경로도 2개가 된다.
        var components = ExcludedComponents(new[] { (40d, 0d, 60d, 40d) });
        Assert.Equal(2, components.Count);

        foreach (var path in components)
        {
            Assert.Equal(4, path.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
            Assert.Equal(3, path.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector));

            double minX = path.SelectMany(piece => piece.Points).Min(pt => pt.X);
            double maxX = path.SelectMany(piece => piece.Points).Max(pt => pt.X);
            Assert.True(maxX <= 40 + 1e-6 || minX >= 60 - 1e-6,
                "경로가 자기 절반(좌/우) 안에 있어야 합니다");

            Assert.Equal(30, path.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector)
                .Sum(c => Math.Abs(c.Points[0].Y - c.Points[1].Y)), 6);
            AssertRunsAvoidRect(path, (40d, 0d, 60d, 40d));
        }
    }

    [Fact]
    public void TrapezoidEdgeExclusionProducesDiagonalConnectors()
    {
        // 왼쪽 모서리에 붙은 제외 (0,0)-(20,40)를 빼면 유효 영역은 (20,0),(100,0),(90,40),(20,40).
        // 오른쪽 경사면을 따라 레인 끝 x가 어긋나므로 턴커넥터는 사선 2개 + 수직 1개가 된다.
        var outer = new List<Vec2> { new(0, 0), new(100, 0), new(90, 40), new(10, 40) };
        var components = Serpentine.GenerateComponents(outer, new[] { (0d, 0d, 20d, 40d) },
            new PatternParams { Margin = 0, Spacing = 10 });
        Assert.Single(components);
        var pieces = components[0];

        Assert.Equal(4, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
        var connectors = pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
        Assert.Equal(3, connectors.Count);
        Assert.Equal(2, connectors.Count(c => Math.Abs(c.Points[0].X - c.Points[1].X) > 1e-6
            && Math.Abs(c.Points[0].Y - c.Points[1].Y) > 1e-6));
        Assert.Equal(1, connectors.Count(c => Math.Abs(c.Points[0].X - c.Points[1].X) <= 1e-6));

        Assert.All(pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun),
            run => Assert.All(run.Points, pt => Assert.True(pt.X >= 20 - 1e-6,
                "발열선이 제외영역 안(" + pt + ")에 있습니다")));

        AssertRunsInsideRing(pieces, new List<Vec2> { new(20, 0), new(100, 0), new(90, 40), new(20, 40) });
    }

    [Fact]
    public void RectangleWithoutExclusionMatchesPlainSerpentine()
    {
        // 제외가 없으면 제외 경로는 일반 생성과 똑같은 연속 폴리라인이어야 한다.
        var plain = Serpentine.Generate(Rect(0, 0, 100, 40), null,
            new PatternParams { Margin = 0, Spacing = 10 }).Path;
        Assert.True(plain.Count >= 2);

        var components = ExcludedComponents(Array.Empty<(double, double, double, double)>());
        Assert.Single(components);
        var actual = Flatten(components[0]);
        Assert.Equal(plain.Count, actual.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].X, actual[i].X, 6);
            Assert.Equal(plain[i].Y, actual[i].Y, 6);
        }
    }

    [Fact]
    public void TwoInteriorHolesNeverBridgeSameLane()
    {
        // 두 내부 홀: 같은 레인에서 갈라진 구간을 가로지르는 연결선(브리지)이 생기지 않아야 한다.
        var components = ExcludedComponents(new[] { (20d, 10d, 30d, 30d), (70d, 10d, 80d, 30d) });
        Assert.Single(components);
        var pieces = components[0];

        var runs = pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun).ToList();
        Assert.Equal(8, runs.Count);
        Assert.Equal(7, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.All(pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector),
            connector => Assert.Equal(2, connector.Points.Count));
        for (int i = 1; i < runs.Count; i++)
            Assert.NotEqual(runs[i - 1].LaneIndex, runs[i].LaneIndex);

        AssertRunsAvoidRect(pieces, (20d, 10d, 30d, 30d));
        AssertRunsAvoidRect(pieces, (70d, 10d, 80d, 30d));

        // 모든 레인 구간이 정확히 한 번씩 덮인다: 발열선 길이 합 = 차집합 스캔라인 폭 합 = 360.
        var region = RegionOps.SubtractRectangles(new IReadOnlyList<Vec2>[] { Rect(0, 0, 100, 40) },
            new[] { (20d, 10d, 30d, 30d), (70d, 10d, 80d, 30d) });
        double expected = new[] { 5d, 15d, 25d, 35d }.Sum(y => ExpectedLaneWidth(region, y));
        Assert.Equal(360, expected, 6);
        Assert.Equal(expected, runs.Sum(run => run.Length), 6);
    }

    [Fact]
    public void FullCoverageExclusionYieldsEmptyRoute()
    {
        // 면 전체를 덮는 제외 → 남는 발열영역이 없다(정상 동작, 예외 아님).
        Assert.Empty(ExcludedComponents(new[] { (-10d, -10d, 110d, 50d) }));
    }

    [Fact]
    public void RotatedFrameKeepsExclusionInFrameCoordinates()
    {
        // FrameY(90°) — 제외는 영역 프레임 좌표로 주어지므로 회전 후에도 같은 위치를 자른다.
        // 100×40 영역에서 FrameY 방향이면 레인은 프레임 x=5..95의 세로줄 10개가 되고,
        // 제외 (40,10)-(60,30)은 x=45/55 레인 두 개를 y∈[10,30]에서 자른다.
        // → 레인 구간 8 + 2×2 = 12개, 한 사슬로 이어져 발열선 12 + 턴커넥터 11.
        var components = ExcludedComponents(new[] { (40d, 10d, 60d, 30d) }, PatternDirection.FrameY);
        Assert.Single(components);
        var pieces = components[0];

        Assert.Equal(12, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(11, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.All(pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector),
            connector => Assert.Equal(2, connector.Points.Count));

        // 발열선 길이 합: 세로 레인 10개 × 40 − 제외 2레인 × 20 = 360 (회전은 길이를 보존).
        Assert.Equal(360, pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Sum(piece => piece.Length), 6);

        AssertRunsAvoidRect(pieces, (40d, 10d, 60d, 30d));
    }

    /// <summary>수평선 y와 영역 링들의 교차 x 좌표를 짝지어(even-odd) 구간 폭 합을 계산한다.</summary>
    private static double ExpectedLaneWidth(List<List<Vec2>> region, double y)
    {
        var xs = new List<double>();
        foreach (List<Vec2> ring in region)
            for (int i = 0; i < ring.Count; i++)
            {
                Vec2 a = ring[i], b = ring[(i + 1) % ring.Count];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    xs.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
        xs.Sort();
        double width = 0;
        for (int i = 0; i + 1 < xs.Count; i += 2) width += xs[i + 1] - xs[i];
        return width;
    }

    /// <summary>발열선을 0.25mm 간격으로 표본화해 주어진 링 안(경계 포함, 1e-6 이내)에 있는지 확인한다.</summary>
    private static void AssertRunsInsideRing(IEnumerable<SerpentineRoutePiece> pieces, IReadOnlyList<Vec2> ring)
    {
        const double step = 0.25, eps = 1e-6;
        foreach (var piece in pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun))
        {
            for (int i = 1; i < piece.Points.Count; i++)
            {
                Vec2 a = piece.Points[i - 1], b = piece.Points[i];
                int steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / step));
                for (int k = 0; k <= steps; k++)
                {
                    Vec2 pt = a + (b - a) * (k / (double)steps);
                    Assert.True(InsidePolygon(ring, pt) || DistanceToRing(ring, pt) <= eps,
                        "발열선이 유효 영역 밖(" + pt + ")을 지납니다");
                }
            }
        }
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
        double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return p.DistanceTo(new Vec2(a.X + t * abx, a.Y + t * aby));
    }

    private static bool InsidePolygon(IReadOnlyList<Vec2> ring, Vec2 p)
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

    /// <summary>컴포넌트(조각 목록) 목록을 기존 좌표 단언용 폴리라인 목록으로 변환한다.</summary>
    private static List<List<Vec2>> FlattenAll(IReadOnlyList<IReadOnlyList<SerpentineRoutePiece>> components) =>
        components.Select(Flatten).ToList();

    /// <summary>조각을 순서대로 이어 붙인다 — 조각 경계의 중복 시작점은 건너뛴다.</summary>
    private static List<Vec2> Flatten(IReadOnlyList<SerpentineRoutePiece> pieces)
    {
        var points = new List<Vec2>();
        foreach (var piece in pieces)
        {
            int start = points.Count > 0 && piece.Points.Count > 0
                && points[^1].DistanceTo(piece.Points[0]) <= 1e-9 ? 1 : 0;
            for (int i = start; i < piece.Points.Count; i++) points.Add(piece.Points[i]);
        }
        return points;
    }

    /// <summary>발열선(HeatingRun)을 0.25mm 간격으로 표본화해 제외 사각형의 엄격한 내부를 지나지
    /// 않는지 본다. 경계 위의 점은 내부가 아니다(제외 판정도 경계를 배제한다).</summary>
    private static void AssertRunsAvoidRect(IEnumerable<SerpentineRoutePiece> pieces,
        (double MinX, double MinY, double MaxX, double MaxY) rect)
    {
        const double eps = 1e-6, step = 0.25;
        foreach (var piece in pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun))
        {
            for (int i = 1; i < piece.Points.Count; i++)
            {
                Vec2 a = piece.Points[i - 1], b = piece.Points[i];
                int steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / step));
                for (int k = 0; k <= steps; k++)
                {
                    Vec2 pt = a + (b - a) * (k / (double)steps);
                    bool inside = pt.X > rect.MinX + eps && pt.X < rect.MaxX - eps
                        && pt.Y > rect.MinY + eps && pt.Y < rect.MaxY - eps;
                    Assert.False(inside, "발열선이 제외영역 내부(" + pt + ")를 지납니다");
                }
            }
        }
    }
}
