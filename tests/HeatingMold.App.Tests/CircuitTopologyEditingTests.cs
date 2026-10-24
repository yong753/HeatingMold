using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using HeatingMold.App;
using HeatingMold.App.Geometry.Occt;
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
using HeatingMold.Design.Circuits;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>회로 topology 화면이 설계가 없는 상태를 거부하고 초안을 확정하지 않는지 검사합니다.</summary>
[Collection("WPF Bridge")]
public sealed class CircuitTopologyEditingTests
{
    /// <summary>확정 경로가 없으면 회로 편집 모드를 켜지 않습니다.</summary>
    [Fact]
    public void CircuitSplitMode_WithoutCommittedProjectRemainsDisabled()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            window.CircuitSplitModeBox.IsChecked = true;
            InvokePrivate(window, "CircuitSplitMode_Changed", window.CircuitSplitModeBox, new RoutedEventArgs());

            Assert.False(window.CircuitSplitModeBox.IsChecked);
            Assert.Contains("확정 발열 경로", window.StatusText.Text);
            Assert.False(GetViewportMode(window));
        });
    }

    /// <summary>적용 명령은 topology 계획이 없는 빈 세션을 변경하지 않습니다.</summary>
    [Fact]
    public void ApplyCircuitTopology_WithoutProjectLeavesSessionEmpty()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            ProjectSession session = (ProjectSession)GetPrivateField(window, "_projectSession");

            InvokePrivate(window, "ApplyCircuitTopology_Click", window.CircuitSplitModeBox, new RoutedEventArgs());

            Assert.Null(session.Current.CurrentProject);
            Assert.Equal("미리보기: 확정 설계 없음", window.CircuitTopologyPreviewText.Text);
        });
    }

    /// <summary>topology 취소는 모든 초안을 버리고 확정 snapshot을 그대로 보존합니다.</summary>
    [Fact]
    public void CancelCircuitTopology_ClearsDraftsAndPreservesCommittedSnapshot()
    {
        WpfTestThread.Run(() =>
        {
            DesignProject project = CreateEmptyProject();
            var snapshot = new SessionSnapshot(
                project,
                runtimeDocument: null,
                geometry: null,
                electricalResults: [],
                areaAllocation: null,
                electricalAreaResult: null,
                currentInputDraft: null,
                dirtySinceSave: false,
                lastSavedRevision: null,
                isPresentationOutOfSync: false);
            var session = new ProjectSession(snapshot);
            AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices() with
            {
                ProjectSession = session
            };
            var window = new MainWindow(services);
            try
            {
                CircuitTopologyEditorViewModel editor = GetCircuitTopologyEditor(window);
                HeatingPathSegmentId turnSegmentId = HeatingPathSegmentId.New();
                CircuitTopologyEndpointKey endpointKey = new(
                    HeatingPathSegmentId.New(),
                    ControlPointId.New(),
                    ConnectionPointRole.Start);
                var plan = new CircuitTopologyPlan(
                    project.Id,
                    project.Revision,
                    new CircuitPathTopologyAnalysis([], []),
                    new CircuitIdentityMapping([]));
                editor.SetTurnState(turnSegmentId, isActive: false);
                editor.RetainEndpointDrafts([endpointKey]);
                editor.SetPlan(plan);
                editor.PendingSurfaceAnchorEndpointKey = endpointKey;
                window.CircuitSplitModeBox.IsChecked = true;

                InvokePrivate(window, "CancelCircuitTopology_Click", window.CircuitSplitModeBox, new RoutedEventArgs());

                Assert.Same(snapshot, session.Current);
                Assert.Same(project, session.Current.CurrentProject);
                Assert.Empty(editor.TurnStates);
                Assert.Empty(editor.EndpointDrafts);
                Assert.Null(editor.Plan);
                Assert.Null(editor.PendingSurfaceAnchorEndpointKey);
                Assert.False(window.CircuitSplitModeBox.IsChecked);
                Assert.False(GetViewportMode(window));
                Assert.Equal("회로 topology 미리보기를 취소했습니다.", window.StatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Esc는 적용 전 topology 미리보기를 취소하고 확정 snapshot을 보존합니다.</summary>
    [Fact]
    public void Escape_CancelsCircuitTopologyPreviewBeforeApplyingIt()
    {
        WpfTestThread.Run(() =>
        {
            DesignProject project = CreateEmptyProject();
            var snapshot = new SessionSnapshot(
                project,
                runtimeDocument: null,
                geometry: null,
                electricalResults: [],
                areaAllocation: null,
                electricalAreaResult: null,
                currentInputDraft: null,
                dirtySinceSave: false,
                lastSavedRevision: null,
                isPresentationOutOfSync: false);
            var session = new ProjectSession(snapshot);
            AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices() with
            {
                ProjectSession = session
            };
            var window = new MainWindow(services);
            try
            {
                CircuitTopologyEditorViewModel editor = GetCircuitTopologyEditor(window);
                editor.SetTurnState(HeatingPathSegmentId.New(), isActive: false);
                window.CircuitSplitModeBox.IsChecked = true;
                SetViewportMode(window, enabled: true);
                using var keySource = new HwndSource(new HwndSourceParameters("Circuit topology key test"));
                var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape);
                keyEvent.RoutedEvent = Keyboard.PreviewKeyDownEvent;

                InvokePrivate(window, "OnMainWindowPreviewKeyDown", window, keyEvent);

                Assert.True(keyEvent.Handled);
                Assert.Same(snapshot, session.Current);
                Assert.Same(project, session.Current.CurrentProject);
                Assert.Empty(editor.TurnStates);
                Assert.False(window.CircuitSplitModeBox.IsChecked);
                Assert.False(GetViewportMode(window));
                Assert.Equal("회로 topology 미리보기를 취소했습니다.", window.StatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Esc로 회로 끝점 앵커 선택을 취소하면 topology 모드로 돌아갑니다.</summary>
    [Fact]
    public void Escape_CancelsCircuitEndpointAnchorAndResumesTopologyEditing()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                CircuitTopologyEditorViewModel editor = GetCircuitTopologyEditor(window);
                CircuitTopologyEndpointKey endpointKey = new(
                    HeatingPathSegmentId.New(),
                    ControlPointId.New(),
                    ConnectionPointRole.Start);
                editor.PendingSurfaceAnchorEndpointKey = endpointKey;
                window.CircuitSplitModeBox.IsChecked = true;
                SetPendingSurfaceAnchorTarget(window, "CircuitEndpoint");
                SetSurfaceAnchorMode(window, enabled: true);
                using var keySource = new HwndSource(new HwndSourceParameters("Circuit endpoint key test"));
                var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };

                InvokePrivate(window, "OnMainWindowPreviewKeyDown", window, keyEvent);

                Assert.True(keyEvent.Handled);
                Assert.Null(editor.PendingSurfaceAnchorEndpointKey);
                Assert.Null(GetPathAnchors(window).PendingSurfaceAnchorTarget);
                Assert.False(GetSurfaceAnchorMode(window));
                Assert.True(window.CircuitSplitModeBox.IsChecked);
                Assert.True(GetViewportMode(window));
                Assert.Equal("표면 앵커 지정을 취소했습니다.", window.StatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Native 런타임 ID를 안정 구간 ID로 해석하고 취소 때 확정 표시를 복구합니다.</summary>
    [Fact]
    public void PickedTurnSegment_CancelsPreviewAndRestoresCommittedVisibility()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            DesignProject project = CreateProjectWithTurn(out HeatingPathSegment turnSegment);
            var snapshot = new SessionSnapshot(
                project,
                runtimeDocument: null,
                geometry: null,
                electricalResults: [],
                areaAllocation: null,
                electricalAreaResult: null,
                currentInputDraft: null,
                dirtySinceSave: false,
                lastSavedRevision: null,
                isPresentationOutOfSync: false);
            var session = new ProjectSession(snapshot);
            var events = new ApplicationEventAdapter();
            var workspace = new OcctGeometryWorkspace();
            var runtimeIds = new RuntimeIdMap();
            int runtimeId = runtimeIds.GetOrAdd(turnSegment.Id, RuntimeGeometryLifetime.Committed);
            SetActiveRuntimeIds(workspace, runtimeIds);
            var coordinator = new CandidateCoordinator(
                session,
                workspace,
                new SingleThreadOperationContext(),
                events);
            AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices() with
            {
                ProjectSession = session,
                Events = events,
                GeometryWorkspace = workspace,
                ChangeCircuitTopology = new ChangeCircuitTopology(session, coordinator)
            };
            var window = new MainWindow(services);
            try
            {
                CircuitTopologyEditorViewModel editor = GetCircuitTopologyEditor(window);
                window.CircuitSplitModeBox.IsChecked = true;
                SetViewportMode(window, enabled: true);

                StubViewport viewport = (StubViewport)GetPrivateField(window, "Viewport");
                viewport.RaiseTurnSegmentPicked(runtimeId);

                Assert.True(editor.TryGetTurnState(turnSegment.Id, out bool previewState));
                Assert.False(previewState);
                Assert.Equal(new[] { (runtimeId, false) }, Bridge.TurnSegmentEnabledCalls);
                Assert.Same(snapshot, session.Current);

                InvokePrivate(window, "CancelCircuitTopology_Click", window.CircuitSplitModeBox, new RoutedEventArgs());

                Assert.Empty(editor.TurnStates);
                Assert.Same(snapshot, session.Current);
                Assert.Equal(new[] { (runtimeId, false), (runtimeId, true) }, Bridge.TurnSegmentEnabledCalls);
                Assert.False(window.CircuitSplitModeBox.IsChecked);
                Assert.False(GetViewportMode(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>창 비활성화 때 topology·앵커 초안을 취소하고 확정 snapshot과 표시를 보존합니다.</summary>
    [Fact]
    public void WindowDeactivated_CancelsTransientCircuitInputAndRestoresCommittedState()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            DesignProject project = CreateProjectWithTurn(out HeatingPathSegment turnSegment);
            var snapshot = new SessionSnapshot(
                project,
                runtimeDocument: null,
                geometry: null,
                electricalResults: [],
                areaAllocation: null,
                electricalAreaResult: null,
                currentInputDraft: null,
                dirtySinceSave: false,
                lastSavedRevision: null,
                isPresentationOutOfSync: false);
            var session = new ProjectSession(snapshot);
            var events = new ApplicationEventAdapter();
            var workspace = new OcctGeometryWorkspace();
            var runtimeIds = new RuntimeIdMap();
            int runtimeId = runtimeIds.GetOrAdd(turnSegment.Id, RuntimeGeometryLifetime.Committed);
            SetActiveRuntimeIds(workspace, runtimeIds);
            var coordinator = new CandidateCoordinator(
                session,
                workspace,
                new SingleThreadOperationContext(),
                events);
            AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices() with
            {
                ProjectSession = session,
                Events = events,
                GeometryWorkspace = workspace,
                ChangeCircuitTopology = new ChangeCircuitTopology(session, coordinator)
            };
            var window = new MainWindow(services);
            try
            {
                CircuitTopologyEditorViewModel editor = GetCircuitTopologyEditor(window);
                StubViewport viewport = (StubViewport)GetPrivateField(window, "Viewport");
                window.CircuitSplitModeBox.IsChecked = true;
                SetViewportMode(window, enabled: true);
                viewport.RaiseTurnSegmentPicked(runtimeId);

                CircuitTopologyEndpointKey endpointKey = new(
                    HeatingPathSegmentId.New(),
                    ControlPointId.New(),
                    ConnectionPointRole.Start);
                editor.PendingSurfaceAnchorEndpointKey = endpointKey;
                SetPendingSurfaceAnchorTarget(window, "CircuitEndpoint");
                SetSurfaceAnchorMode(window, enabled: true);
                int clearedConnectionPreviews = Bridge.ClearConnectionPathPreviewCount;

                InvokePrivate(window, "OnMainWindowDeactivated", window, EventArgs.Empty);

                Assert.Same(snapshot, session.Current);
                Assert.Empty(editor.TurnStates);
                Assert.Empty(editor.EndpointDrafts);
                Assert.Null(editor.Plan);
                Assert.Null(editor.PendingSurfaceAnchorEndpointKey);
                Assert.Null(GetPathAnchors(window).PendingSurfaceAnchorTarget);
                Assert.False(GetSurfaceAnchorMode(window));
                Assert.False(window.CircuitSplitModeBox.IsChecked);
                Assert.False(GetViewportMode(window));
                Assert.Equal(ViewerInputMode.Navigate, viewport.InputMode);
                Assert.Equal(
                    new[] { (runtimeId, false), (runtimeId, true) },
                    Bridge.TurnSegmentEnabledCalls);
                Assert.Equal(clearedConnectionPreviews + 1, Bridge.ClearConnectionPathPreviewCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>회로 입력 mode를 초기화합니다.</summary>
    private static bool GetViewportMode(MainWindow window)
    {
        object viewport = GetPrivateField(window, "Viewport");
        PropertyInfo property = viewport.GetType().GetProperty(
            "CircuitSplitMode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(viewport.GetType().FullName, "CircuitSplitMode");
        return (bool)property.GetValue(viewport);
    }

    /// <summary>테스트 뷰포트의 상호 배타 입력 모드를 지정합니다.</summary>
    private static void SetViewportMode(MainWindow window, bool enabled)
    {
        object viewport = GetPrivateField(window, "Viewport");
        PropertyInfo property = viewport.GetType().GetProperty(
            "CircuitSplitMode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(viewport.GetType().FullName, "CircuitSplitMode");
        property.SetValue(viewport, enabled);
    }

    /// <summary>테스트 창의 금형 표면 앵커 선택 모드를 설정합니다.</summary>
    private static void SetSurfaceAnchorMode(MainWindow window, bool enabled)
    {
        object viewport = GetPrivateField(window, "Viewport");
        PropertyInfo property = viewport.GetType().GetProperty(
            "SurfaceAnchorSelectionMode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(viewport.GetType().FullName, "SurfaceAnchorSelectionMode");
        property.SetValue(viewport, enabled);
    }

    /// <summary>테스트 창의 현재 금형 표면 앵커 선택 모드를 읽습니다.</summary>
    private static bool GetSurfaceAnchorMode(MainWindow window)
    {
        object viewport = GetPrivateField(window, "Viewport");
        PropertyInfo property = viewport.GetType().GetProperty(
            "SurfaceAnchorSelectionMode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(viewport.GetType().FullName, "SurfaceAnchorSelectionMode");
        return (bool)property.GetValue(viewport);
    }

    /// <summary>테스트 창에서 회로 끝점 앵커 대상을 지정합니다.</summary>
    private static void SetPendingSurfaceAnchorTarget(MainWindow window, string targetName)
    {
        SurfaceAnchorTarget target = Enum.Parse<SurfaceAnchorTarget>(targetName, ignoreCase: false);
        GetPathAnchors(window).BeginPicking(target);
    }

    /// <summary>화면 partial의 비공개 필드를 읽어 세션 상태를 확인합니다.</summary>
    private static object GetPrivateField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target)
        ?? throw new MissingFieldException(target.GetType().FullName, name);

    /// <summary>창이 소유한 topology 편집 초안 모델을 가져옵니다.</summary>
    private static CircuitTopologyEditorViewModel GetCircuitTopologyEditor(MainWindow window)
    {
        MainWindowViewModel viewModel = (MainWindowViewModel)GetPrivateField(window, "_mainWindowViewModel");
        return viewModel.CircuitTopology;
    }

    /// <summary>창이 소유한 경로 앵커 입력 상태를 가져옵니다.</summary>
    private static HeatingPathAnchorViewModel GetPathAnchors(MainWindow window)
    {
        MainWindowViewModel viewModel = (MainWindowViewModel)GetPrivateField(window, "_mainWindowViewModel");
        return viewModel.PathAnchors;
    }

    /// <summary>WPF 이벤트 처리 경계를 직접 호출해 테스트합니다.</summary>
    private static object InvokePrivate(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, methodName);
        return method.Invoke(target, arguments);
    }

    /// <summary>topology 변경 판정에 사용할 최소한의 유효 프로젝트를 만듭니다.</summary>
    private static DesignProject CreateEmptyProject()
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes("empty topology project");
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var modelIdentity = new ModelIdentity(sourceHash, 1, "STEP", "test-v1", "face-v1");
        var workingFrame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));

        return DesignProject.CreateEmpty(ProjectId.New(), modelIdentity, workingFrame);
    }

    /// <summary>런타임 선택 검증에 사용할 회전 구간 하나가 있는 프로젝트를 만듭니다.</summary>
    private static DesignProject CreateProjectWithTurn(out HeatingPathSegment turnSegment)
    {
        DesignProject emptyProject = CreateEmptyProject();
        FaceRefId faceId = FaceRefId.New();
        var face = new FaceReference(
            faceId,
            emptyProject.SourceModel.SourceSha256,
            importIndexHint: 0,
            FaceGeometryKind.Plane,
            new AreaMm2(100),
            new Vec3(5, 5, 0),
            "circuit-topology-test-face",
            []);
        BoundaryLoop originalBoundary = CreateBoundaryLoop(faceId);
        BoundaryLoop effectiveBoundary = CreateBoundaryLoop(faceId);
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(effectiveBoundary.Segments[0].Id, 0, 1)]);
        var region = new HeatingRegion(
            RegionId.New(),
            [faceId],
            [originalBoundary],
            [],
            [],
            [effectiveBoundary],
            exitSide);
        var start = new ControlPoint(ControlPointId.New(), new Vec3(1, 5, 0));
        var end = new ControlPoint(ControlPointId.New(), new Vec3(9, 5, 0));
        var startAnchor = new SurfaceAnchor(
            faceId,
            new Vec2(1, 5),
            new Vec2(1, 5),
            start.Position);
        var endAnchor = new SurfaceAnchor(
            faceId,
            new Vec2(9, 5),
            new Vec2(9, 5),
            end.Position);
        var placement = new PlacementConstraints(
            new LengthMm(1),
            new LengthMm(1),
            new LengthMm(0),
            PlacementDirection.AlongX,
            startAnchor,
            endAnchor,
            exitSide);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var generationSource = new HeatingPathGenerationSource(
            region.Id,
            placement,
            wire,
            "circuit-topology-test-v1");
        turnSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.Turn,
            start.Id,
            end.Id,
            sequence: 0,
            isActive: true,
            new LineCurveDefinition(start.Position, end.Position));
        var path = new HeatingPath(
            HeatingPathId.New(),
            generationSource,
            [start, end],
            [turnSegment],
            [turnSegment]);
        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startConnectionPointId = ConnectionPointId.New();
        ConnectionPointId endConnectionPointId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId,
            sequence: 0,
            [new CircuitSegmentReference(turnSegment.Id, 0, isReversed: false)],
            startConnectionPointId,
            endConnectionPointId,
            new Supply(null, null, null));
        var startConnectionPoint = new ConnectionPoint(
            startConnectionPointId,
            circuitId,
            ConnectionPointRole.Start,
            turnSegment.Id,
            start.Id,
            startAnchor,
            new Vec3(1, 5, 2));
        var endConnectionPoint = new ConnectionPoint(
            endConnectionPointId,
            circuitId,
            ConnectionPointRole.End,
            turnSegment.Id,
            end.Id,
            endAnchor,
            new Vec3(9, 5, 2));

        DesignProject project = new(
            emptyProject.Id,
            emptyProject.SchemaVersion,
            emptyProject.Revision,
            emptyProject.Name,
            emptyProject.CreatedUtc,
            emptyProject.ModifiedUtc,
            emptyProject.SourceModel,
            emptyProject.WorkingCoordinateSystem,
            [face],
            [region],
            wire,
            placement,
            [path],
            [circuit],
            [startConnectionPoint, endConnectionPoint],
            [
                CreateConnectionPath(startConnectionPoint),
                CreateConnectionPath(endConnectionPoint)
            ],
            emptyProject.Rules);
        Assert.True(project.ValidateReferences().IsValid);
        return project;
    }

    /// <summary>테스트 발열 영역에 사용할 닫힌 사각 경계를 만듭니다.</summary>
    private static BoundaryLoop CreateBoundaryLoop(FaceRefId faceId)
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

    /// <summary>회로 끝점마다 금형 이탈 표식이 있는 테스트 연결 경로를 만듭니다.</summary>
    private static ConnectionPath CreateConnectionPath(ConnectionPoint connectionPoint)
    {
        Vec3 surfacePoint = connectionPoint.SurfaceAnchor.ModelPoint;
        var exitPoint = new Vec3(surfacePoint.X, surfacePoint.Y, surfacePoint.Z + 1);
        var onMoldSegment = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(),
            sequence: 0,
            ConductorRole.HeatingWireExtension,
            GeometryDomain.OnMold,
            new LengthMm(0),
            new LineCurveDefinition(surfacePoint, exitPoint));
        var externalSegment = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(),
            sequence: 1,
            ConductorRole.SeparateConnectionWire,
            GeometryDomain.External,
            new LengthMm(0),
            new LineCurveDefinition(exitPoint, connectionPoint.ExternalConnectionPoint),
            isMoldExitTransition: true);
        return new ConnectionPath(
            ConnectionPathId.New(),
            connectionPoint.Id,
            [onMoldSegment, externalSegment]);
    }

    /// <summary>테스트 workspace에 확정 Native 런타임 ID 대응표를 설정합니다.</summary>
    private static void SetActiveRuntimeIds(OcctGeometryWorkspace workspace, RuntimeIdMap runtimeIds)
    {
        FieldInfo field = typeof(OcctGeometryWorkspace).GetField(
            "_activeRuntimeIds",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OcctGeometryWorkspace).FullName, "_activeRuntimeIds");
        field.SetValue(workspace, runtimeIds);
    }
}
