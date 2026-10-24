using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Circuits;

namespace HeatingMold.Design.Tests;

/// <summary>회로 그래프가 순서·방향·분기·폐회로를 정확히 판별하는지 검사합니다.</summary>
public sealed class CircuitPathTopologyServiceTests
{
    /// <summary>방향이 뒤집힌 중간 조각을 포함해 안정 순서의 연속 회로를 만듭니다.</summary>
    [Fact]
    public void Analyze_OrdersSegmentsAndRecordsReversedDirection()
    {
        ControlPointId a = Point(1);
        ControlPointId b = Point(2);
        ControlPointId c = Point(3);
        ControlPointId d = Point(4);
        HeatingPathSegment first = Segment(11, a, b);
        HeatingPathSegment reversedMiddle = Segment(12, c, b);
        HeatingPathSegment last = Segment(13, c, d);

        CircuitPathTopologyAnalysis result = new CircuitPathTopologyService().Analyze(
            [last, reversedMiddle, first]);

        Assert.True(result.IsValid);
        CircuitPathComponent component = Assert.Single(result.Components);
        Assert.Equal(a, component.StartControlPointId);
        Assert.Equal(d, component.EndControlPointId);
        Assert.Equal(new[] { first.Id, reversedMiddle.Id, last.Id }, component.Segments.Select(item => item.SegmentId));
        Assert.Equal(new[] { false, true, false }, component.Segments.Select(item => item.IsReversed));
    }

    /// <summary>비활성 회전 구간은 그래프에서 빠져 독립된 회로 성분을 만듭니다.</summary>
    [Fact]
    public void Analyze_InactiveTurnSeparatesTwoValidComponents()
    {
        HeatingPathSegment first = Segment(21, Point(1), Point(2));
        HeatingPathSegment inactiveTurn = Segment(22, Point(2), Point(3), HeatingPathSegmentKind.Turn, false);
        HeatingPathSegment second = Segment(23, Point(3), Point(4));

        CircuitPathTopologyAnalysis result = new CircuitPathTopologyService().Analyze(
            [first, inactiveTurn, second]);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Components.Count);
        Assert.All(result.Components, component => Assert.Single(component.Segments));
    }

    /// <summary>절점 분기가 있으면 일부 경로를 회로인 것처럼 반환하지 않습니다.</summary>
    [Fact]
    public void Analyze_BranchReturnsBlockingIssue()
    {
        ControlPointId branch = Point(2);
        CircuitPathTopologyAnalysis result = new CircuitPathTopologyService().Analyze(
        [
            Segment(31, Point(1), branch),
            Segment(32, branch, Point(3)),
            Segment(33, branch, Point(4))
        ]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Components);
        Assert.Equal("BranchedCircuitPath", Assert.Single(result.Issues).Code);
    }

    /// <summary>끝점이 없는 폐회로는 양 끝점 회로 모델로 변환하지 않습니다.</summary>
    [Fact]
    public void Analyze_ClosedLoopReturnsBlockingIssue()
    {
        ControlPointId a = Point(1);
        ControlPointId b = Point(2);
        ControlPointId c = Point(3);
        CircuitPathTopologyAnalysis result = new CircuitPathTopologyService().Analyze(
        [
            Segment(41, a, b),
            Segment(42, b, c),
            Segment(43, c, a)
        ]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Components);
        Assert.Equal("ClosedCircuitPath", Assert.Single(result.Issues).Code);
    }

    /// <summary>빈 ID나 중복된 안정 ID가 topology 순서에 섞이지 않게 차단합니다.</summary>
    [Fact]
    public void Analyze_DuplicateSegmentIdentityReturnsBlockingIssue()
    {
        HeatingPathSegmentId duplicateId = SegmentId(51);
        HeatingPathSegment first = Segment(duplicateId, Point(1), Point(2), HeatingPathSegmentKind.HeatingRun, true, 101);
        HeatingPathSegment second = Segment(duplicateId, Point(2), Point(3), HeatingPathSegmentKind.HeatingRun, true, 102);

        CircuitPathTopologyAnalysis result = new CircuitPathTopologyService().Analyze([first, second]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Components);
        Assert.Equal("DuplicatePathSegmentId", Assert.Single(result.Issues).Code);
    }

    /// <summary>활성 조각이 하나도 없으면 유효한 빈 분석 결과를 반환합니다.</summary>
    [Fact]
    public void Analyze_NoActiveSegmentsReturnsEmptyTopology()
    {
        CircuitPathTopologyAnalysis result = new CircuitPathTopologyService().Analyze(
            [Segment(61, Point(1), Point(2), HeatingPathSegmentKind.Turn, false)]);

        Assert.True(result.IsValid);
        Assert.Empty(result.Components);
        Assert.Empty(result.Issues);
    }

    private static HeatingPathSegment Segment(
        int segmentId,
        ControlPointId start,
        ControlPointId end,
        HeatingPathSegmentKind kind = HeatingPathSegmentKind.HeatingRun,
        bool isActive = true) =>
        Segment(SegmentId(segmentId), start, end, kind, isActive, segmentId);

    private static HeatingPathSegment Segment(
        HeatingPathSegmentId id,
        ControlPointId start,
        ControlPointId end,
        HeatingPathSegmentKind kind,
        bool isActive,
        int curveSeed) => new(
        id,
        kind,
        start,
        end,
        sequence: curveSeed,
        isActive,
        new LineCurveDefinition(new Vec3(curveSeed * 2, 0, 0), new Vec3(curveSeed * 2 + 1, 0, 0)));

    private static HeatingPathSegmentId SegmentId(int value) => new(CreateGuid(value));
    private static ControlPointId Point(int value) => new(CreateGuid(value));
    private static Guid CreateGuid(int value) => new(value, 0, 0, new byte[8]);
}
