using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>신규 경로 생성이 명시 피치·방향·끝점을 보존하고 끊긴 영역을 잇지 않는지 검사합니다.</summary>
public sealed class HeatingPathPlannerTests
{
    /// <summary>가로 배치에서 외곽 간격과 지름을 더한 피치를 유지하며 단일 연속 초안을 만듭니다.</summary>
    [Fact]
    public void Generate_UsesClearGapPlusDiameterAndPreservesAnchors()
    {
        HeatingPathGenerationRequest request = CreateRequest(
            [
                [new Vec2(0, 0), new Vec2(100, 0), new Vec2(100, 50), new Vec2(0, 50)]
            ],
            PlacementDirection.AlongX,
            new Vec2(20, 0),
            new Vec2(80, 0));

        var result = new ConstrainedHeatingPathPlanner().Generate(request);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code + ":" + string.Join("|", issue.Evidence))));
        HeatingPathDraft draft = result.GetValueOrThrow();
        HeatingPathDraftSegment[] runs = draft.Segments
            .Where(segment => segment.Kind == HeatingPathSegmentKind.HeatingRun)
            .OrderBy(segment => segment.LaneIndex)
            .ToArray();
        Assert.Equal(draft.LaneCount, runs.Length);
        Assert.Equal(new Vec2(20, 0), draft.ControlPoints[0].WorkingUv);
        Assert.Equal(new Vec2(80, 0), draft.ControlPoints[^1].WorkingUv);
        Assert.All(runs, run => Assert.Equal(run.WorkingUvPoints[0].Y, run.WorkingUvPoints[^1].Y, 6));
        Assert.All(runs.Zip(runs.Skip(1)), pair =>
            Assert.Equal(10.3, Math.Abs(pair.Second.WorkingUvPoints[0].Y - pair.First.WorkingUvPoints[0].Y), 5));
        Assert.Equal(Enumerable.Range(0, draft.Segments.Count), draft.Segments.Select(segment => segment.Sequence));
        Assert.All(draft.Segments.Zip(draft.Segments.Skip(1)), pair =>
            Assert.Equal(pair.First.EndControlPointId, pair.Second.StartControlPointId));
        Assert.Contains(draft.Segments, segment => segment.Kind == HeatingPathSegmentKind.Turn);
        Assert.Empty(result.Issues.Where(issue => issue.Severity == HeatingMold.Core.Validation.ValidationSeverity.Error));
    }

    /// <summary>세로 방향은 자동 bbox 각도 대신 지정한 작업 Y축을 따라 레인을 만듭니다.</summary>
    [Fact]
    public void Generate_AlongYProducesVerticalHeatingRuns()
    {
        HeatingPathGenerationRequest request = CreateRequest(
            [
                [new Vec2(50, 0), new Vec2(50, 100), new Vec2(0, 100), new Vec2(0, 0)]
            ],
            PlacementDirection.AlongY,
            new Vec2(50, 20),
            new Vec2(50, 80));

        var result = new ConstrainedHeatingPathPlanner().Generate(request);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        HeatingPathDraftSegment run = Assert.Single(result.GetValueOrThrow().Segments
            .Where(segment => segment.Kind == HeatingPathSegmentKind.HeatingRun));
        Assert.Equal(run.WorkingUvPoints[0].X, run.WorkingUvPoints[^1].X, 6);
        Assert.NotEqual(run.WorkingUvPoints[0].Y, run.WorkingUvPoints[^1].Y);
    }

    /// <summary>모든 수평선이 둘 이상의 분리 구간으로 나뉘면 중간을 잇는 후보를 만들지 않습니다.</summary>
    [Fact]
    public void Generate_RejectsDisconnectedComponentsInsteadOfBridgingThem()
    {
        HeatingPathGenerationRequest request = CreateRequest(
            [
                [new Vec2(0, 0), new Vec2(40, 0), new Vec2(40, 100), new Vec2(0, 100)],
                [new Vec2(60, 0), new Vec2(100, 0), new Vec2(100, 100), new Vec2(60, 100)]
            ],
            PlacementDirection.AlongX,
            new Vec2(20, 0),
            new Vec2(80, 0));

        var result = new ConstrainedHeatingPathPlanner().Generate(request);

        Assert.False(result.IsSuccess);
        ValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal("NoSupportedLaneLayout", issue.Code);
        Assert.Contains(issue.Evidence, evidence =>
            evidence.Contains("다중 구간 라우팅은 아직 지원하지 않습니다", StringComparison.Ordinal));
    }

    /// <summary>회전 생성기는 고정 점 개수 대신 편차와 표본 간격으로 U턴을 세분화합니다.</summary>
    [Fact]
    public void TurnBuilder_RefinesUntilRequestedSpacingIsSatisfied()
    {
        var result = new TurnBuilder().Build(
            new Vec2(0, 0),
            new Vec2(0, 10),
            previousLaneForward: true,
            centerPitchMm: 10,
            maximumDeviationMm: 0.02,
            maximumSampleSpacingMm: 0.5);

        Assert.True(result.IsSuccess);
        TurnBuildResult turn = result.GetValueOrThrow();
        Assert.Equal(new Vec2(0, 0), turn.Points[0]);
        Assert.Equal(new Vec2(0, 10), turn.Points[^1]);
        Assert.True(turn.Points.Zip(turn.Points.Skip(1)).Max(pair => pair.First.DistanceTo(pair.Second)) <= 0.5);
        Assert.True(turn.Points.Count > 12);
    }

    private static HeatingPathGenerationRequest CreateRequest(
        IReadOnlyList<IReadOnlyList<Vec2>> outerRings,
        PlacementDirection direction,
        Vec2 startUv,
        Vec2 endUv)
    {
        FaceRefId faceId = FaceRefId.New();
        BoundaryLoop[] originalLoops = outerRings.Select(ring => CreateLoop(ring, faceId)).ToArray();
        BoundaryLoop[] effectiveLoops = outerRings.Select(ring => CreateLoop(ring, faceId)).ToArray();
        var exitRanges = effectiveLoops.Select(loop => new BoundarySegmentRange(
            loop.Segments[0].Id,
            0.1,
            0.9)).ToArray();
        var exitSide = new ExitSideReference(exitRanges);
        var region = new HeatingRegion(
            RegionId.New(),
            [faceId],
            originalLoops,
            [],
            [],
            effectiveLoops,
            exitSide);
        SurfaceAnchor startAnchor = CreateAnchor(faceId, startUv);
        SurfaceAnchor endAnchor = CreateAnchor(faceId, endUv);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10));
        var constraints = new PlacementConstraints(
            new LengthMm(10),
            new LengthMm(2),
            new LengthMm(0),
            direction,
            startAnchor,
            endAnchor,
            exitSide);
        var workingSystem = new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(7),
            new ModelRevision(3),
            new ProjectionFrame(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
        return new HeatingPathGenerationRequest(
            ProjectId.New(),
            new DesignRevision(4),
            region,
            wire,
            constraints,
            workingSystem,
            new ModelRevision(3),
            ManualEditPolicy.PreserveIfCompatible);
    }

    private static BoundaryLoop CreateLoop(IReadOnlyList<Vec2> vertices, FaceRefId faceId) => new(
        BoundaryLoopId.New(),
        vertices.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(),
            start,
            vertices[(index + 1) % vertices.Count],
            faceId)),
        isHole: false);

    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec2 uv) => new(
        faceId,
        uv,
        uv,
        new Vec3(uv.X, uv.Y, 0));
}
