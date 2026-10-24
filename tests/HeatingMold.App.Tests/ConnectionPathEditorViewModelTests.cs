using System;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.App.Tests;

/// <summary>연결 경로 편집 ViewModel의 요청 변환과 입력 검증을 확인합니다.</summary>
public sealed class ConnectionPathEditorViewModelTests
{
    /// <summary>선택 연결 경로 변경을 WPF binding에 알립니다.</summary>
    [Fact]
    public void SelectedChoice_RaisesPropertyChanged()
    {
        var editor = new ConnectionPathEditorViewModel();
        var choice = new ConnectionPathChoice(
            ConnectionPointId.New(),
            new DesignRevision(1),
            "회로 1 · 시작 · 경로 2구간");
        string changedProperty = null;
        editor.PropertyChanged += (_, eventArgs) => changedProperty = eventArgs.PropertyName;

        editor.SelectedChoice = choice;

        Assert.Same(choice, editor.SelectedChoice);
        Assert.Equal(nameof(ConnectionPathEditorViewModel.SelectedChoice), changedProperty);
    }

    /// <summary>목록 갱신 뒤에도 같은 안정 연결부를 선택하고 새 revision을 반영합니다.</summary>
    [Fact]
    public void RefreshChoices_PreservesSelectedConnectionPointAcrossRevision()
    {
        ConnectionPointId connectionPointId = ConnectionPointId.New();
        CircuitId circuitId = CircuitId.New();
        var editor = new ConnectionPathEditorViewModel();

        editor.RefreshChoices(CreateProject(1, connectionPointId, circuitId));
        editor.RefreshChoices(CreateProject(2, connectionPointId, circuitId));

        Assert.Single(editor.Choices);
        Assert.Equal(connectionPointId, editor.SelectedChoice.ConnectionPointId);
        Assert.Equal(new DesignRevision(2), editor.SelectedChoice.DesignRevision);
    }

    /// <summary>선택 목록 변경 이벤트가 발생하는 동안 갱신 상태를 표시하고 완료 뒤 해제합니다.</summary>
    [Fact]
    public void RefreshChoices_ExposesGuardOnlyWhileChoicesAreChanging()
    {
        ConnectionPointId connectionPointId = ConnectionPointId.New();
        CircuitId circuitId = CircuitId.New();
        var editor = new ConnectionPathEditorViewModel();
        editor.RefreshChoices(CreateProject(1, connectionPointId, circuitId));
        bool guardObservedDuringChange = false;
        editor.Choices.CollectionChanged += (_, _) =>
            guardObservedDuringChange |= editor.IsRefreshingChoices;

        editor.RefreshChoices(CreateProject(2, connectionPointId, circuitId));

        Assert.True(guardObservedDuringChange);
        Assert.False(editor.IsRefreshingChoices);
    }

    /// <summary>목록 변경 이벤트에서 예외가 나도 선택 변경 억제 상태를 해제합니다.</summary>
    [Fact]
    public void RefreshChoices_ClearsGuardWhenCollectionObserverThrows()
    {
        ConnectionPointId connectionPointId = ConnectionPointId.New();
        CircuitId circuitId = CircuitId.New();
        var editor = new ConnectionPathEditorViewModel();
        editor.RefreshChoices(CreateProject(1, connectionPointId, circuitId));
        editor.Choices.CollectionChanged += (_, _) => throw new InvalidOperationException("fixture failure");

        Assert.Throws<InvalidOperationException>(() =>
            editor.RefreshChoices(CreateProject(2, connectionPointId, circuitId)));

        Assert.False(editor.IsRefreshingChoices);
    }

    /// <summary>편집 행 순서와 경로 속성을 계획 요청에 복사합니다.</summary>
    [Fact]
    public void TryCreateRequests_CopiesValidatedRowsInOrder()
    {
        var editor = new ConnectionPathEditorViewModel();
        editor.Legs.Add(new ConnectionPathLegRow(
            new Vec3(1, 2, 3),
            GeometryDomain.OnMold,
            ConductorRole.HeatingWireExtension,
            additionalAllowanceMm: 1.25,
            isMoldExitTransition: false));
        editor.Legs.Add(new ConnectionPathLegRow(
            new Vec3(4, 5, 6),
            GeometryDomain.External,
            ConductorRole.SeparateConnectionWire,
            additionalAllowanceMm: 2.5,
            isMoldExitTransition: true));

        bool succeeded = editor.TryCreateRequests(out ConnectionPathLegRequest[] requests, out int invalidRowNumber);

        Assert.True(succeeded);
        Assert.Equal(0, invalidRowNumber);
        Assert.Equal(2, requests.Length);
        Assert.Equal(new Vec3(1, 2, 3), requests[0].EndPoint);
        Assert.Equal(GeometryDomain.OnMold, requests[0].GeometryDomain);
        Assert.Equal(ConductorRole.HeatingWireExtension, requests[0].ConductorRole);
        Assert.Equal(1.25, requests[0].AdditionalAllowanceMm.Value);
        Assert.False(requests[0].IsMoldExitTransition);
        Assert.Equal(new Vec3(4, 5, 6), requests[1].EndPoint);
        Assert.Equal(GeometryDomain.External, requests[1].GeometryDomain);
        Assert.Equal(ConductorRole.SeparateConnectionWire, requests[1].ConductorRole);
        Assert.Equal(2.5, requests[1].AdditionalAllowanceMm.Value);
        Assert.True(requests[1].IsMoldExitTransition);
    }

    /// <summary>큰 유한 좌표에서도 외부 경유점의 세 좌표를 유한하게 계산합니다.</summary>
    [Fact]
    public void TryAddWaypoint_CalculatesMidpointWithoutOverflow()
    {
        var editor = new ConnectionPathEditorViewModel();
        editor.Legs.Add(new ConnectionPathLegRow(
            new Vec3(1.2e308, -1.2e308, double.MaxValue),
            GeometryDomain.OnMold,
            ConductorRole.HeatingWireExtension,
            additionalAllowanceMm: 0,
            isMoldExitTransition: false));
        editor.Legs.Add(new ConnectionPathLegRow(
            new Vec3(1.6e308, -1.6e308, double.MaxValue),
            GeometryDomain.External,
            ConductorRole.SeparateConnectionWire,
            additionalAllowanceMm: 0,
            isMoldExitTransition: true));

        bool added = editor.TryAddWaypoint();

        Assert.True(added);
        ConnectionPathLegRow waypoint = editor.Legs[1];
        Assert.True(double.IsFinite(waypoint.X));
        Assert.True(double.IsFinite(waypoint.Y));
        Assert.True(double.IsFinite(waypoint.Z));
        Assert.False(editor.Legs[2].IsMoldExitTransition);
    }

    /// <summary>첫 번째 잘못된 좌표나 여유를 거부하고 해당 행 번호를 반환합니다.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryCreateRequests_RejectsInvalidInputAndReportsItsRow(bool invalidAllowance)
    {
        var editor = new ConnectionPathEditorViewModel();
        editor.Legs.Add(new ConnectionPathLegRow(
            new Vec3(0, 0, 0),
            GeometryDomain.OnMold,
            ConductorRole.HeatingWireExtension,
            additionalAllowanceMm: 0,
            isMoldExitTransition: false));
        var invalidRow = new ConnectionPathLegRow(
            new Vec3(1, 0, 0),
            GeometryDomain.External,
            ConductorRole.SeparateConnectionWire,
            additionalAllowanceMm: 0,
            isMoldExitTransition: true);
        if (invalidAllowance)
            invalidRow.AdditionalAllowanceMm = -1;
        else
            invalidRow.X = double.NaN;
        editor.Legs.Add(invalidRow);

        bool succeeded = editor.TryCreateRequests(out ConnectionPathLegRequest[] requests, out int invalidRowNumber);

        Assert.False(succeeded);
        Assert.Empty(requests);
        Assert.Equal(2, invalidRowNumber);
    }

    /// <summary>선택 항목 생성에 필요한 연결 경로를 담은 revision별 프로젝트를 만듭니다.</summary>
    private static DesignProject CreateProject(
        long revision,
        ConnectionPointId connectionPointId,
        CircuitId circuitId)
    {
        DesignProject emptyProject = CreateEmptyProject();
        DateTimeOffset createdUtc = emptyProject.CreatedUtc;
        HeatingPathSegmentId heatingSegmentId = HeatingPathSegmentId.New();
        ConnectionPointId otherConnectionPointId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId,
            sequence: 0,
            [new CircuitSegmentReference(heatingSegmentId, sequence: 0, isReversed: false)],
            connectionPointId,
            otherConnectionPointId,
            new Supply(null, null, null));
        var connectionPoint = new ConnectionPoint(
            connectionPointId,
            circuitId,
            ConnectionPointRole.Start,
            heatingSegmentId,
            ControlPointId.New(),
            new SurfaceAnchor(FaceRefId.New(), default, default, default),
            default);
        var onMoldCurve = new LineCurveDefinition(default, new Vec3(1, 0, 0));
        var externalCurve = new LineCurveDefinition(new Vec3(1, 0, 0), new Vec3(2, 0, 0));
        var connectionPath = new ConnectionPath(
            ConnectionPathId.New(),
            connectionPointId,
            [
                new ConnectionPathSegment(
                    ConnectionPathSegmentId.New(),
                    sequence: 0,
                    ConductorRole.HeatingWireExtension,
                    GeometryDomain.OnMold,
                    new LengthMm(0),
                    onMoldCurve),
                new ConnectionPathSegment(
                    ConnectionPathSegmentId.New(),
                    sequence: 1,
                    ConductorRole.SeparateConnectionWire,
                    GeometryDomain.External,
                    new LengthMm(0),
                    externalCurve,
                    isMoldExitTransition: true)
            ]);

        return new DesignProject(
            emptyProject.Id,
            emptyProject.SchemaVersion,
            new DesignRevision(revision),
            emptyProject.Name,
            createdUtc,
            createdUtc,
            emptyProject.SourceModel,
            emptyProject.WorkingCoordinateSystem,
            emptyProject.FaceReferences,
            emptyProject.HeatingRegions,
            emptyProject.WireSpecification,
            emptyProject.PlacementConstraints,
            emptyProject.HeatingPaths,
            [circuit],
            [connectionPoint],
            [connectionPath],
            emptyProject.Rules);
    }

    /// <summary>테스트 프로젝트의 모델 지문과 작업 좌표계를 만듭니다.</summary>
    private static DesignProject CreateEmptyProject()
    {
        var modelIdentity = new ModelIdentity(new string('a', 64), 1, "STEP", "test-v1", "face-v1");
        var workingFrame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        return DesignProject.CreateEmpty(ProjectId.New(), modelIdentity, workingFrame);
    }
}
