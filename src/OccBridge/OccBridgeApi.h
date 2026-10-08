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

/// <summary>
/// OCCT 최소 브리지 (S1 스파이크, /clr:netcore net8.0).
/// 상태는 프로세스 전역(단일 문서) — 본 개발에서 handle 기반으로 전환 예정.
/// </summary>
public ref class Bridge abstract sealed
{
public:
    /// <summary>STEP 파일 로드 (성공 여부 반환)</summary>
    static bool LoadStep(System::String^ path);

    /// <summary>솔리드·셸·면·평면 수</summary>
    static ShapeInfo^ GetShapeInfo();

    /// <summary>면 타입 이름 (Plane/Cylinder/Cone/Sphere/Torus/BSplineSurface/...)</summary>
    static System::String^ GetFaceType(int faceIndex);

    /// <summary>면의 UV 경계 폴리곤 — 외곽 먼저, 이후 홀 (V3)</summary>
    static array<UvWire^>^ GetFaceUvBoundary(int faceIndex);

    /// <summary>UV → 3D 매핑 (V5)</summary>
    static array<double>^ SampleUvTo3D(int faceIndex, double u, double v);
};

}}
