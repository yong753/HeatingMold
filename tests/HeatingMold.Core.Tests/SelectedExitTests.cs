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
        Assert.Equal(3, pattern.LaneCount);            // 20, 30, 40 — 종료점(45)을 넘는 추가 레인은 필요 없다
        Assert.False(pattern.LaneCountAdjusted);       // 짝수 레인 강제 없음

        SerpentineRoutePiece first = pattern.RoutePieces[0];
        Assert.Equal(SerpentineRoutePieceKind.HeatingRun, first.Kind);
        AssertNear(new Vec2(0, 20), first.Points[0]);
        Assert.Equal(20.0, first.Points[^1].Y, 9);     // 시작점 y에서 축평행
        AssertNear(new Vec2(95, 20), first.Points[^1]); // 마진 인셋에서 정지

        // 마지막 레인이 종료점 가까운 쪽에서 끝나므로 발열선은 (95,40)에서 끝나고,
        // 별도 연결선이 종료점까지 이어진다.
        SerpentineRoutePiece lastHeating = pattern.RoutePieces.Last(
            p => p.Kind == SerpentineRoutePieceKind.HeatingRun);
        Assert.Equal(40.0, lastHeating.Points[0].Y, 9);
        Assert.Equal(40.0, lastHeating.Points[^1].Y, 9);
        AssertNear(new Vec2(95, 40), lastHeating.Points[^1]);

        SerpentineRoutePiece terminal = pattern.RoutePieces[^1];
        Assert.Equal(SerpentineRoutePieceKind.FixedConnector, terminal.Kind);
        AssertNear(new Vec2(95, 40), terminal.Points[0]);
        AssertNear(new Vec2(100, 45), terminal.Points[^1]);
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
        Assert.Equal(5, pattern.LaneCount);            // 50, 40, 30, 20 + 종료점 반대편이라 추가한 10
        AssertNear(new Vec2(0, 50), pattern.RoutePieces[0].Points[0]);

        List<double> laneY = pattern.RoutePieces
            .Where(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun)
            .Select(piece => piece.Points[0].Y)
            .ToList();
        Assert.True(laneY[1] < laneY[0], "두 번째 레인은 종료점 쪽(아래)으로 진행해야 한다");

        // 네 번째 레인은 왼쪽에서 끝나 종료점(100,20)에서 멀다. 같은 간격으로 10을 한 줄
        // 더 만들어 오른쪽에서 끝낸 뒤, 뒤쪽으로 돌아가는 연결선으로 종료점에 닿는다.
        SerpentineRoutePiece lastHeating = pattern.RoutePieces.Last(
            p => p.Kind == SerpentineRoutePieceKind.HeatingRun);
        Assert.Equal(10.0, lastHeating.Points[0].Y, 9);
        AssertNear(new Vec2(5, 10), lastHeating.Points[0]);
        AssertNear(new Vec2(95, 10), lastHeating.Points[^1]);

        SerpentineRoutePiece terminal = pattern.RoutePieces[^1];
        Assert.Equal(SerpentineRoutePieceKind.FixedConnector, terminal.Kind);
        AssertNear(new Vec2(95, 10), terminal.Points[0]);
        AssertNear(new Vec2(100, 20), terminal.Points[^1]);
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

        SerpentineRoutePiece lastHeating = pattern.RoutePieces.Last(
            p => p.Kind == SerpentineRoutePieceKind.HeatingRun);
        Assert.Equal(40.0, lastHeating.Points[^1].X, 9);
        AssertNear(new Vec2(40, 5), lastHeating.Points[^1]);

        SerpentineRoutePiece terminal = pattern.RoutePieces[^1];
        Assert.Equal(SerpentineRoutePieceKind.FixedConnector, terminal.Kind);
        AssertNear(new Vec2(40, 5), terminal.Points[0]);
        AssertNear(new Vec2(40, 0), terminal.Points[^1]);
    }

    [Fact]
    public void SingleLaneKeepsHorizontalRunAndAddsTailConnector()
    {
        var outer = Rect(0, 0, 100, 10);
        var parameters = new PatternParams { Margin = 0, Spacing = 50, Direction = PatternDirection.FrameX };

        // 레인은 하나뿐이므로 첫 발열선이 곧 마지막 발열선이다. 발열선을 종료점으로
        // 비스듬하게 당기지 않고, 정상 끝에서 별도 연결선으로 종료점에 닿는다.
        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameX, Exit(0, 5), Exit(100, 8));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(1, result.Pattern!.LaneCount);
        Assert.Equal(2, result.Pattern.RoutePieces.Count);
        SerpentineRoutePiece heating = result.Pattern.RoutePieces[0];
        Assert.Equal(SerpentineRoutePieceKind.HeatingRun, heating.Kind);
        AssertNear(new Vec2(0, 5), heating.Points[0]);
        AssertNear(new Vec2(100, 5), heating.Points[^1]);

        SerpentineRoutePiece terminal = result.Pattern.RoutePieces[^1];
        Assert.Equal(SerpentineRoutePieceKind.FixedConnector, terminal.Kind);
        AssertNear(new Vec2(100, 5), terminal.Points[0]);
        AssertNear(new Vec2(100, 8), terminal.Points[^1]);
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
        var outer = Rect(0, 0, 100, 60);
        var exclusions = new[] { (MinX: 40d, MinY: 10d, MaxX: 60d, MaxY: 30d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameX };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, parameters, PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(5, pattern.LaneCount);            // 5, 15, 25, 35 + 반대편 종료를 위한 45
        Assert.Equal(7, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        // 레인 사이 4개는 턴, 제외영역이 같은 레인을 가른 2개 틈은 고정 연결선,
        // 마지막 발열선에서 종료점으로 돌아가는 연결선이 하나 더 있다.
        Assert.Equal(4, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Equal(3, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        AssertNear(new Vec2(0, 5), pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(100, 35), pattern.RoutePieces[^1].Points[^1]);
        Assert.Equal(SerpentineRoutePieceKind.FixedConnector, pattern.RoutePieces[^1].Kind);
        AssertNear(new Vec2(100, 45), pattern.RoutePieces[^1].Points[0]);
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
        var outer = Rect(0, 0, 100, 60);
        var exclusions = new[] { (MinX: 40d, MinY: 0d, MaxX: 60d, MaxY: 60d) };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, new PatternParams { Margin = 0, Spacing = 10 },
            PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(5, pattern.LaneCount);            // 5, 15, 25, 35 + 반대편 종료를 위한 45
        // 다섯 레인이 모두 좌우 두 구간으로 갈리므로 발열선 10개다.
        Assert.Equal(10, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(4, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        // 레인마다 같은 CAD 구간의 틈을 잇는 연결선 5개 + 종료 연결선 1개.
        Assert.Equal(6, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.FixedConnector));
        AssertNear(new Vec2(0, 5), pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(100, 35), pattern.RoutePieces[^1].Points[^1]);
        AssertNear(new Vec2(100, 45), pattern.RoutePieces[^1].Points[0]);
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
            PatternDirection.FrameX, Exit(0, 0.5), Exit(0, 39.5));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(40, pattern.LaneCount);
        Assert.Equal(40, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        AssertNear(new Vec2(0, 0.5), pattern.RoutePieces[0].Points[0]);
        AssertNear(new Vec2(0, 39.5), pattern.RoutePieces[^1].Points[^1]);
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
            PatternDirection.FrameX, Exit(0, 0.5), Exit(0, 39.5));

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
    public void NeedsExtraLaneOutsideRegionFailsExplicitly()
    {
        var outer = Rect(0, 0, 100, 40);

        // 5, 15, 25, 35 레인 뒤 마지막 레인이 왼쪽에서 끝나 종료점(100,35)과 멀다.
        // 같은 간격의 다음 레인(45)은 영역 높이 40 밖이므로 일부만 만들지 않고 실패해야 한다.
        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, new PatternParams { Margin = 0, Spacing = 10 },
            PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.False(result.Succeeded);
        Assert.Equal(AnchoredFailureReason.RequiredExtraLaneOutsideRegion, result.Failure);
    }

    [Fact]
    public void ShortExtraLaneIsUsedWhenAnyIntervalExists()
    {
        var outer = Rect(0, 0, 100, 60);
        var exclusions = new[] { (MinX: 0d, MinY: 40d, MaxX: 90d, MaxY: 60d) };

        // 추가 레인 위치(45)에는 폭 10mm 구간만 남지만, 발열구간이 하나라도 있으므로
        // 길이와 무관하게 그 구간으로 레인을 만든다.
        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, new PatternParams { Margin = 0, Spacing = 10 },
            PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(5, pattern.LaneCount);

        SerpentineRoutePiece lastHeating = pattern.RoutePieces.Last(
            p => p.Kind == SerpentineRoutePieceKind.HeatingRun);
        Assert.Equal(45.0, lastHeating.Points[0].Y, 9);
        AssertNear(new Vec2(90, 45), lastHeating.Points[0]);
        AssertNear(new Vec2(100, 45), lastHeating.Points[^1]);

        SerpentineRoutePiece terminal = pattern.RoutePieces[^1];
        Assert.Equal(SerpentineRoutePieceKind.FixedConnector, terminal.Kind);
        AssertNear(new Vec2(100, 45), terminal.Points[0]);
        AssertNear(new Vec2(100, 35), terminal.Points[^1]);
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
