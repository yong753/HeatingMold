using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Editing;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>회전 구간 내부 곡선 편집의 안정 ID와 공유 끝점 보존을 검사합니다.</summary>
public sealed class TurnSegmentCurveEditServiceTests
{
    /// <summary>안정 조각 ID로 곡선만 교체하고 끝점·경로 ID·순서를 보존합니다.</summary>
    [Fact]
    public void CreateDraft_ReplacesTurnInteriorAndPreservesPathIdentity()
    {
        Fixture fixture = CreateFixture();
        var candidateCurve = new PolylineCurveDefinition(
            [fixture.TurnStart.Position, new Vec3(4.4, 1.2, 0), new Vec3(5.6, 1.2, 0), fixture.TurnEnd.Position]);
        var request = CreateRequest(fixture, candidateCurve);

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        TurnSegmentCurveEditDraft draft = result.GetValueOrThrow();
        HeatingPathSegment edited = draft.CandidatePath.Segments.Single(segment => segment.Id == fixture.Turn.Id);
        Assert.Same(candidateCurve, edited.Curve);
        Assert.Equal(fixture.Turn.Id, edited.Id);
        Assert.Equal(fixture.Turn.Sequence, edited.Sequence);
        Assert.Equal(fixture.Turn.StartControlPointId, edited.StartControlPointId);
        Assert.Equal(fixture.Turn.EndControlPointId, edited.EndControlPointId);
        Assert.Equal(fixture.TurnStart.Position, draft.CandidateCurve.Points[0]);
        Assert.Equal(fixture.TurnEnd.Position, draft.CandidateCurve.Points[^1]);
        Assert.Equal(fixture.Path.GeneratedSegments, draft.CandidatePath.GeneratedSegments);
        Assert.Equal(fixture.Path.Segments.Where(segment => segment.Id != fixture.Turn.Id),
            draft.CandidatePath.Segments.Where(segment => segment.Id != fixture.Turn.Id));
        Assert.Equal(fixture.Path.Id, draft.CandidatePath.Id);
    }

    /// <summary>역방향 곡선에서도 경로 시작과 끝 공유 절점을 바꾸지 않습니다.</summary>
    [Fact]
    public void CreateDraft_PreservesReversedCurvePathEndpoints()
    {
        Fixture fixture = CreateFixture(direction: CurveDirection.Reversed);
        var candidateCurve = new PolylineCurveDefinition(
            [fixture.TurnEnd.Position, new Vec3(5.5, -1, 0), fixture.TurnStart.Position],
            CurveDirection.Reversed);
        var request = CreateRequest(fixture, candidateCurve);

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.TurnStart.Position, result.GetValueOrThrow().CandidateCurve.Points[^1]);
        Assert.Equal(fixture.TurnEnd.Position, result.GetValueOrThrow().CandidateCurve.Points[0]);
    }

    /// <summary>공유 절점 좌표를 바꾸려는 곡선 교체는 거부합니다.</summary>
    [Fact]
    public void CreateDraft_RejectsChangedSharedEndpoint()
    {
        Fixture fixture = CreateFixture();
        var request = CreateRequest(fixture, new PolylineCurveDefinition(
            [new Vec3(4.1, 0, 0), new Vec3(5, 2, 0), fixture.TurnEnd.Position]));

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("TurnSegmentEndpointChangeRejected", Assert.Single(result.Issues).Code);
    }

    /// <summary>회전 역할이 아닌 조각을 대상으로 한 편집을 거부합니다.</summary>
    [Fact]
    public void CreateDraft_RejectsNonTurnSegmentId()
    {
        Fixture fixture = CreateFixture();
        var request = new TurnSegmentCurveEditRequest(
            fixture.Path.Id,
            fixture.FirstRun.Id,
            fixture.Project.Revision,
            new PolylineCurveDefinition([fixture.PathStart.Position, new Vec3(2, 1, 0), fixture.TurnStart.Position]));

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("TurnSegmentTargetInvalid", Assert.Single(result.Issues).Code);
    }

    /// <summary>기준 revision이 지난 곡선 편집 요청을 거부합니다.</summary>
    [Fact]
    public void CreateDraft_RejectsStaleRevision()
    {
        Fixture fixture = CreateFixture();
        var request = new TurnSegmentCurveEditRequest(
            fixture.Path.Id,
            fixture.Turn.Id,
            new DesignRevision(fixture.Project.Revision.Value - 1),
            new PolylineCurveDefinition([fixture.TurnStart.Position, new Vec3(5, 2, 0), fixture.TurnEnd.Position]));

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("StaleTurnSegmentEdit", Assert.Single(result.Issues).Code);
    }

    /// <summary>비활성 회전 구간은 topology에서 다시 활성화하기 전까지 편집하지 않습니다.</summary>
    [Fact]
    public void CreateDraft_RejectsInactiveTurnSegment()
    {
        Fixture fixture = CreateFixture(isTurnActive: false);
        var request = CreateRequest(fixture, new PolylineCurveDefinition(
            [fixture.TurnStart.Position, new Vec3(5, 2, 0), fixture.TurnEnd.Position]));

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("InactiveTurnSegmentNotEditable", Assert.Single(result.Issues).Code);
    }

    /// <summary>내부 점이 없는 두 점 곡선을 회전 형상으로 확정하지 않습니다.</summary>
    [Fact]
    public void CreateDraft_RequiresAtLeastOneInteriorPoint()
    {
        Fixture fixture = CreateFixture();
        var request = CreateRequest(fixture,
            new PolylineCurveDefinition([fixture.TurnStart.Position, fixture.TurnEnd.Position]));

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("TurnSegmentShapeMissing", Assert.Single(result.Issues).Code);
    }

    /// <summary>기존 내부 점과 같은 곡선은 변경 후보로 만들지 않습니다.</summary>
    [Fact]
    public void CreateDraft_RejectsUnchangedCurve()
    {
        Fixture fixture = CreateFixture();
        var request = CreateRequest(fixture, (PolylineCurveDefinition)fixture.Turn.Curve);

        OperationResult<TurnSegmentCurveEditDraft> result =
            new TurnSegmentCurveEditService().CreateDraft(request, fixture.Project);

        Assert.False(result.IsSuccess);
        Assert.Equal("TurnSegmentCurveUnchanged", Assert.Single(result.Issues).Code);
    }

    private static TurnSegmentCurveEditRequest CreateRequest(Fixture fixture, PolylineCurveDefinition curve) =>
        new(fixture.Path.Id, fixture.Turn.Id, fixture.Project.Revision, curve);

    private static Fixture CreateFixture(
        CurveDirection direction = CurveDirection.Forward,
        bool isTurnActive = true)
    {
        FaceRefId faceId = FaceRefId.New();
        RegionId regionId = RegionId.New();
        BoundaryLoop originalLoop = CreateLoop(faceId);
        BoundaryLoop effectiveLoop = CreateLoop(faceId);
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(effectiveLoop.Segments[0].Id, 0, 1)]);
        var region = new HeatingRegion(
            regionId, [faceId], [originalLoop], [], [], [effectiveLoop], exitSide);
        SurfaceAnchor startAnchor = CreateAnchor(faceId, new Vec3(0, 0, 0));
        SurfaceAnchor endAnchor = CreateAnchor(faceId, new Vec3(10, 0, 0));
        var constraints = new PlacementConstraints(
            new LengthMm(1), new LengthMm(1), new LengthMm(0), PlacementDirection.AlongX,
            startAnchor, endAnchor, exitSide);
        var generationSource = new HeatingPathGenerationSource(
            regionId,
            constraints,
            new WireSpecification("Fixture alloy", new LengthMm(0.3), new ResistanceOhmsPerMeter(100)),
            "turn-edit-test-v1");

        var pathStart = new ControlPoint(ControlPointId.New(), new Vec3(0, 0, 0));
        var turnStart = new ControlPoint(ControlPointId.New(), new Vec3(4, 0, 0));
        var turnEnd = new ControlPoint(ControlPointId.New(), new Vec3(6, 0, 0));
        var pathEnd = new ControlPoint(ControlPointId.New(), new Vec3(10, 0, 0));
        var firstRun = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            pathStart.Id, turnStart.Id, 0, true,
            new LineCurveDefinition(pathStart.Position, turnStart.Position));

        Vec3[] turnPoints = direction == CurveDirection.Forward
            ? [turnStart.Position, new Vec3(5, 1, 0), turnEnd.Position]
            : [turnEnd.Position, new Vec3(5, 1, 0), turnStart.Position];
        var turn = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
            turnStart.Id, turnEnd.Id, 1, isTurnActive,
            new PolylineCurveDefinition(turnPoints, direction));
        var secondRun = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            turnEnd.Id, pathEnd.Id, 2, true,
            new LineCurveDefinition(turnEnd.Position, pathEnd.Position));
        HeatingPathSegment[] segments = [firstRun, turn, secondRun];
        var path = new CoreHeatingPath(
            HeatingPathId.New(),
            generationSource,
            [pathStart, turnStart, turnEnd, pathEnd],
            segments,
            segments);

        byte[] sourceBytes = [3, 1, 4, 1, 5];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(5), "Turn edit fixture",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new ModelIdentity(sourceHash, sourceBytes.Length, "TurnEditFixture", "1", "face-v1"),
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [new FaceReference(faceId, sourceHash, 0, FaceGeometryKind.Plane, new AreaMm2(100), default, "turn-face", [])],
            [region],
            generationSource.WireSpecification,
            constraints,
            [path], [], [], []);

        return new Fixture(project, path, firstRun, turn, pathStart, turnStart, turnEnd);
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] vertices = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        BoundarySegment[] segments = vertices.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], faceId)).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }

    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec3 point) =>
        new(faceId, new Vec2(point.X, point.Y), new Vec2(point.X, point.Y), point);

    private sealed record Fixture(
        DesignProject Project,
        CoreHeatingPath Path,
        HeatingPathSegment FirstRun,
        HeatingPathSegment Turn,
        ControlPoint PathStart,
        ControlPoint TurnStart,
        ControlPoint TurnEnd);
}
