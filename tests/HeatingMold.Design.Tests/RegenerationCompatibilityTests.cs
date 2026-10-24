using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Editing;
using HeatingMold.Design.HeatingPath;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>재생성 시 사용자 편집을 레인 의미로 대응하고 모호한 경우 교체를 요구하는지 검사합니다.</summary>
public sealed class RegenerationCompatibilityTests
{
    /// <summary>생성 원본과 같은 편집 전 경로에는 보존할 override가 없다고 보고합니다.</summary>
    [Fact]
    public void Analyze_ReturnsNoOverridesForUnchangedPath()
    {
        Fixture fixture = CreateFixture(editedLane: null);

        var result = new RegenerationCompatibility().Analyze(fixture.Path, fixture.Draft, fixture.Revision);

        Assert.True(result.IsSuccess);
        Assert.Equal(RegenerationCompatibilityStatus.NoOverrides, result.GetValueOrThrow().Status);
    }

    /// <summary>편집된 레인에 유일한 새 대응이 있으면 안정 ID 대신 레인 의미를 반환합니다.</summary>
    [Fact]
    public void Analyze_MapsEditedRunToSameLaneMeaning()
    {
        Fixture fixture = CreateFixture(editedLane: 0);

        var result = new RegenerationCompatibility().Analyze(fixture.Path, fixture.Draft, fixture.Revision);

        Assert.True(result.IsSuccess);
        RegenerationCompatibilityResult compatibility = result.GetValueOrThrow();
        Assert.Equal(RegenerationCompatibilityStatus.CanPreserveOverrides, compatibility.Status);
        Assert.Equal(2, compatibility.Mappings.Count);
        RegenerationSegmentMapping mapping = Assert.Single(compatibility.Mappings.Where(item => item.SemanticKey == "run:0"));
        Assert.Equal("run:0", mapping.SemanticKey);
        Assert.Equal(fixture.Path.Segments[0].Id, mapping.PreviousSegmentId);
        Assert.Equal(fixture.Draft.Segments[0].Id, mapping.ReplacementSegmentId);
    }

    /// <summary>공유 절점 이동량을 새 경로의 의미 대응 구간에 적용해 양쪽 연결을 유지합니다.</summary>
    [Fact]
    public void Preserve_ReappliesSharedPointOffsetToMappedSegments()
    {
        Fixture fixture = CreateFixture(editedLane: 0);
        RegenerationCompatibilityResult compatibility = new RegenerationCompatibility()
            .Analyze(fixture.Path, fixture.Draft, fixture.Revision)
            .GetValueOrThrow();
        CoreHeatingPath replacement = CreateReplacement(fixture);

        var result = new ManualEditPreserver().Preserve(fixture.Path, replacement, compatibility);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CoreHeatingPath preserved = result.GetValueOrThrow();
        ControlPoint movedPoint = preserved.ControlPoints[1];
        Assert.Equal(new Vec3(10, 2, 0), movedPoint.Position);

        HeatingPathSegment run = preserved.Segments.Single(segment => segment.Id == fixture.Draft.Segments[0].Id);
        HeatingPathSegment turn = preserved.Segments.Single(segment => segment.Id == fixture.Draft.Segments[1].Id);
        Assert.Equal(movedPoint.Position, ((PolylineCurveDefinition)run.Curve).Points[^1]);
        Assert.Equal(movedPoint.Position, ((PolylineCurveDefinition)turn.Curve).Points[0]);
        Assert.Equal(replacement.GeneratedSegments, preserved.GeneratedSegments);
    }

    /// <summary>새 초안에서 레인 의미를 찾을 수 없으면 자동 편집 보존을 거부합니다.</summary>
    [Fact]
    public void Analyze_RequiresReplaceWhenEditedLaneDisappears()
    {
        Fixture fixture = CreateFixture(editedLane: 4);

        var result = new RegenerationCompatibility().Analyze(fixture.Path, fixture.Draft, fixture.Revision);

        Assert.True(result.IsSuccess);
        RegenerationCompatibilityResult compatibility = result.GetValueOrThrow();
        Assert.Equal(RegenerationCompatibilityStatus.ReplaceRequired, compatibility.Status);
        Assert.Equal("EditedSegmentHasNoUniqueRegenerationMatch", Assert.Single(compatibility.Issues).Code);
    }

    /// <summary>끝점 이동 이외의 곡선 형상 편집은 자동 재생성 보존 대상에서 제외합니다.</summary>
    [Fact]
    public void Analyze_RejectsCurveShapeEditBeyondEndpoints()
    {
        Fixture fixture = CreateFixture(editedLane: 0);
        HeatingPathSegment edited = fixture.Path.Segments[0];
        var changedShape = new HeatingPathSegment(
            edited.Id,
            edited.Kind,
            edited.StartControlPointId,
            edited.EndControlPointId,
            edited.Sequence,
            edited.IsActive,
            new PolylineCurveDefinition([new Vec3(0, 0, 0), new Vec3(5, 3, 0), new Vec3(10, 2, 0)]),
            edited.LaneIndex);
        CoreHeatingPath path = WithCurrentSegment(fixture.Path, changedShape);

        RegenerationCompatibilityResult compatibility = new RegenerationCompatibility()
            .Analyze(path, fixture.Draft, fixture.Revision)
            .GetValueOrThrow();

        Assert.Equal(RegenerationCompatibilityStatus.ReplaceRequired, compatibility.Status);
        Assert.Contains(compatibility.Issues, issue => issue.Code == "EditedSegmentCannotBeRebased");
    }

    /// <summary>고정 시작 앵커를 움직인 경로는 재생성 때 자동으로 편집하지 않습니다.</summary>
    [Fact]
    public void Analyze_RequiresReplaceWhenFixedAnchorWasMoved()
    {
        Fixture fixture = CreateFixture(editedLane: 0);
        ControlPoint start = fixture.Path.ControlPoints[0];
        var movedStart = new ControlPoint(start.Id, new Vec3(0, 1, 0));
        ControlPoint[] controls = fixture.Path.ControlPoints
            .Select(point => point.Id == start.Id ? movedStart : point)
            .ToArray();
        HeatingPathSegment edited = fixture.Path.Segments[0];
        var changedSegment = new HeatingPathSegment(
            edited.Id,
            edited.Kind,
            edited.StartControlPointId,
            edited.EndControlPointId,
            edited.Sequence,
            edited.IsActive,
            new LineCurveDefinition(movedStart.Position, fixture.Path.ControlPoints[1].Position),
            edited.LaneIndex);
        CoreHeatingPath path = WithCurrentSegment(fixture.Path, changedSegment, controls);

        RegenerationCompatibilityResult compatibility = new RegenerationCompatibility()
            .Analyze(path, fixture.Draft, fixture.Revision)
            .GetValueOrThrow();

        Assert.Equal(RegenerationCompatibilityStatus.ReplaceRequired, compatibility.Status);
        Assert.Contains(compatibility.Issues, issue => issue.Code == "EditedSegmentCannotBeRebased");
    }

    private static Fixture CreateFixture(int? editedLane)
    {
        RegionId regionId = RegionId.New();
        FaceRefId faceId = FaceRefId.New();
        var loop = CreateLoop(faceId);
        var exitSide = new ExitSideReference([new BoundarySegmentRange(loop.Segments[0].Id, 0.1, 0.9)]);
        var placement = new PlacementConstraints(
            new LengthMm(10), new LengthMm(2), new LengthMm(0), PlacementDirection.AlongX,
            new SurfaceAnchor(faceId, default, default, default),
            new SurfaceAnchor(faceId, new Vec2(10, 0), new Vec2(10, 0), new Vec3(10, 0, 0)),
            exitSide);
        var source = new HeatingPathGenerationSource(
            regionId,
            placement,
            new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(4)),
            "regeneration-test-v1");

        ControlPoint[] controls =
        [
            new(ControlPointId.New(), new Vec3(0, 0, 0)),
            new(ControlPointId.New(), new Vec3(10, 0, 0)),
            new(ControlPointId.New(), new Vec3(10, 1, 0)),
            new(ControlPointId.New(), new Vec3(20, 1, 0))
        ];
        HeatingPathSegment[] generated =
        [
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun, controls[0].Id, controls[1].Id, 0, true,
                new LineCurveDefinition(controls[0].Position, controls[1].Position), 0),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn, controls[1].Id, controls[2].Id, 1, true,
                new LineCurveDefinition(controls[1].Position, controls[2].Position), 0),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun, controls[2].Id, controls[3].Id, 2, true,
                new LineCurveDefinition(controls[2].Position, controls[3].Position), 1)
        ];
        ControlPoint[] currentControls = controls.ToArray();
        HeatingPathSegment[] current = generated.ToArray();
        if (editedLane.HasValue)
        {
            var editedPoint = new ControlPoint(controls[1].Id, new Vec3(10, 2, 0));
            currentControls[1] = editedPoint;

            HeatingPathSegment original = generated[0];
            current[0] = new HeatingPathSegment(
                original.Id,
                original.Kind,
                original.StartControlPointId,
                original.EndControlPointId,
                original.Sequence,
                original.IsActive,
                new LineCurveDefinition(controls[0].Position, editedPoint.Position),
                original.LaneIndex);
            HeatingPathSegment originalTurn = generated[1];
            current[1] = new HeatingPathSegment(
                originalTurn.Id,
                originalTurn.Kind,
                originalTurn.StartControlPointId,
                originalTurn.EndControlPointId,
                originalTurn.Sequence,
                originalTurn.IsActive,
                new LineCurveDefinition(editedPoint.Position, controls[2].Position),
                originalTurn.LaneIndex);
        }

        var path = new CoreHeatingPath(HeatingPathId.New(), source, currentControls, generated, current);
        HeatingPathDraft draft = CreateDraft(regionId, controls, editedLane ?? 0);
        return new Fixture(path, draft, new DesignRevision(5));
    }

    private static CoreHeatingPath CreateReplacement(Fixture fixture)
    {
        Dictionary<ControlPointId, Vec3> positions = fixture.Draft.ControlPoints.ToDictionary(
            point => point.Id,
            point => new Vec3(point.WorkingUv.X, point.WorkingUv.Y, 0));
        ControlPoint[] controls = fixture.Draft.ControlPoints
            .Select(point => new ControlPoint(point.Id, positions[point.Id]))
            .ToArray();
        HeatingPathSegment[] segments = fixture.Draft.Segments
            .Select(segment => new HeatingPathSegment(
                segment.Id,
                segment.Kind,
                segment.StartControlPointId,
                segment.EndControlPointId,
                segment.Sequence,
                true,
                new PolylineCurveDefinition(segment.WorkingUvPoints.Select(uv => new Vec3(uv.X, uv.Y, 0))),
                segment.LaneIndex))
            .ToArray();
        return new CoreHeatingPath(
            fixture.Draft.Id,
            fixture.Path.GenerationSource,
            controls,
            segments,
            segments);
    }

    private static CoreHeatingPath WithCurrentSegment(
        CoreHeatingPath path,
        HeatingPathSegment replacement,
        IEnumerable<ControlPoint>? controlPoints = null)
    {
        HeatingPathSegment[] segments = path.Segments
            .Select(segment => segment.Id == replacement.Id ? replacement : segment)
            .ToArray();
        return new CoreHeatingPath(
            path.Id,
            path.GenerationSource,
            controlPoints ?? path.ControlPoints,
            path.GeneratedSegments,
            segments);
    }

    private static HeatingPathDraft CreateDraft(
        RegionId regionId,
        IReadOnlyList<ControlPoint> controls,
        int firstLaneIndex)
    {
        HeatingPathDraftControlPoint[] draftPoints = controls
            .Select((point, index) => new HeatingPathDraftControlPoint(
                ControlPointId.New(), new Vec2(index * 10, index == 2 || index == 3 ? 1 : 0)))
            .ToArray();
        HeatingPathDraftSegment[] segments =
        [
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun, draftPoints[0].Id, draftPoints[1].Id,
                0, [draftPoints[0].WorkingUv, draftPoints[1].WorkingUv], firstLaneIndex),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn, draftPoints[1].Id, draftPoints[2].Id,
                1, [draftPoints[1].WorkingUv, draftPoints[2].WorkingUv], 0),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun, draftPoints[2].Id, draftPoints[3].Id,
                2, [draftPoints[2].WorkingUv, draftPoints[3].WorkingUv], 1)
        ];
        return new HeatingPathDraft(
            ProjectId.New(), new DesignRevision(5), HeatingPathId.New(), regionId, 2,
            new HeatingPathCandidateScore(0, 0, 0, 0), draftPoints, segments);
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] points = [new(0, 0), new(20, 0), new(20, 20), new(0, 20)];
        return new BoundaryLoop(
            BoundaryLoopId.New(),
            points.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(), start, points[(index + 1) % points.Length], faceId)),
            false);
    }

    private sealed record Fixture(CoreHeatingPath Path, HeatingPathDraft Draft, DesignRevision Revision);
}
