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

/// <summary>HLR 투영 스모크 결과 (S1 검증 항목 5)</summary>
public ref class HlrResult
{
public:
    property int VisibleEdges;
    property int HiddenEdges;
    property double Milliseconds;     // HLR 소요 시간 (E6 성능 측정)
};

/// <summary>
/// OCCT 최소 브리지 (S1 스파이크, /clr:netcore net8.0).
/// 상태는 프로세스 전역(단일 문서) — 본 개발에서 handle 기반으로 전환 예정.
/// </summary>
public ref class Bridge abstract sealed
{
public:
    // ---- 로드/기하 (S1 항목 2·3·4) ----
    /// <summary>STEP 파일 로드 (성공 여부 반환). 뷰어 초기화 후면 즉시 표시+Fit</summary>
    static bool LoadStep(System::String^ path);

    /// <summary>솔리드·셸·면·평면 수</summary>
    static ShapeInfo^ GetShapeInfo();

    /// <summary>면 타입 이름 (Plane/Cylinder/Cone/Sphere/Torus/BSplineSurface/...)</summary>
    static System::String^ GetFaceType(int faceIndex);

    /// <summary>면의 UV 경계 폴리곤 — 외곽 먼저, 이후 홀 (V3)</summary>
    static array<UvWire^>^ GetFaceUvBoundary(int faceIndex);

    /// <summary>UV → 3D 매핑 (V5)</summary>
    static array<double>^ SampleUvTo3D(int faceIndex, double u, double v);

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

    /// <summary>상대 줌 (factor &gt; 1 확대, &lt; 1 축소) — 내부적으로 Redraw</summary>
    static void ZoomView(double factor);

    /// <summary>표시 셰이프 색상 변경 (RGB 0~1) — SetColor API 스모크</summary>
    static void SetShapeColor(double r, double g, double b);

    // ---- 면 하이라이트/선택 (W5 — 마우스 호버·클릭) ----
    /// <summary>호버: 커서 아래 면을 동적 하이라이트하고 faceId 반환 (변경 시만 Redraw, 없으면 -1)</summary>
    static int HoverFace(int x, int y);

    /// <summary>호버 하이라이트 해제 + Redraw (마우스가 창을 벗어날 때)</summary>
    static void ClearHover();

    /// <summary>클릭: 커서 아래 면을 선택(하이라이트 유지)하고 faceId 반환 (빈 곳이면 선택 해제 후 -1)</summary>
    static int SelectFace(int x, int y);

    // ---- 오버레이 (S3 통합 데모) ----
    /// <summary>UV 폴리라인을 면 위 3D 와이어(닫힘이면 리본 면)로 오버레이 표시</summary>
    static bool BuildWire3D(int faceIndex, array<double>^ uvFlat, bool closed);

    /// <summary>지정 방향에서 바라보는 시점으로 설정 + FitAll (S3 평면 뷰)</summary>
    static void SetViewDirection(double dx, double dy, double dz);

    /// <summary>면 중앙 UV의 단위 법선 [nx,ny,nz] (S3 정면 뷰)</summary>
    static array<double>^ GetFaceNormal(int faceIndex);

    // ---- HLR (S1 항목 5, S3 측면 뷰에서 본격 사용) ----
    /// <summary>현재 셰이프를 축측(1,1,1) 방향으로 HLR 투영 — 가시/은선 엣지 수 + 소요 ms</summary>
    static HlrResult^ HlrProject();
};

}}
