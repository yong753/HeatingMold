using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 제외영역 변경/재생성 실패 시 되돌릴 작업영역 경로 상태.
/// 제자리에서 변경되는 목록(Circuits, CircuitPieceIndices)은 캡처 시점 복사본을 보관한다.
/// </summary>
internal sealed record AreaRouteSnapshot(
    List<(double MinX, double MinY, double MaxX, double MaxY)> Exclusions,
    SerpentineResult Pattern,
    List<List<Vec2>> Paths,
    List<List<SerpentineRoutePiece>> ExclusionPieces,
    int[] NativePieceIds,
    double[] RoutePieceLengths,
    List<RoutePieceResult> ProjectedPieces,
    bool PatternDisplayed,
    string LastExitFailure,
    Vec2? StartAnchor,
    Vec2? EndAnchor,
    List<AnchoredCandidate> AnchorCandidates,
    List<ExcludedAnchorCandidate> ExcludedCandidates,
    ExcludedAnchorCandidate ExcludedPlan,
    SerpentineComponentsFailureReason? ExcludedCandidateFailure,
    string ExcludedPlanKey,
    List<CircuitRoute> Circuits,
    List<List<int>> CircuitPieceIndices,
    List<ExitEdgeReference> ExitEdgeRefs,
    AppliedGenerationInputs AppliedInputs,
    CommittedRouteContext CommittedMapping,
    IExitBoundary2D ExitBoundary,
    IExitBoundary2D RegionExitBoundary,
    CadExitBoundary2D RegionCadBoundary,
    ExitBoundaryPoint? SelectedStartExit,
    ExitBoundaryPoint? SelectedEndExit,
    AnchoredCandidate SelectedAnchorCandidate,
    bool RouteEdited);
