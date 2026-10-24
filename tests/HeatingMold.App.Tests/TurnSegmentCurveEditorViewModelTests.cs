using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using HeatingMold.App;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Editing;
using HeatingMold.OccBridge;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.App.Tests;

/// <summary>회전 곡선 입력이 안정 참조와 공유 끝점을 보존하는지 검사합니다.</summary>
[Collection("WPF Bridge")]
public sealed class TurnSegmentCurveEditorViewModelTests
{
    /// <summary>활성 회전 구간만 안정 ID와 revision을 가진 선택 항목으로 노출합니다.</summary>
    [Fact]
    public void Refresh_ListsOnlyActiveTurnCurvesWithStableIds()
    {
        Fixture fixture = CreateFixture();
        var editor = new TurnSegmentCurveEditorViewModel();

        editor.Refresh(fixture.Project);

        TurnSegmentCurveChoice choice = Assert.Single(editor.Choices);
        Assert.Equal(fixture.Path.Id, choice.PathId);
        Assert.Equal(fixture.ActiveTurn.Id, choice.SegmentId);
        Assert.Equal(fixture.Project.Revision, choice.Revision);
        Assert.Equal(2, editor.InteriorPoints.Count);
    }

    /// <summary>역방향 다중선의 정의 순서와 양 끝점을 보존하며 내부점만 요청에 반영합니다.</summary>
    [Fact]
    public void TryCreateRequest_PreservesReversedCurveEndpointsAndDirection()
    {
        Fixture fixture = CreateFixture(CurveDirection.Reversed);
        var editor = new TurnSegmentCurveEditorViewModel();
        editor.Refresh(fixture.Project);
        editor.InteriorPoints[0].X = "1.25";
        editor.InteriorPoints[0].Y = "1.5";

        bool created = editor.TryCreateRequest(out TurnSegmentCurveEditRequest request, out string diagnostic);

        Assert.True(created, diagnostic);
        Assert.Equal(fixture.ActiveTurn.Id, request.SegmentId);
        Assert.Equal(fixture.Project.Revision, request.BaseRevision);
        Assert.Equal(CurveDirection.Reversed, request.CandidateCurve.Direction);
        Assert.Equal(fixture.Curve.Points[0], request.CandidateCurve.Points[0]);
        Assert.Equal(fixture.Curve.Points[^1], request.CandidateCurve.Points[^1]);
        Assert.Equal(new Vec3(1.25, 1.5, 0), request.CandidateCurve.Points[^2]);
    }

    /// <summary>설계 revision이 바뀌면 같은 경로·조각 ID를 유지하고 현재 좌표를 다시 불러옵니다.</summary>
    [Fact]
    public void Refresh_PreservesStableSelectionAndReloadsNewRevisionCoordinates()
    {
        Fixture fixture = CreateFixture();
        var editor = new TurnSegmentCurveEditorViewModel();
        editor.Refresh(fixture.Project);
        editor.InteriorPoints[0].X = "9";
        DesignProject nextProject = CreateProject(fixture.Path, new DesignRevision(9), fixture.Project.Id);

        editor.Refresh(nextProject);

        Assert.Equal(fixture.Path.Id, editor.SelectedChoice.PathId);
        Assert.Equal(fixture.ActiveTurn.Id, editor.SelectedChoice.SegmentId);
        Assert.Equal(new DesignRevision(9), editor.SelectedChoice.Revision);
        Assert.Equal("1", editor.InteriorPoints[0].X);
    }

    /// <summary>비수치와 무한 좌표를 요청에 넣지 않고 사용자 진단을 반환합니다.</summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryCreateRequest_RejectsNonFiniteOrInvalidCoordinates(string coordinate)
    {
        Fixture fixture = CreateFixture();
        var editor = new TurnSegmentCurveEditorViewModel();
        editor.Refresh(fixture.Project);
        editor.InteriorPoints[0].X = coordinate;

        bool created = editor.TryCreateRequest(out TurnSegmentCurveEditRequest request, out string diagnostic);

        Assert.False(created);
        Assert.Null(request);
        Assert.Contains("유한한 숫자", diagnostic);
    }

    /// <summary>연속 내부점이 겹치는 곡선은 생성하지 않고 입력 오류로 돌려줍니다.</summary>
    [Fact]
    public void TryCreateRequest_RejectsAdjacentDuplicateInteriorPoints()
    {
        Fixture fixture = CreateFixture();
        var editor = new TurnSegmentCurveEditorViewModel();
        editor.Refresh(fixture.Project);
        editor.InteriorPoints[0].X = "2";
        editor.InteriorPoints[0].Y = "1";

        bool created = editor.TryCreateRequest(out TurnSegmentCurveEditRequest request, out string diagnostic);

        Assert.False(created);
        Assert.Null(request);
        Assert.Contains("연속 내부점", diagnostic);
    }

    /// <summary>미리보기·취소·부분 표시 실패가 Native 임시 표시만 바꾸고 확정 snapshot을 보존합니다.</summary>
    [Fact]
    public void WindowPreviewAndCancel_PreserveCommittedSnapshot()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            Fixture fixture = CreateFixture();
            var initialSnapshot = new SessionSnapshot(
                fixture.Project,
                runtimeDocument: null,
                geometry: null,
                electricalResults: [],
                areaAllocation: null,
                electricalAreaResult: null,
                currentInputDraft: null,
                dirtySinceSave: false,
                lastSavedRevision: null,
                isPresentationOutOfSync: false);
            var session = new ProjectSession(initialSnapshot);
            var events = new ApplicationEventAdapter();
            var coordinator = new CandidateCoordinator(
                session,
                AppCompositionRoot.CreateGeometryWorkspace(),
                new SingleThreadOperationContext(),
                events);
            AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices() with
            {
                ProjectSession = session,
                Events = events,
                EditTurnSegmentCurve = new EditTurnSegmentCurve(session, coordinator)
            };
            var window = new MainWindow(services);
            MainWindowViewModel viewModel = GetViewModel(window);
            viewModel.TurnSegmentCurves.Refresh(fixture.Project);
            viewModel.TurnSegmentCurves.InteriorPoints[0].X = "1.5";

            InvokeWindowHandler(window, "PreviewTurnSegmentCurve_Click");

            Assert.Same(initialSnapshot, session.Current);
            Assert.Contains(1.5, Assert.Single(Bridge.ControlPointEditPreviewPolylines));
            Assert.True(viewModel.TurnSegmentCurves.IsPreviewActive);
            Assert.Equal("미리보기 중 · 확정 설계는 바뀌지 않았습니다.", window.TurnSegmentCurveEditorStatusText.Text);

            InvokeWindowHandler(window, "CancelTurnSegmentCurve_Click");

            Assert.Empty(Bridge.ControlPointEditPreviewPolylines);
            Assert.Equal(1, Bridge.ClearControlPointEditPreviewCount);
            Assert.False(viewModel.TurnSegmentCurves.IsPreviewActive);
            Assert.Same(initialSnapshot, session.Current);

            Bridge.FailControlPointEditPreviewAfterSetting = true;
            InvokeWindowHandler(window, "PreviewTurnSegmentCurve_Click");

            Assert.False(viewModel.TurnSegmentCurves.IsPreviewActive);
            Assert.Empty(Bridge.ControlPointEditPreviewPolylines);
            Assert.Equal(2, Bridge.ClearControlPointEditPreviewCount);
            Assert.Contains("표시하지 못했습니다", window.TurnSegmentCurveEditorStatusText.Text);
            Assert.Same(initialSnapshot, session.Current);
        });
    }

    /// <summary>테스트 창에서 내부 편집기 상태를 가져옵니다.</summary>
    private static MainWindowViewModel GetViewModel(MainWindow window)
    {
        FieldInfo field = typeof(MainWindow).GetField("_mainWindowViewModel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, "_mainWindowViewModel");
        return (MainWindowViewModel)field.GetValue(window);
    }

    /// <summary>테스트 창의 Preview/Cancel UI 이벤트 처리기를 직접 호출합니다.</summary>
    private static void InvokeWindowHandler(MainWindow window, string handlerName)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, handlerName);
        method.Invoke(window, [window, new RoutedEventArgs()]);
    }

    /// <summary>편집 입력을 검증할 회전·일반·비활성 경로와 프로젝트를 만듭니다.</summary>
    private static Fixture CreateFixture(CurveDirection direction = CurveDirection.Forward)
    {
        var points = new[]
        {
            new Vec3(0, 0, 0),
            new Vec3(1, 1, 0),
            new Vec3(2, 1, 0),
            new Vec3(3, 0, 0)
        };
        var curve = new PolylineCurveDefinition(points, direction);
        ControlPoint[] controlPoints = points.Select(point => new ControlPoint(ControlPointId.New(), point)).ToArray();
        var activeTurn = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.Turn,
            controlPoints[0].Id,
            controlPoints[^1].Id,
            0,
            isActive: true,
            curve,
            laneIndex: null);
        var heatingRun = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            controlPoints[0].Id,
            controlPoints[^1].Id,
            1,
            isActive: true,
            new LineCurveDefinition(points[0], points[^1]),
            laneIndex: null);
        var inactiveTurn = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.Turn,
            controlPoints[0].Id,
            controlPoints[^1].Id,
            2,
            isActive: false,
            curve,
            laneIndex: null);
        var segments = new[] { activeTurn, heatingRun, inactiveTurn };
        FaceRefId faceId = FaceRefId.New();
        BoundaryLoop loop = CreateLoop(faceId);
        var anchor = new SurfaceAnchor(faceId, new Vec2(0, 0), new Vec2(0, 0), new Vec3(0, 0, 0));
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var placement = new PlacementConstraints(
            new LengthMm(3),
            new LengthMm(1),
            new LengthMm(0),
            PlacementDirection.AlongX,
            anchor,
            new SurfaceAnchor(faceId, new Vec2(3, 0), new Vec2(3, 0), new Vec3(3, 0, 0)),
            new ExitSideReference([new BoundarySegmentRange(loop.Segments[0].Id, 0.1, 0.9)]));
        var path = new CoreHeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(RegionId.New(), placement, wire, "turn-curve-editor-test-v1"),
            controlPoints,
            segments,
            segments);

        return new Fixture(
            path,
            activeTurn,
            curve,
            CreateProject(path, new DesignRevision(8), ProjectId.New()));
    }

    /// <summary>revision이 지정된 경로를 설계 프로젝트의 확정 컬렉션에 넣습니다.</summary>
    private static DesignProject CreateProject(
        CoreHeatingPath path,
        DesignRevision revision,
        ProjectId projectId)
    {
        DateTimeOffset createdUtc = DateTimeOffset.UnixEpoch;
        var sourceModel = new ModelIdentity(new string('a', 64), 1, "STEP", "test-v1", "face-v1");
        var workingFrame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));

        return new DesignProject(
            projectId,
            schemaVersion: 1,
            revision,
            "회전 곡선 편집 테스트",
            createdUtc,
            createdUtc,
            sourceModel,
            workingFrame,
            Array.Empty<FaceReference>(),
            Array.Empty<HeatingRegion>(),
            wireSpecification: null,
            placementConstraints: null,
            [path],
            Array.Empty<Circuit>(),
            Array.Empty<ConnectionPoint>(),
            Array.Empty<ConnectionPath>(),
            DesignRuleSet.Initial);
    }

    /// <summary>배치 입력이 참조할 사각 경계 루프를 만듭니다.</summary>
    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] vertices = [new(0, 0), new(3, 0), new(3, 3), new(0, 3)];
        return new BoundaryLoop(
            BoundaryLoopId.New(),
            vertices.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(),
                start,
                vertices[(index + 1) % vertices.Length],
                faceId)),
            isHole: false);
    }

    /// <summary>회전 곡선 화면 테스트에 필요한 안정 ID와 프로젝트를 묶습니다.</summary>
    private sealed record Fixture(
        CoreHeatingPath Path,
        HeatingPathSegment ActiveTurn,
        PolylineCurveDefinition Curve,
        DesignProject Project);
}
