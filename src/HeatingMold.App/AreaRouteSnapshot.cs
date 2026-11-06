using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 제외영역 변경/재생성 실패 시 되돌릴 작업영역 경로 상태.
/// 목록 참조는 캡처 시점의 객체를 보관하며, 제외 목록만 별도 복사본을 유지한다.
/// </summary>
internal sealed record AreaRouteSnapshot(
    List<(double MinX, double MinY, double MaxX, double MaxY)> Exclusions,
    SerpentineResult Pattern,
    List<List<Vec2>> Paths,
    List<List<SerpentineRoutePiece>> ExclusionPieces,
    int[] NativePieceIds,
    double[] RoutePieceLengths,
    List<RoutePieceResult> ProjectedPieces,
    List<bool> TurnEnabled,
    bool PatternDisplayed,
    double[] LeadLengths,
    string LastExitFailure,
    Vec2? StartAnchor,
    Vec2? EndAnchor,
    List<AnchoredCandidate> AnchorCandidates,
    List<ExcludedAnchorCandidate> ExcludedCandidates,
    ExcludedAnchorCandidate ExcludedPlan,
    SerpentineComponentsFailureReason? ExcludedCandidateFailure,
    string ExcludedPlanKey,
    AppliedGenerationInputs AppliedInputs,
    CommittedRouteContext CommittedMapping);
