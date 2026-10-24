using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Circuits;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>절단 시 시작 회로 ID를 유지하고 통합 시 survivor를 요구하는지 검사합니다.</summary>
public sealed class CircuitIdentityMapperTests
{
    /// <summary>회로가 두 성분으로 잘리면 기존 시작점을 포함한 성분만 ID와 전원을 이어받습니다.</summary>
    [Fact]
    public void Map_SplitKeepsIdentityOnComponentContainingOldStart()
    {
        Fixture fixture = CreateFixture(split: true);
        CircuitPathTopologyAnalysis topology = new CircuitPathTopologyService().Analyze(fixture.Segments);

        var result = new CircuitIdentityMapper().Map(topology, fixture.Circuits, fixture.ConnectionPoints);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CircuitIdentityAssignment[] assignments = result.GetValueOrThrow().Assignments.ToArray();
        Assert.Equal(2, assignments.Length);
        CircuitIdentityAssignment retained = Assert.Single(assignments.Where(item => item.PreservesExistingIdentity));
        CircuitIdentityAssignment added = Assert.Single(assignments.Where(item => !item.PreservesExistingIdentity));
        Assert.Equal(fixture.Circuits[0].Id, retained.CircuitId);
        Assert.Equal(fixture.Circuits[0].Supply, retained.PreservedSupply);
        Assert.Equal(fixture.Circuits[0].Id, added.ParentCircuitId);
        Assert.Contains(
            fixture.ConnectionPoints[1].Id,
            new[] { added.StartConnectionPointId, added.EndConnectionPointId }.Where(id => id.HasValue).Select(id => id!.Value));
    }

    /// <summary>두 기존 회로를 연결한 topology는 자동으로 survivor를 고르지 않습니다.</summary>
    [Fact]
    public void Map_MergeRequiresExplicitSurvivor()
    {
        Fixture fixture = CreateFixture(split: false, twoCircuits: true);
        CircuitPathTopologyAnalysis topology = new CircuitPathTopologyService().Analyze(fixture.Segments);

        var result = new CircuitIdentityMapper().Map(topology, fixture.Circuits, fixture.ConnectionPoints);

        Assert.False(result.IsSuccess);
        Assert.Equal("CircuitMergeRequiresSurvivor", Assert.Single(result.Issues).Code);
    }

    /// <summary>명시한 통합 survivor만 ID와 endpoint 방향을 유지하고 다른 회로는 은퇴 계보로 남깁니다.</summary>
    [Fact]
    public void Map_MergePreservesRequestedSurvivor()
    {
        Fixture fixture = CreateFixture(split: false, twoCircuits: true);
        CircuitPathTopologyAnalysis topology = new CircuitPathTopologyService().Analyze(fixture.Segments);

        var result = new CircuitIdentityMapper().Map(
            topology,
            fixture.Circuits,
            fixture.ConnectionPoints,
            fixture.Circuits[0].Id);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CircuitIdentityAssignment assignment = Assert.Single(result.GetValueOrThrow().Assignments);
        Assert.Equal(fixture.Circuits[0].Id, assignment.CircuitId);
        Assert.Equal(fixture.Circuits[1].Id, Assert.Single(assignment.MergedCircuitIds));
        Assert.Equal(fixture.ConnectionPoints[0].Id, assignment.StartConnectionPointId);
        Assert.True(assignment.PreservesExistingIdentity);
    }

    private static Fixture CreateFixture(bool split, bool twoCircuits = false)
    {
        FaceRefId faceId = FaceRefId.New();
        ControlPoint[] points =
        [
            new(ControlPointId.New(), new Vec3(0, 0, 0)),
            new(ControlPointId.New(), new Vec3(10, 0, 0)),
            new(ControlPointId.New(), new Vec3(10, 10, 0)),
            new(ControlPointId.New(), new Vec3(20, 10, 0))
        ];
        HeatingPathSegment[] segments =
        [
            CreateSegment(points[0], points[1], HeatingPathSegmentKind.HeatingRun, 0, isActive: true),
            CreateSegment(points[1], points[2], HeatingPathSegmentKind.Turn, 0, isActive: !split),
            CreateSegment(points[2], points[3], HeatingPathSegmentKind.HeatingRun, 1, isActive: true)
        ];
        var supply = new Supply(new VoltageVrms(220), new VoltageVrms(220), new CurrentAmps(16));
        Circuit[] circuits;
        if (twoCircuits)
        {
            circuits =
            [
                new Circuit(CircuitId.New(), 0, [new CircuitSegmentReference(segments[0].Id, 0, false)],
                    ConnectionPointId.New(), ConnectionPointId.New(), supply),
                new Circuit(CircuitId.New(), 1, [new CircuitSegmentReference(segments[2].Id, 0, false)],
                    ConnectionPointId.New(), ConnectionPointId.New(), supply)
            ];
        }
        else
        {
            circuits =
            [
                new Circuit(CircuitId.New(), 0,
                    segments.Select((segment, index) => new CircuitSegmentReference(segment.Id, index, false)),
                    ConnectionPointId.New(), ConnectionPointId.New(), supply)
            ];
        }

        ConnectionPoint[] connectionPoints = twoCircuits
            ?
            [
                CreateConnectionPoint(circuits[0], ConnectionPointRole.Start, segments[0], points[0], faceId),
                CreateConnectionPoint(circuits[0], ConnectionPointRole.End, segments[0], points[1], faceId),
                CreateConnectionPoint(circuits[1], ConnectionPointRole.Start, segments[2], points[2], faceId),
                CreateConnectionPoint(circuits[1], ConnectionPointRole.End, segments[2], points[3], faceId)
            ]
            :
            [
                CreateConnectionPoint(circuits[0], ConnectionPointRole.Start, segments[0], points[0], faceId),
                CreateConnectionPoint(circuits[0], ConnectionPointRole.End, segments[2], points[3], faceId)
            ];

        return new Fixture(segments, circuits, connectionPoints);
    }

    private static HeatingPathSegment CreateSegment(
        ControlPoint start,
        ControlPoint end,
        HeatingPathSegmentKind kind,
        int sequence,
        bool isActive) => new(
        HeatingPathSegmentId.New(), kind, start.Id, end.Id, sequence, isActive,
        new LineCurveDefinition(start.Position, end.Position),
        kind == HeatingPathSegmentKind.HeatingRun ? sequence == 0 ? 0 : 1 : 0);

    private static ConnectionPoint CreateConnectionPoint(
        Circuit circuit,
        ConnectionPointRole role,
        HeatingPathSegment segment,
        ControlPoint point,
        FaceRefId faceId)
    {
        var anchor = new SurfaceAnchor(faceId, default, default, point.Position);
        return new ConnectionPoint(
            role == ConnectionPointRole.Start ? circuit.StartConnectionPointId : circuit.EndConnectionPointId,
            circuit.Id,
            role,
            segment.Id,
            point.Id,
            anchor,
            point.Position);
    }

    private sealed record Fixture(
        IReadOnlyList<HeatingPathSegment> Segments,
        IReadOnlyList<Circuit> Circuits,
        IReadOnlyList<ConnectionPoint> ConnectionPoints);
}
