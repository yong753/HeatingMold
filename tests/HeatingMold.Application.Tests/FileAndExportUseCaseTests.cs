using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.Tests;

/// <summary>프로젝트 파일과 출력 유스케이스의 실패 보존 및 revision 규칙을 검사합니다.</summary>
public sealed class FileAndExportUseCaseTests
{
    /// <summary>정상 저장은 저장한 현재 revision만 clean 상태로 바꿉니다.</summary>
    [Fact]
    public void SaveProject_SuccessMarksCapturedRevisionClean()
    {
        Fixture fixture = Fixture.Create(dirty: true);
        var store = new FakePackageStore();
        var useCase = new SaveProject(fixture.Session, store);

        OperationResult<SaveProjectOutcome> result = useCase.Execute("design.hmproj");

        Assert.True(result.IsSuccess);
        Assert.True(result.GetValueOrThrow().MarkedCurrentRevisionClean);
        Assert.False(fixture.Session.Current.DirtySinceSave);
        Assert.Equal(fixture.Session.Current.CurrentProject!.Revision, fixture.Session.Current.LastSavedRevision);
        Assert.Equal("design.hmproj", store.LastWrittenPath);
        Assert.Same(fixture.Project, store.LastWrittenSnapshot!.Project);
    }

    /// <summary>저장 포트 실패는 세션 revision과 dirty 상태를 변경하지 않습니다.</summary>
    [Fact]
    public void SaveProject_WriteFailurePreservesDirtySession()
    {
        Fixture fixture = Fixture.Create(dirty: true);
        SessionSnapshot before = fixture.Session.Current;
        var store = new FakePackageStore
        {
            WriteResult = OperationResult<ProjectPackageWriteReceipt>.Failure([Fixture.Issue("DiskFull")])
        };

        OperationResult<SaveProjectOutcome> result = new SaveProject(fixture.Session, store).Execute("design.hmproj");

        Assert.False(result.IsSuccess);
        Assert.Same(before, fixture.Session.Current);
    }

    /// <summary>파일 기록 중 새 revision이 확정되면 이전 파일은 저장하되 현재 설계는 dirty로 둡니다.</summary>
    [Fact]
    public void SaveProject_RevisionChangeDuringWriteDoesNotMarkNewRevisionClean()
    {
        Fixture fixture = Fixture.Create(dirty: true);
        var store = new FakePackageStore
        {
            BeforeWrite = () => fixture.CommitAxisChange()
        };

        OperationResult<SaveProjectOutcome> result = new SaveProject(fixture.Session, store).Execute("design.hmproj");

        Assert.True(result.IsSuccess);
        Assert.False(result.GetValueOrThrow().MarkedCurrentRevisionClean);
        Assert.Equal(new DesignRevision(1), fixture.Session.Current.CurrentProject!.Revision);
        Assert.True(fixture.Session.Current.DirtySinceSave);
        Assert.Equal(new DesignRevision(0), result.GetValueOrThrow().Receipt.SavedRevision);
    }

    /// <summary>패키지 읽기 오류는 복원 준비를 시작하지 않고 기존 snapshot을 보존합니다.</summary>
    [Fact]
    public void OpenProject_ReadFailurePreservesCurrentSession()
    {
        Fixture fixture = Fixture.Create();
        SessionSnapshot before = fixture.Session.Current;
        var store = new FakePackageStore
        {
            ReadResult = OperationResult<ProjectPackageReadResult>.Failure([Fixture.Issue("PackageIntegrityFailure")])
        };

        CandidateOutcome outcome = new OpenProject(fixture.Session, store, fixture.Coordinator).Execute("broken.hmproj");

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Same(before, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.RestoreCount);
    }

    /// <summary>복원 작업은 패키지 원본으로 staged 문서를 만들고 저장 기준 revision을 clean으로 유지합니다.</summary>
    [Fact]
    public void OpenProject_RestoresModelSourceBeforeReplacingSession()
    {
        Fixture fixture = Fixture.Create();
        ProjectPackageReadResult package = new(fixture.Project, fixture.SourceModel);
        var store = new FakePackageStore { ReadResult = OperationResult<ProjectPackageReadResult>.Success(package) };

        CandidateOutcome outcome = new OpenProject(fixture.Session, store, fixture.Coordinator).Execute("design.hmproj");

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Equal(new DesignRevision(1), fixture.Session.Current.CurrentProject!.Revision);
        Assert.Equal(fixture.Project.Revision, fixture.Session.Current.LastSavedRevision);
        Assert.False(fixture.Session.Current.DirtySinceSave);
        Assert.Same(fixture.SourceModel, fixture.Workspace.LastRestoreSource);
        Assert.Equal(1, fixture.Workspace.RestoreCount);
    }

    /// <summary>미적용 draft가 있으면 DXF 또는 STEP 출력 포트를 호출하지 않습니다.</summary>
    [Fact]
    public void ExportDesign_PendingDraftBlocksOutput()
    {
        Fixture fixture = Fixture.Create();
        DesignProject candidate = Fixture.CopyWithRevision(fixture.Project, fixture.Project.Revision.Next());
        Assert.True(fixture.Session.TrySetDraft(new InputDraft(
            InputDraftKind.PlacementConstraints, fixture.Project.Revision, candidate)));
        var exporter = new FakeExporter();

        OperationResult<ExportReceipt> result = new ExportDesign(fixture.Session, exporter).Execute(
            "design.dxf", new ExportOptions(ExportFormat.Dxf));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, exporter.CallCount);
        Assert.Equal(WorkflowState.DraftPending, fixture.Session.WorkflowState);
    }

    /// <summary>면적 분할 stamp가 오래되면 현재 기하와 결합하지 않고 출력을 차단합니다.</summary>
    [Fact]
    public void ExportDesign_StaleDerivedAreaBlocksOutput()
    {
        Fixture fixture = Fixture.Create();
        DependencyStamp staleStamp = Fixture.CreateStamp(new DesignRevision(99), fixture.Project.SourceModel.SourceSha256);
        var staleAllocation = new AppliedAreaAllocation(
            staleStamp,
            new AreaMm2(0),
            [],
            new AreaMm2(0),
            new AreaAllocationQuality(0.1, 0.1, 1, 0, 0.1, 1, true, "area-v1"));
        SessionSnapshot staleSession = new(
            fixture.Project,
            fixture.RuntimeDocument,
            fixture.Geometry,
            [],
            staleAllocation,
            null,
            null,
            dirtySinceSave: false,
            lastSavedRevision: fixture.Project.Revision,
            isPresentationOutOfSync: false,
            sourceModel: fixture.SourceModel);
        var session = new ProjectSession(staleSession);
        var exporter = new FakeExporter();

        OperationResult<ExportReceipt> result = new ExportDesign(session, exporter).Execute(
            "design.step", new ExportOptions(ExportFormat.Step));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, exporter.CallCount);
    }

    /// <summary>유효한 고정 snapshot만 선택한 형식과 경로로 exporter에 전달합니다.</summary>
    [Fact]
    public void ExportDesign_ValidSnapshotCallsExporter()
    {
        Fixture fixture = Fixture.Create();
        var exporter = new FakeExporter();

        OperationResult<ExportReceipt> result = new ExportDesign(fixture.Session, exporter).Execute(
            "design.step", new ExportOptions(ExportFormat.Step));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, exporter.CallCount);
        Assert.Equal("design.step", exporter.LastPath);
        Assert.Equal(fixture.Project.Revision, exporter.LastSnapshot!.Project.Revision);
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Created = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

        public ProjectSession Session { get; }
        public DesignProject Project { get; }
        public ModelSource SourceModel { get; }
        public RuntimeDocument RuntimeDocument { get; }
        public GeometrySnapshot Geometry { get; }
        public FakeGeometryWorkspace Workspace { get; }
        public CandidateCoordinator Coordinator { get; }

        private Fixture(bool dirty)
        {
            byte[] bytes = [1, 3, 5, 7, 9];
            string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var identity = new ModelIdentity(sourceHash, 1, "TestImporter", "1", "face-v1");
            SourceModel = new ModelSource(identity, bytes);
            Project = DesignProject.CreateEmpty(
                ProjectId.New(), identity, Fixture.Axis(), "File fixture", Created);
            RuntimeDocument = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(1), sourceHash);
            Geometry = new GeometrySnapshot(
                RuntimeDocument.DocumentId,
                RuntimeDocument.ModelRevision,
                CreateStamp(Project.Revision, sourceHash),
                []);
            var snapshot = new SessionSnapshot(
                Project, RuntimeDocument, Geometry, [], null, null, null,
                dirty, dirty ? null : Project.Revision, isPresentationOutOfSync: false, SourceModel);
            Session = new ProjectSession(snapshot);
            Workspace = new FakeGeometryWorkspace();
            Coordinator = new CandidateCoordinator(Session, Workspace, new FakeOperationContext(), new FakeApplicationEvents());
        }

        public static Fixture Create(bool dirty = false) => new(dirty);

        public void CommitAxisChange()
        {
            var useCase = new ChangeWorkingAxis(Session, Coordinator);
            CandidateOutcome outcome = useCase.Execute(new WorkingCoordinateSystemDefinition(
                new Vec3(2, 3, 4), new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
            Assert.Equal(CandidateStatus.Committed, outcome.Status);
        }

        public static DesignProject CopyWithRevision(DesignProject project, DesignRevision revision) => new(
            project.Id, project.SchemaVersion, revision, project.Name, project.CreatedUtc, project.ModifiedUtc,
            project.SourceModel, project.WorkingCoordinateSystem, project.FaceReferences, project.HeatingRegions,
            project.WireSpecification, project.PlacementConstraints, project.HeatingPaths, project.Circuits,
            project.ConnectionPoints, project.ConnectionPaths, project.Rules);

        public static DependencyStamp CreateStamp(DesignRevision revision, string sourceHash) => new(
            revision, sourceHash, new string('b', 64), new string('c', 64), "fixture-v1");

        public static ValidationIssue Issue(string code) => new(
            code, ValidationSeverity.Error, $"Test.{code}", [], "test-v1");

        private static WorkingCoordinateSystemDefinition Axis() => new(
            default, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));
    }

    private sealed class FakePackageStore : IProjectPackageStore
    {
        public OperationResult<ProjectPackageReadResult>? ReadResult { get; init; }
        public OperationResult<ProjectPackageWriteReceipt>? WriteResult { get; init; }
        public ProjectSaveSnapshot? LastWrittenSnapshot { get; private set; }
        public string? LastWrittenPath { get; private set; }
        public Action? BeforeWrite { get; init; }

        public OperationResult<ProjectPackageReadResult> Read(string path, CancellationToken cancellationToken = default) =>
            ReadResult ?? OperationResult<ProjectPackageReadResult>.Failure([Fixture.Issue("NotConfigured")]);

        public OperationResult<ProjectPackageWriteReceipt> Write(
            string path,
            ProjectSaveSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            BeforeWrite?.Invoke();
            LastWrittenSnapshot = snapshot;
            LastWrittenPath = path;
            return WriteResult ?? OperationResult<ProjectPackageWriteReceipt>.Success(
                new ProjectPackageWriteReceipt(path, snapshot.Project.Revision, 128, new string('a', 64)));
        }
    }

    private sealed class FakeExporter : IDesignExporter
    {
        public int CallCount { get; private set; }
        public ExportSnapshot? LastSnapshot { get; private set; }
        public string? LastPath { get; private set; }

        public OperationResult<ExportReceipt> Export(
            ExportSnapshot snapshot,
            ExportOptions options,
            string path,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSnapshot = snapshot;
            LastPath = path;
            return OperationResult<ExportReceipt>.Success(new ExportReceipt(path, options.Format, 64, new string('d', 64)));
        }
    }

    private sealed class FakeGeometryWorkspace : IGeometryWorkspace
    {
        public int RestoreCount { get; private set; }
        public ModelSource? LastRestoreSource { get; private set; }

        public IPreparedGeometry PrepareModel(DesignProject candidate, ModelSource modelSource, CancellationToken cancellationToken = default) =>
            PrepareCandidate(candidate);

        public IPreparedGeometry Prepare(DesignProject candidate, CancellationToken cancellationToken = default) =>
            PrepareCandidate(candidate);

        public IPreparedGeometry Restore(
            DesignProject project,
            ModelSource modelSource,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            LastRestoreSource = modelSource;
            return PrepareCandidate(project);
        }

        private static IPreparedGeometry PrepareCandidate(DesignProject project)
        {
            string sourceHash = project.SourceModel.SourceSha256;
            var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(3), sourceHash);
            var snapshot = new GeometrySnapshot(
                runtime.DocumentId,
                runtime.ModelRevision,
                Fixture.CreateStamp(project.Revision, sourceHash),
                []);
            return new FakePreparedGeometry(runtime, snapshot);
        }
    }

    private sealed class FakePreparedGeometry : IPreparedGeometry
    {
        public GeometrySnapshot Snapshot { get; }
        public IGeometryQueries Queries { get; } = new FakeGeometryQueries();
        public RuntimeDocument RuntimeDocument { get; }

        public FakePreparedGeometry(RuntimeDocument runtimeDocument, GeometrySnapshot snapshot)
        {
            RuntimeDocument = runtimeDocument;
            Snapshot = snapshot;
        }

        public GeometryCommitReceipt Publish() => new(true, RuntimeDocument);
        public void Dispose() { }
    }

    private sealed class FakeGeometryQueries : IGeometryQueries
    {
        public GeometryQueryResult<SurfaceHit> QueryFirstHit(
            WorkingCoordinateSystem coordinateSystem,
            Vec2 workingUv,
            FaceRefId selectedFaceReferenceId,
            CancellationToken cancellationToken = default) => GeometryQueryResult<SurfaceHit>.Miss();

        public GeometryQueryResult<SelectedTopology> GetSelectedTopology(
            IReadOnlyList<FaceRefId> selectedFaceReferences,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SelectedTopology>.Succeeded(new SelectedTopology([], []));

        public GeometryQueryResult<bool> AreAdjacent(
            FaceRefId first,
            FaceRefId second,
            CancellationToken cancellationToken = default) => GeometryQueryResult<bool>.Succeeded(false);

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
            IReadOnlyList<CurveGeometrySnapshot> curves,
            LengthMm wireDiameterMm,
            LengthMm requiredClearGapMm,
            GeometryToleranceProfile tolerances,
            CancellationToken cancellationToken = default) => GeometryQueryResult<ClearanceMeasurement>.Unresolved([]);

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(
            CurveGeometrySnapshot curve,
            LengthMm wireDiameterMm,
            SurfaceAnchor anchor,
            WorkingCoordinateSystem coordinateSystem,
            LengthMm requiredInsertionDepthMm,
            CancellationToken cancellationToken = default) => GeometryQueryResult<DepthMeasurement>.Unresolved([]);

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(
            HeatingRegion region,
            GeometryToleranceProfile tolerances,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SurfaceElementSet>.Succeeded(new SurfaceElementSet([], new HeatingMold.Core.Primitives.AreaMm2(0)));

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(
            ConnectionPath connectionPath,
            WorkingCoordinateSystem coordinateSystem,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(new ConnectionObstacleCheck(false, null, null, false));
    }

    private sealed class FakeOperationContext : IOperationContext
    {
        public void VerifyGeometryThread() { }
    }

    private sealed class FakeApplicationEvents : IApplicationEvents
    {
        public void Publish(ApplicationEvent applicationEvent) { }
    }
}
