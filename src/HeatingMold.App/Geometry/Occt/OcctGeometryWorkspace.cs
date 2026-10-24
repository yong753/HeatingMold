#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>후보 Native 문서, Face 대응, 최종 곡선 snapshot과 query 수명을 조립합니다.</summary>
public sealed class OcctGeometryWorkspace : IGeometryWorkspace, IElectricalGeometryWorkspace
{
    private const double SavedFrameToleranceMm = 1e-6;

    private readonly FaceFeatureBridgeAdapter _faceFeatureAdapter = new();
    private readonly FaceReferenceMapBuilder _faceReferenceMapBuilder = new();
    private readonly CurveGeometrySnapshotBuilder _snapshotBuilder = new();
    private ModelSource _activeModelSource;
    private RuntimeDocument _activeRuntimeDocument;
    private RuntimeIdMap _activeRuntimeIds;
    private GeometrySnapshot _activeGeometrySnapshot;

    /// <summary>Native 표시 ID를 영속 경로 요소로 역변환합니다.</summary>
    internal bool TryResolveActiveRuntimeId(int runtimeId, out RuntimeGeometryReference reference)
    {
        if (_activeRuntimeIds is not null)
            return _activeRuntimeIds.TryResolve(runtimeId, out reference);

        reference = default;
        return false;
    }

    /// <summary>확정 Native 런타임 ID를 발열 경로 구간의 안정 ID로 변환합니다.</summary>
    internal bool TryResolveActiveHeatingPathSegmentId(
        int runtimeId,
        out HeatingPathSegmentId segmentId)
    {
        segmentId = default;
        if (!TryResolveActiveRuntimeId(runtimeId, out RuntimeGeometryReference reference) ||
            reference.Kind != RuntimeGeometryKind.HeatingPathSegment ||
            reference.Lifetime != RuntimeGeometryLifetime.Committed ||
            reference.EntityId == Guid.Empty)
        {
            return false;
        }

        segmentId = new HeatingPathSegmentId(reference.EntityId);
        return true;
    }

    /// <summary>발열 구간의 안정 ID가 현재 확정 Native 문서에 대응되면 런타임 ID를 반환합니다.</summary>
    internal bool TryGetActiveRuntimeId(HeatingPathSegmentId segmentId, out int runtimeId)
    {
        runtimeId = default;
        return _activeRuntimeIds is not null &&
            _activeRuntimeIds.TryGetRuntimeId(segmentId, RuntimeGeometryLifetime.Committed, out runtimeId);
    }

    /// <summary>현재 활성화한 모델의 확정 문서 revision에 맞춰 후보 곡선을 준비합니다.</summary>
    public IPreparedGeometry Prepare(
        DesignProject candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_activeModelSource is null || _activeRuntimeDocument is null)
            throw new InvalidOperationException("현재 Native 문서를 이 workspace에서 준비한 적이 없습니다.");
        if (Bridge.GetModelRevision() != _activeRuntimeDocument.ModelRevision.Value)
            throw new InvalidOperationException("workspace가 추적하는 활성 Native 문서 revision이 현재 문서와 다릅니다.");

        return PrepareCandidate(candidate, _activeModelSource, initializeNewModel: false, cancellationToken);
    }

    /// <summary>원본 바이트에서 별도 STEP 후보를 준비하고 Publish 전에는 활성 문서를 유지합니다.</summary>
    public IPreparedGeometry PrepareModel(
        DesignProject candidate,
        ModelSource modelSource,
        CancellationToken cancellationToken = default) =>
        PrepareCandidate(candidate, modelSource, initializeNewModel: true, cancellationToken);

    /// <summary>저장된 원본 바이트로 별도 문서를 열고 Face와 곡선을 다시 대응합니다.</summary>
    public IPreparedGeometry Restore(
        DesignProject project,
        ModelSource modelSource,
        CancellationToken cancellationToken = default) =>
        PrepareCandidate(project, modelSource, initializeNewModel: false, cancellationToken);

    /// <summary>전기 사양 변경은 활성 Native 문서와 기존 곡선 측정값을 그대로 재사용합니다.</summary>
    public IPreparedGeometry PrepareElectricalUpdate(
        DesignProject candidate,
        GeometrySnapshot currentGeometry,
        RuntimeDocument currentRuntimeDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentGeometry);
        ArgumentNullException.ThrowIfNull(currentRuntimeDocument);
        cancellationToken.ThrowIfCancellationRequested();

        if (_activeRuntimeDocument is null || _activeGeometrySnapshot is null)
        {
            throw new InvalidOperationException("재사용할 확정 Native 기하 상태가 없습니다.");
        }
        if (!SameRuntimeDocument(_activeRuntimeDocument, currentRuntimeDocument) ||
            currentGeometry.DocumentId != _activeGeometrySnapshot.DocumentId ||
            currentGeometry.ModelRevision != _activeGeometrySnapshot.ModelRevision ||
            currentGeometry.DependencyStamp != _activeGeometrySnapshot.DependencyStamp)
        {
            throw new InvalidOperationException("전기 재계산 요청의 기하 snapshot이 workspace의 활성 상태와 다릅니다.");
        }
        if (Bridge.GetModelRevision() != currentRuntimeDocument.ModelRevision.Value)
            throw new InvalidOperationException("전기 재계산 중 활성 Native 문서 revision이 바뀌었습니다.");

        GeometrySnapshot reboundSnapshot = _snapshotBuilder.Rebind(candidate, currentGeometry);
        return new ReusedPreparedGeometry(
            reboundSnapshot,
            currentRuntimeDocument,
            OnElectricalUpdatePublished);
    }

    private IPreparedGeometry PrepareCandidate(
        DesignProject project,
        ModelSource modelSource,
        bool initializeNewModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(modelSource);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                project.SourceModel.SourceSha256,
                modelSource.Identity.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("후보 설계와 원본 모델의 SHA-256이 다릅니다.");
        }
        if (initializeNewModel && !IsEmptyInitialModelProject(project))
            throw new InvalidOperationException("새 모델 준비에는 설계 입력이 없는 초기 프로젝트가 필요합니다.");

        PreparedCandidateStepSession candidateSession = null;
        try
        {
            string temporaryStepPath = WriteTemporaryStepFile(modelSource, cancellationToken);
            try
            {
                if (!PreparedCandidateStepSession.TryPrepare(
                        temporaryStepPath,
                        out candidateSession,
                        out string diagnostic))
                {
                    throw new InvalidOperationException(diagnostic);
                }
            }
            finally
            {
                File.Delete(temporaryStepPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_faceFeatureAdapter.TryReadPreparedCandidateFeatures(
                    candidateSession.CandidateId,
                    out IReadOnlyList<NativeFaceFeature> currentFaces,
                    out string faceDiagnostic))
            {
                throw new InvalidOperationException(faceDiagnostic);
            }

            WorkingCoordinateSystemDefinition savedFrame = project.WorkingCoordinateSystem;
            if (!candidateSession.TryCreateWorkingSurfaceSession(
                    savedFrame.Normal,
                    savedFrame.AxisX,
                    out PreparedCandidateWorkingSurfaceSession workingSession,
                    out string coordinateDiagnostic))
            {
                throw new InvalidOperationException(coordinateDiagnostic);
            }

            WorkingCoordinateSystem coordinateSystem = workingSession.CoordinateSystem;
            DesignProject effectiveProject = initializeNewModel
                ? CreateInitialModelProject(project, modelSource.Identity, coordinateSystem.Projection, currentFaces)
                : project;
            if (!initializeNewModel)
                ValidateSavedFrame(savedFrame, coordinateSystem.Projection);

            OperationResult<IReadOnlyDictionary<FaceRefId, int>> faceMapResult =
                _faceReferenceMapBuilder.Build(effectiveProject, modelSource.Identity, currentFaces);
            if (!faceMapResult.IsSuccess)
                throw CreateFaceMappingException(faceMapResult.Issues);

            IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices = faceMapResult.GetValueOrThrow();
            OcctGeometryQueries queries = AppCompositionRoot.CreatePreparedCandidateOcctGeometryQueries(
                workingSession,
                nativeFaceIndices,
                project.Rules.GeometryTolerances);
            PreparedCandidateWorkingPathProjectionAdapter pathAdapter =
                candidateSession.CreateWorkingPathProjectionAdapter(
                    workingSession,
                    project.Rules.GeometryTolerances);

            RuntimeIdMap runtimeIds = new();
            var stagedIds = new List<int>();
            var finalIds = new List<int>();
            var heatingMeasurements = new Dictionary<HeatingPathSegmentId, OcctCurveMeasurement>();
            var connectionMeasurements = new Dictionary<ConnectionPathSegmentId, OcctCurveMeasurement>();
            int stagingOrdinal = 0;

            HeatingPathSegment[] stagedHeatingSegments = effectiveProject.HeatingPaths
                .SelectMany(path => path.Segments)
                .Where(segment => segment.IsActive || segment.Kind == HeatingPathSegmentKind.Turn)
                .OrderBy(segment => segment.Id.Value)
                .ToArray();
            foreach (HeatingPathSegment segment in stagedHeatingSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int stagingId = CreateStagingId(stagingOrdinal++);
                string kind = segment.Kind == HeatingPathSegmentKind.Turn
                    ? "TurnSegment"
                    : "HeatingRunSegment";
                OcctCurveMeasurement measurement = pathAdapter.StageExactCurveSegment(
                    coordinateSystem,
                    stagingId,
                    kind,
                    segment.Curve);

                stagedIds.Add(stagingId);
                finalIds.Add(runtimeIds.GetOrAdd(segment.Id, RuntimeGeometryLifetime.Committed));
                if (segment.IsActive)
                    heatingMeasurements.Add(segment.Id, measurement);
            }

            ConnectionPathSegment[] connectionSegments = effectiveProject.ConnectionPaths
                .SelectMany(path => path.Segments)
                .OrderBy(segment => segment.Id.Value)
                .ToArray();
            foreach (ConnectionPathSegment segment in connectionSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int stagingId = CreateStagingId(stagingOrdinal++);
                OcctCurveMeasurement measurement = pathAdapter.StageExactCurveSegment(
                    coordinateSystem,
                    stagingId,
                    "ConnectionPathSegment",
                    segment.Curve);

                stagedIds.Add(stagingId);
                finalIds.Add(runtimeIds.GetOrAdd(segment.Id, RuntimeGeometryLifetime.Committed));
                connectionMeasurements.Add(segment.Id, measurement);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!pathAdapter.CommitWorkingPathGeometry(
                    coordinateSystem,
                    stagedIds,
                    finalIds,
                    Array.Empty<int>()))
            {
                throw new InvalidOperationException("후보 Native 곡선 묶음을 확정하지 못했습니다.");
            }

            var documentId = GeometryDocumentId.New();
            var modelRevision = new ModelRevision(candidateSession.PublishedModelRevision);
            var runtimeDocument = new RuntimeDocument(
                documentId,
                modelRevision,
                modelSource.Identity.SourceSha256);
            GeometrySnapshot snapshot = _snapshotBuilder.Build(
                effectiveProject,
                documentId,
                modelRevision,
                heatingMeasurements,
                connectionMeasurements);
            (HeatingPathSegmentId SegmentId, bool IsActive)[] turnSegmentStates = effectiveProject.HeatingPaths
                .SelectMany(path => path.Segments)
                .Where(segment => segment.Kind == HeatingPathSegmentKind.Turn)
                .Select(segment => (segment.Id, segment.IsActive))
                .ToArray();

            return new PreparedOcctGeometry(
                candidateSession,
                initializeNewModel ? effectiveProject : null,
                snapshot,
                queries,
                coordinateSystem,
                runtimeDocument,
                modelSource,
                runtimeIds,
                turnSegmentStates,
                OnCandidatePublished);
        }
        catch
        {
            candidateSession?.Dispose();
            throw;
        }
    }

    private void OnCandidatePublished(
        ModelSource modelSource,
        RuntimeDocument runtimeDocument,
        RuntimeIdMap runtimeIds,
        GeometrySnapshot geometrySnapshot)
    {
        _activeModelSource = modelSource;
        _activeRuntimeDocument = runtimeDocument;
        _activeRuntimeIds = runtimeIds;
        _activeGeometrySnapshot = geometrySnapshot;
    }

    private void OnElectricalUpdatePublished(GeometrySnapshot geometrySnapshot) =>
        _activeGeometrySnapshot = geometrySnapshot;

    private static bool SameRuntimeDocument(RuntimeDocument first, RuntimeDocument second) =>
        first.DocumentId == second.DocumentId &&
        first.ModelRevision == second.ModelRevision &&
        string.Equals(first.SourceModelSha256, second.SourceModelSha256, StringComparison.Ordinal);

    /// <summary>새 모델의 실제 Native frame과 Face 특징을 빈 프로젝트에 저장합니다.</summary>
    private static DesignProject CreateInitialModelProject(
        DesignProject project,
        HeatingMold.Core.Model.ModelIdentity modelIdentity,
        ProjectionFrame projection,
        IReadOnlyList<NativeFaceFeature> currentFaces)
    {
        var workingFrame = new WorkingCoordinateSystemDefinition(
            projection.Origin,
            projection.AxisX,
            projection.AxisY,
            projection.Normal);
        FaceReference[] faceReferences = currentFaces
            .Select(feature => new FaceReference(
                FaceRefId.New(),
                modelIdentity.SourceSha256,
                feature.NativeFaceIndex,
                feature.GeometryKind,
                feature.SurfaceArea,
                feature.Center,
                feature.BoundaryFingerprint,
                feature.AdjacentFaceFingerprints))
            .ToArray();

        return new DesignProject(
            project.Id,
            project.SchemaVersion,
            project.Revision,
            project.Name,
            project.CreatedUtc,
            project.ModifiedUtc,
            project.SourceModel,
            workingFrame,
            faceReferences,
            project.HeatingRegions,
            project.WireSpecification,
            project.PlacementConstraints,
            project.HeatingPaths,
            project.Circuits,
            project.ConnectionPoints,
            project.ConnectionPaths,
            project.Rules);
    }

    /// <summary>새 모델 정규화가 기존의 확정 설계를 덮어쓰지 않도록 입력을 확인합니다.</summary>
    private static bool IsEmptyInitialModelProject(DesignProject project) =>
        project.FaceReferences.Count == 0 &&
        project.HeatingRegions.Count == 0 &&
        project.WireSpecification is null &&
        project.PlacementConstraints is null &&
        project.HeatingPaths.Count == 0 &&
        project.Circuits.Count == 0 &&
        project.ConnectionPoints.Count == 0 &&
        project.ConnectionPaths.Count == 0;

    private static string WriteTemporaryStepFile(ModelSource modelSource, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"HeatingMold-{Guid.NewGuid():N}.step");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            modelSource.CopyTo(stream, cancellationToken);
            stream.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            if (File.Exists(path))
                File.Delete(path);
            throw;
        }
    }

    private static InvalidOperationException CreateFaceMappingException(IReadOnlyList<ValidationIssue> issues)
    {
        string[] diagnostics = issues
            .Select(issue =>
            {
                string evidence = issue.Evidence.Count == 0
                    ? string.Empty
                    : $" ({string.Join("; ", issue.Evidence)})";
                return $"{issue.Code}{evidence}";
            })
            .ToArray();
        return new InvalidOperationException(
            $"저장 Face 참조를 현재 Native 면에 모두 대응하지 못했습니다: {string.Join(" | ", diagnostics)}");
    }

    private static void ValidateSavedFrame(WorkingCoordinateSystemDefinition saved, ProjectionFrame current)
    {
        if ((saved.Origin - current.Origin).Length > SavedFrameToleranceMm ||
            (saved.AxisX - current.AxisX).Length > SavedFrameToleranceMm ||
            (saved.AxisY - current.AxisY).Length > SavedFrameToleranceMm ||
            (saved.Normal - current.Normal).Length > SavedFrameToleranceMm)
        {
            throw new InvalidOperationException("복원 후보의 작업 좌표계가 저장된 원점 또는 축과 다릅니다.");
        }
    }

    private static int CreateStagingId(int ordinal)
    {
        if (ordinal < 0 || ordinal > int.MaxValue)
            throw new InvalidOperationException("후보 곡선 staging ID 공간을 모두 사용했습니다.");

        return int.MinValue + ordinal;
    }

    private sealed class PreparedOcctGeometry : IPreparedGeometry
    {
        private readonly PreparedCandidateStepSession _candidateSession;
        private readonly ModelSource _modelSource;
        private readonly RuntimeIdMap _runtimeIds;
        private readonly IReadOnlyList<(HeatingPathSegmentId SegmentId, bool IsActive)> _turnSegmentStates;
        private readonly DesignProject? _effectiveProject;
        private readonly Action<ModelSource, RuntimeDocument, RuntimeIdMap, GeometrySnapshot> _onPublished;
        private bool _isFinalized;

        public GeometrySnapshot Snapshot { get; }
        public DesignProject? EffectiveProject => _effectiveProject;
        public IGeometryQueries Queries { get; }
        public WorkingCoordinateSystem CoordinateSystem { get; }
        public RuntimeDocument RuntimeDocument { get; }

        public PreparedOcctGeometry(
            PreparedCandidateStepSession candidateSession,
            DesignProject? effectiveProject,
            GeometrySnapshot snapshot,
            IGeometryQueries queries,
            WorkingCoordinateSystem coordinateSystem,
            RuntimeDocument runtimeDocument,
            ModelSource modelSource,
            RuntimeIdMap runtimeIds,
            IReadOnlyList<(HeatingPathSegmentId SegmentId, bool IsActive)> turnSegmentStates,
            Action<ModelSource, RuntimeDocument, RuntimeIdMap, GeometrySnapshot> onPublished)
        {
            _candidateSession = candidateSession ?? throw new ArgumentNullException(nameof(candidateSession));
            _effectiveProject = effectiveProject;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Queries = queries ?? throw new ArgumentNullException(nameof(queries));
            CoordinateSystem = coordinateSystem ?? throw new ArgumentNullException(nameof(coordinateSystem));
            RuntimeDocument = runtimeDocument ?? throw new ArgumentNullException(nameof(runtimeDocument));
            _modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
            _runtimeIds = runtimeIds ?? throw new ArgumentNullException(nameof(runtimeIds));
            _turnSegmentStates = turnSegmentStates?.ToArray()
                ?? throw new ArgumentNullException(nameof(turnSegmentStates));
            _onPublished = onPublished ?? throw new ArgumentNullException(nameof(onPublished));
        }

        /// <summary>준비 후보를 한 번만 publish하고 Native 표시 상태를 receipt에 보존합니다.</summary>
        public GeometryCommitReceipt Publish()
        {
            ThrowIfFinalized();
            _isFinalized = true;
            ModelLoadResult result = _candidateSession.Publish();
            if (result.Status == ModelLoadStatus.FailedBeforeCommit)
            {
                return new GeometryCommitReceipt(
                    isCommitted: false,
                    runtimeDocument: null,
                    failureCode: string.IsNullOrWhiteSpace(result.Diagnostic)
                        ? "NativePublishFailed"
                        : result.Diagnostic);
            }

            var committedRuntimeDocument = new RuntimeDocument(
                RuntimeDocument.DocumentId,
                new ModelRevision(result.ModelRevision),
                RuntimeDocument.SourceModelSha256);
            _onPublished(
                _modelSource,
                committedRuntimeDocument,
                _runtimeIds,
                Snapshot);
            bool turnPresentationSynchronized = SynchronizeTurnPresentation();

            return new GeometryCommitReceipt(
                isCommitted: true,
                runtimeDocument: committedRuntimeDocument,
                presentationOutOfSync: result.Status == ModelLoadStatus.CommittedPresentationOutOfSync ||
                    !turnPresentationSynchronized);
        }

        /// <summary>확정한 모든 회전 구간의 Native 표시를 프로젝트 활성 상태에 맞춥니다.</summary>
        private bool SynchronizeTurnPresentation()
        {
            bool synchronized = true;
            foreach ((HeatingPathSegmentId segmentId, bool isActive) in _turnSegmentStates)
            {
                if (!_runtimeIds.TryGetRuntimeId(
                        segmentId,
                        RuntimeGeometryLifetime.Committed,
                        out int runtimeId) ||
                    !Bridge.SetTurnSegmentEnabled(runtimeId, isActive, 0.2, 0.8, 0.3))
                {
                    synchronized = false;
                }
            }

            return synchronized;
        }

        /// <summary>publish하지 않은 후보 문서와 context를 한 번만 폐기합니다.</summary>
        public void Dispose()
        {
            if (_isFinalized)
                return;

            _isFinalized = true;
            _candidateSession.Dispose();
        }

        private void ThrowIfFinalized()
        {
            if (_isFinalized)
                throw new ObjectDisposedException(nameof(PreparedOcctGeometry));
        }
    }

    private sealed class ReusedPreparedGeometry : IPreparedGeometry
    {
        private readonly Action<GeometrySnapshot> _onPublished;
        private bool _isFinalized;

        public GeometrySnapshot Snapshot { get; }
        public IGeometryQueries Queries => GeometryQueriesUnavailable.Instance;
        public WorkingCoordinateSystem? CoordinateSystem => null;
        public RuntimeDocument RuntimeDocument { get; }

        public ReusedPreparedGeometry(
            GeometrySnapshot snapshot,
            RuntimeDocument runtimeDocument,
            Action<GeometrySnapshot> onPublished)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            RuntimeDocument = runtimeDocument ?? throw new ArgumentNullException(nameof(runtimeDocument));
            _onPublished = onPublished ?? throw new ArgumentNullException(nameof(onPublished));
        }

        /// <summary>Native 변경 없이 새 설계 지문을 활성 workspace 상태에 반영합니다.</summary>
        public GeometryCommitReceipt Publish()
        {
            if (_isFinalized)
                throw new ObjectDisposedException(nameof(ReusedPreparedGeometry));

            _isFinalized = true;
            _onPublished(Snapshot);
            return new GeometryCommitReceipt(true, RuntimeDocument);
        }

        /// <summary>Native 소유권 변화가 없으므로 미확정 준비 객체만 종료합니다.</summary>
        public void Dispose() => _isFinalized = true;
    }
}
