using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

/// <summary>선택한 시작점에서 사행 전체를 생성하는 새 인출점 규칙 검증.</summary>
public class SelectedExitTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    private static ExitBoundaryPoint Exit(double x, double y) => new(new Vec2(x, y), -1, 0);

    private static void AssertNear(Vec2 expected, Vec2 actual, double tolerance = 1e-9) =>
        Assert.True(expected.DistanceTo(actual) <= tolerance,
            $"expected ({expected.X}, {expected.Y}) but was ({actual.X}, {actual.Y})");

    [Fact]
    public void FirstRunStartsAtSelectedPointAndStaysAxisParallel()
    {
        var outer = Rect(0, 0, 100, 60);
        var parameters = new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.FrameX };

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameX, Exit(0, 20), Exit(100, 45));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(3, pattern.LaneCount);            // 20, 30, 40 — 종료점(45)을 넘는 레인은 만들지 않는다
        Assert.False(pattern.LaneCountAdjusted);       // 짝수 레인 강제 없음

        SerpentineRoutePiece first = pattern.RoutePieces[0];
        Assert.Equal(SerpentineRoutePieceKind.HeatingRun, first.Kind);
        AssertNear(new Vec2(0, 20), first.Points[0]);
        Assert.Equal(20.0, first.Points[^1].Y, 9);     // 시작점 y에서 축평행
        AssertNear(new Vec2(95, 20), first.Points[^1]); // 마진 인셋에서 정지
        AssertNear(new Vec2(100, 45), pattern.RoutePieces[^1].Points[^1]);
        // 종료점보다 먼 레인은 생성하지 않는다.
        Assert.All(pattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.HeatingRun),
            piece => Assert.True(piece.Points[0].Y <= 45 + 1e-9));
    }

    [Fact]
    public void EndPointBelowStartSweepsDownward()
    {
        var outer = Rect(0, 0, 100, 60);
        var parameters = new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.FrameX };

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameX, Exit(0, 50), Exit(100, 20));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(4, pattern.LaneCount);            // 50, 40, 30, 20 — 종료점(20)에서 멈춘다
        AssertNear(new Vec2(0, 50), pattern.RoutePieces[0].Points[0]);

        List<double> laneY = pattern.RoutePieces
            .Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Select(piece => piece.Points[0].Y)
            .ToList();
        Assert.True(laneY[1] < laneY[0], "두 번째 레인은 종료점 쪽(아래)으로 진행해야 한다");
    }

    [Fact]
    public void VerticalLayoutRunsAlongFrameYAxis()
    {
        var outer = Rect(0, 0, 60, 100);
        var parameters = new PatternParams { Margin = 5, Spacing = 10, Direction = PatternDirection.FrameY };

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameY, Exit(20, 100), Exit(40, 0));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(90.0, pattern.AngleDeg, 9);

        SerpentineRoutePiece first = pattern.RoutePieces[0];
        AssertNear(new Vec2(20, 100), first.Points[0]);
        Assert.Equal(20.0, first.Points[^1].X, 9);     // 세로축 평행
        AssertNear(new Vec2(20, 5), first.Points[^1]); // 반대편 마진에서 정지
        AssertNear(new Vec2(40, 0), pattern.RoutePieces[^1].Points[^1]);
    }

    [Fact]
    public void SingleLaneKeepsSelectedEndpointsWithoutExtraPieces()
    {
        var outer = Rect(0, 0, 100, 10);
        var parameters = new PatternParams { Margin = 0, Spacing = 50, Direction = PatternDirection.FrameX };

        // 레인은 하나뿐이므로 첫 발열선이 곧 마지막 발열선이다. 조각을 추가하지 않고
        // 시작점과 수동 종료점을 그대로 잇는다.
        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameX, Exit(0, 5), Exit(100, 8));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(1, result.Pattern!.LaneCount);
        Assert.Single(result.Pattern.RoutePieces);
        AssertNear(new Vec2(0, 5), result.Pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(100, 8), result.Pattern.RoutePieces[^1].Points[^1]);
    }

    [Fact]
    public void HorizontalLayoutWithEqualExitHeightsIsRejected()
    {
        var outer = Rect(0, 0, 100, 60);

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, new PatternParams { Margin = 5, Spacing = 10 },
            PatternDirection.FrameX, Exit(0, 20), Exit(100, 20));

        Assert.False(result.Succeeded);
        Assert.Equal(AnchoredFailureReason.IndeterminateDirection, result.Failure);
    }

    [Fact]
    public void VerticalLayoutWithEqualExitColumnsIsRejected()
    {
        var outer = Rect(0, 0, 60, 100);

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, new PatternParams { Margin = 5, Spacing = 10 },
            PatternDirection.FrameY, Exit(20, 100), Exit(20, 0));

        Assert.False(result.Succeeded);
        Assert.Equal(AnchoredFailureReason.IndeterminateDirection, result.Failure);
    }

    [Fact]
    public void ExclusionsKeepOneCompleteCircuitFromSelectedStart()
    {
        var outer = Rect(0, 0, 100, 40);
        var exclusions = new[] { (MinX: 40d, MinY: 10d, MaxX: 60d, MaxY: 30d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameX };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, parameters, PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(6, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        // 레인 사이 3개는 턴, 제외영역이 같은 레인을 가른 2개 틈은 고정 연결선이다.
        Assert.Equal(3, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Equal(2, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        AssertNear(new Vec2(0, 5), pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(100, 35), pattern.RoutePieces[^1].Points[^1]);
        Assert.All(pattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.HeatingRun),
            piece => Assert.All(piece.Points, point => Assert.False(
                point.X > 40 + 1e-9 && point.X < 60 - 1e-9,
                "발열선이 제외영역 안에 있습니다: " + point)));
    }

    [Fact]
    public void FullSpanExclusionConnectsAcrossWithConnectorsInsteadOfFailing()
    {
        // 제외영역이 영역을 완전히 가로질러도 발열선만 제외영역을 피하면 되고,
        // 각 레인의 좌우 구간을 잇는 고정 연결선은 제외영역을 관통할 수 있다.
        var outer = Rect(0, 0, 100, 40);
        var exclusions = new[] { (MinX: 40d, MinY: 0d, MaxX: 60d, MaxY: 40d) };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, new PatternParams { Margin = 0, Spacing = 10 },
            PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(8, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(3, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Equal(4, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        AssertNear(new Vec2(0, 5), pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(100, 35), pattern.RoutePieces[^1].Points[^1]);
        Assert.All(pattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.HeatingRun),
            piece => Assert.All(piece.Points, point => Assert.False(
                point.X > 40 + 1e-9 && point.X < 60 - 1e-9,
                "발열선이 제외영역 안에 있습니다: " + point)));
    }

    [Fact]
    public void MoreThanTwentyLanesAreGeneratedWithoutNodeLimit()
    {
        // 간격 1mm, 높이 40mm → 40레인. 예전 20구간 제한에 걸리던 입력이다.
        // 오른쪽 아래 제외영역은 아래쪽 10레인만 짧게 만들고, 그 사이 연결선은 사선이 된다.
        var outer = Rect(0, 0, 600, 40);
        var exclusions = new[] { (MinX: 400d, MinY: 0d, MaxX: 600d, MaxY: 10d) };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, new PatternParams { Margin = 0, Spacing = 1 },
            PatternDirection.FrameX, Exit(0, 0.5), Exit(600, 39.5));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(40, pattern.LaneCount);
        Assert.Equal(40, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        AssertNear(new Vec2(0, 0.5), pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(600, 39.5), pattern.RoutePieces[^1].Points[^1]);
        // 제외영역과 겹치는 아래쪽 10레인은 400mm로 짧아진다.
        Assert.Equal(10, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun
            && p.Length < 599));
    }

    [Fact]
    public void ManySplitLanesUseStraightFixedConnectorsAcrossExclusion()
    {
        // 40레인 중 아래쪽 10레인이 제외영역 때문에 좌우 두 구간으로 갈린다.
        // 같은 레인의 틈은 제외영역을 관통하는 고정 연결선으로 잇고, 발열선은 제외영역을 피한다.
        var outer = Rect(0, 0, 600, 40);
        var exclusions = new[] { (MinX: 200d, MinY: 0d, MaxX: 400d, MaxY: 10d) };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, new PatternParams { Margin = 0, Spacing = 1 },
            PatternDirection.FrameX, Exit(0, 0.5), Exit(600, 39.5));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(40, pattern.LaneCount);
        Assert.Equal(50, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(39, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Equal(10, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        Assert.All(pattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.HeatingRun),
            piece => Assert.All(piece.Points, point => Assert.False(
                point.X > 200 + 1e-9 && point.X < 400 - 1e-9,
                "발열선이 제외영역 안에 있습니다: " + point)));
    }

    [Fact]
    public void StartPointInsideInsetIntervalIsRejectedExplicitly()
    {
        var outer = Rect(0, 0, 100, 60);

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, new PatternParams { Margin = 5, Spacing = 10 },
            PatternDirection.FrameX, Exit(50, 25), Exit(0, 40));

        Assert.False(result.Succeeded);
        Assert.Equal(AnchoredFailureReason.AnchorNotAtLaneEnd, result.Failure);
    }

    [Fact]
    public void StartPointOutsideOuterBoundaryHasNoLane()
    {
        var outer = Rect(0, 0, 100, 60);

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, new PatternParams { Margin = 5, Spacing = 10 },
            PatternDirection.FrameX, Exit(50, 70), Exit(0, 20));

        Assert.False(result.Succeeded);
        Assert.Equal(AnchoredFailureReason.NoLanes, result.Failure);
    }
}
