using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.Application.Sessions;
using HeatingMold.App;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.Quality;
using HeatingMold.Electrical.Area;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>회로 전기 결과 패널이 미계산·오래된 값을 통과로 보이지 않는지 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class ElectricalResultsPresentationTests
{
    /// <summary>전기 계산 결과가 없는 회로는 0 대신 미계산과 빈 값을 표시합니다.</summary>
    [Fact]
    public void BuildCircuitRows_MissingResult_UsesUnavailableValues()
    {
        Circuit circuit = CreateCircuit();

        IReadOnlyList<CircuitElectricalDisplayRow> rows = ElectricalResultsPresentation.BuildCircuitRows(
            [circuit],
            [],
            CreateStamp(1),
            hasElectricalDraft: false);

        CircuitElectricalDisplayRow row = Assert.Single(rows);
        Assert.Equal("Missing", row.StatusKind);
        Assert.Contains("계산 결과 없음", row.Status);
        Assert.Contains("—", row.LengthAndResistance);
        Assert.DoesNotContain("0.00", row.LengthAndResistance);
        Assert.Equal(circuit.Id, row.CircuitId);
    }

    /// <summary>기하 지문이 다른 과거 결과는 통과 상태를 그대로 현재값처럼 표시하지 않습니다.</summary>
    [Fact]
    public void BuildCircuitRows_StaleResult_IsMarkedAsPrevious()
    {
        Circuit circuit = CreateCircuit();
        ElectricalResult result = CreateIncompleteResult(circuit.Id, CreateStamp(1));

        IReadOnlyList<CircuitElectricalDisplayRow> rows = ElectricalResultsPresentation.BuildCircuitRows(
            [circuit],
            [result],
            CreateStamp(2),
            hasElectricalDraft: false);

        CircuitElectricalDisplayRow row = Assert.Single(rows);
        Assert.Equal("Stale", row.StatusKind);
        Assert.StartsWith("이전 결과", row.Status);
        Assert.Contains("현재 확정 형상에 적용할 수 없습니다", row.Details);
        Assert.Contains("별도 연결선 0.00 mm", row.LengthBreakdownDetails);
        Assert.Contains("(저항 제외)", row.LengthBreakdownDetails);
    }

    /// <summary>전기 입력 초안이 적용되지 않았으면 기존 결과에 그 상태를 덧붙입니다.</summary>
    [Fact]
    public void BuildCircuitRows_ElectricalDraft_MarksCommittedValuesAsUnapplied()
    {
        Circuit circuit = CreateCircuit();
        ElectricalResult result = CreateIncompleteResult(circuit.Id, CreateStamp(1));

        IReadOnlyList<CircuitElectricalDisplayRow> rows = ElectricalResultsPresentation.BuildCircuitRows(
            [circuit],
            [result],
            CreateStamp(1),
            hasElectricalDraft: true);

        CircuitElectricalDisplayRow row = Assert.Single(rows);
        Assert.Equal("Incomplete", row.StatusKind);
        Assert.Contains("전기 입력 초안 미적용", row.Details);
        Assert.Contains("필수 입력 누락: 실제 전압", row.Details);
    }

    /// <summary>비수렴 면적과 전기 미평가 상태를 회로 행까지 보존하고 열 성능으로 과장하지 않습니다.</summary>
    [Fact]
    public void Build_AppliedArea_ReportsAllocationAndUnevaluatedElectricalArea()
    {
        Circuit circuit = CreateCircuit();
        DependencyStamp stamp = CreateStamp(1);
        string createdSourceHash = stamp.SourceModelSha256;
        var identity = new ModelIdentity(createdSourceHash, 1, "Fixture", "1", "face-v1");
        var workingAxis = new WorkingCoordinateSystemDefinition(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DateTimeOffset createdUtc = DateTimeOffset.UtcNow;
        var project = new DesignProject(
            ProjectId.New(),
            1,
            new DesignRevision(0),
            "면적 표시 fixture",
            createdUtc,
            createdUtc,
            identity,
            workingAxis,
            [],
            [],
            null,
            null,
            [],
            [circuit],
            [],
            []);
        var runtime = new RuntimeDocument(
            GeometryDocumentId.New(),
            new ModelRevision(1),
            identity.SourceSha256);
        var geometry = new GeometrySnapshot(
            runtime.DocumentId,
            runtime.ModelRevision,
            stamp,
            []);
        var allocation = new AppliedAreaAllocation(
            stamp,
            new AreaMm2(10_000),
            [new KeyValuePair<CircuitId, AreaMm2>(circuit.Id, new AreaMm2(6_000))],
            new AreaMm2(4_000),
            new AreaAllocationQuality(0.05, 0.1, 10, 0.8, 0.5, 2, false, "area-v1"));
        ElectricallyCompliantAreaResult electricalArea =
            new ElectricallyCompliantAreaAggregator().Aggregate(allocation, []);
        var snapshot = new SessionSnapshot(
            project,
            runtime,
            geometry,
            [],
            allocation,
            electricalArea,
            null,
            dirtySinceSave: false,
            lastSavedRevision: null,
            isPresentationOutOfSync: false);

        ElectricalPanelData panel = ElectricalResultsPresentation.Build(snapshot);

        Assert.Contains("현재 분할", panel.AreaStatus);
        Assert.Contains("전체 적용면적: 100.00 cm²", panel.AreaSummary);
        Assert.Contains("미배정 적용면적: 40.00 cm²", panel.AreaSummary);
        Assert.Contains("미평가 회로 적용면적: 60.00 cm²", panel.AreaSummary);
        Assert.Contains("면적 보존 수렴 미확인", panel.AreaStatus);
        Assert.Contains("실제 온도 도달 면적이나 가열 성능을 뜻하지 않습니다", panel.AreaSummary);
        CircuitAppliedAreaDisplayRow circuitAreaRow = Assert.Single(panel.CircuitAreaRows);
        Assert.Contains("면적 수렴 미확인", circuitAreaRow.Status);
        Assert.Contains("계산 결과 없음", circuitAreaRow.Status);
    }

    /// <summary>품질 패널은 최대 관측 위치와 레인 편차를 보여 주고 합격 판정으로 부르지 않습니다.</summary>
    [Fact]
    public void Build_QualityPanel_ReportsObservedLocationsAndPitchDeviation()
    {
        DependencyStamp stamp = CreateStamp(1);
        var identity = new ModelIdentity(stamp.SourceModelSha256, 1, "Fixture", "1", "face-v1");
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(0), "품질 표시 fixture",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            identity,
            new WorkingCoordinateSystemDefinition(
                new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [], [], null, null, [], [], [], []);
        var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(1), identity.SourceSha256);
        var geometry = new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, []);
        RegionId regionId = RegionId.New();
        HeatingPathSegmentId firstSegmentId = HeatingPathSegmentId.New();
        HeatingPathSegmentId secondSegmentId = HeatingPathSegmentId.New();
        var report = new CenterPitchQualityReport(
            regionId,
            stamp,
            new AreaMm2(100),
            maximumSampledCenterlineDistanceMm: 25,
            maximumSampledCenterlineDistanceUpperBoundMm: 25.1,
            surfaceProbeCount: 4,
            new LengthMm(0.3),
            new LengthMm(10.3),
            [
                new SurfacePathDistanceSample(0, new Vec3(9, 8, 7), firstSegmentId, 15, 0.1),
                new SurfacePathDistanceSample(1, new Vec3(1, 2, 3), firstSegmentId, 25, 0.1)
            ],
            [new LanePitchMeasurement(
                0, 1, firstSegmentId, secondSegmentId,
                49, 50, 51, new Vec3(4, 5, 6), 0.2, 8,
                new LengthMm(10.3), 39.7)],
            "quality-v1");
        var snapshot = new SessionSnapshot(
            project, runtime, geometry, [], null, null,
            null, dirtySinceSave: false, lastSavedRevision: null, isPresentationOutOfSync: false,
            centerPitchQualityReports: [report]);

        CenterPitchQualityPanelData panel = CenterPitchQualityPresentation.Build(snapshot);

        CenterPitchQualityDisplayRow row = Assert.Single(panel.Rows);
        Assert.Contains("표본 기반 관측값", panel.Status);
        Assert.Contains("열적 합격 판정은 아닙니다", panel.Status);
        Assert.Contains("25.00 mm", row.CoverageSummary);
        Assert.Contains("표본 4개", row.CoverageSummary);
        Assert.Contains("(1.00, 2.00, 3.00)", row.CoverageLocation);
        CenterPitchQualityLanePitchDisplayRow laneRow = Assert.Single(row.LanePitchRows);
        Assert.Contains("목표 10.30 mm 대비 +39.70 mm", laneRow.Text);
        Assert.Contains("(4.00, 5.00, 6.00)", laneRow.Text);
        Assert.True(row.CanShowCoverageLocation);
        Assert.Equal(new Vec3(1, 2, 3), row.CoverageLocationSelection?.Location);
    }

    /// <summary>형상 지문이 바뀌거나 화면 복구 중이면 표본 위치 선택을 거부합니다.</summary>
    [Fact]
    public void TryResolveCurrentLocation_RejectsStaleOrUnsynchronizedSelection()
    {
        DependencyStamp stamp = CreateStamp(1);
        SessionSnapshot snapshot = CreateQualitySnapshot(stamp, isPresentationOutOfSync: false);
        CenterPitchQualityDisplayRow row = Assert.Single(CenterPitchQualityPresentation.Build(snapshot).Rows);
        CenterPitchQualityLocationSelection selection = Assert.IsType<CenterPitchQualityLocationSelection>(row.CoverageLocationSelection);

        Assert.True(CenterPitchQualityPresentation.TryResolveCurrentLocation(snapshot, selection, out Vec3 location));
        Assert.Equal(new Vec3(1, 2, 3), location);
        Assert.False(CenterPitchQualityPresentation.TryResolveCurrentLocation(
            snapshot,
            selection with { DependencyStamp = CreateStamp(2) },
            out _));
        Assert.False(CenterPitchQualityPresentation.TryResolveCurrentLocation(
            snapshot,
            selection with { Location = new Vec3(99, 99, 99) },
            out _));
        Assert.False(CenterPitchQualityPresentation.TryResolveCurrentLocation(
            snapshot.WithPresentationOutOfSync(),
            selection,
            out _));
    }

    /// <summary>패널 행 클릭은 최신 위치를 표시하고 화면 불일치 뒤에는 지웁니다.</summary>
    [Fact]
    public void QualityLocationClick_ShowsCurrentSampleAndRejectsOutOfSyncSession()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            SessionSnapshot snapshot = CreateQualitySnapshot(CreateStamp(1), isPresentationOutOfSync: false);
            CenterPitchQualityLocationSelection selection = Assert.IsType<CenterPitchQualityLocationSelection>(
                Assert.Single(CenterPitchQualityPresentation.Build(snapshot).Rows).CoverageLocationSelection);
            AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices() with
            {
                ProjectSession = new ProjectSession(snapshot)
            };
            var window = new MainWindow(services);
            var button = new Button { Tag = selection };

            InvokeQualityLocationClick(window, button);

            Assert.Equal(1, Bridge.ShowQualityIssueLocationCount);
            Assert.Equal(new[] { 1d, 2d, 3d }, Bridge.LastQualityIssueLocation);

            ProjectSession session = (ProjectSession)GetPrivateField(window, "_projectSession");
            session.MarkPresentationOutOfSync();
            InvokeQualityLocationClick(window, button);

            Assert.Equal(1, Bridge.ShowQualityIssueLocationCount);
            Assert.Equal(1, Bridge.ClearQualityIssueLocationCount);
            Assert.Contains("표본 위치를 표시할 수 없습니다", window.CenterPitchQualityStatusText.Text);
        });
    }

    /// <summary>지정한 지문으로 품질 표본이 포함된 확정 snapshot을 만듭니다.</summary>
    private static SessionSnapshot CreateQualitySnapshot(DependencyStamp stamp, bool isPresentationOutOfSync)
    {
        var identity = new ModelIdentity(stamp.SourceModelSha256, 1, "Fixture", "1", "face-v1");
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(0), "품질 선택 fixture",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            identity,
            new WorkingCoordinateSystemDefinition(
                new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [], [], null, null, [], [], [], []);
        var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(1), identity.SourceSha256);
        var geometry = new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, []);
        RegionId regionId = RegionId.New();
        HeatingPathSegmentId segmentId = HeatingPathSegmentId.New();
        var report = new CenterPitchQualityReport(
            regionId,
            stamp,
            new AreaMm2(10),
            12,
            12.1,
            1,
            new LengthMm(0.1),
            new LengthMm(10),
            [new SurfacePathDistanceSample(0, new Vec3(1, 2, 3), segmentId, 12, 0.1)],
            [],
            "quality-v1");

        return new SessionSnapshot(
            project, runtime, geometry, [], null, null,
            null, dirtySinceSave: false, lastSavedRevision: null, isPresentationOutOfSync: isPresentationOutOfSync,
            centerPitchQualityReports: [report]);
    }

    /// <summary>WPF 클릭 handler를 대역 버튼으로 호출합니다.</summary>
    private static void InvokeQualityLocationClick(MainWindow window, Button button)
    {
        MethodInfo handler = typeof(MainWindow).GetMethod(
            "QualityLocation_Click",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "QualityLocation_Click");
        handler.Invoke(window, [button, new RoutedEventArgs()]);
    }

    /// <summary>화면 테스트에서 비공개 필드를 확인합니다.</summary>
    private static object GetPrivateField(object target, string fieldName) =>
        target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target)
        ?? throw new MissingFieldException(target.GetType().FullName, fieldName);

    /// <summary>표시 검사에 사용할 결과를 필수 전압 누락 상태로 만듭니다.</summary>
    private static ElectricalResult CreateIncompleteResult(CircuitId circuitId, DependencyStamp stamp) => new(
        circuitId,
        stamp,
        new LengthBreakdown([]),
        "fixture-electrical-v1",
        new LengthMm(0),
        resistanceLengthMeters: null,
        actualVoltageVrms: null,
        resistanceOhmsPerMeter: null,
        resistanceOhms: null,
        currentAmps: null,
        powerWatts: null,
        ElectricalStatus.Incomplete,
        Array.Empty<ElectricalLimitResult>(),
        inputIssues: ["Missing:ActualVoltageVrms"]);

    /// <summary>안정 ID와 순서를 가진 테스트용 회로를 만듭니다.</summary>
    private static Circuit CreateCircuit()
    {
        CircuitId circuitId = CircuitId.New();
        return new Circuit(
            circuitId,
            sequence: 0,
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), sequence: 0, isReversed: false)],
            ConnectionPointId.New(),
            ConnectionPointId.New(),
            new Supply(null, null, null));
    }

    /// <summary>서로 다른 입력 지문을 가진 계산 의존 지문을 만듭니다.</summary>
    private static DependencyStamp CreateStamp(int generation)
    {
        string hash = generation.ToString("X64");
        return new DependencyStamp(
            new DesignRevision(0),
            hash,
            new string('A', 64),
            new string((char)('B' + generation), 64),
            "fixture-stamp-v1");
    }
}
