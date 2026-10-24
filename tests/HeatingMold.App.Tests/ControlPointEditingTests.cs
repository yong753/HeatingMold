using System.Collections.Generic;
using System.Linq;
using HeatingMold.App;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;

namespace HeatingMold.App.Tests;

/// <summary>제어점 화면 목록이 안정 ID를 쓰고 안전하게 편집 대상을 거르는지 검사합니다.</summary>
public sealed class ControlPointEditingTests
{
    /// <summary>내부 절점은 경로·점의 안정 ID를 보존하고 회로 연결 끝점은 제외합니다.</summary>
    [Fact]
    public void CreateControlPointChoices_UsesStableIdsAndExcludesCircuitEndpoints()
    {
        HeatingPath path = CreatePath(
            useUnsupportedCurve: false,
            out ControlPointId startId,
            out ControlPointId middleId,
            out ControlPointId endId);
        var connectionEndpointIds = new HashSet<ControlPointId> { startId, endId };
        var revision = new DesignRevision(12);

        IReadOnlyList<ControlPointChoice> choices = ControlPointEditorViewModel.CreateChoices(
            path,
            pathIndex: 2,
            revision,
            connectionEndpointIds);

        ControlPointChoice choice = Assert.Single(choices);
        Assert.Equal(path.Id, choice.PathId);
        Assert.Equal(middleId, choice.ControlPointId);
        Assert.Equal(path.GenerationSource.RegionId, choice.RegionId);
        Assert.Equal(revision, choice.Revision);
        Assert.Contains(middleId.Value.ToString("N"), choice.DisplayName);
        Assert.Contains("경로 3", choice.DisplayName);
    }

    /// <summary>한 절점에 미지원 B-spline 구간이 연결되면 이동 목록에서 제외합니다.</summary>
    [Fact]
    public void CreateControlPointChoices_ExcludesPointWhenAttachedCurveIsUnsupported()
    {
        HeatingPath path = CreatePath(
            useUnsupportedCurve: true,
            out ControlPointId startId,
            out _,
            out ControlPointId endId);
        var connectionEndpointIds = new HashSet<ControlPointId> { startId, endId };

        IReadOnlyList<ControlPointChoice> choices = ControlPointEditorViewModel.CreateChoices(
            path,
            pathIndex: 0,
            new DesignRevision(12),
            connectionEndpointIds);

        Assert.Empty(choices);
    }

    /// <summary>설계 갱신 뒤 선택 안정 ID를 유지하고 이전 앵커와 드래그를 폐기합니다.</summary>
    [Fact]
    public void RefreshChoices_PreservesSelectionAndClearsStaleInputState()
    {
        HeatingPath path = CreatePath(
            useUnsupportedCurve: false,
            out _,
            out ControlPointId middleId,
            out _);
        ProjectId projectId = ProjectId.New();
        DesignProject project = CreateProject(path, new DesignRevision(12), projectId);
        var editor = new ControlPointEditorViewModel();
        editor.RefreshChoices(project);
        editor.SelectedChoice = editor.Choices.Single(choice => choice.ControlPointId == middleId);
        editor.SetTargetAnchor(CreateAnchor(FaceRefId.New(), new Vec3(5, 1, 0)), project.Revision);
        editor.BeginDrag(new ControlPointDragState(
            editor.SelectedChoice,
            0.05,
            GeometryDocumentId.New(),
            new ModelRevision(4)));

        editor.RefreshChoices(CreateProject(path, new DesignRevision(13), projectId));

        Assert.Equal(middleId, editor.SelectedChoice.ControlPointId);
        Assert.Equal(new DesignRevision(13), editor.SelectedChoice.Revision);
        Assert.Null(editor.TargetAnchor);
        Assert.Null(editor.TargetRevision);
        Assert.Null(editor.ActiveDrag);
    }

    /// <summary>편집 가능성과 곡선 지원 범위를 검사할 공유 절점 경로를 만듭니다.</summary>
    private static HeatingPath CreatePath(
        bool useUnsupportedCurve,
        out ControlPointId startId,
        out ControlPointId middleId,
        out ControlPointId endId)
    {
        FaceRefId faceId = FaceRefId.New();
        RegionId regionId = RegionId.New();
        BoundaryLoop loop = CreateLoop(faceId);
        var exitSide = new ExitSideReference([new BoundarySegmentRange(loop.Segments[0].Id, 0.1, 0.9)]);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var placement = new PlacementConstraints(
            new LengthMm(10),
            new LengthMm(2),
            new LengthMm(0),
            PlacementDirection.AlongX,
            CreateAnchor(faceId, new Vec3(0, 0, 0)),
            CreateAnchor(faceId, new Vec3(10, 0, 0)),
            exitSide);

        ControlPoint start = new(ControlPointId.New(), new Vec3(0, 0, 0));
        ControlPoint middle = new(ControlPointId.New(), new Vec3(5, 0, 0));
        ControlPoint end = new(ControlPointId.New(), new Vec3(10, 0, 0));
        CurveDefinition firstCurve = useUnsupportedCurve
            ? new BSplineCurveDefinition(1, [start.Position, middle.Position], [1, 1], [0, 1], [2, 2], false, 0, 1)
            : new LineCurveDefinition(start.Position, middle.Position);
        HeatingPathSegment[] segments =
        [
            new HeatingPathSegment(
                HeatingPathSegmentId.New(),
                HeatingPathSegmentKind.HeatingRun,
                start.Id,
                middle.Id,
                0,
                true,
                firstCurve,
                0),
            new HeatingPathSegment(
                HeatingPathSegmentId.New(),
                HeatingPathSegmentKind.HeatingRun,
                middle.Id,
                end.Id,
                1,
                true,
                new LineCurveDefinition(middle.Position, end.Position),
                1)
        ];

        startId = start.Id;
        middleId = middle.Id;
        endId = end.Id;
        return new HeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(regionId, placement, wire, "app-control-point-test-v1"),
            [start, middle, end],
            segments,
            segments);
    }

    /// <summary>제어점 선택 항목 검증에 사용할 revision 고정 프로젝트를 만듭니다.</summary>
    private static DesignProject CreateProject(HeatingPath path, DesignRevision revision, ProjectId projectId)
    {
        var modelIdentity = new ModelIdentity(new string('a', 64), 1, "STEP", "test-v1", "face-v1");
        var workingFrame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DesignProject emptyProject = DesignProject.CreateEmpty(projectId, modelIdentity, workingFrame);
        return new DesignProject(
            emptyProject.Id,
            emptyProject.SchemaVersion,
            revision,
            emptyProject.Name,
            emptyProject.CreatedUtc,
            emptyProject.CreatedUtc,
            emptyProject.SourceModel,
            emptyProject.WorkingCoordinateSystem,
            emptyProject.FaceReferences,
            emptyProject.HeatingRegions,
            emptyProject.WireSpecification,
            emptyProject.PlacementConstraints,
            [path],
            emptyProject.Circuits,
            emptyProject.ConnectionPoints,
            emptyProject.ConnectionPaths,
            emptyProject.Rules);
    }

    /// <summary>발열 영역과 배치 제약에 쓸 사각형 경계 루프를 만듭니다.</summary>
    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] vertices = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        return new BoundaryLoop(
            BoundaryLoopId.New(),
            vertices.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(),
                start,
                vertices[(index + 1) % vertices.Length],
                faceId)),
            isHole: false);
    }

    /// <summary>작업 평면과 모델 좌표를 가진 표면 앵커를 만듭니다.</summary>
    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec3 point) =>
        new(faceId, new Vec2(point.X, point.Y), new Vec2(point.X, point.Y), point);
}
