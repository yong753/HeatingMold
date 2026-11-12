using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Tests;

public class RouteVertexMoveConstraintTests
{
    private static Vec2 Constrained(Vec2 original, Vec2 requested, double angleDeg)
    {
        Assert.True(RouteVertexMoveConstraint.TryConstrain(original, requested, angleDeg,
            out Vec2 constrained, out string failure), failure);
        return constrained;
    }

    [Fact]
    public void HorizontalRoute_MovesOnlyAlongTheLane()
    {
        // 가로 생성(0도): 좌우만 적용되고 열의 높이(Y)는 그대로다.
        Vec2 result = Constrained(new Vec2(10, 5), new Vec2(14, 9), 0);

        Assert.Equal(14, result.X, 12);
        Assert.Equal(5, result.Y, 12);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void VerticalRoute_MovesOnlyAlongTheLane(double angleDeg)
    {
        // 세로 생성: 상하만 적용되고 열의 위치(X)는 그대로다.
        Vec2 result = Constrained(new Vec2(10, 5), new Vec2(14, 9), angleDeg);

        Assert.Equal(10, result.X, 12);
        Assert.Equal(9, result.Y, 12);
    }

    [Fact]
    public void PerpendicularOnlyMove_ProducesTheOriginalPoint()
    {
        // 세로 생성에서 좌우로만 끄는 요청은 간격을 바꾸므로 적용되는 변위가 없다.
        Vec2 result = Constrained(new Vec2(10, 5), new Vec2(14, 5), 90);

        Assert.Equal(new Vec2(10, 5), result);
    }

    [Fact]
    public void InclinedRoute_KeepsThePerpendicularCoordinate()
    {
        const double angleDeg = 30;
        var original = new Vec2(0, 0);
        var requested = new Vec2(3, 1);
        double radians = angleDeg * Math.PI / 180.0;
        var perpendicular = new Vec2(-Math.Sin(radians), Math.Cos(radians));

        Vec2 result = Constrained(original, requested, angleDeg);

        // 이동 후에도 진행 방향과 수직인 좌표 성분은 변하지 않는다.
        double before = (original.X * perpendicular.X) + (original.Y * perpendicular.Y);
        double after = (result.X * perpendicular.X) + (result.Y * perpendicular.Y);
        Assert.Equal(before, after, 9);
        // 이동 방향 성분은 요청대로 반영된다.
        double along = ((requested.X - original.X) * Math.Cos(radians))
                       + ((requested.Y - original.Y) * Math.Sin(radians));
        Assert.Equal(along, result.DistanceTo(original), 9);
        // 같은 결과에 제약을 다시 적용해도 좌표가 흔들리지 않는다.
        Assert.True(result.DistanceTo(Constrained(result, requested, angleDeg)) <= 1e-12);
    }

    [Fact]
    public void VerticalLaneEditKeepsTheLanePitch()
    {
        // 세로 생성 2열 경로: 열 위치 X = 0 / 10, 피치 10.
        var pieces = new List<SerpentineRoutePiece>
        {
            new(SerpentineRoutePieceKind.HeatingRun, 0, new List<Vec2> { new(0, 0), new(0, 10) }),
            new(SerpentineRoutePieceKind.TurnConnector, 0, new List<Vec2> { new(0, 10), new(10, 10) }),
            new(SerpentineRoutePieceKind.HeatingRun, 1, new List<Vec2> { new(10, 10), new(10, 0) }),
        };
        var source = new SerpentineResult
        {
            Path = new List<Vec2> { new(0, 0), new(0, 10), new(10, 10), new(10, 0) },
            Segments = new List<SerpentineSegment>
            {
                new(0, new Vec2(0, 0), new Vec2(0, 10)),
                new(1, new Vec2(10, 10), new Vec2(10, 0)),
            },
            LaneCount = 2,
            AngleDeg = 90,
            TotalLength = 30,
            RoutePieces = pieces,
        };

        Vec2 moved = Constrained(new Vec2(0, 10), new Vec2(4, 13), source.AngleDeg);
        var edited = SerpentineVertexEdit.ApplyVertexEdits(source, new List<(RouteVertexOccurrence, Vec2)>
        {
            (new RouteVertexOccurrence(0, 1), moved),
            (new RouteVertexOccurrence(1, 0), moved),
        });

        Assert.Equal(new Vec2(0, 13), edited.RoutePieces[0].Points[1]);
        // 각 열의 X 위치는 그대로이므로 발열사 간격(피치 10)이 유지된다.
        Assert.All(edited.RoutePieces[0].Points, point => Assert.Equal(0, point.X, 12));
        Assert.All(edited.RoutePieces[2].Points, point => Assert.Equal(10, point.X, 12));
        Assert.Equal(10, edited.RoutePieces[2].Points[0].X - edited.RoutePieces[0].Points[1].X, 12);
    }

    [Fact]
    public void NonFiniteInput_IsRejectedExplicitly()
    {
        Assert.False(RouteVertexMoveConstraint.TryConstrain(
            new Vec2(double.NaN, 0), new Vec2(1, 1), 0, out _, out string originalFailure));
        Assert.Contains("유한", originalFailure);

        Assert.False(RouteVertexMoveConstraint.TryConstrain(
            new Vec2(0, 0), new Vec2(double.PositiveInfinity, 1), 0, out _, out string moveFailure));
        Assert.Contains("유한", moveFailure);

        Assert.False(RouteVertexMoveConstraint.TryConstrain(
            new Vec2(0, 0), new Vec2(1, 1), double.NaN, out _, out string angleFailure));
        Assert.Contains("생성 방향", angleFailure);
    }
}
