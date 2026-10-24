using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Editing;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>공유 절점 편집이 연결 곡선을 함께 바꾸고 오래된 요청을 거부하는지 검사합니다.</summary>
public sealed class ControlPointEditServiceTests
{
    /// <summary>공유 절점을 이동하면 시작·끝 양쪽 곡선의 같은 절점이 함께 바뀝니다.</summary>
    [Fact]
    public void Move_UpdatesEverySegmentConnectedToSharedPoint()
    {
        Fixture fixture = CreateFixture();
        SurfaceAnchor target = CreateAnchor(fixture.FaceId, new Vec3(5, 2, 0));
        var request = new ControlPointEditRequest(
            fixture.Path.Id,
            fixture.SharedPointId,
            fixture.Project.Revision,
            target,
            endpointToleranceMm: 0.01);

        var result = new ControlPointEditService().Move(request, fixture.Project);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        HeatingPathEditDraft draft = result.GetValueOrThrow();
        Assert.Equal(2, draft.AffectedSegmentIds.Count);
        Assert.Equal(target.ModelPoint, draft.CandidatePath.ControlPoints.Single(point => point.Id == fixture.SharedPointId).Position);
        Assert.Equal(target.ModelPoint, ((LineCurveDefinition)draft.CandidatePath.Segments[0].Curve).End);
        Assert.Equal(target.ModelPoint, ((LineCurveDefinition)draft.CandidatePath.Segments[1].Curve).Start);
        Assert.Equal(fixture.Path.GeneratedSegments, draft.CandidatePath.GeneratedSegments);
    }

    /// <summary>기준 revision이 달라진 편집 요청은 후보를 만들지 않습니다.</summary>
    [Fact]
    public void Move_RejectsStaleRevision()
    {
        Fixture fixture = CreateFixture();
        var request = new ControlPointEditRequest(
            fixture.Path.Id,
            fixture.SharedPointId,
            new DesignRevision(fixture.Project.Revision.Value - 1),
            CreateAnchor(fixture.FaceId, new Vec3(5, 2, 0)),
            endpointToleranceMm: 0.01);

        var result = new ControlPointEditService().Move(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("StaleControlPointEdit", Assert.Single(result.Issues).Code);
    }

    /// <summary>B-spline 표본을 극점으로 오해해 편집하지 않고 명시적인 편집기 부재 진단을 반환합니다.</summary>
    [Fact]
    public void Move_RejectsBSplineWithoutNativeEndpointEditor()
    {
        Fixture fixture = CreateFixture(useBSpline: true);
        var request = new ControlPointEditRequest(
            fixture.Path.Id,
            fixture.SharedPointId,
            fixture.Project.Revision,
            CreateAnchor(fixture.FaceId, new Vec3(5, 2, 0)),
            endpointToleranceMm: 0.01);

        var result = new ControlPointEditService().Move(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("UnsupportedControlPointEdit", Assert.Single(result.Issues).Code);
        Assert.Contains("Native", result.Issues[0].Evidence[0]);
    }

    private static Fixture CreateFixture(bool useBSpline = false)
    {
        FaceRefId faceId = FaceRefId.New();
        RegionId regionId = RegionId.New();
        BoundaryLoop originalLoop = CreateLoop(faceId);
        BoundaryLoop effectiveLoop = CreateLoop(faceId);
        var exitSide = new ExitSideReference([new BoundarySegmentRange(effectiveLoop.Segments[0].Id, 0.1, 0.9)]);
        var region = new HeatingRegion(regionId, [faceId], [originalLoop], [], [], [effectiveLoop], exitSide);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var placement = new PlacementConstraints(
            new LengthMm(10), new LengthMm(2), new LengthMm(0), PlacementDirection.AlongX,
            CreateAnchor(faceId, new Vec3(0, 0, 0)), CreateAnchor(faceId, new Vec3(10, 0, 0)), exitSide);
        ControlPoint first = new(ControlPointId.New(), new Vec3(0, 0, 0));
        ControlPoint shared = new(ControlPointId.New(), new Vec3(5, 0, 0));
        ControlPoint last = new(ControlPointId.New(), new Vec3(10, 0, 0));
        CurveDefinition firstCurve = useBSpline
            ? new BSplineCurveDefinition(1, [first.Position, shared.Position], [1, 1], [0, 1], [2, 2], false, 0, 1)
            : new LineCurveDefinition(first.Position, shared.Position);
        var firstSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            first.Id, shared.Id, 0, true, firstCurve, 0);
        var secondSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
            shared.Id, last.Id, 1, true, new LineCurveDefinition(shared.Position, last.Position));
        var path = new CoreHeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(regionId, placement, wire, "edit-test-v1"),
            [first, shared, last],
            [firstSegment, secondSegment],
            [firstSegment, secondSegment]);

        byte[] sourceBytes = [2, 4, 6, 8];
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(3), "Edit fixture",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new ModelIdentity(hash, sourceBytes.Length, "EditFixture", "1", "face-v1"),
            new WorkingCoordinateSystemDefinition(default, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [new FaceReference(faceId, hash, 0, FaceGeometryKind.Plane, new AreaMm2(100), default, "edit-face", [])],
            [region], wire, placement, [path], [], [], []);

        return new Fixture(project, path, faceId, shared.Id);
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] vertices = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        return new BoundaryLoop(
            BoundaryLoopId.New(),
            vertices.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], faceId)),
            isHole: false);
    }

    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec3 point) =>
        new(faceId, new Vec2(point.X, point.Y), new Vec2(point.X, point.Y), point);

    private sealed record Fixture(DesignProject Project, CoreHeatingPath Path, FaceRefId FaceId, ControlPointId SharedPointId);
}
