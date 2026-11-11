using System;
using System.Collections.Generic;
using System.Windows.Media;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 확정된 작업영역 하나. 확정 시점의 Face 집합/투영 프레임과 해당 영역에서
/// 생성한 발열 경로 상태를 함께 보관해 다른 영역과 독립적으로 유지한다.
/// </summary>
internal sealed class WorkingArea
{
    public string Name { get; init; } = "";
    public Color Color { get; init; }
    /// <summary>네이티브 제외영역 채움 메시(AreaHighlight) 키. 0이면 미할당.</summary>
    public int AreaId { get; set; }
    public HashSet<int> FaceIdSet { get; } = new();
    public List<List<Vec2>> Region { get; } = new();
    public ProjectionFrame Frame { get; init; }
    public List<(double MinX, double MinY, double MaxX, double MaxY)> Exclusions { get; set; } = new();

    /// <summary>사행 배치 방향(자동/프레임 X/프레임 Y).</summary>
    public PatternDirection Direction { get; set; } = PatternDirection.Auto;

    /// <summary>영역별 마진 입력 문자열. 마지막 성공 생성 조건과는 별개의 UI 초안이다.</summary>
    public string MarginText { get; set; } = "5";

    /// <summary>영역별 간격 입력 문자열. 마지막 성공 생성 조건과는 별개의 UI 초안이다.</summary>
    public string SpacingText { get; set; } = "50";

    /// <summary>이 영역의 목표 발열전력 입력 문자열(W). 빈 문자열이면 미입력이다.</summary>
    public string TargetPowerText { get; set; } = "";

    /// <summary>
    /// 이 영역의 목표 발열전력(W). 확정된 값이며 다른 영역과 독립이다.
    /// 회로 판정은 이 값과 실제 길이로 계산한 필요 전압/전류로 수행한다.
    /// </summary>
    public double? TargetPowerW { get; set; }

    /// <summary>목표 발열전력 입력이 올바르지 않을 때의 사유. null이면 형식 오류가 없다.</summary>
    public string TargetPowerError { get; set; }

    /// <summary>인출면 CAD Face ID. -1이면 미지정.</summary>
    public int ExitFaceId { get; set; } = -1;
    public long ExitFaceRevision { get; set; }

    /// <summary>인출면과 가열면의 공유 모서리를 작업영역 2D로 투영한 폴리라인.</summary>
    public List<Vec2> ExitChain { get; set; } = new();

    /// <summary>ExitChain과 1:1로 대응하는 실제 CAD 모서리 3D 폴리라인.</summary>
    public List<Vec3> ExitChain3D { get; set; } = new();

    /// <summary>ExitChain의 점 사이 구간별 실제 인출 Face ID (개수 = ExitChain.Count - 1).</summary>
    public List<int> ExitChainFaceIds { get; set; } = new();

    /// <summary>3D 체인이 2D 체인과 점 개수까지 일치할 때만 곡면 인출 리드 보정에 사용한다.</summary>
    public bool HasExitChain3D => ExitChain.Count >= 2 && ExitChain3D.Count == ExitChain.Count;

    /// <summary>인출면 공유 모서리 위의 시작점(=전선 입구)/종료점(=출구).</summary>
    public Vec2? StartAnchor { get; set; }
    public Vec2? EndAnchor { get; set; }

    /// <summary>선택한 인출 경계 후보의 구간별 원본 CAD edge 참조. 모델/후보가 바뀌면 무효.</summary>
    public List<ExitEdgeReference> ExitEdgeRefs { get; set; } = new();

    /// <summary>원본 edge의 작업평면 투영 질의. 표시용 체인과는 독립된 설계 제약이다.</summary>
    public IExitBoundary2D ExitBoundary { get; set; }

    /// <summary>적용영역 전체 외곽 경계의 원본 CAD edge 질의. 인출점 직접 선택과 생성이 쓴다.</summary>
    public IExitBoundary2D RegionExitBoundary { get; set; }

    /// <summary>제외영역과 무관한 적용영역 원본 CAD 외곽. 유효 경계를 다시 만들 때 기준이다.</summary>
    public CadExitBoundary2D RegionCadBoundary { get; set; }

    /// <summary>사용자가 외곽 Edge 위에서 직접 고른 시작/종료 인출점. 원본 edge/parameter binding을 포함한다.</summary>
    public ExitBoundaryPoint? SelectedStartExit { get; set; }
    public ExitBoundaryPoint? SelectedEndExit { get; set; }

    /// <summary>적용영역 전체 경계의 정확 CAD 판정. 표시/생성용 Region 폴리곤과 독립이다.</summary>
    public ExactRegionContainment RegionBoundary { get; set; }

    /// <summary>선택한 후보의 identity와 양끝 원본 parameter를 보존한다.</summary>
    public AnchoredCandidate SelectedAnchorCandidate { get; set; }

    /// <summary>영역의 완결 회로 경로. 생성된 발열경로 전체가 하나의 회로다.</summary>
    public List<CircuitRoute> Circuits { get; set; } = new();

    /// <summary>회로별 조각이 원본 Pattern에서 차지하는 인덱스. 길이/ID 대응에 쓴다.</summary>
    public List<List<int>> CircuitPieceIndices { get; set; } = new();

    /// <summary>현재 방향에서 같은 인출면으로 양 끝을 맞출 수 있는 후보 쌍.</summary>
    public List<AnchoredCandidate> AnchorCandidates { get; set; } = new();

    /// <summary>제외영역까지 반영해 계산한 시작/종료점 후보(경로 계획 포함).</summary>
    public List<ExcludedAnchorCandidate> ExcludedCandidates { get; set; } = new();

    /// <summary>선택한 제외영역 경로 계획. 입력이 바뀌면 무효화된다.</summary>
    public ExcludedAnchorCandidate ExcludedPlan { get; set; }

    /// <summary>제외영역 후보 계산이 실패한 이유(후보가 없을 때).</summary>
    public SerpentineComponentsFailureReason? ExcludedCandidateFailure { get; set; }

    /// <summary>제외영역 경로 계획을 만든 입력(마진/간격/방향/제외/인출면/모델)의 지문.</summary>
    public string ExcludedPlanKey { get; set; } = "";

    /// <summary>인출면 고정 생성 실패 사유(사용자 표시용).</summary>
    public string LastExitFailure { get; set; }

    public SerpentineResult Pattern { get; set; }
    public List<RoutePieceResult> ProjectedPieces { get; set; } = new();
    public int[] NativePieceIds { get; set; } = Array.Empty<int>();
    public double[] RoutePieceLengths { get; set; } = Array.Empty<double>();
    public List<List<Vec2>> Paths { get; set; } = new();
    /// <summary>제외영역이 있는 면의 경로를 조각별 kind와 함께 보관한다(가열 런/우회 스커트 표시용).
    /// 제외가 없거나 편집 모드(RoutePieces)면 null이다.</summary>
    public List<List<SerpentineRoutePiece>> ExclusionPieces { get; set; }
    public bool PatternDisplayed { get; set; }

    /// <summary>생성 후 꼭지점 편집으로 경로가 수정되었는지. 재생성 시 삭제 경고에 사용한다.</summary>
    public bool RouteEdited { get; set; }

    /// <summary>마지막 성공 생성에 사용한 배치 조건. null이면 아직 생성 이력이 없다.</summary>
    public AppliedGenerationInputs AppliedInputs { get; set; }

    /// <summary>마지막으로 성공적으로 표시된 경로의 매핑 조건. 다른 영역 재스테이징 시 사용한다.</summary>
    public CommittedRouteContext CommittedMapping { get; set; }

    /// <summary>
    /// 이 영역을 만든 분할 절단선(작업평면 2D 전체 선분). 비어 있으면 분할로 만들어진 영역이 아니다.
    /// 절단선 자체가 이 영역의 경계 provenance이며 CAD edge나 제외 사각형 변으로 취급하지 않는다.
    /// </summary>
    public List<(Vec2 A, Vec2 B)> SplitCutLines { get; set; } = new();

    /// <summary>같은 분할에서 나온 영역들을 묶는 세션 한정 키. 저장/복원에는 쓰지 않는다.</summary>
    public string SplitFamilyKey { get; set; } = "";

    public bool HasSplitBoundary => SplitCutLines.Count > 0;

    /// <summary>제외영역을 반영한 실제 발열영역(프레임 2D). 제외가 없으면 원본 Region을 그대로 돌려준다.</summary>
    public List<List<Vec2>> EffectiveRegion() =>
        Exclusions.Count == 0 ? Region : RegionOps.SubtractRectangles(Region, Exclusions);

    /// <summary>적용영역 외곽 경계의 원본 CAD 참조와 표시 체인이 준비되었는지.</summary>
    public bool HasExitBoundary => ExitBoundary != null && ExitChain.Count >= 2 && ExitEdgeRefs.Count > 0;

    /// <summary>새 인출점 방식의 생성 준비: 두 점이 모두 원본 edge binding을 가져야 한다.</summary>
    public bool HasSelectedExits => RegionExitBoundary != null && HasExitBoundary
        && (SelectedStartExit?.Binding.HasValue == true || SelectedStartExit?.ArtificialBinding.HasValue == true)
        && (SelectedEndExit?.Binding.HasValue == true || SelectedEndExit?.ArtificialBinding.HasValue == true);

    public bool HasExitAnchors => HasExitFace && StartAnchor.HasValue && EndAnchor.HasValue &&
        SelectedAnchorCandidate?.StartBinding.HasValue == true && SelectedAnchorCandidate?.EndBinding.HasValue == true;
    public bool HasExitFace => ExitFaceId >= 0 && ExitChain.Count >= 2 && ExitEdgeRefs.Count > 0 && ExitBoundary != null;

    public string Label => string.Format("{0} · Face {1}", Name, FaceIdSet.Count);
    public Brush Swatch => new SolidColorBrush(Color);

    public override string ToString() => Label;
}
