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
    public void BuildGenerationRegionExpandsExclusionsByMargin()
    {
        // 외곽 0..40x0..20, 마진 2 → 36x16=576. 제외 10..14x6..10을 2만큼 확장하면
        // 8..16x4..12=64를 차감해 512가 남는다. 원본 제외영역은 그대로다.
        var exclusions = new[] { (10d, 6d, 14d, 10d) };
        var remaining = RegionOps.BuildGenerationRegion(Rect(0, 0, 40, 20), exclusions, 2.0);

        Assert.Equal(512, remaining.Sum(r => RegionOps.SignedArea(r)), 6);
        Assert.Equal((10d, 6d, 14d, 10d), exclusions[0]);
    }

    [Fact]
    public void BuildGenerationRegionWithZeroMarginMatchesExistingSubtraction()
    {
        var outer = new IReadOnlyList<Vec2>[] { Rect(0, 0, 20, 10) };
        var exclusions = new[] { (8d, 2d, 12d, 8d) };

        var generation = RegionOps.BuildGenerationRegion(outer[0], exclusions, 0.0);
        var existing = RegionOps.SubtractRectangles(
            RegionOps.BuildPatternRegion(outer[0], null, 0.0), exclusions);

        Assert.Equal(existing.Sum(r => RegionOps.SignedArea(r)),
            generation.Sum(r => RegionOps.SignedArea(r)), 6);
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

    /// <summary>100×40 / Margin 0 / Spacing 10 → 레인 y=5,15,25,35 (각 런 0~100). 분리되면 실패한다.</summary>
    private static IReadOnlyList<IReadOnlyList<SerpentineRoutePiece>> ExcludedComponents(
        (double MinX, double MinY, double MaxX, double MaxY)[] exclusions,
        PatternDirection direction = PatternDirection.FrameX)
    {
        var result = Serpentine.GenerateComponents(Rect(0, 0, 100, 40), exclusions,
            new PatternParams { Margin = 0, Spacing = 10, Direction = direction });
        Assert.Null(result.Failure);
        return result.Components;
    }

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
    public void FullSpanExclusionIsRejectedInsteadOfSplittingIntoTwoPaths()
    {
        // 제외 (40,0)-(60,40)가 영역을 완전히 가로질러 발열영역이 둘로 나뉘면
        // 독립 경로 2개를 만들지 않고 실패로 처리한다(전폭 분리 금지 정책).
        var result = Serpentine.GenerateComponents(Rect(0, 0, 100, 40), new[] { (40d, 0d, 60d, 40d) },
            new PatternParams { Margin = 0, Spacing = 10 });
        Assert.Empty(result.Components);
        Assert.Equal(SerpentineComponentsFailureReason.SplitByExclusion, result.Failure);
    }

    [Fact]
    public void TrapezoidEdgeExclusionProducesDiagonalConnectors()
    {
        // 왼쪽 모서리에 붙은 제외 (0,0)-(20,40)를 빼면 유효 영역은 (20,0),(100,0),(90,40),(20,40).
        // 오른쪽 경사면을 따라 레인 끝 x가 어긋나므로 턴커넥터는 사선 1개 + 수직 2개가 최소다.
        // (수직 2×10 + 사선 √(10²+2.5²) = 30.3078…, 사선 2개 조합 30.6156…보다 짧다.)
        var outer = new List<Vec2> { new(0, 0), new(100, 0), new(90, 40), new(10, 40) };
        var result = Serpentine.GenerateComponents(outer, new[] { (0d, 0d, 20d, 40d) },
            new PatternParams { Margin = 0, Spacing = 10 });
        Assert.Null(result.Failure);
        Assert.Single(result.Components);
        var pieces = result.Components[0];

        Assert.Equal(4, pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
        var connectors = pieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
        Assert.Equal(3, connectors.Count);
        Assert.Equal(1, connectors.Count(c => Math.Abs(c.Points[0].X - c.Points[1].X) > 1e-6
            && Math.Abs(c.Points[0].Y - c.Points[1].Y) > 1e-6));
        Assert.Equal(2, connectors.Count(c => Math.Abs(c.Points[0].X - c.Points[1].X) <= 1e-6));
        Assert.Equal(2 * 10 + Math.Sqrt(100 + 6.25), connectors.Sum(c => c.Length), 6);

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

    [Fact]
    public void AnchoredExclusionStartsAndEndsOnExitFaceWithoutCrossingExclusion()
    {
        // 100×50, Margin 5 → 발열영역 x∈[5,95], y∈[5,45], 레인 y=10/20/30/40.
        // 제외 (40,15)-(60,35)는 마진 적용 후 (35,10)-(65,40)이 되어 레인 y=20/30과
        // 경계 레인 하나(y=10 또는 40)를 좌우 조각으로 가르므로 노드 7개다.
        // 모든 오른쪽 레인 끝(95)에서 5mm 리드가 생긴다.
        var outer = Rect(0, 0, 100, 50);
        var exclusions = new[] { (40d, 15d, 60d, 35d) };
        var parameters = new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.FrameX };
        var chain = new List<Vec2> { new(100, 0), new(100, 50) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.FrameX);
        Assert.Null(candidates.Failure);
        Assert.NotEmpty(candidates.Candidates);
        Assert.All(candidates.Candidates, candidate => Assert.Equal(0, candidate.AngleDeg, 9));

        var candidate = candidates.Candidates[0];
        var anchored = Serpentine.GenerateAnchoredComponents(
            outer, exclusions, parameters, chain, candidate, PatternDirection.FrameX);
        Assert.True(anchored.Succeeded);
        var pattern = anchored.Pattern!;

        Assert.Equal(candidate.Start, pattern.Path[0]);
        Assert.Equal(candidate.End, pattern.Path[^1]);

        var runs = pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun).ToList();
        var connectors = pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
        Assert.Equal(7, runs.Count);
        Assert.Equal(280, runs.Sum(piece => piece.Length), 6);
        Assert.Equal(candidate.Start, runs[0].Points[0]);
        Assert.Equal(candidate.End, runs[^1].Points[^1]);

        Assert.Equal(6, connectors.Count);
        Assert.Equal(124.86833, connectors.Sum(piece => piece.Length), 6);


        Assert.Equal(404.86833, pattern.TotalLength, 6);
        Assert.Equal(pattern.TotalLength, runs.Sum(piece => piece.Length) + connectors.Sum(piece => piece.Length), 6);

        AssertRunsAvoidRect(pattern.RoutePieces, (35d, 10d, 65d, 40d));
    }

    [Fact]
    public void AnchoredExclusionHonorsAutoAxisSelection()
    {
        var outer = Rect(0, 0, 100, 50);
        var exclusions = new[] { (40d, 15d, 60d, 35d) };
        var parameters = new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.Auto };
        var chain = new List<Vec2> { new(100, 0), new(100, 50) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.Auto);
        Assert.Null(candidates.Failure);
        Assert.NotEmpty(candidates.Candidates);
        Assert.All(candidates.Candidates, candidate => Assert.Equal(0, candidate.AngleDeg, 9));
    }

    [Fact]
    public void AnchoredExclusionHonorsFrameYExitFace()
    {
        // FrameY: 레인은 x=5..95의 세로줄 10개, 인출면은 위쪽 y=50. 제외 (40,15)-(60,35)는
        // 세로 레인 2개(x=45/55)를 자른다 → 노드 12개(짝수 레인이라 양 끝이 서로 반대편).
        var outer = Rect(0, 0, 100, 50);
        var exclusions = new[] { (40d, 15d, 60d, 35d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameY };
        var chain = new List<Vec2> { new(0, 50), new(100, 50) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.FrameY);
        Assert.Null(candidates.Failure);
        Assert.NotEmpty(candidates.Candidates);
        Assert.All(candidates.Candidates, candidate => Assert.Equal(90, candidate.AngleDeg, 9));

        var anchored = Serpentine.GenerateAnchoredComponents(
            outer, exclusions, parameters, chain, candidates.Candidates[0], PatternDirection.FrameY);
        Assert.True(anchored.Succeeded);
        var pattern = anchored.Pattern!;
        Assert.Equal(90, pattern.AngleDeg, 9);
        Assert.Equal(12, pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(11, pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector));

        Assert.Equal(460, pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Sum(piece => piece.Length), 6);
        AssertRunsAvoidRect(pattern.RoutePieces, (40d, 15d, 60d, 35d));
    }

    [Fact]
    public void AnchoredEdgeExclusionKeepsZeroLeadAndRunsOutside()
    {
        // 왼쪽 경계에 붙은 제외 (0,10)-(20,30): 오른쪽 레인 끝은 인출면 위에 정확히 놓인다.
        var outer = Rect(0, 0, 100, 40);
        var exclusions = new[] { (0d, 10d, 20d, 30d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameX };
        var chain = new List<Vec2> { new(100, 0), new(100, 40) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.FrameX);
        Assert.Null(candidates.Failure);
        Assert.NotEmpty(candidates.Candidates);

        var anchored = Serpentine.GenerateAnchoredComponents(
            outer, exclusions, parameters, chain, candidates.Candidates[0], PatternDirection.FrameX);
        Assert.True(anchored.Succeeded);
        var pattern = anchored.Pattern!;

        Assert.Equal(4, pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(3, pattern.RoutePieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector));

        Assert.Equal(360, pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Sum(piece => piece.Length), 6);
        AssertRunsAvoidRect(pattern.RoutePieces, (0d, 10d, 20d, 30d));
    }

    [Fact]
    public void AnchoredExclusionWithTwoSplitLanesAndMarginTwo()
    {
        // 작업영역 30x16, 마진 2, 간격 5 → 유효영역 26x12, 수평 레인 2개(y=-2.5, 2.5).
        // 제외 (0,-2)-(6,2)는 마진 적용 후 (-2,-4)-(8,4)이 되어 두 레인을 모두 절단하되
        // 영역을 완전히 분리하지는 않는다(위아래 2mm 여유). 인출면 x=15, 레인 끝 x=±13 → 리드 2+2.
        var outer = Rect(-15, -8, 15, 8);
        var exclusions = new[] { (0d, -2d, 6d, 2d) };
        var parameters = new PatternParams { Margin = 2, Spacing = 5, Direction = PatternDirection.FrameX };
        var chain = new List<Vec2> { new(15, -8), new(15, 8) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.FrameX);
        Assert.Null(candidates.Failure);
        Assert.NotEmpty(candidates.Candidates);
        Assert.All(candidates.Candidates, candidate => Assert.Equal(0, candidate.AngleDeg, 9));

        var anchored = Serpentine.GenerateAnchoredComponents(
            outer, exclusions, parameters, chain, candidates.Candidates[0], PatternDirection.FrameX);
        Assert.True(anchored.Succeeded);
        var pattern = anchored.Pattern!;

        var runs = pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun).ToList();
        var connectors = pattern.RoutePieces.Where(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
        Assert.Equal(4, runs.Count);
        Assert.Equal(3, connectors.Count);

        Assert.Equal(36, runs.Sum(piece => piece.Length), 6);
        Assert.Equal(candidates.Candidates[0].Start, runs[0].Points[0]);
        Assert.Equal(candidates.Candidates[0].End, runs[^1].Points[^1]);
        Assert.True(candidates.Candidates[0].Start.DistanceTo(pattern.Path[0]) < 1e-9);
        Assert.True(candidates.Candidates[0].End.DistanceTo(pattern.Path[^1]) < 1e-9);
        AssertRunsAvoidRect(pattern.RoutePieces, (-2d, -4d, 8d, 4d));
    }

    [Fact]
    public void ExcludedAnchorCandidatesRejectTooManyLaneFragments()
    {
        // 10레인 중 8레인이 세 개의 내부 홀로 4분할 → 노드 34개. 노드 개수 상한은 없지만
        // 이 조합은 정확 탐색의 고유 상태 수가 계산 예산(2,000,000)을 넘어 TooComplex로 끝난다.
        var outer = Rect(0, 0, 100, 100);
        var exclusions = new[] { (20d, 12d, 30d, 88d), (45d, 12d, 55d, 88d), (70d, 12d, 80d, 88d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameX };
        var chain = new List<Vec2> { new(100, 0), new(100, 100) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.FrameX);
        Assert.Empty(candidates.Candidates);
        Assert.Equal(SerpentineComponentsFailureReason.TooComplex, candidates.Failure);
    }

    [Fact]
    public void FullSpanExclusionHasNoAnchoredCandidates()
    {
        var outer = Rect(0, 0, 100, 40);
        var exclusions = new[] { (40d, 0d, 60d, 40d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameX };
        var chain = new List<Vec2> { new(100, 0), new(100, 40) };

        var candidates = Serpentine.ComputeCandidatesComponents(
            outer, exclusions, parameters, chain, PatternDirection.FrameX);
        Assert.Empty(candidates.Candidates);
        Assert.Equal(SerpentineComponentsFailureReason.SplitByExclusion, candidates.Failure);
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
