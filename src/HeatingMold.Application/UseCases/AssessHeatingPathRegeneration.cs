using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Editing;
using HeatingMold.Design.HeatingPath;

namespace HeatingMold.Application.UseCases;

/// <summary>경로를 확정하기 전에 새 초안과 기존 수동 편집의 대응 상태를 계산합니다.</summary>
public sealed class AssessHeatingPathRegeneration
{
    private readonly ConstrainedHeatingPathPlanner _planner;
    private readonly RegenerationCompatibility _regenerationCompatibility;

    /// <summary>경로 계획기와 편집 대응 분석기를 연결합니다.</summary>
    public AssessHeatingPathRegeneration(
        ConstrainedHeatingPathPlanner? planner = null,
        RegenerationCompatibility? regenerationCompatibility = null)
    {
        _planner = planner ?? new ConstrainedHeatingPathPlanner();
        _regenerationCompatibility = regenerationCompatibility ?? new RegenerationCompatibility();
    }

    /// <summary>현재 설계와 요청이 일치할 때 정책 선택에 필요한 생성·편집 정보를 반환합니다.</summary>
    public OperationResult<HeatingPathRegenerationAssessment> Execute(
        DesignProject currentProject,
        HeatingPathGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(request);

        if (currentProject.Id != request.ProjectId || currentProject.Revision != request.BaseRevision)
            return Failure("StaleHeatingPathRequest", "경로 생성 요청의 프로젝트 또는 기준 revision이 현재 설계와 다릅니다.");
        if (request.CoordinateSystem.Revision != request.ExpectedModelRevision)
            return Failure("StaleSurfaceMappingContext", "작업 좌표계와 모델 기하 revision이 일치하지 않습니다.");

        HeatingRegion? region = currentProject.HeatingRegions
            .SingleOrDefault(candidate => candidate.Id == request.Region.Id);
        if (region is null || !HaveSameRegionBoundary(region, request.Region))
            return Failure("StaleHeatingRegion", "경로 생성 요청의 영역 경계가 현재 설계와 다릅니다.", request.Region.Id.Value);

        if (cancellationToken.IsCancellationRequested)
            return Cancelled(request);

        OperationResult<HeatingPathDraft> draftResult;
        try
        {
            draftResult = _planner.Generate(request);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(request);
        }

        if (!draftResult.IsSuccess)
            return OperationResult<HeatingPathRegenerationAssessment>.Failure(draftResult.Issues);
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(request);

        HeatingPath? existingPath = currentProject.HeatingPaths
            .SingleOrDefault(path => path.GenerationSource.RegionId == region.Id);
        RegenerationCompatibilityResult compatibility;
        if (existingPath is null)
        {
            compatibility = new RegenerationCompatibilityResult(
                RegenerationCompatibilityStatus.NoOverrides,
                [],
                []);
        }
        else
        {
            OperationResult<RegenerationCompatibilityResult> compatibilityResult =
                _regenerationCompatibility.Analyze(
                    existingPath,
                    draftResult.GetValueOrThrow(),
                    currentProject.Revision);
            if (!compatibilityResult.IsSuccess)
            {
                return OperationResult<HeatingPathRegenerationAssessment>.Failure(
                    compatibilityResult.Issues);
            }

            compatibility = compatibilityResult.GetValueOrThrow();
        }

        var assessment = new HeatingPathRegenerationAssessment(
            currentProject.Id,
            currentProject.Revision,
            draftResult.GetValueOrThrow(),
            existingPath,
            compatibility,
            HeatingPathRegenerationImpactAnalyzer.Calculate(currentProject, request));

        return OperationResult<HeatingPathRegenerationAssessment>.Success(
            assessment,
            compatibility.Issues);
    }

    /// <summary>기존 요청 경계가 현재 확정 영역과 같은지 확인합니다.</summary>
    private static bool HaveSameRegionBoundary(HeatingRegion first, HeatingRegion second) =>
        first.FaceReferences.SequenceEqual(second.FaceReferences) &&
        first.EffectiveBoundaryLoops.Select(loop => loop.Id)
            .SequenceEqual(second.EffectiveBoundaryLoops.Select(loop => loop.Id)) &&
        first.ExitSide.Ranges.SequenceEqual(second.ExitSide.Ranges);

    /// <summary>취소된 계획을 성공 초안 없이 진단으로 반환합니다.</summary>
    private static OperationResult<HeatingPathRegenerationAssessment> Cancelled(
        HeatingPathGenerationRequest request) =>
        Failure("OperationCancelled", "경로 재생성 검토가 취소되었습니다.", request.Region.Id.Value);

    /// <summary>요청 불일치 진단을 생성합니다.</summary>
    private static OperationResult<HeatingPathRegenerationAssessment> Failure(
        string code,
        string evidence,
        params Guid[] entityIds) =>
        OperationResult<HeatingPathRegenerationAssessment>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            entityIds,
            "application-regeneration-assessment-v1",
            evidence: [evidence])]);
}

/// <summary>정책 확인 화면에 필요한 새 경로와 기존 편집 대응 정보를 묶습니다.</summary>
public sealed class HeatingPathRegenerationAssessment
{
    /// <summary>진단 기준 설계의 안정 ID입니다.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>진단 기준 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>선택한 입력으로 계획한 새 경로 조각입니다.</summary>
    public HeatingPathDraft Draft { get; }

    /// <summary>같은 영역에서 기존에 확정된 경로이며 처음 생성하는 경우 null입니다.</summary>
    public HeatingPath? ExistingPath { get; }

    /// <summary>현재 편집의 대응 상태와 의미가 같은 새 조각 ID를 보관합니다.</summary>
    public RegenerationCompatibilityResult Compatibility { get; }

    /// <summary>같은 재생성 후보에서 교체될 경로·회로·연결부 목록입니다.</summary>
    public HeatingPathRegenerationImpact Impact { get; }

    /// <summary>기존 편집을 모두 보존할 수 있는지 나타냅니다.</summary>
    public bool CanPreserveCurrentEdits =>
        Compatibility.Status != RegenerationCompatibilityStatus.ReplaceRequired;

    /// <summary>기존 편집을 보존하려면 새 경로로 교체할지 사용자 선택이 필요한지 나타냅니다.</summary>
    public bool RequiresReplacementChoice =>
        ExistingPath is not null &&
        Compatibility.Status == RegenerationCompatibilityStatus.ReplaceRequired;

    /// <summary>한 경로 재생성으로는 유지할 수 없는 다른 경로도 영향 범위에 있는지 나타냅니다.</summary>
    public bool RequiresAdditionalPathReplacement => Impact.HeatingPathIds.Any(
        pathId => ExistingPath is null || pathId != ExistingPath.Id);

    /// <summary>편집 대응이 가능한 기존 구간 수를 반환합니다.</summary>
    public int MappedSegmentCount => Compatibility.Mappings.Count;

    /// <summary>진단 기준이 서로 일치하는 불변 결과를 만듭니다.</summary>
    internal HeatingPathRegenerationAssessment(
        ProjectId projectId,
        DesignRevision baseRevision,
        HeatingPathDraft draft,
        HeatingPath? existingPath,
        RegenerationCompatibilityResult compatibility,
        HeatingPathRegenerationImpact impact)
    {
        if (projectId.IsEmpty)
            throw new ArgumentException("진단에는 유효한 프로젝트 ID가 필요합니다.", nameof(projectId));
        if (draft is null)
            throw new ArgumentNullException(nameof(draft));
        if (draft.BaseRevision != baseRevision)
            throw new ArgumentException("생성 초안과 진단의 기준 revision이 다릅니다.", nameof(draft));
        if (compatibility is null)
            throw new ArgumentNullException(nameof(compatibility));
        if (impact is null)
            throw new ArgumentNullException(nameof(impact));
        if (existingPath is null && compatibility.Status != RegenerationCompatibilityStatus.NoOverrides)
            throw new ArgumentException("기존 경로 유무와 편집 대응 상태가 일치하지 않습니다.", nameof(compatibility));

        ProjectId = projectId;
        BaseRevision = baseRevision;
        Draft = draft;
        ExistingPath = existingPath;
        Compatibility = compatibility;
        Impact = impact;
    }
}
