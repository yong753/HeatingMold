using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Circuits;

namespace HeatingMold.App.Tests;

/// <summary>회로 topology 화면 초안과 Application 입력 조립의 경계를 확인합니다.</summary>
public sealed class CircuitTopologyEditorViewModelTests
{
    /// <summary>입력 모드 동기화 중첩과 예외 종료에도 이벤트 억제 범위를 균형 있게 복구합니다.</summary>
    [Fact]
    public void SuppressModeChangedEvents_NestsAndRestoresAfterException()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        Assert.False(viewModel.IsModeChangeSuppressed);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (viewModel.SuppressModeChangedEvents())
            {
                Assert.True(viewModel.IsModeChangeSuppressed);
                using (viewModel.SuppressModeChangedEvents())
                    Assert.True(viewModel.IsModeChangeSuppressed);

                Assert.True(viewModel.IsModeChangeSuppressed);
                throw new InvalidOperationException("fixture failure");
            }
        }));

        Assert.False(viewModel.IsModeChangeSuppressed);
    }

    [Fact]
    public void RetainEndpointDrafts_PreservesMatchingInputAndRemovesStaleKeys()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        CircuitTopologyEndpointKey retainedKey = CreateEndpointKey();
        CircuitTopologyEndpointKey staleKey = CreateEndpointKey();

        viewModel.RetainEndpointDrafts([retainedKey, staleKey]);
        Assert.True(viewModel.TryGetEndpointDraft(retainedKey, out CircuitTopologyEndpointDraft originalDraft));
        ConnectionPointId originalId = originalDraft.ConnectionPointId;
        originalDraft.ExternalConnectionPoint = new Vec3(12, 34, 56);

        viewModel.RetainEndpointDrafts([retainedKey]);

        Assert.True(viewModel.TryGetEndpointDraft(retainedKey, out CircuitTopologyEndpointDraft retainedDraft));
        Assert.Same(originalDraft, retainedDraft);
        Assert.Equal(originalId, retainedDraft.ConnectionPointId);
        Assert.Equal(new Vec3(12, 34, 56), retainedDraft.ExternalConnectionPoint);
        Assert.False(viewModel.TryGetEndpointDraft(staleKey, out _));
    }

    /// <summary>외부 좌표 입력은 현재 문화권의 세 유한 좌표만 저장하고 잘못된 값은 보존합니다.</summary>
    [Fact]
    public void TryUpdateExternalConnectionPoint_UsesCurrentCultureAndRejectsNonFiniteCoordinates()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        CircuitTopologyEndpointKey endpointKey = CreateEndpointKey();
        viewModel.RetainEndpointDrafts([endpointKey]);
        Assert.True(viewModel.TryGetEndpointDraft(endpointKey, out CircuitTopologyEndpointDraft draft));

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.True(viewModel.TryUpdateExternalConnectionPoint(
                endpointKey,
                "12,5",
                "-3,25",
                "4",
                out string failure));
            Assert.Equal(string.Empty, failure);
            Assert.Equal(new Vec3(12.5, -3.25, 4), draft.ExternalConnectionPoint);

            Assert.False(viewModel.TryUpdateExternalConnectionPoint(
                endpointKey,
                "20",
                "NaN",
                "6",
                out failure));
            Assert.Contains("유한한 mm", failure);
            Assert.Equal(new Vec3(12.5, -3.25, 4), draft.ExternalConnectionPoint);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>끝점 선택지를 다시 만들 때 같은 선택과 미확정 좌표를 유지하고 사라진 항목을 버립니다.</summary>
    [Fact]
    public void SetEndpointChoices_PreservesSelectionAndDraftAndClearsStaleChoices()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        CircuitTopologyEndpointChoice firstChoice = CreateChoice(ConnectionPointRole.Start);
        CircuitTopologyEndpointChoice secondChoice = CreateChoice(ConnectionPointRole.End);

        viewModel.SetEndpointChoices([firstChoice, secondChoice, firstChoice]);
        Assert.Equal(2, viewModel.EndpointChoices.Count);
        Assert.Equal(firstChoice.Key, viewModel.SelectedEndpointChoice.Key);
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(
            firstChoice.Key,
            "1.25",
            "2.5",
            "3.75",
            out _));

        viewModel.SetEndpointChoices([secondChoice, firstChoice]);

        Assert.Equal(firstChoice.Key, viewModel.SelectedEndpointChoice.Key);
        Assert.Equal(
            1.25.ToString("G17", CultureInfo.CurrentCulture),
            viewModel.ExternalXCoordinateText);
        Assert.Contains("외부 좌표 저장됨", viewModel.EndpointStatusText);

        viewModel.SetEndpointChoices([secondChoice]);

        Assert.Equal(secondChoice.Key, viewModel.SelectedEndpointChoice.Key);
        Assert.False(viewModel.TryGetEndpointDraft(firstChoice.Key, out _));
        Assert.Equal(string.Empty, viewModel.ExternalXCoordinateText);
    }

    /// <summary>완성된 신규 끝점 입력을 회로별 연결부와 새 연결부 ID로 조립합니다.</summary>
    [Fact]
    public void TryBuildCircuitEndpointInputs_UsesCompleteNewEndpointDrafts()
    {
        EndpointPlanFixture fixture = CreateEndpointPlan();
        var viewModel = new CircuitTopologyEditorViewModel();
        viewModel.SetEndpointChoices([
            CreateChoice(fixture.StartKey),
            CreateChoice(fixture.EndKey)]);
        Assert.True(viewModel.TryGetEndpointDraft(fixture.StartKey, out CircuitTopologyEndpointDraft startDraft));
        Assert.True(viewModel.TryGetEndpointDraft(fixture.EndKey, out CircuitTopologyEndpointDraft endDraft));
        startDraft.SurfaceAnchor = CreateAnchor();
        endDraft.SurfaceAnchor = CreateAnchor();
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.StartKey, "1", "2", "3", out _));
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.EndKey, "4", "5", "6", out _));

        Assert.True(viewModel.TryBuildCircuitEndpointInputs(
            fixture.Project,
            fixture.Plan,
            out Dictionary<CircuitId, CircuitEndpointPair> endpointPairs,
            out HashSet<ConnectionPointId> newPointIds,
            out string failure));

        KeyValuePair<CircuitId, CircuitEndpointPair> mappedEndpoints = Assert.Single(endpointPairs);
        CircuitEndpointPair endpointPair = mappedEndpoints.Value;
        Assert.Equal(string.Empty, failure);
        Assert.Equal(fixture.CircuitId, mappedEndpoints.Key);
        Assert.Equal(startDraft.ConnectionPointId, endpointPair.Start.Id);
        Assert.Equal(endDraft.ConnectionPointId, endpointPair.End.Id);
        Assert.True(newPointIds.SetEquals([startDraft.ConnectionPointId, endDraft.ConnectionPointId]));
        Assert.Equal(new Vec3(1, 2, 3), endpointPair.Start.ExternalConnectionPoint);
        Assert.Equal(new Vec3(4, 5, 6), endpointPair.End.ExternalConnectionPoint);
    }

    /// <summary>끝점 하나라도 입력이 빠지면 일부 회로 입력을 반환하지 않고 전체 구성을 거부합니다.</summary>
    [Fact]
    public void TryBuildCircuitEndpointInputs_RejectsIncompleteDraftWithoutPartialOutput()
    {
        EndpointPlanFixture fixture = CreateEndpointPlan();
        EndpointPlanFixture laterFixture = CreateEndpointPlan();
        var plan = new CircuitTopologyPlan(
            fixture.Project.Id,
            fixture.Project.Revision,
            new CircuitPathTopologyAnalysis([], []),
            new CircuitIdentityMapping([
                fixture.Plan.Mapping.Assignments[0],
                laterFixture.Plan.Mapping.Assignments[0]]));
        var viewModel = new CircuitTopologyEditorViewModel();
        viewModel.SetEndpointChoices([
            CreateChoice(fixture.StartKey),
            CreateChoice(fixture.EndKey),
            CreateChoice(laterFixture.StartKey)]);
        Assert.True(viewModel.TryGetEndpointDraft(fixture.StartKey, out CircuitTopologyEndpointDraft startDraft));
        Assert.True(viewModel.TryGetEndpointDraft(fixture.EndKey, out CircuitTopologyEndpointDraft endDraft));
        Assert.True(viewModel.TryGetEndpointDraft(laterFixture.StartKey, out CircuitTopologyEndpointDraft laterStartDraft));
        startDraft.SurfaceAnchor = CreateAnchor();
        endDraft.SurfaceAnchor = CreateAnchor();
        laterStartDraft.SurfaceAnchor = CreateAnchor();
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.StartKey, "1", "2", "3", out _));
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.EndKey, "4", "5", "6", out _));
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(laterFixture.StartKey, "7", "8", "9", out _));

        Assert.False(viewModel.TryBuildCircuitEndpointInputs(
            fixture.Project,
            plan,
            out Dictionary<CircuitId, CircuitEndpointPair> endpointPairs,
            out HashSet<ConnectionPointId> newPointIds,
            out string failure));

        Assert.Empty(endpointPairs);
        Assert.Empty(newPointIds);
        Assert.Contains("새 회로 끝점마다", failure);
        Assert.False(viewModel.TryGetEndpointDraft(laterFixture.EndKey, out _));
    }

    /// <summary>완성된 끝점 초안에서 좌표계 revision과 재료 역할이 있는 두 연결 구간을 만듭니다.</summary>
    [Fact]
    public void TryBuildConnectionPathInputs_UsesCompleteDraftsAndExpectedRevision()
    {
        EndpointPlanFixture fixture = CreateEndpointPlan();
        var viewModel = new CircuitTopologyEditorViewModel();
        viewModel.SetEndpointChoices([
            CreateChoice(fixture.StartKey),
            CreateChoice(fixture.EndKey)]);
        Assert.True(viewModel.TryGetEndpointDraft(fixture.StartKey, out CircuitTopologyEndpointDraft startDraft));
        Assert.True(viewModel.TryGetEndpointDraft(fixture.EndKey, out CircuitTopologyEndpointDraft endDraft));
        SurfaceAnchor startAnchor = CreateAnchor();
        SurfaceAnchor endAnchor = new(FaceRefId.New(), new Vec2(4, 5), new Vec2(4, 5), new Vec3(4, 5, 6));
        startDraft.SurfaceAnchor = startAnchor;
        endDraft.SurfaceAnchor = endAnchor;
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.StartKey, "10", "11", "12", out _));
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.EndKey, "20", "21", "22", out _));
        WorkingCoordinateSystem coordinateSystem = CreateCoordinateSystem(modelRevision: 7);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.True(viewModel.TryBuildConnectionPathInputs(
                [startDraft.ConnectionPointId, endDraft.ConnectionPointId],
                coordinateSystem,
                new ModelRevision(7),
                moldExitAllowanceText: "0,5",
                externalAllowanceText: "1,25",
                out Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput> inputs,
                out string failure));

            Assert.Equal(string.Empty, failure);
            Assert.Equal(2, inputs.Count);
            CircuitTopologyConnectionPathInput startInput = inputs[startDraft.ConnectionPointId];
            Assert.Equal(new ModelRevision(7), startInput.ExpectedModelRevision);
            Assert.Equal(coordinateSystem, startInput.CoordinateSystem);
            Assert.Equal(2, startInput.Legs.Count);
            Assert.Equal(startAnchor.ModelPoint, startInput.Legs[0].EndPoint);
            Assert.Equal(GeometryDomain.OnMold, startInput.Legs[0].GeometryDomain);
            Assert.Equal(ConductorRole.HeatingWireExtension, startInput.Legs[0].ConductorRole);
            Assert.Equal(0.5, startInput.Legs[0].AdditionalAllowanceMm.Value);
            Assert.Equal(new Vec3(10, 11, 12), startInput.Legs[1].EndPoint);
            Assert.Equal(GeometryDomain.External, startInput.Legs[1].GeometryDomain);
            Assert.Equal(ConductorRole.SeparateConnectionWire, startInput.Legs[1].ConductorRole);
            Assert.Equal(1.25, startInput.Legs[1].AdditionalAllowanceMm.Value);
            Assert.True(startInput.Legs[1].IsMoldExitTransition);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>뒤쪽 끝점 입력이 누락되면 앞쪽 연결 경로 입력도 함께 반환하지 않습니다.</summary>
    [Fact]
    public void TryBuildConnectionPathInputs_RejectsIncompleteDraftWithoutPartialOutput()
    {
        EndpointPlanFixture fixture = CreateEndpointPlan();
        var viewModel = new CircuitTopologyEditorViewModel();
        viewModel.SetEndpointChoices([
            CreateChoice(fixture.StartKey),
            CreateChoice(fixture.EndKey)]);
        Assert.True(viewModel.TryGetEndpointDraft(fixture.StartKey, out CircuitTopologyEndpointDraft startDraft));
        Assert.True(viewModel.TryGetEndpointDraft(fixture.EndKey, out CircuitTopologyEndpointDraft endDraft));
        startDraft.SurfaceAnchor = CreateAnchor();
        Assert.True(viewModel.TryUpdateExternalConnectionPoint(fixture.StartKey, "1", "2", "3", out _));

        Assert.False(viewModel.TryBuildConnectionPathInputs(
            [startDraft.ConnectionPointId, endDraft.ConnectionPointId],
            CreateCoordinateSystem(modelRevision: 5),
            new ModelRevision(5),
            moldExitAllowanceText: "0",
            externalAllowanceText: "0",
            out Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput> inputs,
            out string failure));

        Assert.Empty(inputs);
        Assert.Contains("각 신규 연결부마다", failure);
    }

    /// <summary>음수·NaN·무한대 여유를 입력하면 연결 경로 입력을 만들지 않습니다.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryBuildConnectionPathInputs_RejectsInvalidAllowanceWithoutPartialOutput(string allowance)
    {
        var viewModel = new CircuitTopologyEditorViewModel();

        Assert.False(viewModel.TryBuildConnectionPathInputs(
            [],
            CreateCoordinateSystem(modelRevision: 2),
            new ModelRevision(2),
            allowance,
            "0",
            out Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput> inputs,
            out string failure));

        Assert.Empty(inputs);
        Assert.Contains("0 이상의 유한한 mm", failure);
    }

    [Fact]
    public void SetTurnState_NullRemovesThePreviewOverride()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        HeatingPathSegmentId segmentId = HeatingPathSegmentId.New();

        viewModel.SetTurnState(segmentId, false);
        Assert.True(viewModel.TryGetTurnState(segmentId, out bool previewState));
        Assert.False(previewState);

        viewModel.SetTurnState(segmentId, null);

        Assert.False(viewModel.TryGetTurnState(segmentId, out _));
    }

    /// <summary>회전 선택지는 구간 순서와 안정 ID로 정렬하고 현재 draft 상태를 표시합니다.</summary>
    [Fact]
    public void RefreshTurnChoices_SortsSegmentsAndUsesPreviewState()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        ControlPointId firstStartId = ControlPointId.New();
        ControlPointId firstEndId = ControlPointId.New();
        ControlPointId secondStartId = ControlPointId.New();
        ControlPointId secondEndId = ControlPointId.New();
        HeatingPathSegment first = CreateTurnSegment(
            HeatingPathSegmentId.New(),
            firstStartId,
            firstEndId,
            sequence: 1,
            isActive: true);
        HeatingPathSegment second = CreateTurnSegment(
            HeatingPathSegmentId.New(),
            secondStartId,
            secondEndId,
            sequence: 0,
            isActive: true);
        HeatingPathSegment inactiveLine = new(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            ControlPointId.New(),
            ControlPointId.New(),
            sequence: 2,
            isActive: true,
            new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
        viewModel.SetTurnState(first.Id, false);

        viewModel.RefreshTurnChoices([first, inactiveLine, second]);

        Assert.Equal(new[] { second.Id, first.Id }, viewModel.TurnChoices.Select(choice => choice.SegmentId));
        Assert.True(viewModel.TurnChoices[0].IsActive);
        Assert.False(viewModel.TurnChoices[1].IsActive);
        viewModel.SetTurnChoiceState(second.Id, false);
        Assert.False(viewModel.TurnChoices[0].IsActive);
        Assert.StartsWith("회전 구간 · ", viewModel.TurnChoices[0].DisplayName);
    }

    /// <summary>survivor 목록 재생성 때 동일한 안정 ID 선택을 유지하고 사라진 선택은 지웁니다.</summary>
    [Fact]
    public void RefreshSurvivorChoices_PreservesOnlyExistingStableSelection()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        Circuit firstCircuit = CreateCircuit(sequence: 1);
        Circuit secondCircuit = CreateCircuit(sequence: 0);
        viewModel.RefreshSurvivorChoices([firstCircuit, secondCircuit]);
        Assert.Equal(secondCircuit.Id, viewModel.SurvivorChoices[0].CircuitId);
        Assert.Equal(firstCircuit.Id, viewModel.SurvivorChoices[1].CircuitId);
        viewModel.SelectSurvivorChoice(viewModel.SurvivorChoices[1]);

        viewModel.RefreshSurvivorChoices([secondCircuit, firstCircuit]);

        Assert.Equal(firstCircuit.Id, viewModel.SelectedSurvivorChoice.CircuitId);
        Assert.StartsWith("회로 2 · ID ", viewModel.SelectedSurvivorChoice.DisplayName);

        viewModel.RefreshSurvivorChoices([secondCircuit]);

        Assert.Null(viewModel.SelectedSurvivorChoice);
    }

    [Fact]
    public void Reset_ClearsTurnAndEndpointDrafts()
    {
        var viewModel = new CircuitTopologyEditorViewModel();
        HeatingPathSegmentId segmentId = HeatingPathSegmentId.New();
        CircuitTopologyEndpointKey endpointKey = CreateEndpointKey();
        CircuitTopologyPlan plan = CreatePlan();
        viewModel.SetTurnState(segmentId, true);
        viewModel.RetainEndpointDrafts([endpointKey]);
        viewModel.SetPlan(plan);
        viewModel.PendingSurfaceAnchorEndpointKey = endpointKey;
        Assert.Same(plan, viewModel.Plan);

        viewModel.Reset();

        Assert.Empty(viewModel.TurnStates);
        Assert.Empty(viewModel.EndpointDrafts);
        Assert.Null(viewModel.Plan);
        Assert.Null(viewModel.PendingSurfaceAnchorEndpointKey);
    }

    [Fact]
    public void CircuitTurnChoice_NotifiesOnlyWhenActiveStateChanges()
    {
        var choice = new CircuitTurnChoice(HeatingPathSegmentId.New(), "회전 구간", isActive: true);
        var changedProperties = new List<string>();
        choice.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        choice.IsActive = true;
        choice.IsActive = false;

        Assert.Equal(new[] { nameof(CircuitTurnChoice.IsActive) }, changedProperties);
    }

    private static CircuitTopologyEndpointKey CreateEndpointKey() =>
        new(HeatingPathSegmentId.New(), ControlPointId.New(), ConnectionPointRole.Start);

    private static CircuitTopologyEndpointChoice CreateChoice(ConnectionPointRole role)
    {
        CircuitTopologyEndpointKey key = new(
            HeatingPathSegmentId.New(),
            ControlPointId.New(),
            role);
        return new CircuitTopologyEndpointChoice(
            key,
            CircuitId.New(),
            RegionId.New(),
            $"신규 연결부 · {role}");
    }

    private static CircuitTopologyEndpointChoice CreateChoice(CircuitTopologyEndpointKey key) =>
        new(key, CircuitId.New(), RegionId.New(), $"신규 연결부 · {key.Role}");

    private static SurfaceAnchor CreateAnchor() =>
        new(FaceRefId.New(), new Vec2(1, 2), new Vec2(1, 2), new Vec3(1, 2, 3));

    /// <summary>테스트에서 회전 곡선의 안정 ID, 순서와 활성 상태를 지정합니다.</summary>
    private static HeatingPathSegment CreateTurnSegment(
        HeatingPathSegmentId segmentId,
        ControlPointId startId,
        ControlPointId endId,
        int sequence,
        bool isActive) =>
        new(
            segmentId,
            HeatingPathSegmentKind.Turn,
            startId,
            endId,
            sequence,
            isActive,
            new LineCurveDefinition(new Vec3(0, sequence, 0), new Vec3(1, sequence, 0)));

    /// <summary>survivor 목록 회귀에 사용할 ID와 순서의 회로를 만듭니다.</summary>
    private static Circuit CreateCircuit(int sequence) =>
        new(
            CircuitId.New(),
            sequence,
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), 0, isReversed: false)],
            ConnectionPointId.New(),
            ConnectionPointId.New(),
            new Supply(null, null, null));

    /// <summary>요청한 모델 revision을 가진 테스트 작업 좌표계를 만듭니다.</summary>
    private static WorkingCoordinateSystem CreateCoordinateSystem(long modelRevision) =>
        new(
            new WorkingCoordinateSystemId(1),
            new ModelRevision(modelRevision),
            new ProjectionFrame(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));

    private static EndpointPlanFixture CreateEndpointPlan()
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes("circuit topology endpoint plan");
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var modelIdentity = new ModelIdentity(sourceHash, 1, "STEP", "test-v1", "face-v1");
        var workingFrame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DesignProject project = DesignProject.CreateEmpty(ProjectId.New(), modelIdentity, workingFrame);
        var component = new CircuitPathComponent(
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), 0, isReversed: false)],
            ControlPointId.New(),
            ControlPointId.New());
        CircuitId circuitId = CircuitId.New();
        var assignment = new CircuitIdentityAssignment(
            circuitId,
            parentCircuitId: null,
            preservesExistingIdentity: false,
            component,
            startConnectionPointId: null,
            endConnectionPointId: null,
            preservedSupply: null,
            mergedCircuitIds: []);
        var plan = new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            new CircuitPathTopologyAnalysis([], []),
            new CircuitIdentityMapping([assignment]));
        var startKey = new CircuitTopologyEndpointKey(
            component.Segments[0].SegmentId,
            component.StartControlPointId,
            ConnectionPointRole.Start);
        var endKey = new CircuitTopologyEndpointKey(
            component.Segments[^1].SegmentId,
            component.EndControlPointId,
            ConnectionPointRole.End);

        return new EndpointPlanFixture(project, plan, circuitId, startKey, endKey);
    }

    private sealed record EndpointPlanFixture(
        DesignProject Project,
        CircuitTopologyPlan Plan,
        CircuitId CircuitId,
        CircuitTopologyEndpointKey StartKey,
        CircuitTopologyEndpointKey EndKey);

    private static CircuitTopologyPlan CreatePlan()
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes("circuit topology view model");
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var modelIdentity = new ModelIdentity(sourceHash, 1, "STEP", "test-v1", "face-v1");
        var workingFrame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DesignProject project = DesignProject.CreateEmpty(ProjectId.New(), modelIdentity, workingFrame);

        return new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            new CircuitPathTopologyAnalysis([], []),
            new CircuitIdentityMapping([]));
    }
}
