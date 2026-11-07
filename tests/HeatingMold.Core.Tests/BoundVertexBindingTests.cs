using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

/// <summary>
/// 일반 꼭지점 편집이 인출점의 원본 CAD parameter를 잘못 보존하지 않는지 검증한다.
/// 인출점이 실제로 움직였을 때는 이전 위치의 parameter를 버려야 하고, 다음 꺾임점만
/// 움직였을 때는 양 끝의 저장된 parameter가 그대로 유지되어야 한다.
/// </summary>
public sealed class BoundVertexBindingTests
{
    private static ExitEdgeBinding StartBinding => new(9, 41, 3, 11, 0.0, 10.0, 0.25, false);
    private static ExitEdgeBinding EndBinding => new(9, 41, 3, 11, 0.0, 10.0, 0.75, false);

    private static SerpentineResult BoundRoute()
    {
        var points = new[] { new Vec2(0, 0), new Vec2(5, 0), new Vec2(10, 0) };
        var pieces = new[]
        {
            new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, points),
        };
        return new SerpentineResult
        {
            Path = points,
            Segments = new[] { new SerpentineSegment(0, points[0], points[^1]) },
            LaneCount = 1,
            AngleDeg = 0,
            TotalLength = 10,
            RoutePieces = pieces,
            StartExit = new CircuitExitPoint(CircuitExitRole.Start, points[0], 0, 0.25) { Binding = StartBinding },
            EndExit = new CircuitExitPoint(CircuitExitRole.End, points[^1], 0, 0.75) { Binding = EndBinding },
        };
    }

    [Fact]
    public void UnconstrainedEndpointEditMustNotKeepAParameterForTheOldPosition()
    {
        var startMoved = SerpentineVertexEdit.ApplyVertexEdits(BoundRoute(),
            new[] { (new RouteVertexOccurrence(0, 0), new Vec2(0, 1)) });

        Assert.Equal(new Vec2(0, 1), startMoved.StartExit!.Point);
        Assert.Null(startMoved.StartExit.Binding);
        Assert.Equal(new Vec2(10, 0), startMoved.EndExit!.Point);
        Assert.Equal(EndBinding, startMoved.EndExit.Binding);

        var endMoved = SerpentineVertexEdit.ApplyVertexEdits(BoundRoute(),
            new[] { (new RouteVertexOccurrence(0, 2), new Vec2(10, 1)) });

        Assert.Equal(new Vec2(0, 0), endMoved.StartExit!.Point);
        Assert.Equal(StartBinding, endMoved.StartExit.Binding);
        Assert.Equal(new Vec2(10, 1), endMoved.EndExit!.Point);
        Assert.Null(endMoved.EndExit.Binding);
    }

    [Fact]
    public void MovingTheNextBendPreservesBothSavedEndpointParameters()
    {
        var edited = SerpentineVertexEdit.ApplyVertexEdits(BoundRoute(),
            new[] { (new RouteVertexOccurrence(0, 1), new Vec2(5, 2)) });

        Assert.Equal(new Vec2(0, 0), edited.StartExit!.Point);
        Assert.Equal(StartBinding, edited.StartExit.Binding);
        Assert.Equal(new Vec2(10, 0), edited.EndExit!.Point);
        Assert.Equal(EndBinding, edited.EndExit.Binding);
        Assert.Equal(new Vec2(5, 2), edited.RoutePieces[0].Points[1]);
    }

    [Fact]
    public void UnchangedEndpointCoordinateKeepsItsSavedParameter()
    {
        var edited = SerpentineVertexEdit.ApplyVertexEdits(BoundRoute(),
            new[] { (new RouteVertexOccurrence(0, 0), new Vec2(0, 0)) });

        Assert.Equal(StartBinding, edited.StartExit!.Binding);
        Assert.Equal(EndBinding, edited.EndExit!.Binding);
    }
}
