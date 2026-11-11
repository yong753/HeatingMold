using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class RouteLengthLabelsTests
{
    private static RouteLengthLabelSource Run(
        int pieceIndex, int laneIndex, double length, params Vec2[] points) =>
        new("A", pieceIndex, SerpentineRoutePieceKind.HeatingRun, laneIndex, length, points);

    private static RouteLengthLabelSource Turn(
        int pieceIndex, int laneIndex, double length, params Vec2[] points) =>
        new("A", pieceIndex, SerpentineRoutePieceKind.TurnConnector, laneIndex, length, points);

    private static RouteLengthLabelSource Lead(
        int pieceIndex, double length, params Vec2[] points) =>
        new("A", pieceIndex, SerpentineRoutePieceKind.FixedConnector, 0, length, points);

    private static ProjectionFrame Frame() =>
        new(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0));

    [Fact]
    public void TryBuild_PlacesLaneLabelsOnOneSideAndTurnLabelOutside()
    {
        var sources = new[]
        {
            Run(0, 0, 100, new Vec2(0, 0), new Vec2(100, 0)),
            Turn(1, 0, 50, new Vec2(100, 0), new Vec2(100, 50)),
            Run(2, 1, 100, new Vec2(100, 50), new Vec2(0, 50)),
        };

        bool ok = RouteLengthLabels.TryBuild(
            sources, Frame(), new RouteLengthLabelOptions { OffsetMm = 6, TextHeightMm = 8 },
            out var labels, out string? error);

        Assert.True(ok, error);
        Assert.Equal(3, labels.Count);

        var firstRun = labels.Single(l => l.PieceIndex == 0);
        var secondRun = labels.Single(l => l.PieceIndex == 2);
        var turn = labels.Single(l => l.PieceIndex == 1);

        // 두 레인 모두 진행 방향과 무관하게 같은 쪽(위)에 놓인다.
        Assert.True(firstRun.Position2D.Y > 0, "첫 레인 라벨이 선 위에 있어야 합니다.");
        Assert.True(secondRun.Position2D.Y > 50, "둘째 레인 라벨이 선 위에 있어야 합니다.");

        // 턴 라벨은 인접 레인이 뻗은 반대쪽(바깥)에 놓인다.
        Assert.True(turn.Position2D.X > 100, "턴 라벨이 레인 바깥쪽에 있어야 합니다.");
    }

    [Fact]
    public void TryBuild_FormatsLengthWithOneDecimalAndInvariantCulture()
    {
        var sources = new[]
        {
            Run(0, 0, 820.44, new Vec2(0, 0), new Vec2(100, 0)),
        };

        Assert.True(RouteLengthLabels.TryBuild(
            sources, Frame(), null, out var labels, out string? error), error);

        Assert.Equal("820.4", Assert.Single(labels).Text);
    }

    [Fact]
    public void TryBuild_SkipsFixedConnectorsButStillKeepsThemAsObstacles()
    {
        var sources = new[]
        {
            Lead(0, 7, new Vec2(0, 0), new Vec2(5, 0)),
            Run(1, 0, 95, new Vec2(5, 0), new Vec2(100, 0)),
        };

        Assert.True(RouteLengthLabels.TryBuild(
            sources, Frame(), null, out var labels, out string? error), error);

        Assert.Equal(new[] { 1 }, labels.Select(l => l.PieceIndex).ToArray());
    }

    [Fact]
    public void TryBuild_KeepsSplitLaneFragmentsAsSeparateLabels()
    {
        var sources = new[]
        {
            Run(0, 0, 40, new Vec2(0, 0), new Vec2(40, 0)),
            Run(1, 0, 30, new Vec2(70, 0), new Vec2(100, 0)),
        };

        Assert.True(RouteLengthLabels.TryBuild(
            sources, Frame(), null, out var labels, out string? error), error);

        Assert.Equal(2, labels.Count);
        Assert.All(labels, l => Assert.Equal(RouteLengthLabelKind.HeatingRun, l.Kind));
        Assert.Equal(new[] { "40.0", "30.0" }, labels.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void TryBuild_ComputesAnchorOnTheConfirmedWorkingPlane()
    {
        var frame = new ProjectionFrame(
            new Vec3(10, 20, 30), new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        var points3D = new[] { new Vec3(10, 20, 30), new Vec3(110, 20, 30) };
        var source = new RouteLengthLabelSource(
            "A", 0, SerpentineRoutePieceKind.HeatingRun, 0, 100,
            new[] { new Vec2(0, 0), new Vec2(100, 0) }, points3D);

        Assert.True(RouteLengthLabels.TryBuild(
            new[] { source }, frame, new RouteLengthLabelOptions { OffsetMm = 6, TextHeightMm = 8 },
            out var labels, out string? error), error);

        var label = Assert.Single(labels);
        Assert.NotNull(label.Position3D);
        Assert.Equal(30, label.Position3D!.Value.Z, 9);
        Assert.Equal(60, label.Position3D.Value.X, 9);
        Assert.Equal(26, label.Position3D.Value.Y, 9);
    }

    [Fact]
    public void TryBuild_RejectsNonPositiveCommittedLength()
    {
        var sources = new[]
        {
            Run(0, 0, 0, new Vec2(0, 0), new Vec2(100, 0)),
        };

        bool ok = RouteLengthLabels.TryBuild(sources, Frame(), null, out var labels, out string? error);

        Assert.False(ok);
        Assert.Empty(labels);
        Assert.Contains("길이", error);
    }

    [Fact]
    public void TryBuild_RejectsMismatchedProjectionAndRoutePoints()
    {
        var source = new RouteLengthLabelSource(
            "A", 0, SerpentineRoutePieceKind.HeatingRun, 0, 100,
            new[] { new Vec2(0, 0), new Vec2(100, 0) },
            new[] { new Vec3(0, 0, 0) });

        bool ok = RouteLengthLabels.TryBuild(
            new[] { source }, Frame(), null, out var labels, out string? error);

        Assert.False(ok);
        Assert.Empty(labels);
        Assert.Contains("개수", error);
    }

    [Fact]
    public void TryBuild_MovesLabelAwayFromCrossingRoute()
    {
        // 가로 레인 위 여백에 세로 레인이 지나간다. 라벨은 그 선을 덮지 않아야 한다.
        var sources = new[]
        {
            Run(0, 0, 100, new Vec2(0, 0), new Vec2(100, 0)),
            Run(1, 1, 44, new Vec2(50, 6), new Vec2(50, 50)),
        };

        Assert.True(RouteLengthLabels.TryBuild(
            sources, Frame(), new RouteLengthLabelOptions { OffsetMm = 6, TextHeightMm = 8 },
            out var labels, out string? error), error);

        var horizontal = labels.Single(l => l.PieceIndex == 0);
        Assert.True(Math.Abs(horizontal.Position2D.X - 50) > 4,
            "라벨이 교차하는 세로 선 위에 남아 있습니다.");
    }

    [Fact]
    public void TryFormatLength_RejectsInvalidValues()
    {
        Assert.True(RouteLengthLabels.TryFormatLength(0, out string zero));
        Assert.Equal("0.0", zero);
        Assert.False(RouteLengthLabels.TryFormatLength(-1, out _));
        Assert.False(RouteLengthLabels.TryFormatLength(double.NaN, out _));
        Assert.False(RouteLengthLabels.TryFormatLength(double.PositiveInfinity, out _));
    }
}
