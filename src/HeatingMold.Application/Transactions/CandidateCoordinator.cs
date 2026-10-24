using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Area;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.Transactions;

/// <summary>기존 확정 상태를 보존하며 기하 후보와 관리형 snapshot을 원자 확정합니다.</summary>
public sealed class CandidateCoordinator
{
    private readonly ProjectSession _session;
    private readonly IGeometryWorkspace _geometryWorkspace;
    private readonly IOperationContext _operationContext;
    private readonly IApplicationEvents _events;
    private readonly ProjectDerivedStateBuilder _derivedStateBuilder;

    /// <summary>세션, 기하 staging, 호출 스레드와 알림 포트를 조합합니다.</summary>
    public CandidateCoordinator(
        ProjectSession session,
        IGeometryWorkspace geometryWorkspace,
        IOperationContext operationContext,
        IApplicationEvents events,
        ProjectDerivedStateBuilder? derivedStateBuilder = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _geometryWorkspace = geometryWorkspace ?? throw new ArgumentNullException(nameof(geometryWorkspace));
        _operationContext = operationContext ?? throw new ArgumentNullException(nameof(operationContext));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _derivedStateBuilder = derivedStateBuilder ?? new ProjectDerivedStateBuilder();
    }

    /// <summary>현재 모델에서 후보를 준비하는 공통 기하 workspace 진입점입니다.</summary>
    public CandidateOutcome Execute(
        DesignProject candidate,
        DesignRevision? expectedBaseRevision,
        Func<IPreparedGeometry, OperationResult<CandidateDerivedState>> deriveResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Execute(
            candidate,
            expectedBaseRevision,
            token => _geometryWorkspace.Prepare(candidate, token),
            deriveResults,
            cancellationToken);
    }

    /// <summary>후보 설계를 준비하고 표준 파생 상태를 계산한 뒤 한 번에 확정합니다.</summary>
    public CandidateOutcome Execute(
        DesignProject candidate,
        DesignRevision? expectedBaseRevision,
        CancellationToken cancellationToken = default) =>
        Execute(
            candidate,
            expectedBaseRevision,
            prepared => _derivedStateBuilder.Build(prepared.EffectiveProject ?? candidate, prepared),
            cancellationToken);

    /// <summary>원본 모델을 별도 staging 문서에 열고 성공 시 관리형 상태를 교체합니다.</summary>
    public CandidateOutcome ExecuteModel(
        DesignProject candidate,
        DesignRevision? expectedBaseRevision,
        ModelSource modelSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelSource);
        try
        {
            return ExecuteCore(
                candidate,
                expectedBaseRevision,
                token => _geometryWorkspace.PrepareModel(candidate, modelSource, token),
                _ => OperationResult<CandidateDerivedState>.Success(new CandidateDerivedState()),
                cancellationToken,
                sourceModelOverride: modelSource,
                restoredSavedRevision: null,
                allowEffectiveProject: true);
        }
        finally
        {
            DisposeSourceUnlessSessionOwnsIt(modelSource);
        }
    }

    /// <summary>패키지 설계를 저장 revision의 clean 기준과 함께 별도 문서에 복원한 뒤 세션을 교체합니다.</summary>
    public CandidateOutcome ExecuteRestore(
        DesignProject candidate,
        DesignRevision? expectedBaseRevision,
        ModelSource modelSource,
        CancellationToken cancellationToken = default,
        AppliedAreaAllocation? cachedAreaAllocation = null,
        DesignRevision? packageSavedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(modelSource);
        try
        {
            return ExecuteCore(
                candidate,
                expectedBaseRevision,
                token => _geometryWorkspace.Restore(candidate, modelSource, token),
                prepared => _derivedStateBuilder.Build(candidate, prepared, cachedAreaAllocation),
                cancellationToken,
                sourceModelOverride: modelSource,
                restoredSavedRevision: packageSavedRevision ?? candidate.Revision,
                allowEffectiveProject: false);
        }
        finally
        {
            DisposeSourceUnlessSessionOwnsIt(modelSource);
        }
    }

    /// <summary>검증된 후보와 모든 파생 결과를 준비한 뒤에만 Native·세션 상태를 바꿉니다.</summary>
    public CandidateOutcome Execute(
        DesignProject candidate,
        DesignRevision? expectedBaseRevision,
        Func<CancellationToken, IPreparedGeometry> prepareGeometry,
        Func<IPreparedGeometry, OperationResult<CandidateDerivedState>> deriveResults,
        CancellationToken cancellationToken = default,
        ModelSource? sourceModelOverride = null) =>
        ExecuteCore(
            candidate,
            expectedBaseRevision,
            prepareGeometry,
            deriveResults,
            cancellationToken,
            sourceModelOverride,
            restoredSavedRevision: null,
            allowEffectiveProject: false);

    private CandidateOutcome ExecuteCore(
        DesignProject candidate,
        DesignRevision? expectedBaseRevision,
        Func<CancellationToken, IPreparedGeometry> prepareGeometry,
        Func<IPreparedGeometry, OperationResult<CandidateDerivedState>> deriveResults,
        CancellationToken cancellationToken,
        ModelSource? sourceModelOverride,
        DesignRevision? restoredSavedRevision,
        bool allowEffectiveProject)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(prepareGeometry);
        ArgumentNullException.ThrowIfNull(deriveResults);
        if (cancellationToken.IsCancellationRequested)
            return Outcome(CandidateStatus.Cancelled, "OperationCancelled", candidate);
        if (!_session.TryBeginOperation(out ProjectOperationLease? lease) || lease is null)
            return Outcome(CandidateStatus.Busy, "OperationAlreadyRunning", candidate);

        using (lease)
        {
            IPreparedGeometry? prepared = null;
            bool nativeCommitted = false;
            DesignProject committedProject = candidate;
            try
            {
                if (lease.BaseRevision != expectedBaseRevision)
                    return Outcome(CandidateStatus.Stale, "StaleBaseRevision", candidate, lease.BaseSnapshot);
                if (!HasAllowedRevisionTransition(lease.BaseSnapshot.CurrentProject, candidate))
                    return Outcome(CandidateStatus.Stale, "CandidateRevisionMismatch", candidate, lease.BaseSnapshot);
                if (lease.BaseSnapshot.CurrentInputDraft is { } currentDraft &&
                    !ReferenceEquals(currentDraft.CandidateProject, candidate))
                {
                    return Outcome(CandidateStatus.ValidationFailed, "PendingInputDraftMustBeAppliedOrCancelled", candidate,
                        lease.BaseSnapshot);
                }

                ValidationReport referenceReport = candidate.ValidateReferences();
                if (!referenceReport.IsValid)
                    return new CandidateOutcome(CandidateStatus.ValidationFailed, lease.BaseSnapshot, referenceReport.Issues);

                cancellationToken.ThrowIfCancellationRequested();
                _operationContext.VerifyGeometryThread();
                prepared = prepareGeometry(cancellationToken)
                    ?? throw new InvalidOperationException("기하 작업 포트가 null 후보를 반환했습니다.");
                if (prepared.EffectiveProject is { } effectiveProject)
                {
                    if (!allowEffectiveProject)
                        throw new InvalidOperationException("이 작업은 기하 어댑터의 후보 변경을 허용하지 않습니다.");

                    committedProject = ValidateEffectiveProject(candidate, effectiveProject);
                }

                ValidationReport preparedReferenceReport = committedProject.ValidateReferences();
                if (!preparedReferenceReport.IsValid)
                {
                    return new CandidateOutcome(
                        CandidateStatus.ValidationFailed,
                        lease.BaseSnapshot,
                        preparedReferenceReport.Issues);
                }

                ValidatePreparedGeometry(committedProject, prepared);

                OperationResult<CandidateDerivedState> derivedResult = deriveResults(prepared);
                if (!derivedResult.IsSuccess)
                    return new CandidateOutcome(CandidateStatus.ValidationFailed, lease.BaseSnapshot, derivedResult.Issues);

                CandidateDerivedState derivedState = derivedResult.GetValueOrThrow();
                ValidateDerivedState(prepared, derivedState);
                cancellationToken.ThrowIfCancellationRequested();
                _operationContext.VerifyGeometryThread();

                var nextSnapshot = new SessionSnapshot(
                    committedProject,
                    prepared.RuntimeDocument,
                    prepared.Snapshot,
                    derivedState.ElectricalResults,
                    derivedState.AreaAllocation,
                    derivedState.ElectricalAreaResult,
                    currentInputDraft: null,
                    dirtySinceSave: !restoredSavedRevision.HasValue,
                    lastSavedRevision: restoredSavedRevision ?? lease.BaseSnapshot.LastSavedRevision,
                    isPresentationOutOfSync: false,
                    sourceModel: sourceModelOverride ?? lease.BaseSnapshot.SourceModel,
                    centerPitchQualityReports: derivedState.CenterPitchQualityReports);

                GeometryCommitReceipt receipt = prepared.Publish()
                    ?? throw new InvalidOperationException("Native Publish가 null receipt를 반환했습니다.");
                if (!receipt.IsCommitted)
                {
                    return Outcome(
                        CandidateStatus.PublishFailed,
                        receipt.FailureCode ?? "NativePublishFailed",
                        candidate,
                        lease.BaseSnapshot);
                }

                nativeCommitted = true;
                if (!SameRuntimeDocument(receipt.RuntimeDocument, prepared.RuntimeDocument))
                {
                    _session.MarkPresentationOutOfSync();
                    TryPublishRepairEvent(candidate.Revision);
                    return Outcome(
                        CandidateStatus.PostCommitFailure,
                        "NativeCommitReceiptMismatch",
                        candidate,
                        _session.Current);
                }

                if (!lease.Commit(nextSnapshot))
                {
                    _session.MarkPresentationOutOfSync();
                    TryPublishRepairEvent(candidate.Revision);
                    return Outcome(
                        CandidateStatus.PostCommitFailure,
                        "SessionCommitAfterNativePublishFailed",
                        candidate,
                        _session.Current);
                }

                if (receipt.PresentationOutOfSync)
                {
                    _session.MarkPresentationOutOfSync();
                    TryPublishRepairEvent(candidate.Revision);
                    return Outcome(
                        CandidateStatus.PostCommitFailure,
                        "NativePresentationOutOfSync",
                        candidate,
                        _session.Current);
                }

                try
                {
                    _events.Publish(new ApplicationEvent(
                        ApplicationEventKind.SessionChanged,
                        candidate.Revision));
                    return new CandidateOutcome(CandidateStatus.Committed, _session.Current, derivedResult.Issues);
                }
                catch
                {
                    _session.MarkPresentationOutOfSync();
                    TryPublishRepairEvent(candidate.Revision);
                    return Outcome(
                        CandidateStatus.PostCommitFailure,
                        "PostCommitFailure",
                        candidate,
                        _session.Current);
                }
            }
            catch (OperationCanceledException)
            {
                return Outcome(
                    CandidateStatus.Cancelled,
                    "OperationCancelled",
                    candidate,
                    nativeCommitted ? _session.Current : lease.BaseSnapshot);
            }
            catch (Exception exception)
            {
                CandidateStatus status = nativeCommitted
                    ? CandidateStatus.PostCommitFailure
                    : CandidateStatus.PrepareFailed;
                if (nativeCommitted)
                {
                    _session.MarkPresentationOutOfSync();
                    TryPublishRepairEvent(candidate.Revision);
                }

                return FailureOutcome(status, exception, candidate, nativeCommitted ? _session.Current : lease.BaseSnapshot);
            }
            finally
            {
                prepared?.Dispose();
            }
        }
    }

    private static bool HasAllowedRevisionTransition(DesignProject? current, DesignProject candidate)
    {
        if (current is null || current.Id != candidate.Id)
            return true;

        return current.Revision.Next() == candidate.Revision;
    }

    /// <summary>세션 확정에 사용되지 않은 모델 후보의 임시 자원을 즉시 해제합니다.</summary>
    private void DisposeSourceUnlessSessionOwnsIt(ModelSource modelSource)
    {
        if (!ReferenceEquals(_session.Current.SourceModel, modelSource))
            modelSource.Dispose();
    }

    private static DesignProject ValidateEffectiveProject(
        DesignProject requestedProject,
        DesignProject effectiveProject)
    {
        bool sameProjectIdentity =
            requestedProject.Id == effectiveProject.Id &&
            requestedProject.SchemaVersion == effectiveProject.SchemaVersion &&
            requestedProject.Revision == effectiveProject.Revision &&
            requestedProject.Name == effectiveProject.Name &&
            requestedProject.CreatedUtc == effectiveProject.CreatedUtc &&
            requestedProject.ModifiedUtc == effectiveProject.ModifiedUtc &&
            requestedProject.SourceModel == effectiveProject.SourceModel &&
            requestedProject.Rules == effectiveProject.Rules;
        bool requestedProjectIsEmpty =
            requestedProject.FaceReferences.Count == 0 &&
            requestedProject.HeatingRegions.Count == 0 &&
            requestedProject.WireSpecification is null &&
            requestedProject.PlacementConstraints is null &&
            requestedProject.HeatingPaths.Count == 0 &&
            requestedProject.Circuits.Count == 0 &&
            requestedProject.ConnectionPoints.Count == 0 &&
            requestedProject.ConnectionPaths.Count == 0;
        bool effectiveProjectContainsOnlyModelData =
            effectiveProject.HeatingRegions.Count == 0 &&
            effectiveProject.WireSpecification is null &&
            effectiveProject.PlacementConstraints is null &&
            effectiveProject.HeatingPaths.Count == 0 &&
            effectiveProject.Circuits.Count == 0 &&
            effectiveProject.ConnectionPoints.Count == 0 &&
            effectiveProject.ConnectionPaths.Count == 0;

        if (!sameProjectIdentity || !requestedProjectIsEmpty || !effectiveProjectContainsOnlyModelData)
        {
            throw new InvalidOperationException(
                "기하 어댑터는 빈 신규 모델의 작업좌표계와 Face 참조만 보정할 수 있습니다.");
        }

        return effectiveProject;
    }

    private static void ValidatePreparedGeometry(DesignProject candidate, IPreparedGeometry prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared.Snapshot);
        ArgumentNullException.ThrowIfNull(prepared.Queries);
        ArgumentNullException.ThrowIfNull(prepared.RuntimeDocument);
        if (prepared.RuntimeDocument.SourceModelSha256 != candidate.SourceModel.SourceSha256)
            throw new InvalidOperationException("후보 기하 문서의 원본 모델 해시가 설계와 다릅니다.");
        if (prepared.Snapshot.DocumentId != prepared.RuntimeDocument.DocumentId ||
            prepared.Snapshot.ModelRevision != prepared.RuntimeDocument.ModelRevision)
        {
            throw new InvalidOperationException("기하 스냅샷과 런타임 문서의 ID 또는 revision이 다릅니다.");
        }
        if (prepared.Snapshot.DependencyStamp.DesignRevision != candidate.Revision ||
            prepared.Snapshot.DependencyStamp.SourceModelSha256 != candidate.SourceModel.SourceSha256)
        {
            throw new InvalidOperationException("기하 스냅샷의 설계 revision 또는 원본 모델 지문이 후보와 다릅니다.");
        }

        Dictionary<HeatingPathSegmentId, CurveDefinition> expectedHeatingCurves = candidate.HeatingPaths
            .SelectMany(path => path.Segments)
            .Where(segment => segment.IsActive)
            .ToDictionary(segment => segment.Id, segment => segment.Curve);
        Dictionary<ConnectionPathSegmentId, CurveDefinition> expectedConnectionCurves = candidate.ConnectionPaths
            .SelectMany(path => path.Segments)
            .ToDictionary(segment => segment.Id, segment => segment.Curve);
        HashSet<HeatingPathSegmentId> actualHeatingCurveIds = prepared.Snapshot.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .Select(curve => curve.HeatingPathSegmentId!.Value)
            .ToHashSet();
        HashSet<ConnectionPathSegmentId> actualConnectionCurveIds = prepared.Snapshot.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .Select(curve => curve.ConnectionPathSegmentId!.Value)
            .ToHashSet();

        if (!expectedHeatingCurves.Keys.ToHashSet().SetEquals(actualHeatingCurveIds) ||
            !expectedConnectionCurves.Keys.ToHashSet().SetEquals(actualConnectionCurveIds))
        {
            throw new InvalidOperationException(
                "후보 기하 스냅샷의 곡선 ID 집합이 활성 발열 경로·연결 경로와 일치하지 않습니다.");
        }

        foreach (CurveGeometrySnapshot curve in prepared.Snapshot.Curves)
        {
            if (!curve.Approximation.IsComplete)
                throw new InvalidOperationException("후보 기하 스냅샷에 완료되지 않은 곡선 표본 근사가 있습니다.");

            if (curve.HeatingPathSegmentId is { } heatingId &&
                !CurveDefinitionComparer.AreEquivalent(expectedHeatingCurves[heatingId], curve.Definition))
            {
                throw new InvalidOperationException(
                    "후보 기하 스냅샷의 발열 곡선 정의가 저장 설계와 일치하지 않습니다.");
            }

            if (curve.ConnectionPathSegmentId is { } connectionId &&
                !CurveDefinitionComparer.AreEquivalent(expectedConnectionCurves[connectionId], curve.Definition))
            {
                throw new InvalidOperationException(
                    "후보 기하 스냅샷의 연결 곡선 정의가 저장 설계와 일치하지 않습니다.");
            }
        }
    }

    private static void ValidateDerivedState(IPreparedGeometry prepared, CandidateDerivedState derivedState)
    {
        if (derivedState.ElectricalResults.Any(result => result.DependencyStamp != prepared.Snapshot.DependencyStamp))
            throw new InvalidOperationException("전기 결과와 기하 결과의 DependencyStamp가 다릅니다.");
        if (derivedState.AreaAllocation is not null &&
            derivedState.AreaAllocation.DependencyStamp != prepared.Snapshot.DependencyStamp)
        {
            throw new InvalidOperationException("면적 분할과 기하 결과의 DependencyStamp가 다릅니다.");
        }
        if (derivedState.ElectricalAreaResult is not null &&
            derivedState.ElectricalAreaResult.Allocation.DependencyStamp != prepared.Snapshot.DependencyStamp)
        {
            throw new InvalidOperationException("전기 면적 결과의 DependencyStamp가 후보 기하와 다릅니다.");
        }
        if (derivedState.CenterPitchQualityReports.Any(report => report.DependencyStamp != prepared.Snapshot.DependencyStamp))
            throw new InvalidOperationException("품질 결과와 후보 기하의 DependencyStamp가 다릅니다.");
    }

    private void TryPublishRepairEvent(DesignRevision revision)
    {
        try
        {
            _events.Publish(new ApplicationEvent(ApplicationEventKind.PresentationRepairNeeded, revision,
                "Application.PresentationOutOfSync"));
        }
        catch
        {
            // 표시 복구 알림 실패는 이미 확정된 설계를 변경하지 않습니다.
        }
    }

    private static bool SameRuntimeDocument(RuntimeDocument? first, RuntimeDocument second) =>
        first is not null && first.DocumentId == second.DocumentId &&
        first.ModelRevision == second.ModelRevision &&
        first.SourceModelSha256 == second.SourceModelSha256;

    private static CandidateOutcome Outcome(
        CandidateStatus status,
        string code,
        DesignProject candidate,
        SessionSnapshot? snapshot = null)
    {
        var issue = new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            [candidate.Id.Value],
            candidate.Rules.ValidationRuleVersion);
        return new CandidateOutcome(status, snapshot, [issue]);
    }

    private static CandidateOutcome FailureOutcome(
        CandidateStatus status,
        Exception exception,
        DesignProject candidate,
        SessionSnapshot snapshot)
    {
        var issue = new ValidationIssue(
            status == CandidateStatus.PostCommitFailure ? "PostCommitFailure" : "CandidatePreparationFailed",
            ValidationSeverity.Error,
            status == CandidateStatus.PostCommitFailure
                ? "Application.PostCommitFailure"
                : "Application.CandidatePreparationFailed",
            [candidate.Id.Value],
            candidate.Rules.ValidationRuleVersion,
            evidence: [exception.GetType().Name, exception.Message]);
        return new CandidateOutcome(status, snapshot, [issue]);
    }
}
