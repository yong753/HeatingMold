using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Quality;

namespace HeatingMold.Application.Tests;

public sealed partial class CandidateCoordinatorTests
{
    /// <summary>전기 사양 적용은 Native 곡선과 회로 면적을 보존하고 새 stamp만 발급합니다.</summary>
    [Fact]
    public void UpdateElectricalSpecification_ReusesGeometryAndAreaAllocation()
    {
        ElectricalUpdateFixture fixture = CreateElectricalUpdateFixture();
        SessionSnapshot before = fixture.Session.Current;
        DesignProject project = before.CurrentProject!;
        Circuit circuit = Assert.Single(project.Circuits);
        WireSpecification replacementWire = new(
            project.WireSpecification!.MaterialName,
            project.WireSpecification.DiameterMm,
            new ResistanceOhmsPerMeter(250));
        var replacementSupply = new Supply(
            new VoltageVrms(110),
            new VoltageVrms(120),
            new CurrentAmps(10),
            new PowerWatts(1000));
        var useCase = new UpdateElectricalSpecification(
            fixture.Session,
            fixture.Coordinator,
            fixture.GeometryReuseWorkspace);

        OperationResult<InputDraft> draftResult = useCase.CreateDraft(
            replacementWire,
            new Dictionary<CircuitId, Supply> { [circuit.Id] = replacementSupply });
        Assert.True(draftResult.IsSuccess);
        Assert.Same(project, fixture.Session.Current.CurrentProject);
        Assert.Equal(InputDraftKind.ElectricalSpecification, fixture.Session.Current.CurrentInputDraft!.Kind);

        CandidateOutcome outcome = useCase.Apply();

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        SessionSnapshot updated = fixture.Session.Current;
        Assert.Null(updated.CurrentInputDraft);
        Assert.Equal(before.Geometry!.DocumentId, updated.Geometry!.DocumentId);
        Assert.Equal(before.Geometry.ModelRevision, updated.Geometry.ModelRevision);
        Assert.NotEqual(before.Geometry.DependencyStamp, updated.Geometry.DependencyStamp);
        Assert.Equal(before.Geometry.Curves.Count, updated.Geometry.Curves.Count);
        for (int index = 0; index < before.Geometry.Curves.Count; index++)
        {
            CurveGeometrySnapshot oldCurve = before.Geometry.Curves[index];
            CurveGeometrySnapshot newCurve = updated.Geometry.Curves[index];
            Assert.Equal(oldCurve.LengthMm, newCurve.LengthMm);
            Assert.Equal(oldCurve.Start, newCurve.Start);
            Assert.Equal(oldCurve.End, newCurve.End);
            Assert.Equal(oldCurve.Samples, newCurve.Samples);
            Assert.Same(oldCurve.Approximation, newCurve.Approximation);
        }

        Assert.Equal(before.AreaAllocation!.TotalEffectiveSurfaceAreaMm2, updated.AreaAllocation!.TotalEffectiveSurfaceAreaMm2);
        Assert.Equal(
            before.AreaAllocation.CircuitAppliedAreas.ToArray(),
            updated.AreaAllocation.CircuitAppliedAreas.ToArray());
        Assert.Equal(before.AreaAllocation.UnassignedAppliedAreaMm2, updated.AreaAllocation.UnassignedAppliedAreaMm2);
        Assert.Same(before.AreaAllocation.Quality, updated.AreaAllocation.Quality);
        Assert.Equal(updated.Geometry.DependencyStamp, updated.AreaAllocation.DependencyStamp);
        Assert.Equal(updated.Geometry.DependencyStamp, Assert.Single(updated.ElectricalResults).DependencyStamp);
        Assert.Equal(new ResistanceOhmsPerMeter(250), updated.ElectricalResults[0].ResistanceOhmsPerMeter);
        Assert.Equal(new VoltageVrms(110), updated.ElectricalResults[0].ActualVoltageVrms);
        Assert.Equal(updated.Geometry.DependencyStamp, updated.ElectricalAreaResult!.Allocation.DependencyStamp);
        CenterPitchQualityReport qualityReport = Assert.Single(updated.CenterPitchQualityReports);
        Assert.Equal(updated.Geometry.DependencyStamp, qualityReport.DependencyStamp);
        Assert.Equal(0, qualityReport.MaximumSampledCenterlineDistanceUpperBoundMm);
        Assert.Equal(1, fixture.GeometryReuseWorkspace.PrepareCount);
        Assert.Equal(0, fixture.StandardGeometryWorkspace.PrepareCount);
    }

    /// <summary>선경 변경은 전기 초안에서 막아 형상에 필요한 경로 재생성을 보장합니다.</summary>
    [Fact]
    public void UpdateElectricalSpecification_RejectsWireDiameterChange()
    {
        ElectricalUpdateFixture fixture = CreateElectricalUpdateFixture();
        DesignProject project = fixture.Session.Current.CurrentProject!;
        Circuit circuit = Assert.Single(project.Circuits);
        var useCase = new UpdateElectricalSpecification(
            fixture.Session,
            fixture.Coordinator,
            fixture.GeometryReuseWorkspace);

        OperationResult<InputDraft> result = useCase.CreateDraft(
            new WireSpecification(
                project.WireSpecification!.MaterialName,
                new LengthMm(project.WireSpecification.DiameterMm.Value + 0.1),
                project.WireSpecification.ResistanceOhmsPerMeter),
            new Dictionary<CircuitId, Supply> { [circuit.Id] = circuit.Supply });

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "ElectricalUpdateCannotChangeWireDiameter");
        Assert.Null(fixture.Session.Current.CurrentInputDraft);
        Assert.Equal(0, fixture.GeometryReuseWorkspace.PrepareCount);
    }

    /// <summary>기존 geometry 준비 실패는 전기 draft와 확정 면적·전기 결과를 그대로 둡니다.</summary>
    [Fact]
    public void UpdateElectricalSpecification_PrepareFailureKeepsDraftAndCommittedSnapshot()
    {
        ElectricalUpdateFixture fixture = CreateElectricalUpdateFixture();
        DesignProject project = fixture.Session.Current.CurrentProject!;
        Circuit circuit = Assert.Single(project.Circuits);
        var useCase = new UpdateElectricalSpecification(
            fixture.Session,
            fixture.Coordinator,
            fixture.GeometryReuseWorkspace);
        Assert.True(useCase.CreateDraft(
            new WireSpecification(
                project.WireSpecification!.MaterialName,
                project.WireSpecification.DiameterMm,
                new ResistanceOhmsPerMeter(250)),
            new Dictionary<CircuitId, Supply> { [circuit.Id] = circuit.Supply }).IsSuccess);
        SessionSnapshot beforeApply = fixture.Session.Current;
        fixture.GeometryReuseWorkspace.PrepareException = new InvalidOperationException("재사용 형상 오류");

        CandidateOutcome outcome = useCase.Apply();

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        Assert.Same(beforeApply, fixture.Session.Current);
        Assert.Equal(InputDraftKind.ElectricalSpecification, fixture.Session.Current.CurrentInputDraft!.Kind);
        Assert.Equal(1, fixture.GeometryReuseWorkspace.PrepareCount);
        Assert.Equal(0, fixture.StandardGeometryWorkspace.PrepareCount);
    }

    /// <summary>취소는 전기 입력만 지우고 확정 설계와 기존 기하를 유지합니다.</summary>
    [Fact]
    public void UpdateElectricalSpecification_CancelDraftPreservesCommittedState()
    {
        ElectricalUpdateFixture fixture = CreateElectricalUpdateFixture();
        SessionSnapshot before = fixture.Session.Current;
        DesignProject project = before.CurrentProject!;
        Circuit circuit = Assert.Single(project.Circuits);
        var useCase = new UpdateElectricalSpecification(
            fixture.Session,
            fixture.Coordinator,
            fixture.GeometryReuseWorkspace);
        Assert.True(useCase.CreateDraft(
            project.WireSpecification!,
            new Dictionary<CircuitId, Supply> { [circuit.Id] = circuit.Supply }).IsSuccess);

        Assert.True(useCase.CancelDraft());

        Assert.Same(project, fixture.Session.Current.CurrentProject);
        Assert.Same(before.Geometry, fixture.Session.Current.Geometry);
        Assert.Same(before.AreaAllocation, fixture.Session.Current.AreaAllocation);
        Assert.Null(fixture.Session.Current.CurrentInputDraft);
        Assert.Equal(0, fixture.GeometryReuseWorkspace.PrepareCount);
    }

    /// <summary>전기 편집 유스케이스가 다른 종류의 입력 초안을 대신 취소하지 않습니다.</summary>
    [Fact]
    public void UpdateElectricalSpecification_CancelDoesNotClearAnotherDraftKind()
    {
        Fixture fixture = Fixture.Create();
        var pendingDraft = new InputDraft(
            InputDraftKind.PlacementConstraints,
            fixture.InitialSnapshot.CurrentProject!.Revision,
            fixture.NextProject);
        Assert.True(fixture.Session.TrySetDraft(pendingDraft));
        var useCase = new UpdateElectricalSpecification(
            fixture.Session,
            fixture.Coordinator,
            new FakeElectricalGeometryWorkspace());

        Assert.False(useCase.CancelDraft());

        Assert.Same(pendingDraft, fixture.Session.Current.CurrentInputDraft);
    }

    private static ElectricalUpdateFixture CreateElectricalUpdateFixture()
    {
        DesignProject project = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        var runtimeDocument = new RuntimeDocument(
            GeometryDocumentId.New(),
            new ModelRevision(1),
            project.SourceModel.SourceSha256);
        var stamp = new DependencyStamp(
            project.Revision,
            project.SourceModel.SourceSha256,
            new string('b', 64),
            new string('c', 64),
            "electrical-update-test-v1");
        var geometry = new GeometrySnapshot(
            runtimeDocument.DocumentId,
            runtimeDocument.ModelRevision,
            stamp,
            FakeGeometryWorkspace.CreateCurveSnapshots(project));
        Circuit circuit = Assert.Single(project.Circuits);
        var quality = new AreaAllocationQuality(
            0.1,
            0.1,
            100,
            0,
            0.01,
            1,
            true,
            project.Rules.AreaRuleVersion);
        var allocation = new AppliedAreaAllocation(
            stamp,
            new AreaMm2(100),
            [new KeyValuePair<CircuitId, AreaMm2>(circuit.Id, new AreaMm2(100))],
            new AreaMm2(0),
            quality);
        var qualityReport = new CenterPitchQualityReport(
            project.HeatingRegions[0].Id,
            stamp,
            new AreaMm2(100),
            0,
            0,
            0,
            project.WireSpecification!.DiameterMm,
            null,
            [],
            [],
            "quality-test-v1");
        var initialSnapshot = new SessionSnapshot(
            project,
            runtimeDocument,
            geometry,
            [],
            allocation,
            null,
            null,
            dirtySinceSave: false,
            lastSavedRevision: project.Revision,
            isPresentationOutOfSync: false,
            centerPitchQualityReports: [qualityReport]);
        var session = new ProjectSession(initialSnapshot);
        var standardWorkspace = new FakeGeometryWorkspace();
        var events = new FakeApplicationEvents();
        var coordinator = new CandidateCoordinator(
            session,
            standardWorkspace,
            new FakeOperationContext(),
            events);
        var geometryReuseWorkspace = new FakeElectricalGeometryWorkspace();
        return new ElectricalUpdateFixture(
            session,
            coordinator,
            standardWorkspace,
            geometryReuseWorkspace);
    }

    private sealed record ElectricalUpdateFixture(
        ProjectSession Session,
        CandidateCoordinator Coordinator,
        FakeGeometryWorkspace StandardGeometryWorkspace,
        FakeElectricalGeometryWorkspace GeometryReuseWorkspace);

    private sealed class FakeElectricalGeometryWorkspace : IElectricalGeometryWorkspace
    {
        public int PrepareCount { get; private set; }
        public Exception? PrepareException { get; set; }

        public IPreparedGeometry PrepareElectricalUpdate(
            DesignProject candidate,
            GeometrySnapshot currentGeometry,
            RuntimeDocument currentRuntimeDocument,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareException is not null)
                throw PrepareException;

            var stamp = new DependencyStamp(
                candidate.Revision,
                currentGeometry.DependencyStamp.SourceModelSha256,
                currentGeometry.DependencyStamp.WorkingCoordinateSystemFingerprint,
                new string('d', 64),
                currentGeometry.DependencyStamp.RuleVersion);
            var reboundGeometry = new GeometrySnapshot(
                currentGeometry.DocumentId,
                currentGeometry.ModelRevision,
                stamp,
                currentGeometry.Curves);
            return new FakePreparedGeometry(currentRuntimeDocument, reboundGeometry);
        }
    }
}
