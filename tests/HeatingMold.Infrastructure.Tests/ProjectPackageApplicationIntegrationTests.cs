using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Persistence;

namespace HeatingMold.Infrastructure.Tests;

/// <summary>실제 패키지 저장소와 Application 세션의 열기·저장 흐름을 함께 검증합니다.</summary>
public sealed class ProjectPackageApplicationIntegrationTests
{
    /// <summary>저장한 프로젝트를 세션에 복원하고 다시 저장해 원본과 revision을 보존합니다.</summary>
    [Fact]
    public void OpenAndSave_RoundTripsProjectThroughRealPackageStore()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string sourcePath = Path.Combine(temporaryDirectory.Path, "source.hmproj");
        string savedPath = Path.Combine(temporaryDirectory.Path, "saved.hmproj");
        string storageDirectory = Path.Combine(temporaryDirectory.Path, "session-models");
        byte[] modelBytes = [3, 7, 11, 19, 29];
        string modelHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes))
            .ToLowerInvariant();
        var identity = new ModelIdentity(modelHash, 1, "Integration fixture", "1", "face-v1");
        var sourceModel = new ModelSource(identity, modelBytes);
        var coordinateSystem = new WorkingCoordinateSystemDefinition(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DateTimeOffset createdUtc = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        DesignProject project = DesignProject.CreateEmpty(
            ProjectId.New(), identity, coordinateSystem, "Package integration", createdUtc);
        var packageStore = new ProjectPackageStore(inMemoryThresholdBytes: 1, temporaryDirectory: storageDirectory);
        var savedSnapshot = new SessionSnapshot(
            project,
            runtimeDocument: null,
            geometry: null,
            electricalResults: [],
            areaAllocation: null,
            electricalAreaResult: null,
            currentInputDraft: null,
            dirtySinceSave: false,
            lastSavedRevision: project.Revision,
            isPresentationOutOfSync: false,
            sourceModel: sourceModel);

        OperationResult<ProjectPackageWriteReceipt> initialWrite = packageStore.Write(
            sourcePath, new ProjectSaveSnapshot(savedSnapshot));
        Assert.True(initialWrite.IsSuccess, string.Join(Environment.NewLine,
            initialWrite.Issues.SelectMany(issue => issue.Evidence)));

        using var session = new ProjectSession();
        var workspace = new FakeGeometryWorkspace();
        var coordinator = new CandidateCoordinator(
            session,
            workspace,
            new FakeOperationContext(),
            new FakeApplicationEvents());
        CandidateOutcome open = new OpenProject(session, packageStore, coordinator).Execute(sourcePath);

        Assert.Equal(CandidateStatus.Committed, open.Status);
        Assert.Equal(project.Id, session.Current.CurrentProject!.Id);
        Assert.Equal(project.Revision, session.Current.CurrentProject.Revision);
        Assert.Equal(project.Revision, session.Current.LastSavedRevision);
        Assert.False(session.Current.DirtySinceSave);
        ModelSource firstSessionSource = session.Current.SourceModel!;
        Assert.True(firstSessionSource.IsTemporaryFileBacked);
        Assert.Single(Directory.GetFiles(storageDirectory, "*.step"));
        using (Stream capturedModel = firstSessionSource.OpenRead())
        using (var capturedBytes = new MemoryStream())
        {
            capturedModel.CopyTo(capturedBytes);
            Assert.Equal(modelBytes, capturedBytes.ToArray());
        }
        Assert.Equal(1, workspace.RestoreCount);

        CandidateOutcome reopen = new OpenProject(session, packageStore, coordinator).Execute(sourcePath);
        Assert.Equal(CandidateStatus.Committed, reopen.Status);
        Assert.Equal(project.Revision.Next(), session.Current.CurrentProject!.Revision);
        Assert.Throws<ObjectDisposedException>(() => firstSessionSource.OpenRead());
        Assert.Single(Directory.GetFiles(storageDirectory, "*.step"));

        OperationResult<SaveProjectOutcome> save = new SaveProject(session, packageStore).Execute(savedPath);

        Assert.True(save.IsSuccess, string.Join(Environment.NewLine,
            save.Issues.SelectMany(issue => issue.Evidence)));
        Assert.True(save.GetValueOrThrow().MarkedCurrentRevisionClean);
        Assert.False(session.Current.DirtySinceSave);
        Assert.Equal(project.Revision.Next(), session.Current.LastSavedRevision);
        OperationResult<ProjectPackageReadResult> reopened = packageStore.Read(savedPath);
        Assert.True(reopened.IsSuccess, string.Join(Environment.NewLine,
            reopened.Issues.SelectMany(issue => issue.Evidence)));
        Assert.Equal(project.Id, reopened.GetValueOrThrow().Project.Id);
        Assert.Equal(project.Revision.Next(), reopened.GetValueOrThrow().Project.Revision);
        Assert.Equal(modelHash, reopened.GetValueOrThrow().SourceModel.Identity.SourceSha256);
        ModelSource reopenedSource = reopened.GetValueOrThrow().SourceModel;
        Assert.True(reopenedSource.IsTemporaryFileBacked);
        using (Stream reopenedModel = reopenedSource.OpenRead())
        using (var reopenedBytes = new MemoryStream())
        {
            reopenedModel.CopyTo(reopenedBytes);
            Assert.Equal(modelBytes, reopenedBytes.ToArray());
        }

        Assert.True(session.TryBeginOperation(out ProjectOperationLease? lease));
        session.Dispose();
        Assert.Equal(2, Directory.GetFiles(storageDirectory, "*.step").Length);
        lease!.Dispose();
        Assert.Single(Directory.GetFiles(storageDirectory, "*.step"));
        reopenedSource.Dispose();
        Assert.Empty(Directory.GetFiles(storageDirectory, "*.step"));
    }

    private sealed class FakeGeometryWorkspace : IGeometryWorkspace
    {
        public int RestoreCount { get; private set; }

        public IPreparedGeometry PrepareModel(
            DesignProject candidate,
            ModelSource modelSource,
            CancellationToken cancellationToken = default) => CreatePreparedGeometry(candidate);

        public IPreparedGeometry Prepare(
            DesignProject candidate,
            CancellationToken cancellationToken = default) => CreatePreparedGeometry(candidate);

        public IPreparedGeometry Restore(
            DesignProject project,
            ModelSource modelSource,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            if (modelSource.Identity.SourceSha256 != project.SourceModel.SourceSha256)
                throw new InvalidOperationException("복원 원본이 설계의 모델과 다릅니다.");

            return CreatePreparedGeometry(project);
        }

        private static IPreparedGeometry CreatePreparedGeometry(DesignProject project)
        {
            var runtimeDocument = new RuntimeDocument(
                GeometryDocumentId.New(), new ModelRevision(1), project.SourceModel.SourceSha256);
            var snapshot = new GeometrySnapshot(
                runtimeDocument.DocumentId,
                runtimeDocument.ModelRevision,
                CreateStamp(project),
                []);
            return new FakePreparedGeometry(runtimeDocument, snapshot);
        }

        private static DependencyStamp CreateStamp(DesignProject project) => new(
            project.Revision,
            project.SourceModel.SourceSha256,
            new string('a', 64),
            new string('b', 64),
            "package-integration-v1");
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

        public void Dispose()
        {
        }
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
            GeometryQueryResult<SurfaceElementSet>.Succeeded(new SurfaceElementSet([], new AreaMm2(0)));

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(
            ConnectionPath connectionPath,
            WorkingCoordinateSystem coordinateSystem,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<ConnectionObstacleCheck>.Unresolved([]);
    }

    private sealed class FakeOperationContext : IOperationContext
    {
        public void VerifyGeometryThread()
        {
        }
    }

    private sealed class FakeApplicationEvents : IApplicationEvents
    {
        public void Publish(ApplicationEvent applicationEvent)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"HeatingMold-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
