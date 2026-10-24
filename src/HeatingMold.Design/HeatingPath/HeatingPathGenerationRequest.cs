using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;

namespace HeatingMold.Design.HeatingPath;

/// <summary>기존 경로에 수동 편집이 있을 때 재생성에 적용할 정책입니다.</summary>
public enum ManualEditPolicy
{
    /// <summary>의미와 공유 절점 대응이 안전하게 확인된 편집만 보존합니다.</summary>
    PreserveIfCompatible,

    /// <summary>기존 수동 편집을 버리고 새로 생성한 경로로 교체합니다.</summary>
    ReplaceEditedHeatingPath
}

/// <summary>영역, 도체, 배치 조건과 열린 모델 revision을 묶는 경로 생성 입력입니다.</summary>
public sealed class HeatingPathGenerationRequest
{
    /// <summary>요청이 속한 프로젝트의 안정 ID입니다.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>후보 확정 시 적용할 기준 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>경로를 배치할 선택·조정 영역입니다.</summary>
    public HeatingRegion Region { get; }

    /// <summary>발열 도체의 지름과 전기 사양입니다.</summary>
    public WireSpecification WireSpecification { get; }

    /// <summary>간격, 깊이, 진행 방향과 양 끝 앵커입니다.</summary>
    public PlacementConstraints PlacementConstraints { get; }

    /// <summary>모델에 고정된 런타임 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystem CoordinateSystem { get; }

    /// <summary>요청자가 기준으로 삼은 열린 모델 기하 revision입니다.</summary>
    public ModelRevision ExpectedModelRevision { get; }

    /// <summary>유한 후보 탐색에 사용할 공차와 최대 후보 수입니다.</summary>
    public HeatingPathGenerationSettings Settings { get; }

    /// <summary>기존 경로의 사용자 편집을 처리할 재생성 정책입니다.</summary>
    public ManualEditPolicy ManualEditPolicy { get; }

    /// <summary>모든 생성 입력, 앵커와 모델 세대의 일관성을 확인해 요청을 만듭니다.</summary>
    public HeatingPathGenerationRequest(
        ProjectId projectId,
        DesignRevision baseRevision,
        HeatingRegion region,
        WireSpecification wireSpecification,
        PlacementConstraints placementConstraints,
        WorkingCoordinateSystem coordinateSystem,
        ModelRevision expectedModelRevision,
        ManualEditPolicy manualEditPolicy,
        HeatingPathGenerationSettings? settings = null)
    {
        if (projectId.IsEmpty)
            throw new ArgumentException("경로 생성에는 프로젝트 ID가 필요합니다.", nameof(projectId));

        ProjectId = projectId;
        BaseRevision = baseRevision;
        Region = region ?? throw new ArgumentNullException(nameof(region));
        WireSpecification = wireSpecification ?? throw new ArgumentNullException(nameof(wireSpecification));
        PlacementConstraints = placementConstraints ?? throw new ArgumentNullException(nameof(placementConstraints));
        CoordinateSystem = coordinateSystem ?? throw new ArgumentNullException(nameof(coordinateSystem));
        ExpectedModelRevision = expectedModelRevision;
        if (!Enum.IsDefined(manualEditPolicy))
            throw new ArgumentOutOfRangeException(nameof(manualEditPolicy));
        ManualEditPolicy = manualEditPolicy;
        Settings = settings ?? HeatingPathGenerationSettings.Initial;

        if (CoordinateSystem.Revision != ExpectedModelRevision)
            throw new ArgumentException("작업 좌표계와 요청의 모델 기하 revision이 다릅니다.", nameof(coordinateSystem));
        if (!double.IsFinite(WireSpecification.DiameterMm.Value) || WireSpecification.DiameterMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(wireSpecification), "발열사 지름은 양의 유한값이어야 합니다.");
        if (!double.IsFinite(PlacementConstraints.MinimumClearGapMm.Value) ||
            PlacementConstraints.MinimumClearGapMm.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(placementConstraints), "발열사 최소 간격 g는 양의 유한값이어야 합니다.");
        }
        if (!double.IsFinite(PlacementConstraints.InsertionDepthMm.Value) ||
            PlacementConstraints.InsertionDepthMm.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(placementConstraints), "삽입 깊이 D는 유한한 0 이상이어야 합니다.");
        }
        if (!double.IsFinite(PlacementConstraints.BoundaryMarginMm.Value) ||
            PlacementConstraints.BoundaryMarginMm.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(placementConstraints), "경계 여유는 유한한 0 이상이어야 합니다.");
        }
        if (PlacementConstraints.StartAnchor.WorkingUv.DistanceTo(PlacementConstraints.EndAnchor.WorkingUv) <=
            Settings.AnchorCoincidenceToleranceMm)
        {
            throw new ArgumentException("시작·끝 앵커는 서로 다른 위치여야 합니다.", nameof(placementConstraints));
        }
        if (!region.FaceReferences.Contains(PlacementConstraints.StartAnchor.FaceReferenceId) ||
            !region.FaceReferences.Contains(PlacementConstraints.EndAnchor.FaceReferenceId))
        {
            throw new ArgumentException("양 끝 앵커는 선택 영역의 Face 참조를 사용해야 합니다.", nameof(placementConstraints));
        }
        if (!HaveSameRanges(region.ExitSide, PlacementConstraints.ExitSide))
            throw new ArgumentException("배치 조건의 출구 측면은 선택 영역의 확정 출구 측면과 같아야 합니다.", nameof(placementConstraints));
    }

    private static bool HaveSameRanges(ExitSideReference first, ExitSideReference second) =>
        first.Ranges.OrderBy(range => range.BoundarySegmentId.Value)
            .SequenceEqual(second.Ranges.OrderBy(range => range.BoundarySegmentId.Value));
}

/// <summary>후보 예산과 경로 근사 한도를 명시하는 생성 설정입니다.</summary>
public sealed record HeatingPathGenerationSettings
{
    /// <summary>하나의 요청에서 평가할 후보 배치 상한입니다.</summary>
    public int MaximumCandidateCount { get; }

    /// <summary>회전 곡선의 최대 현 편차(mm)입니다.</summary>
    public double MaximumTurnDeviationMm { get; }

    /// <summary>회전 곡선 표본 사이의 최대 간격(mm)입니다.</summary>
    public double MaximumTurnSampleSpacingMm { get; }

    /// <summary>절점 또는 앵커의 같은 위치 판정 공차(mm)입니다.</summary>
    public double AnchorCoincidenceToleranceMm { get; }

    /// <summary>초기 후보 수와 기하 공차 설정입니다.</summary>
    public static HeatingPathGenerationSettings Initial { get; } = new(256, 0.05, 2.0, 0.01);

    /// <summary>양의 후보 한도와 유한한 공차로 생성 설정을 만듭니다.</summary>
    public HeatingPathGenerationSettings(
        int maximumCandidateCount,
        double maximumTurnDeviationMm,
        double maximumTurnSampleSpacingMm,
        double anchorCoincidenceToleranceMm)
    {
        if (maximumCandidateCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidateCount));
        MaximumCandidateCount = maximumCandidateCount;
        MaximumTurnDeviationMm = RequirePositive(maximumTurnDeviationMm, nameof(maximumTurnDeviationMm));
        MaximumTurnSampleSpacingMm = RequirePositive(maximumTurnSampleSpacingMm, nameof(maximumTurnSampleSpacingMm));
        AnchorCoincidenceToleranceMm = RequirePositive(anchorCoincidenceToleranceMm, nameof(anchorCoincidenceToleranceMm));
    }

    private static double RequirePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "생성 공차는 양의 유한값이어야 합니다.");

        return value;
    }
}
