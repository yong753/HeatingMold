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

/// <summary>원본 CAD 위상에서 추출한 선택 Face 통합 경계의 3D polyline.</summary>
public ref class RegionBoundaryLoop
{
public:
    property array<double>^ Points; // flat: x0,y0,z0,...
    property bool Closed;
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
    static void ReleaseWorkingContext(long long id);
    static WorkingHit^ QueryWorkingSurface(long long id, long long revision, double u, double v);
    /// <summary>Cast against the complete model; succeeds only when its nearest trimmed hit belongs to faceIds.</summary>
    static WorkingHit^ QueryWorkingSurfaceForFaces(long long id, long long revision, array<int>^ faceIds, double u, double v);
    static bool AreWorkingFacesAdjacent(long long id, long long revision, int faceA, int faceB);
    static RoutePieceResult^ DisplayWorkingRoutePiece(long long id, long long revision, int pieceId,
        System::String^ kind, array<double>^ xyzFlat);
    static bool StageWorkingPolyline(long long id, long long revision, int stageId, array<double>^ xyzFlat);
    static RoutePieceResult^ StageProjectedWorkingRoutePiece(long long id, long long revision, int stageId,
        System::String^ kind, array<double>^ xyzFlat, array<int>^ faceIds, double dx, double dy, double dz);
    static RoutePieceResult^ StageProjectedWorkingPolyline(long long id, long long revision, int stageId,
        array<double>^ xyzFlat, array<int>^ faceIds, double dx, double dy, double dz);
    /// <summary>Camera-ray nearest visible face hit with face id, point, outward normal and UV.</summary>
    static FacePickResult^ PickFacePoint(int x, int y);
    /// <summary>3D polylines of the CAD edges shared by the exit face and the heating faces (empty when not adjacent).</summary>
    static array<array<double>^>^ GetSharedBoundaryEdges(int exitFaceId, array<int>^ heatingFaceIds, double deflection);
    static bool CommitWorkingRouteGeometry(long long id, long long revision, array<int>^ stagedRouteIds,
        array<int>^ finalRouteIds, array<int>^ stagedPolylineIds);
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

    /// <summary>면 타입 이름 (Plane/Cylinder/Cone/Sphere/Torus/BSplineSurface/...)</summary>
    static System::String^ GetFaceType(int faceIndex);

    /// <summary>면의 UV 경계 폴리곤 — 외곽 먼저, 이후 홀 (V3)</summary>
    static array<UvWire^>^ GetFaceUvBoundary(int faceIndex);
    /// <summary>임시 0.01mm Sewing 후 공유 내부 Edge를 제거하고 외곽 루프를 3D 적응 샘플링해 반환.</summary>
    static array<RegionBoundaryLoop^>^ GetSelectedRegionBoundary(array<int>^ faceIds, double deflection);
    /// <summary>UV → 3D 매핑 (V5)</summary>
    static array<double>^ SampleUvTo3D(int faceIndex, double u, double v);
    /// <summary>Pick the nearest visible point on the selected faces only when it is on their trim boundary.</summary>
    static array<double>^ PickSelectedBoundaryPoint(array<int>^ faceIds, int x, int y);
    /// <summary>Project source wire along a fixed axis, then shift result by insertionDepth along that axis; returns [success, length, edgeCount, wireCount, statusCode].</summary>
    static array<double>^ ProjectPathWireOntoSelectedFaces(array<int>^ faceIds, array<double>^ xyzFlat,
                                                           double dx, double dy, double dz, double insertionDepth);
    /// <summary>Project and display one route piece, replacing the cached piece with the same stable ID.</summary>
    static RoutePieceResult^ ProjectRoutePiece(int pieceId, System::String^ kind, array<double>^ xyzFlat,
                                               array<int>^ faceIds, double dx, double dy, double dz, double depth);
    /// <summary>Toggle connector presentation; disabled connectors are shown as dashed guides.</summary>
    static bool SetConnectorEnabled(int pieceId, bool enabled, double r, double g, double b);
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
    /// <summary>면 선택(초록)만 해제. 확정 영역 하이라이트는 건드리지 않는다.</summary>
    static void ClearFaceSelection();

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
    static void ClearOverlays();

    /// <summary>방향과 화면 위쪽 벡터로 카메라를 설정</summary>
    static void SetViewFrame(double dx, double dy, double dz, double ux, double uy, double uz);
    /// <summary>로드된 형상에서 자동 추정한 주축(0=X, 1=Y, 2=Z)</summary>
    static array<double>^ GetAutoAxis(int axis);
    static array<int>^ SelectFacesInRectangle(int x0, int y0, int x1, int y1, bool toggle);
    static void SetSelectionRectangle(int x0, int y0, int x1, int y1);
    static void ClearSelectionRectangle();
    static array<double>^ ScreenPointOnPlane(int x, int y, double ox, double oy, double oz, double nx, double ny, double nz);
    static bool AddExclusionOverlay(array<double>^ xyzFlat);
    static void SetExclusionPreview(array<double>^ xyzFlat);
    static void ClearExclusionOverlays();


    // ---- HLR (S1 항목 5, S3 측면 뷰에서 본격 사용) ----
    /// <summary>현재 셰이프를 축측(1,1,1) 방향으로 HLR 투영 — 가시/은선 엣지 수 + 소요 ms</summary>
    /// <summary>M4 - full-shape HLR projection along n (in-plane X axis) -> 2D polylines</summary>
    static array<HlrPolyline^>^ HlrEdges(double nx, double ny, double nz,
                                         double xx, double xy, double xz, bool hiddenToo);
};

}}
