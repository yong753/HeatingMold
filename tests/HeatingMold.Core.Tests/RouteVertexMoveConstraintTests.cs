using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

/// <summary>
/// 꼭지점 이동 제한이 발열 진행 방향 성분만 남기고, 발열사 간격을 결정하는 수직
/// 위치를 생성 당시 값 그대로 보존하는지 확인한다.
/// </summary>
public class RouteVertexMoveConstraintTests
{
    [Fact]
    public void HorizontalGenerationMovesOnlyAlongTheLaneDirection()
    {
        Assert.True(RouteVertexMoveConstraint.TryConstrain(new Vec2(10, 5), new Vec2(14, 9), 0,
            out Vec2 constrained, out string failure), failure);

        Assert.Equal(new Vec2(14, 5), constrained);
    }

    [Fact]
    public void VerticalGenerationMovesOnlyAlongTheLaneDirection()
    {
        Assert.True(RouteVertexMoveConstraint.TryConstrain(new Vec2(10, 5), new Vec2(14, 9), 90,
            out Vec2 constrained, out string failure), failure);
        Assert.Equal(new Vec2(10, 9), constrained);

        // 270°는 같은 축의 반대 진행 방향이므로 이동 결과도 같다.
        Assert.True(RouteVertexMoveConstraint.TryConstrain(new Vec2(10, 5), new Vec2(14, 9), 270,
            out Vec2 opposite, out string oppositeFailure), oppositeFailure);
        Assert.Equal(new Vec2(10, 9), opposite);
    }

    [Fact]
    public void PerpendicularOnlyDragLeavesTheVertexUnchanged()
    {
        Assert.True(RouteVertexMoveConstraint.TryConstrain(new Vec2(10, 5), new Vec2(3, 5), 90,
            out Vec2 constrained, out string failure), failure);

        Assert.Equal(new Vec2(10, 5), constrained);
        Assert.Equal(0, constrained.DistanceTo(new Vec2(10, 5)), 12);
    }

    [Fact]
    public void InclinedGenerationPreservesThePerpendicularCoordinate()
    {
        var original = new Vec2(-3, 7);
        var requested = new Vec2(11, -2);
        Assert.True(RouteVertexMoveConstraint.TryConstrain(original, requested, 30,
            out Vec2 constrained, out string failure), failure);

        double radians = 30 * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double along = (requested.X - original.X) * cos + (requested.Y - original.Y) * sin;
        Assert.Equal(original.X + along * cos, constrained.X, 12);
        Assert.Equal(original.Y + along * sin, constrained.Y, 12);
        // 간격을 결정하는 수직 좌표는 이동 전과 같다.
        Assert.Equal(-sin * original.X + cos * original.Y,
            -sin * constrained.X + cos * constrained.Y, 12);
        // 허용 방향 성분은 요청 그대로 반영된다.
        Assert.Equal(along, (constrained.X - original.X) * cos + (constrained.Y - original.Y) * sin, 12);
    }

    [Fact]
    public void ApplyingTheConstraintTwiceIsStable()
    {
        var original = new Vec2(4, -2);
        Assert.True(RouteVertexMoveConstraint.TryConstrain(original, new Vec2(9, 6), 37.5,
            out Vec2 first, out string firstFailure), firstFailure);
        Assert.True(RouteVertexMoveConstraint.TryConstrain(original, first, 37.5,
            out Vec2 second, out string secondFailure), secondFailure);

        Assert.Equal(0, first.DistanceTo(second), 12);
    }

    [Fact]
    public void VerticalLaneEditKeepsTheLanePitch()
    {
        // 세로 생성: 레인은 Y 방향, 발열사 간격은 X 방향이다.
        var lane0 = new List<Vec2> { new(-10, -20), new(-10, 20) };
        var connector = new List<Vec2> { new(-10, 20), new(-20, 20) };
        var lane1 = new List<Vec2> { new(-20, 20), new(-20, -20) };
        var route = new SerpentineResult
        {
            Path = new List<Vec2> { new(-10, -20), new(-10, 20), new(-20, 20), new(-20, -20) },
            Segments = new List<SerpentineSegment> { new(0, lane0[0], lane0[^1]), new(1, lane1[0], lane1[^1]) },
            LaneCount = 2,
            AngleDeg = 90,
            TotalLength = 80,
            RoutePieces = new[]
            {
                new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, lane0),
                new SerpentineRoutePiece(SerpentineRoutePieceKind.TurnConnector, 0, connector),
                new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 1, lane1),
            },
        };

        // 대각선 이동을 요청해도 X(간격 방향)는 그대로 두고 Y 성분만 반영한다.
        Assert.True(RouteVertexMoveConstraint.TryConstrain(new Vec2(-10, 20), new Vec2(-7, 23), 90,
            out Vec2 target, out string failure), failure);
        Assert.Equal(new Vec2(-10, 23), target);

        var edited = SerpentineVertexEdit.ApplyVertexEdits(route, new[]
        {
            (new RouteVertexOccurrence(0, 1), target),
            (new RouteVertexOccurrence(1, 0), target),
        });

        Assert.Equal(-10, edited.RoutePieces[0].Points[1].X, 12);
        Assert.Equal(-20, edited.RoutePieces[2].Points[0].X, 12);
        Assert.Equal(10, Math.Abs(edited.RoutePieces[2].Points[0].X - edited.RoutePieces[0].Points[1].X), 12);
    }

    [Fact]
    public void InvalidInputsFailExplicitly()
    {
        Assert.False(RouteVertexMoveConstraint.TryConstrain(new Vec2(double.NaN, 0), new Vec2(1, 1), 0,
            out _, out string coordinateFailure));
        Assert.False(string.IsNullOrEmpty(coordinateFailure));

        Assert.False(RouteVertexMoveConstraint.TryConstrain(new Vec2(0, 0),
            new Vec2(double.PositiveInfinity, 1), 0, out _, out string requestedFailure));
        Assert.False(string.IsNullOrEmpty(requestedFailure));

        Assert.False(RouteVertexMoveConstraint.TryConstrain(new Vec2(0, 0), new Vec2(1, 1), double.NaN,
            out _, out string angleFailure));
        Assert.False(string.IsNullOrEmpty(angleFailure));
    }
}
