using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;

namespace HeatingMold.Application.UseCases;

/// <summary>선택한 Face topology를 발열 영역으로 만들고 프로젝트 후보로 확정합니다.</summary>
public sealed class CreateHeatingRegion
{
    private readonly ProjectSession _session;
    private readonly CandidateCoordinator _coordinator;
    private readonly RegionBuilder _regionBuilder;

    /// <summary>세션, 후보 확정기와 영역 생성 서비스를 연결합니다.</summary>
    public CreateHeatingRegion(
        ProjectSession session,
        CandidateCoordinator coordinator,
        RegionBuilder? regionBuilder = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _regionBuilder = regionBuilder ?? new RegionBuilder();
    }

    /// <summary>현재 모델의 경계를 질의하고 유효한 새 영역만 다음 revision으로 확정합니다.</summary>
    public CandidateOutcome Execute(
        RegionId regionId,
        IReadOnlyList<FaceRefId> selectedFaces,
        ExitSideReference selectedExitSide,
        DesignRevision expectedBaseRevision,
        IGeometryQueries geometryQueries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedFaces);
        ArgumentNullException.ThrowIfNull(selectedExitSide);
        ArgumentNullException.ThrowIfNull(geometryQueries);

        SessionSnapshot current = _session.Current;
        CandidateOutcome? invalidRequest = ValidateRequest(
            current,
            regionId,
            selectedFaces,
            selectedExitSide,
            expectedBaseRevision,
            out DesignProject project,
            out FaceRefId[] faceIds);
        if (invalidRequest is not null)
            return invalidRequest;
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(current, regionId);

        GeometryQueryResult<SelectedTopology> topologyResult;
        try
        {
            topologyResult = geometryQueries.GetSelectedTopology(faceIds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(current, regionId);
        }
        catch (Exception exception)
        {
            return Failure(
                current,
                "SelectedTopologyQueryFailed",
                $"선택 Face topology 질의에 실패했습니다 ({exception.GetType().Name}).",
                faceIds.Select(face => face.Value).ToArray());
        }

        if (topologyResult.HasValue &&
            topologyResult.Value!.ModelRevision is { } topologyRevision &&
            current.Geometry?.ModelRevision != topologyRevision)
        {
            return Failure(current, "StaleSelectedTopology", "선택 topology가 현재 Native 모델 revision에서 생성되지 않았습니다.");
        }

        return BuildAndCommit(
            current,
            project,
            regionId,
            faceIds,
            selectedExitSide,
            expectedBaseRevision,
            topologyResult,
            cancellationToken);
    }

    /// <summary>화면에서 선택한 경계와 같은 revision의 topology 결과를 재사용해 영역을 확정합니다.</summary>
    public CandidateOutcome ExecuteFromTopology(
        RegionId regionId,
        IReadOnlyList<FaceRefId> selectedFaces,
        ExitSideReference selectedExitSide,
        DesignRevision expectedBaseRevision,
        ModelRevision expectedModelRevision,
        GeometryQueryResult<SelectedTopology> topologyResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedFaces);
        ArgumentNullException.ThrowIfNull(selectedExitSide);
        ArgumentNullException.ThrowIfNull(topologyResult);

        SessionSnapshot current = _session.Current;
        CandidateOutcome? invalidRequest = ValidateRequest(
            current,
            regionId,
            selectedFaces,
            selectedExitSide,
            expectedBaseRevision,
            out DesignProject project,
            out FaceRefId[] faceIds);
        if (invalidRequest is not null)
            return invalidRequest;
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(current, regionId);

        if (current.Geometry?.ModelRevision != expectedModelRevision ||
            !topologyResult.HasValue ||
            topologyResult.Value!.ModelRevision != expectedModelRevision)
        {
            return Failure(
                current,
                "StaleSelectedTopology",
                "선택 topology와 현재 프로젝트의 Native 모델 revision이 다릅니다.",
                faceIds.Select(face => face.Value).ToArray());
        }

        return BuildAndCommit(
            current,
            project,
            regionId,
            faceIds,
            selectedExitSide,
            expectedBaseRevision,
            topologyResult,
            cancellationToken);
    }

    private CandidateOutcome? ValidateRequest(
        SessionSnapshot current,
        RegionId regionId,
        IReadOnlyList<FaceRefId> selectedFaces,
        ExitSideReference selectedExitSide,
        DesignRevision expectedBaseRevision,
        out DesignProject project,
        out FaceRefId[] faceIds)
    {
        project = null!;
        faceIds = Array.Empty<FaceRefId>();

        if (current.CurrentProject is not { } currentProject)
            return Failure(current, "ProjectNotLoaded", "발열 영역을 만들려면 먼저 프로젝트를 열어야 합니다.");
        if (currentProject.Revision != expectedBaseRevision)
            return Failure(current, "StaleRegionCreation", "영역 생성의 기준 revision이 현재 설계와 다릅니다.");
        if (regionId.IsEmpty)
            return Failure(current, "InvalidRegionId", "발열 영역 ID가 비어 있습니다.");
        if (currentProject.HeatingRegions.Any(region => region.Id == regionId))
            return Failure(current, "HeatingRegionAlreadyExists", "같은 ID를 가진 발열 영역이 이미 있습니다.", regionId.Value);

        faceIds = selectedFaces.ToArray();
        if (faceIds.Length == 0 || faceIds.Any(face => face.IsEmpty) || faceIds.Distinct().Count() != faceIds.Length)
        {
            return Failure(
                current,
                "InvalidSelectedFaces",
                "영역 선택에는 중복 없는 유효한 Face 참조가 하나 이상 필요합니다.");
        }

        HashSet<FaceRefId> knownFaces = currentProject.FaceReferences.Select(face => face.Id).ToHashSet();
        FaceRefId[] missingFaces = faceIds.Where(face => !knownFaces.Contains(face)).ToArray();
        if (missingFaces.Length > 0)
        {
            return Failure(
                current,
                "MissingSelectedFaceReference",
                "선택 Face가 현재 모델의 영속 참조에 없습니다.",
                missingFaces.Select(face => face.Value).ToArray());
        }

        project = currentProject;
        return null;
    }

    private CandidateOutcome BuildAndCommit(
        SessionSnapshot current,
        DesignProject project,
        RegionId regionId,
        IReadOnlyList<FaceRefId> faceIds,
        ExitSideReference selectedExitSide,
        DesignRevision expectedBaseRevision,
        GeometryQueryResult<SelectedTopology> topologyResult,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(current, regionId);
        if (!topologyResult.HasValue)
        {
            return new CandidateOutcome(
                CandidateStatus.ValidationFailed,
                current,
                topologyResult.Issues.Count > 0
                    ? topologyResult.Issues
                    : [new ValidationIssue(
                        "SelectedTopologyUnavailable",
                        ValidationSeverity.Error,
                        "Application.SelectedTopologyUnavailable",
                        faceIds.Select(face => face.Value),
                        "application-region-v1",
                        evidence: ["선택 Face의 경계 topology를 사용할 수 없습니다."]) ]);
        }

        OperationResult<HeatingRegion> buildResult = _regionBuilder.Build(
            project,
            regionId,
            faceIds,
            topologyResult,
            selectedExitSide);
        if (!buildResult.IsSuccess)
            return new CandidateOutcome(CandidateStatus.ValidationFailed, current, buildResult.Issues);

        HeatingRegion region = buildResult.GetValueOrThrow();
        DesignProject candidate = DesignProjectCandidateFactory.CreateNext(
            project,
            heatingRegions: project.HeatingRegions.Append(region));
        CandidateOutcome outcome = _coordinator.Execute(candidate, expectedBaseRevision, cancellationToken);

        return new CandidateOutcome(
            outcome.Status,
            outcome.Snapshot,
            buildResult.Issues.Concat(outcome.Issues));
    }

    private static CandidateOutcome Cancelled(SessionSnapshot snapshot, RegionId regionId) =>
        new(
            CandidateStatus.Cancelled,
            snapshot,
            [new ValidationIssue(
                "OperationCancelled",
                ValidationSeverity.Error,
                "Application.OperationCancelled",
                [regionId.Value],
                "application-region-v1")]);

    private static CandidateOutcome Failure(
        SessionSnapshot snapshot,
        string code,
        string evidence,
        params Guid[] entityIds) =>
        new(
            CandidateStatus.ValidationFailed,
            snapshot,
            [new ValidationIssue(
                code,
                ValidationSeverity.Error,
                $"Application.{code}",
                entityIds,
                "application-region-v1",
                evidence: [evidence])]);
}
