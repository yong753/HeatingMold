#pragma once

// 주의: 전역 using namespace System 은 Windows SDK 헤더(servprov.h 등)의
// COM 타입(IServiceProvider)과 충돌(C3699) → 헤더에서는 정규화 이름 사용.
namespace HeatingMold { namespace OccBridge {

/// <summary>셰이프 구성 요약 (S1 검증 항목 2)</summary>
public ref class ShapeInfo
{
public:
    property int Solids;
    property int Shells;
    property int Faces;
    property int Planes;
};

/// <summary>UV 경계 와이어 1개 — 평면 2D 좌표 벌크(double flat: u0,v0,u1,v1,...)</summary>
public ref class UvWire
{
public:
    property bool IsOuter;            // true = 외곽, false = 홀
    property bool Closed;
    property array<double>^ Points;
};

ref class ExitEdgeReference;

/// <summary>원본 CAD 위상에서 추출한 선택 Face 통합 경계의 3D polyline.</summary>
public ref class RegionBoundaryLoop
{
public:
    property array<double>^ Points; // flat: x0,y0,z0,...
    property bool Closed;
    // 루프를 구성하는 원본 CAD Edge 참조(와이어 순서, Points 진행 방향 기준).
    // 표시용 샘플과 독립된 정확 경계 판정의 정본이다.
    property array<ExitEdgeReference^>^ EdgeReferences;
};

/// <summary>
/// 확정 영역 외곽 경계에서 도출한 인출 측면 후보 (연속 경계 Edge의 묶음).
/// faceId는 경계 반대편 원본 모델 Face이며, Points는 그 구간의 3D polyline이다.
/// </summary>
public ref class ExitEdgeReference
{
public:
    property long long Revision;       // 참조를 만든 모델 revision
    property long long EdgeKey;        // native가 보관하는 원본 Edge 영속 키
    property System::String^ EdgeSignature; // 재가져오기 안정 원본 Edge topology signature
    property int FaceId;               // 이 구간의 반대편(인출 측면) 원본 Face
    property double FirstParameter;    // 원본 Edge trim 범위 시작
    property double LastParameter;     // 원본 Edge trim 범위 끝
    property bool Reversed;            // 후보 Points 진행 방향과 Edge 파라미터 방향이 반대인지
    property int StartIndex;           // ExitSideCandidate.Points의 시작 점 인덱스
    property int EndIndex;             // 끝 점 인덱스 (포함)
};

public ref class ExitSideCandidate
{
public:
    property int FaceId;
    property array<double>^ Points; // flat: x0,y0,z0,...
    // Points 구간별(점 N개 → N-1개) 반대편 면 id. 병합된 후보에서 구간마다 면이 다를 수 있다.
    property array<int>^ SegmentFaceIds;
    // 후보 Polyline 구간이 대응하는 원본 CAD Edge 참조(순서대로).
    property array<ExitEdgeReference^>^ EdgeReferences;
};

public enum class ExitEdgeQueryStatus { Hit, Miss, Ambiguous, InvalidInput, StaleContext, GeometryFailure,
    TangentOrOverlap, DegenerateProjection, MissingReference };

/// <summary>2D 작업평면 데이텀을 원본 인출 Edge 위로 평가한 결과.</summary>
public ref class ExitEdgeHit
{
public:
    property ExitEdgeQueryStatus Status;
    property int FaceId;             // 평가점을 지지하는 선택 영역의 원본 Face
    property double Parameter;       // 원본 Edge 파라미터
    property array<double>^ Point;   // xyz
    property array<double>^ Tangent; // Edge 파라미터 증가 방향 단위 xyz
    property array<double>^ DatumPoint; // 평가점을 작업평면에 투영한 uv
    property long long VertexKey;   // 실제 원본 공유 vertex의 revision-local ID; 내부점은 0
    property long long EdgeKey;
    property long long Revision;
    property long long ContextId;
    property long long ContextRevision;
};

public ref class ExitEdgeRayResult
{
public:
    property ExitEdgeQueryStatus Status;
    property array<ExitEdgeHit^>^ Hits;
};

public ref class HlrPolyline
{
public:
    property bool Visible;
    property array<double>^ Points;
};

/// <summary>Projected route piece result [length,start xyz,end xyz].</summary>
public ref class RoutePieceResult
{
public:
    property double Length;
    property array<double>^ Start;
    property array<double>^ End;
};

/// <summary>확정 경로 조각의 순서 보존 3D 점 체인과 점별 원본 지지 Face.</summary>
public ref class CommittedRouteChain
{
public:
    property array<double>^ Points;
    property array<int>^ SupportFaceIds;
};

/// <summary>
/// STEP 내보내기용 확정 회로 하나. Points는 확정된 순서의 3D 점 체인(flat xyz)이며,
/// 네이티브는 이 좌표를 재생성/재매핑/평면 투영 없이 그대로 사용한다.
/// </summary>
public ref class StepExportCircuit
{
public:
    StepExportCircuit(System::String^ name, System::String^ areaName,
        int colorR, int colorG, int colorB, array<double>^ points)
    {
        Name = name;
        AreaName = areaName;
        ColorR = colorR;
        ColorG = colorG;
        ColorB = colorB;
        Points = points;
    }
    property System::String^ Name;
    property System::String^ AreaName;
    property int ColorR;
    property int ColorG;
    property int ColorB;
    property array<double>^ Points;
};

/// <summary>격리된 STEP 복원 후보의 Face/Edge 토폴로지 지문.</summary>
public ref class ProjectRestoreCandidateInfo
{
public:
    property int CandidateId;
    property array<System::String^>^ FaceSignatures;
    property array<System::String^>^ EdgeSignatures;
};

/// <summary>
/// OCCT 최소 브리지 (S1 스파이크, /clr:netcore net8.0).
/// 상태는 프로세스 전역(단일 문서) — 본 개발에서 handle 기반으로 전환 예정.
/// </summary>
public enum class WorkingQueryStatus { Hit, Miss, Tangent, InvalidInput, StaleContext, GeometryFailure };
public ref class WorkingContext
{
public:
    property long long Id;
    property long long Revision;
    property array<double>^ Frame; // origin, X, Y, N (12 doubles)
    property array<double>^ Triangles; // world XYZ triples, 9 doubles per triangle
    property double MeshDeflection;
};
public ref class WorkingHit
{
public:
    property long long ContextId;
    property long long Revision;
    property array<double>^ DatumPoint;
    property WorkingQueryStatus Status;
    property array<double>^ Point;
    property array<double>^ Normal;
    property int FaceId;
    property bool IsSelectedFace;
};
/// <summary>Camera-ray pick of the nearest visible face. Read-only: never mutates selection.</summary>
public ref class FacePickResult
{
public:
    property int Status;          // 0=hit, 1=miss, 2=tangent, 3=invalid/geometry
    property int FaceId;
    property long long Revision;
    property array<double>^ Point;   // xyz
    property array<double>^ Normal;  // outward unit normal
    property array<double>^ Uv;      // surface (u,v) at hit
};

/// <summary>Revision-owned presentation/camera savepoint. Release after restore or commit.</summary>
public ref class WorkingPresentation
{
public:
    property long long Id;
    property long long Revision;
    property int BoundaryCount;
    property array<double>^ BoundaryBounds;
};

public ref class Bridge abstract sealed
{
public:
    static long long GetModelRevision();
    static WorkingContext^ CreateWorkingContext(double nx, double ny, double nz, double xx, double xy, double xz);
    /// <summary>저장된 origin/X/Y/N(12 doubles)을 그대로 사용해 복원용 컨텍스트를 만든다.
    /// bbox 중심으로 원점을 재계산하지 않으며, 축이 정규직교 우수 좌표계가 아니면 null.</summary>
    static WorkingContext^ CreateWorkingContextWithFrame(array<double>^ frame);
    static void ReleaseWorkingContext(long long id);
    static WorkingHit^ QueryWorkingSurface(long long id, long long revision, double u, double v);
    /// <summary>Cast against the complete model; succeeds only when its nearest trimmed hit belongs to faceIds.</summary>
    static WorkingHit^ QueryWorkingSurfaceForFaces(long long id, long long revision, array<int>^ faceIds, double u, double v);
    /// <summary>Cast only against the selected faces (trimmed, nearest IN/ON hit). Deterministic: identical datum → identical 3D point.</summary>
    static WorkingHit^ MapWorkingPointOntoFaces(long long id, long long revision, array<int>^ faceIds, double u, double v);
    /// <summary>Nearest point on the selected faces within tolerance, for mapping fallbacks.</summary>
    static WorkingHit^ SnapWorkingPointToFaces(double x, double y, double z, array<int>^ faceIds, double tolerance);
    static bool AreWorkingFacesAdjacent(long long id, long long revision, int faceA, int faceB);
    static RoutePieceResult^ DisplayWorkingRoutePiece(long long id, long long revision, int pieceId,
        System::String^ kind, array<double>^ xyzFlat);
    /// <summary>확정된 경로 조각의 순서 보존 3D 체인을 요청 순서대로 읽는다.
    /// 재생성/재매핑 없이 staged/누락/비활성/소유 불일치가 하나라도 있으면 null.</summary>
    static array<array<double>^>^ GetCommittedRouteChains(long long id, long long revision,
        array<int>^ pieceIds);
    /// <summary>GetCommittedRouteChains와 같은 검증으로 3D 점 체인과 점별 지지 Face를 함께 읽는다.
    /// 지지 Face 정보가 없는 기존 조각은 빈 배열이며, 하나라도 불일치하면 null.</summary>
    static array<CommittedRouteChain^>^ GetCommittedRouteChainsDetailed(long long id, long long revision,
        array<int>^ pieceIds);
    /// <summary>
    /// 현재 로드된 3D 모델과 확정 발열경로/발열사를 하나의 STEP 파일로 쓴다.
    /// 회로 좌표는 확정 체인을 그대로 쓰고, 모델 좌표계/mm 단위를 유지한다.
    /// 파일은 targetPath에 직접 쓰며, 성공 시 별도 리더로 다시 읽어 파트 수와 경계를 검증한다.
    /// 실패는 ASCII 토큰(no-model, invalid-diameter, ...)으로 돌려주고 managed가 사용자 메시지로 옮긴다.
    /// </summary>
    static bool ExportStepWithRoutes(System::String^ targetPath, System::String^ modelName,
        double wireDiameterMm, array<StepExportCircuit^>^ circuits,
        [System::Runtime::InteropServices::Out] System::String^% failureReason);
    static bool StageWorkingPolyline(long long id, long long revision, int stageId, array<double>^ xyzFlat);
    static RoutePieceResult^ StageProjectedWorkingRoutePiece(long long id, long long revision, int stageId,
        System::String^ kind, array<double>^ xyzFlat, array<int>^ faceIds, double dx, double dy, double dz);
    /// <summary>Stage an already-mapped 3D route piece without re-projection; points are validated against the selected faces.</summary>
    static RoutePieceResult^ StageSurfaceMappedRoutePiece(long long id, long long revision, int stageId,
        System::String^ kind, array<double>^ xyzFlat, array<int>^ faceIds);
    /// <summary>Stage a globally surface-mapped piece; each point is validated against the face it came from,
    /// so points outside the selected faces are accepted when they lie on the model surface.</summary>
    static RoutePieceResult^ StageSurfaceMappedRoutePieceWithFaces(long long id, long long revision, int stageId,
        System::String^ kind, array<double>^ xyzFlat, array<int>^ supportFaceIds);
    /// <summary>마지막 경로 조각 스테이징 실패 원인(성공 시 빈 문자열).</summary>
    static System::String^ GetWorkingStageDiagnostic();
    static RoutePieceResult^ StageProjectedWorkingPolyline(long long id, long long revision, int stageId,
        System::String^ kind, array<double>^ xyzFlat, array<int>^ faceIds, double dx, double dy, double dz);
    /// <summary>Camera-ray nearest visible face hit with face id, point, outward normal and UV.</summary>
    static FacePickResult^ PickFacePoint(int x, int y);
    /// <summary>3D polylines of the CAD edges shared by the exit face and the heating faces (empty when not adjacent).</summary>
    static array<array<double>^>^ GetSharedBoundaryEdges(int exitFaceId, array<int>^ heatingFaceIds, double deflection);
    static bool CommitWorkingRouteGeometry(long long id, long long revision, array<int>^ stagedRouteIds,
        array<int>^ finalRouteIds, array<int>^ stagedPolylineIds);
    /// <summary>Atomic context-owned retain/replace/delete commit. Does not redraw.</summary>
    static bool CommitWorkingRouteGeometryDelta(long long id, long long revision, array<int>^ stagedRouteIds,
        array<int>^ finalRouteIds, array<int>^ stagedPolylineIds, array<int>^ retainedRouteIds,
        array<int>^ deletedRouteIds);
    /// <summary>커밋은 성공했지만 마지막 Redraw가 실패했는지 한 번만 보고한다(읽은 뒤 초기화).</summary>
    static bool ConsumeCommitRedrawFailure();
    static void DiscardWorkingRouteGeometry(long long id, long long revision,
        array<int>^ stagedRouteIds, array<int>^ stagedPolylineIds);
    static bool IsWorkingPointVisible(long long id, long long revision, double x, double y, double z, double tolerance);
    static void ShowWorkingBoundary(array<array<double>^>^ loops);
    static void ShowWorkingBoundary(array<array<double>^>^ loops, array<bool>^ holes);
    static bool ReturnToWorkingFront(long long id, long long revision);
    static WorkingPresentation^ CaptureWorkingPresentation();
    static bool RestoreWorkingPresentation(long long id);
    static void ReleaseWorkingPresentation(long long id);
    // ---- 로드/기하 (S1 항목 2·3·4) ----
    /// <summary>STEP 파일 로드 (성공 여부 반환). 뷰어 초기화 후면 즉시 표시+Fit</summary>
    static bool LoadStep(System::String^ path);

    /// <summary>솔리드·셸·면·평면 수</summary>
    static ShapeInfo^ GetShapeInfo();

    /// <summary>현재 모델 Face별 토폴로지 지문. 같은 STEP 재로드 시 FaceId를
    /// 재매핑하는 영속 참조다. 지문이 중복되면 복원 시 명시적으로 실패한다.</summary>
    static array<System::String^>^ GetFaceTopologySignatures();

    /// <summary>현재 문서를 건드리지 않고 STEP을 격리 후보로 읽는다. 실패 시 null.</summary>
    static ProjectRestoreCandidateInfo^ BeginProjectRestoreCandidate(System::String^ path, int importPolicyVersion);
    /// <summary>후보를 현재 문서로 한 번에 교체한다. 표시/카메라는 새 모델에 맞춘다.</summary>
    static bool CommitProjectRestoreCandidate(int candidateId);
    static void DiscardProjectRestoreCandidate(int candidateId);
    static System::String^ GetProjectRestoreCandidateError();

    /// <summary>면 타입 이름 (Plane/Cylinder/Cone/Sphere/Torus/BSplineSurface/...)</summary>
    static System::String^ GetFaceType(int faceIndex);

    /// <summary>면의 경계가 모두 심/퇴화 에지인 닫힌 곡면인지 여부 (영역 경계를 만들 수 없음)</summary>
    static bool FaceRegionIsClosed(int faceIndex);

    /// <summary>원통/원뿔 면의 축 방향. 그 외 타입은 null</summary>
    static array<double>^ GetFaceAxis(int faceIndex);

    /// <summary>
    /// 임시 0.01mm Sewing 후 공유 내부 Edge를 제거하고 외곽 루프를 3D 적응 샘플링해 반환.
    /// 외곽 순서는 기존 정점 연결만 따라 결정하며 Wire/Edge를 재조립하지 않는다.
    /// </summary>
    static array<RegionBoundaryLoop^>^ GetSelectedRegionBoundary(array<int>^ faceIds, double deflection);
    /// <summary>위 호출과 동일하되 실패한 단계의 진단 문자열을 함께 반환한다.</summary>
    static array<RegionBoundaryLoop^>^ GetSelectedRegionBoundary(array<int>^ faceIds, double deflection,
                                                                 [System::Runtime::InteropServices::Out] System::String^% failureReason);
    /// <summary>
    /// 확정 영역 외곽 경계 Edge의 반대편 인접면을 인출 측면 후보로 반환한다.
    /// 연속 경계 Edge가 같은 면을 공유하면 하나의 후보(run)로 묶인다.
    /// 나체 Edge(인접면 없음)는 후보에서 제외한다. 후보가 없으면 null/빈 배열.
    /// </summary>
    static array<ExitSideCandidate^>^ GetBoundaryExitSides(array<int>^ faceIds, double deflection);
    static array<ExitSideCandidate^>^ GetBoundaryExitSides(array<int>^ faceIds, double deflection,
                                                          double nx, double ny, double nz);
    /// <summary>
    /// 작업평면 2D 데이텀에서 원본 인출 Edge 위의 3D 점을 평가한다.
    /// 범위 밖/모호/무효/오래된 참조는 실패하며 다른 Edge나 표면으로 대체하지 않는다.
    /// </summary>
    static ExitEdgeHit^ EvaluateExitEdge(long long id, long long revision,
                                          long long edgeRevision, long long edgeKey,
                                          double u, double v, double tolerance);
    /// <summary>Nearest point on this reference's original projected curve, restricted to its trim.</summary>
    static ExitEdgeHit^ ProjectExitEdge(long long id, long long revision, ExitEdgeReference^ reference,
        double u, double v, double tolerance);
    /// <summary>Evaluate the saved original parameter directly; no nearest-point search.</summary>
    static ExitEdgeHit^ EvaluateExitEdgeParameter(long long id, long long revision,
        ExitEdgeReference^ reference, double parameter);
    /// <summary>Exact projected-curve intersections strictly forward of the ray origin. Direction need not be unit.</summary>
    static ExitEdgeRayResult^ IntersectExitEdgeRay(long long id, long long revision,
        ExitEdgeReference^ reference, double u, double v, double du, double dv, double tolerance);
    /// <summary>인출 측면 후보를 임시 강조 표시한다 (이전 강조는 지운다).</summary>
    static void ShowExitCandidates(array<array<double>^>^ polylines);
    static void ClearExitCandidates();
    /// <summary>화면 픽셀 거리 기준으로 가장 가까운 인출 측면 후보 인덱스를 반환한다. 없으면 -1.</summary>
    static int PickExitCandidate(array<array<double>^>^ polylines, int x, int y, double pixelTolerance);
    /// <summary>후보 하나만 강조한다 (호버). index &lt; 0 이면 모든 후보를 기본 표시로 되돌린다.</summary>
    static void HighlightExitCandidate(int index);
    /// <summary>인출면 선택 모드에서 후보 면 하나를 호버색으로 강조한다. faceId &lt; 0 이면 해제.</summary>
    static void HighlightExitFace(int faceId);
    /// <summary>Pick the nearest visible point on the selected faces only when it is on their trim boundary.</summary>
    static array<double>^ PickSelectedBoundaryPoint(array<int>^ faceIds, int x, int y);
    /// <summary>Toggle connector presentation; disabled connectors are shown as dashed guides.</summary>
    /// <summary>redraw=false면 호출측이 여러 커넥터를 갱신한 뒤 한 번만 Redraw한다.</summary>
    static bool SetConnectorEnabled(int pieceId, bool enabled, double r, double g, double b, bool redraw);
    /// <summary>Re-color staged or committed route pieces of one working context in a single
    /// validated batch. Any unknown piece, owner mismatch, duplicate id or invalid component
    /// rejects the whole batch without changing a single color. No redraw when redraw=false.</summary>
    static bool SetRoutePieceColors(long long id, long long revision, array<int>^ pieceIds,
        array<double>^ rgb, bool redraw);
    static void ClearRoutePieces();
    /// <summary>Project datum start-to-destination onto heating faces, then continue externally.
    /// Returns [status (1=success; negative=failure), total, surface, external] lengths in mm.</summary>
    static array<double>^ ProjectPowerLead(int leadId, array<double>^ sourceAndDestination,
        array<double>^ actualStart, array<int>^ faceIds, double dx, double dy, double dz, double depth);
    /// <summary>previewOnly uses the same validation but replaces only transient cyan geometry.</summary>
    static array<double>^ ProjectPowerLead(int leadId, array<double>^ sourceAndDestination,
        array<double>^ actualStart, array<int>^ faceIds, double dx, double dy, double dz, double depth,
        bool previewOnly);
    /// <summary>Remove only the transient power destination/lead; committed leads are untouched.</summary>
    static void ClearPowerLeadPreview();
    static void ClearPowerLeads();
    /// <summary>Return connector piece ID under screen point, or -1.</summary>
    static int PickConnector(int x, int y);
    static int PickRoutePiece(int x, int y);
    static void HighlightCircuit(array<int>^ pieceIds);
    /// <summary>발열 경로 인출점/끝점 마커를 다시 그린다. xyzFlat은 [x,y,z,...].</summary>
    static void SetRouteEndpoints(array<double>^ xyzFlat);
    /// <summary>Update endpoint presentation without redraw; false reports presentation failure only.</summary>
    static bool SetRouteEndpointsNoRedraw(array<double>^ xyzFlat);
    /// <summary>Replace the whole on-screen route length label set in one atomic presentation step.
    /// anchors are flat xyz (3 per label), texts and rgb (3 per label) must match that count.
    /// leaders are flat xyz pairs (6 per label, start then end) and hasLeader selects which are drawn.
    /// The labels are presentation only: they never change route geometry, lengths or picking.</summary>
    static bool ReplaceRouteLengthLabels(long long id, long long revision,
        array<double>^ anchors, array<System::String^>^ texts, array<double>^ rgb, double height,
        array<double>^ leaders, array<bool>^ hasLeader);
    /// <summary>Remove only the route length labels.</summary>
    static void ClearRouteLengthLabels();

    // ---- 꼭지점 편집 오버레이 ----
    /// <summary>편집 핸들을 표시한다. 좌표는 표시 인덱스 순서의 flat xyz.</summary>
    static bool SetVertexHandles(long long id, long long revision, array<double>^ xyzFlat);
    static void ClearVertexHandles();
    /// <summary>지정 인덱스 핸들만 활성 색으로 강조한다 (index &lt; 0이면 기본색).</summary>
    static void HighlightVertexHandle(int index);
    /// <summary>화면 픽셀 거리 기준 가장 가까운 핸들 표시 인덱스, 없으면 -1.</summary>
    static int PickVertexHandle(int x, int y, double pixelTolerance);
    /// <summary>드래그 중인 조각 하나의 미리보기. invalid=true면 빨간색.</summary>
    static bool ShowRoutePreview(int pieceIndex, array<double>^ xyzFlat, bool invalid);
    static void ClearRoutePreview();

    // ---- AIS 뷰어 (S1 항목 1·6) — WPF HwndHost 자식 HWND 임베드 ----
    /// <summary>자식 HWND에 OpenGl 뷰어 초기화 (1회). 이미 로드된 셰이프가 있으면 표시+Fit</summary>
    static bool InitViewer(System::IntPtr hwnd, int width, int height);

    /// <summary>창 크기 변경 반영 (MustBeResized + Redraw)</summary>
    static void ResizeView(int width, int height);

    /// <summary>FitAll + Redraw</summary>
    static void FitView();

    /// <summary>회전 시작 (마우스 다운 좌표)</summary>
    static void RotateStart(int x, int y);

    /// <summary>회전 계속 (마우스 이동 좌표) — 내부적으로 Redraw</summary>
    static void RotateMove(int x, int y);

    /// <summary>회전 종료 (현재는 no-op — StartRotation/Rotation 쌍으로 충분)</summary>
    static void RotateEnd();
    static void PanStart(int x, int y);
    static void PanMove(int x, int y);
    static void PanEnd();

    /// <summary>상대 줌 (factor &gt; 1 확대, &lt; 1 축소) — 내부적으로 Redraw</summary>
    static void ZoomView(double factor);
    /// <summary>커서 위치를 기준으로 휠 줌 (deltaY는 화면 픽셀 단위)</summary>
    static void ZoomViewAtPoint(int x, int y, int deltaY);

    /// <summary>표시 셰이프 색상 변경 (RGB 0~1) — SetColor API 스모크</summary>
    static void SetShapeColor(double r, double g, double b);

    // ---- 면 하이라이트/선택 (W5 — 마우스 호버·클릭) ----
    /// <summary>호버: 커서 아래 면을 동적 하이라이트하고 faceId 반환 (변경 시만 Redraw, 없으면 -1)</summary>
    static int HoverFace(int x, int y);
    /// <summary>선택 정보는 보존하고 선택/호버 하이라이트 숨김. 다음 면 선택 시 복원.</summary>
    static void HideFaceHighlights();

    /// <summary>확정된 작업(발열)영역을 붉은색으로 유지. 선택이 바뀌어도 해제 전까지 남는다.</summary>
    static void SetConfirmedFaceHighlights(array<int>^ faceIds);
    /// <summary>확정 작업영역을 영역별 색상으로 유지. rgb는 Face별 RGB(0~1) 3개씩.</summary>
    static void SetConfirmedFaceHighlightColors(array<int>^ faceIds, array<double>^ rgb);
    /// <summary>확정 영역 하이라이트 해제 — 선택된 면은 기존 초록색으로 되돌린다.</summary>
    static void ClearConfirmedFaceHighlights();
    /// <summary>활성 작업영역의 인출면을 영역 톤 색으로 지속 강조한다. faceId &lt; 0이면 해제.</summary>
    static void SetExitFaceHighlight(int faceId, double r, double g, double b);
    static void ClearExitFaceHighlight();
    /// <summary>활성 작업영역의 인출 경계 edge를 원색(무광) 폴리라인으로 지속 강조한다. 이전 강조는 교체된다.</summary>
    static void SetExitEdgeHighlight(array<double>^ points, double r, double g, double b);
    static void ClearExitEdgeHighlight();
    /// <summary>면 선택(초록)만 해제. 확정 영역 하이라이트는 건드리지 않는다.</summary>
    static void ClearFaceSelection();

    /// <summary>진단: face-ID 선택 이미지를 실제로 생성한 횟수(캐시 재사용 검증용)</summary>
    static long long GetFaceIdBuildCount();
    /// <summary>현재 선택된 face id 목록(정렬). 선택 상태를 바꾸지 않는다.</summary>
    static array<int>^ GetSelectedFaceIds();
    /// <summary>테스트/자동화: face ID 목록으로 네이티브 선택을 교체한다.
    /// 실제 AIS 선택에서 다시 수집한 face id를 반환한다. 실패 시 null.</summary>
    static array<int>^ SelectFaceIds(array<int>^ faceIds);
    /// <summary>표시 진단(테스트용): 경로/연결구간/활성구간/끝점 마커/선택 면/월드 오버레이 개수.</summary>
    static System::String^ GetDisplayDiagnostic();

    /// <summary>호버 하이라이트 해제 + Redraw (마우스가 창을 벗어날 때)</summary>
    static void ClearHover();

    /// <summary>클릭: 커서 아래 면을 선택(하이라이트 유지)하고 faceId 반환 (빈 곳이면 -1; 확정 영역이 있으면 선택 유지)</summary>
    static int SelectFace(int x, int y, bool toggle);

    /// <summary>월드 좌표를 화면 픽셀로 변환 (변환 불가 시 null)</summary>
    static array<int>^ WorldToScreen(double x, double y, double z);
    /// <summary>면 ID의 표면 중앙 법선·점 조회 — {nx,ny,nz,px,py,pz} (실패 시 null)</summary>
    static array<double>^ GetFaceNormal(int faceId);

    // ---- 오버레이 (S3 통합 데모) ----
    static void RedrawView();
    static bool TryRedrawView();
    static void ClearOverlays();

    /// <summary>방향과 화면 위쪽 벡터로 카메라를 설정</summary>
    static void SetViewFrame(double dx, double dy, double dz, double ux, double uy, double uz);
    /// <summary>로드된 형상에서 자동 추정한 주축(0=X, 1=Y, 2=Z)</summary>
    static array<double>^ GetAutoAxis(int axis);
    /// <summary>사각형과 일부라도 교차하는 모든 모델 면을 선택한다(화면에 가려진 면 포함).
    /// 실패(선택 판정/적용) 시 null, 성공 시 빈 배열일 수 있다(유효한 빈 선택).</summary>
    static array<int>^ SelectFacesInRectangle(int x0, int y0, int x1, int y1);
    static void SetSelectionRectangle(int x0, int y0, int x1, int y1);
    static void ClearSelectionRectangle();
    /// <summary>사각형 선택 프리뷰 세션 시작(기준 선택 보관). 이미 활성이면 무시.</summary>
    static void BeginFaceRectanglePreview();
    /// <summary>드래그 중 사각형과 교차하는 모델 면을 골라 하이라이트한다(실제 확정은 SelectFacesInRectangle).</summary>
    static void PreviewFacesInRectangle(int x0, int y0, int x1, int y1);
    /// <summary>프리뷰 취소 — 드래그 시작 시점의 선택으로 되돌린다.</summary>
    static void CancelFaceRectanglePreview();
    static array<double>^ ScreenPointOnPlane(int x, int y, double ox, double oy, double oz, double nx, double ny, double nz);
    static bool AddExclusionOverlay(array<double>^ xyzFlat);
    static void SetExclusionPreview(array<double>^ xyzFlat);
    static void ClearExclusionOverlays();

    // ---- 제외영역 차감 발열면 채움 표시 ----
    /// <summary>확정 면의 표시 삼각분할을 frame 2D로 사영해 rects를 뺀 채움 메시를 스테이징한다.
    /// 실패하면 기존 엔트리를 건드리지 않는다(revision/workingId 검증 포함).</summary>
    static bool StageAreaHighlightMesh(int areaId, long long modelRevision, long long workingId,
        array<int>^ faceIds, array<double>^ rects, array<double>^ frame, array<double>^ color);
    /// <summary>분할 영역 부분 표시. planes는 (axis, limit, keepLess) 3개씩이고 rects는 비어도 된다.
    /// 원본 면 삼각분할을 축 정렬 반평면으로 먼저 자른 뒤 제외 사각형을 뺀다.</summary>
    static bool StageAreaHighlightMeshClipped(int areaId, long long modelRevision, long long workingId,
        array<int>^ faceIds, array<double>^ rects, array<double>^ planes, array<double>^ frame,
        array<double>^ color);
    /// <summary>스테이징된 메시를 AIS_Triangulation으로 표시하고 해당 Face의 통짜 색칠을 해제한다.</summary>
    static bool CommitAreaHighlight(int areaId);
    /// <summary>스테이징만 폐기한다. 커밋된 표시는 Commit 성공 전까지 유지된다.</summary>
    static void DiscardAreaHighlight(int areaId);
    /// <summary>영역의 채움 메시 표시를 제거한다(면 색 복원은 호출측 몫).</summary>
    static void ClearAreaHighlight(int areaId);
    static void ClearAllAreaHighlights();
    /// <summary>표시 중인 삼각형 수. 엔트리가 없으면 -1, 전부 제외면 0.</summary>
    static int GetAreaHighlightTriangleCount(int areaId);


    // ---- HLR (S1 항목 5, S3 측면 뷰에서 본격 사용) ----
    /// <summary>현재 셰이프를 축측(1,1,1) 방향으로 HLR 투영 — 가시/은선 엣지 수 + 소요 ms</summary>
    /// <summary>M4 - full-shape HLR projection along n (in-plane X axis) -> 2D polylines</summary>
    static array<HlrPolyline^>^ HlrEdges(double nx, double ny, double nz,
                                         double xx, double xy, double xz, bool hiddenToo);
};

}}
