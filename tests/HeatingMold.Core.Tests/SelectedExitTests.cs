using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class SelectedExitTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    private static ExitBoundaryPoint Exit(double x, double y) => new(new Vec2(x, y), -1, 0);

    [Fact]
    public void SelectedExitsReplaceOnlyFirstAndLastEndpoints()
    {
        var outer = Rect(0, 0, 100, 60);
        var parameters = new PatternParams { Margin = 5, Spacing = 10 };
        SerpentineResult baseline = Serpentine.Generate(outer, null, parameters);

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.Auto, Exit(0, 15), Exit(60, 60));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(baseline.RoutePieces.Count, pattern.RoutePieces.Count);
        Assert.Equal(5, pattern.LaneCount);
        Assert.Equal(new Vec2(0, 15), pattern.RoutePieces[0].Points[0]);
        Assert.Equal(new Vec2(60, 60), pattern.RoutePieces[^1].Points[^1]);

        // 몸통(중간 조각)은 그대로다.
        for (int i = 1; i < pattern.RoutePieces.Count - 1; i++)
        {
            Assert.Equal(baseline.RoutePieces[i].Kind, pattern.RoutePieces[i].Kind);
            Assert.Equal(baseline.RoutePieces[i].LaneIndex, pattern.RoutePieces[i].LaneIndex);
            Assert.Equal(baseline.RoutePieces[i].Points, pattern.RoutePieces[i].Points);
        }
        // 첫/마지막 조각은 선택점 쪽 끝만 바뀐다.
        Assert.Equal(baseline.RoutePieces[0].Points[^1], pattern.RoutePieces[0].Points[^1]);
        Assert.Equal(baseline.RoutePieces[^1].Points[0], pattern.RoutePieces[^1].Points[0]);
    }

    [Fact]
    public void SelectedExitsSupportSingleLaneWithoutAddingPieces()
    {
        var outer = Rect(0, 0, 100, 10);
        var parameters = new PatternParams { Margin = 2, Spacing = 50 };

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameX, Exit(0, 5), Exit(100, 5));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(1, result.Pattern!.LaneCount);
        Assert.Single(result.Pattern.RoutePieces);
        Assert.Equal(new Vec2(0, 5), result.Pattern.RoutePieces[0].Points[0]);
        Assert.Equal(new Vec2(100, 5), result.Pattern.RoutePieces[^1].Points[^1]);
    }

    [Fact]
    public void SelectedExitsKeepOddLaneCount()
    {
        var outer = Rect(0, 0, 100, 60);
        var parameters = new PatternParams { Margin = 5, Spacing = 10 };

        var result = Serpentine.GenerateWithSelectedExits(
            outer, null, parameters, PatternDirection.FrameX, Exit(0, 15), Exit(100, 55));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(5, result.Pattern!.LaneCount);
    }

    [Fact]
    public void ExclusionsKeepOneCompleteCircuitAndSelectedEndpoints()
    {
        var outer = Rect(0, 0, 100, 40);
        var exclusions = new[] { (MinX: 40d, MinY: 10d, MaxX: 60d, MaxY: 30d) };
        var parameters = new PatternParams { Margin = 0, Spacing = 10, Direction = PatternDirection.FrameX };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, parameters, PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.Equal(6, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.HeatingRun));
        Assert.Equal(5, pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector));
        Assert.Equal(new Vec2(0, 5), pattern.RoutePieces[0].Points[0]);
        Assert.Equal(new Vec2(100, 35), pattern.RoutePieces[^1].Points[^1]);
    }

    [Fact]
    public void FullSpanExclusionStillFailsInsteadOfPartialPath()
    {
        var outer = Rect(0, 0, 100, 40);
        var exclusions = new[] { (MinX: 40d, MinY: 0d, MaxX: 60d, MaxY: 40d) };

        var result = Serpentine.GenerateComponentsWithSelectedExits(
            outer, exclusions, new PatternParams { Margin = 0, Spacing = 10 },
            PatternDirection.FrameX, Exit(0, 5), Exit(100, 35));

        Assert.False(result.Succeeded);
        Assert.Equal(AnchoredFailureReason.RegionSplit, result.Failure);
    }
}
