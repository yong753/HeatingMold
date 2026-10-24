using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using Xunit;

namespace HeatingMold.App.Tests;

/// <summary>화면에서 고른 경계 구간을 영속 인출 측면으로 바꾸는 규칙입니다.</summary>
public sealed class ExitSideSelectionTests
{
    /// <summary>하나의 경계 구간을 전체 구간 범위로 확정합니다.</summary>
    [Fact]
    public void BuildExitSideReference_AcceptsOneBoundarySegment()
    {
        BoundaryLoop loop = CreateSquare();

        var result = MainWindow.BuildExitSideReference([loop], [loop.Segments[1].Id]);

        Assert.True(result.IsSuccess);
        BoundarySegmentRange range = Assert.Single(result.GetValueOrThrow().Ranges);
        Assert.Equal(loop.Segments[1].Id, range.BoundarySegmentId);
        Assert.Equal(0, range.StartParameter);
        Assert.Equal(1, range.EndParameter);
    }

    /// <summary>루프의 마지막과 첫 경계 구간도 연속 측면 순서로 보존합니다.</summary>
    [Fact]
    public void BuildExitSideReference_PreservesOrderAcrossLoopClosure()
    {
        BoundaryLoop loop = CreateSquare();

        var result = MainWindow.BuildExitSideReference(
            [loop],
            [loop.Segments[0].Id, loop.Segments[3].Id]);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [loop.Segments[3].Id, loop.Segments[0].Id],
            result.GetValueOrThrow().Ranges.Select(range => range.BoundarySegmentId));
    }

    /// <summary>경계에서 떨어진 구간을 하나의 인출 측면으로 묶지 않습니다.</summary>
    [Fact]
    public void BuildExitSideReference_RejectsDiscontinuousSegments()
    {
        BoundaryLoop loop = CreateSquare();

        var result = MainWindow.BuildExitSideReference(
            [loop],
            [loop.Segments[0].Id, loop.Segments[2].Id]);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidExitSideSelection", Assert.Single(result.Issues).Code);
    }

    /// <summary>서로 다른 경계 루프의 구간을 한 출구 측면으로 묶지 않습니다.</summary>
    [Fact]
    public void BuildExitSideReference_RejectsSegmentsFromDifferentLoops()
    {
        BoundaryLoop firstLoop = CreateSquare();
        BoundaryLoop secondLoop = CreateSquare(xOffset: 20);

        var result = MainWindow.BuildExitSideReference(
            [firstLoop, secondLoop],
            [firstLoop.Segments[0].Id, secondLoop.Segments[0].Id]);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidExitSideSelection", Assert.Single(result.Issues).Code);
    }

    /// <summary>닫힌 경계 전체를 출구 측면으로 선택하지 못하게 합니다.</summary>
    [Fact]
    public void BuildExitSideReference_RejectsEntireClosedBoundary()
    {
        BoundaryLoop loop = CreateSquare();

        var result = MainWindow.BuildExitSideReference(
            [loop],
            loop.Segments.Select(segment => segment.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidExitSideSelection", Assert.Single(result.Issues).Code);
    }

    private static BoundaryLoop CreateSquare(double xOffset = 0)
    {
        Vec2[] vertices =
        [
            new(xOffset, 0),
            new(xOffset + 10, 0),
            new(xOffset + 10, 10),
            new(xOffset, 10)
        ];
        BoundarySegment[] segments = Enumerable.Range(0, vertices.Length)
            .Select(index => new BoundarySegment(
                BoundarySegmentId.New(),
                vertices[index],
                vertices[(index + 1) % vertices.Length]))
            .ToArray();

        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }
}
