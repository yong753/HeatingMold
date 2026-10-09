// S1 브리지 스파이크 — OCCT 7.9.3 네이티브 로직 (C++/CLI /clr:netcore)
// 네이티브 메커니즘은 2.1 사전 스모크(temp)에서 검증 완료 — 본 코드는 브리지 경유 검증용.
// 설계: OCCT 호출은 전부 native 헬퍼(#pragma managed(off))로 격리,
//       ref class 메서드는 marshaling만 담당 (C2711/C3821 회피).
#include "OccBridgeApi.h"

// Windows SDK (WNT_Window 경유) — using namespace System 보다 먼저 포함 (C3699 회피)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <msclr/marshal_cppstd.h>

#include <STEPControl_Reader.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Wire.hxx>
#include <TopoDS_Edge.hxx>
#include <TopExp_Explorer.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <TopAbs_Orientation.hxx>
#include <BRep_Tool.hxx>
#include <BRepTools.hxx>
#include <BRepTools_WireExplorer.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <gp_Pnt.hxx>
#include <gp_Pnt2d.hxx>
#include <gp_Ax2.hxx>
#include <gp_Dir.hxx>

// 뷰어 (S1 항목 1·6) — WNT_Window/Aspect_DisplayConnection=TKService, OpenGl_GraphicDriver=TKOpenGl, V3d_*/AIS_*=TKV3d
#include <Aspect_DisplayConnection.hxx>
#include <OpenGl_GraphicDriver.hxx>
#include <V3d_Viewer.hxx>
#include <V3d_View.hxx>
#include <AIS_InteractiveContext.hxx>
#include <AIS_Shape.hxx>
#include <WNT_Window.hxx>
#include <Quantity_Color.hxx>
#include <Graphic3d_Camera.hxx>
#include <OpenGl_Context.hxx>
#include <Message.hxx>
#include <Message_PrinterOStream.hxx>

// HLR (S1 항목 5) — TKHLR
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
#include <HLRAlgo_Projector.hxx>

#include <chrono>
#include <cmath>
#include <cstdlib>
#include <fstream>
#include <string>
#include <vector>

using namespace System;
using namespace System::Collections::Generic;

#pragma managed(push, off)
namespace
{
    TopoDS_Shape g_shape;
    std::vector<TopoDS_Face> g_faces;

    // ---- AIS 뷰어 상태 (S1 항목 1·6) — 프로세스 전역, UI 스레드 전용 ----
    Handle(Aspect_DisplayConnection) g_disp;
    Handle(OpenGl_GraphicDriver) g_driver;
    Handle(V3d_Viewer) g_viewer;
    Handle(AIS_InteractiveContext) g_ctx;
    Handle(V3d_View) g_view;
    Handle(WNT_Window) g_window;
    Handle(AIS_Shape) g_aisShape;
    Handle(AIS_Shape) g_overlay;   // S3 사행 오버레이 (별도 색상)

    void RebuildFaceCache()
    {
        g_faces.clear();
        if (g_shape.IsNull()) return;
        for (TopExp_Explorer ex(g_shape, TopAbs_FACE); ex.More(); ex.Next())
            g_faces.push_back(TopoDS::Face(ex.Current()));
    }

    void ClearDisplayedShape()
    {
        if (!g_ctx.IsNull() && !g_aisShape.IsNull())
        {
            g_ctx->Remove(g_aisShape, Standard_False);
            g_aisShape.Nullify();
        }
    }

    bool LoadStepFile(const char* path)
    {
        STEPControl_Reader reader;
        if (reader.ReadFile(path) != IFSelect_RetDone) return false;
        reader.TransferRoots();
        g_shape = reader.OneShape();
        if (g_shape.IsNull()) return false;
        RebuildFaceCache();

        // 뷰어가 이미 초기화된 경우 즉시 표시 (S1 항목 1)
        if (!g_ctx.IsNull())
        {
            ClearDisplayedShape();
            g_aisShape = new AIS_Shape(g_shape);
            g_ctx->Display(g_aisShape, Standard_False);
            g_view->FitAll(0.01, Standard_False);
            g_view->Redraw();
        }
        return true;
    }

    const char* SurfaceTypeName(GeomAbs_SurfaceType t)
    {
        switch (t)
        {
        case GeomAbs_Plane: return "Plane";
        case GeomAbs_Cylinder: return "Cylinder";
        case GeomAbs_Cone: return "Cone";
        case GeomAbs_Sphere: return "Sphere";
        case GeomAbs_Torus: return "Torus";
        case GeomAbs_BezierSurface: return "BezierSurface";
        case GeomAbs_BSplineSurface: return "BSplineSurface";
        case GeomAbs_SurfaceOfRevolution: return "SurfaceOfRevolution";
        case GeomAbs_SurfaceOfExtrusion: return "SurfaceOfExtrusion";
        case GeomAbs_OffsetSurface: return "OffsetSurface";
        default: return "Other";
        }
    }

    struct ShapeCounts
    {
        int solids, shells, faces, planes;
    };

    ShapeCounts ComputeCounts()
    {
        ShapeCounts c = { 0, 0, 0, 0 };
        if (g_shape.IsNull()) return c;
        for (TopExp_Explorer ex(g_shape, TopAbs_SOLID); ex.More(); ex.Next()) ++c.solids;
        for (TopExp_Explorer ex(g_shape, TopAbs_SHELL); ex.More(); ex.Next()) ++c.shells;
        c.faces = (int)g_faces.size();
        for (size_t i = 0; i < g_faces.size(); ++i)
            if (BRepAdaptor_Surface(g_faces[i]).GetType() == GeomAbs_Plane) ++c.planes;
        return c;
    }

    struct NativeWire
    {
        bool isOuter;
        bool closed;
        std::vector<double> pts;   // flat: u0,v0,u1,v1,...
    };

    bool ExtractUvWires(int faceIndex, std::vector<NativeWire>& out)
    {
        if (faceIndex < 0 || faceIndex >= (int)g_faces.size()) return false;
        const TopoDS_Face& face = g_faces[faceIndex];
        const TopoDS_Wire outer = BRepTools::OuterWire(face);

        // pass 0: 외곽 와이어, pass 1: 홀 와이어
        for (int pass = 0; pass < 2; ++pass)
        {
            for (TopExp_Explorer ex(face, TopAbs_WIRE); ex.More(); ex.Next())
            {
                const TopoDS_Wire wire = TopoDS::Wire(ex.Current());
                const bool isOuter = wire.IsSame(outer);
                if ((pass == 0) != isOuter) continue;

                NativeWire w;
                w.isOuter = isOuter;
                w.closed = BRep_Tool::IsClosed(wire);

                for (BRepTools_WireExplorer we(wire, face); we.More(); we.Next())
                {
                    const TopoDS_Edge edge = we.Current();
                    Standard_Real f = 0, l = 0;
                    Handle(Geom2d_Curve) c2d = BRep_Tool::CurveOnSurface(edge, face, f, l);
                    if (c2d.IsNull()) continue;

                    // MVP 샘플링: 직선 2점, 곡선 20점 (품질 상향은 본 개발)
                    Geom2dAdaptor_Curve adapt(c2d, f, l);
                    const int n = (adapt.GetType() == GeomAbs_Line) ? 2 : 20;
                    const bool reversed = (edge.Orientation() == TopAbs_REVERSED);

                    for (int k = 0; k < n; ++k)
                    {
                        const double t = f + (l - f) * k / (n - 1);
                        const gp_Pnt2d p = c2d->Value(reversed ? (l - (t - f)) : t);
                        const double u = p.X(), v = p.Y();

                        // 연속 중복점 제거 (엣지 접합부)
                        const size_t cnt = w.pts.size();
                        if (cnt >= 2 && std::fabs(w.pts[cnt - 2] - u) < 1e-9 && std::fabs(w.pts[cnt - 1] - v) < 1e-9)
                            continue;
                        w.pts.push_back(u);
                        w.pts.push_back(v);
                    }
                }
                out.push_back(w);
            }
        }
        return true;
    }

    bool SamplePoint(int faceIndex, double u, double v, double* out)
    {
        if (faceIndex < 0 || faceIndex >= (int)g_faces.size()) return false;
        BRepAdaptor_Surface surf(g_faces[faceIndex]);
        const gp_Pnt p = surf.Value(u, v);
        out[0] = p.X(); out[1] = p.Y(); out[2] = p.Z();
        return true;
    }

    const char* FaceTypeName(int faceIndex)
    {
        if (faceIndex < 0 || faceIndex >= (int)g_faces.size()) return nullptr;
        return SurfaceTypeName(BRepAdaptor_Surface(g_faces[faceIndex]).GetType());
    }

    // ================= 뷰어 natives (S1 항목 1·6) =================

    bool InitViewerNative(void* hwnd)
    {
        if (!g_view.IsNull()) return true;
        if (hwnd == nullptr) return false;

        // 진단 로그: HEATINGMOLD_OCC_LOG 환경변수가 있으면 OCCT 메시지 + GL 컨텍스트 정보를 파일로 수집
        // (.NET의 프로세스 환경변수 변경은 CRT getenv에 반영되지 않음 → Win32 API로 읽음)
        char logPathBuf[MAX_PATH] = { 0 };
        const DWORD logLen = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", logPathBuf, MAX_PATH);
        const char* logPath = (logLen > 0 && logLen < MAX_PATH) ? logPathBuf : nullptr;
        const bool hasLog = (logPath != nullptr && *logPath != '\0');
        std::ofstream diag;
        if (hasLog)
        {
            diag.open(logPath, std::ios::app);
            Handle(Message_PrinterOStream) printer =
                new Message_PrinterOStream(logPath, Standard_False, Message_Info);
            Message::DefaultMessenger()->AddPrinter(printer);
        }

        try
        {
            g_disp = new Aspect_DisplayConnection();
            g_driver = new OpenGl_GraphicDriver(g_disp);
            g_viewer = new V3d_Viewer(g_driver);
            g_viewer->SetDefaultLights();
            g_viewer->SetLightOn();

            g_ctx = new AIS_InteractiveContext(g_viewer);
            g_window = new WNT_Window((Aspect_Handle)hwnd);
            g_view = new V3d_View(g_viewer);
            g_view->SetWindow(g_window);
            g_view->SetImmediateUpdate(Standard_False);
            g_view->TriedronDisplay(Aspect_TOTP_LEFT_LOWER, Quantity_NOC_GOLD, 0.08, V3d_ZBUFFER);
            if (!g_window->IsMapped()) g_window->Map();

            if (diag.is_open())
            {
                const bool ctxOk = g_driver->InitContext();
                diag << "[bridge] InitContext=" << (ctxOk ? "true" : "false") << "\n";
                const Handle(OpenGl_Context)& glCtx = g_driver->GetSharedContext();
                if (!glCtx.IsNull())
                {
                    diag << "[bridge] ctxValid=" << (glCtx->IsValid() ? "true" : "false")
                          << " gl>=2.1=" << (glCtx->IsGlGreaterEqual(2, 1) ? "true" : "false")
                          << " vendor=" << glCtx->Vendor().ToCString() << "\n";
                }
                else
                {
                    diag << "[bridge] sharedContext=null\n";
                }
                diag.flush();
            }

            if (!g_shape.IsNull())
            {
                g_aisShape = new AIS_Shape(g_shape);
                g_ctx->Display(g_aisShape, Standard_False);
            }
            g_view->MustBeResized();
            g_view->FitAll(0.01, Standard_False);
            g_view->Redraw();
            return true;
        }
        catch (const Standard_Failure& f)
        {
            if (diag.is_open())
            {
                diag << "[bridge] Standard_Failure: " << f.GetMessageString() << "\n";
                diag.flush();
            }
            return false;
        }
    }

    void ResizeViewNative()
    {
        if (g_view.IsNull()) return;
        g_view->MustBeResized();
        g_view->Redraw();
    }

    void FitViewNative()
    {
        if (g_view.IsNull()) return;
        g_view->FitAll(0.01, Standard_False);
        g_view->Redraw();
    }

    void RotateStartNative(int x, int y)
    {
        if (!g_view.IsNull()) g_view->StartRotation(x, y);
    }

    void RotateMoveNative(int x, int y)
    {
        if (g_view.IsNull()) return;
        g_view->Rotation(x, y);
        g_view->Redraw();
    }

    void ZoomViewNative(double factor)
    {
        if (g_view.IsNull()) return;
        g_view->SetZoom(factor, Standard_False);   // 현재 배율 기준 상대 줌
        g_view->Redraw();
    }

    void SetShapeColorNative(double r, double g, double b)
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        g_ctx->SetColor(g_aisShape, Quantity_Color(r, g, b, Quantity_TOC_RGB), Standard_False);
        g_view->Redraw();
    }

    void GetViewStateNative(double* out)
    {
        out[0] = out[1] = out[2] = out[3] = 0.0;
        if (g_view.IsNull()) return;
        const Handle(Graphic3d_Camera)& cam = g_view->Camera();
        if (cam.IsNull()) return;
        const gp_Dir dir = cam->Direction();
        out[0] = dir.X(); out[1] = dir.Y(); out[2] = dir.Z();
        out[3] = cam->Scale();
    }

    // ================= 오버레이 natives (S3) =================

    bool BuildWire3DNative(int faceIndex, const std::vector<double>& uv, bool closed)
    {
        if (faceIndex < 0 || faceIndex >= (int)g_faces.size()) return false;
        if (uv.size() < 4) return false;

        BRepAdaptor_Surface surf(g_faces[faceIndex]);
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i = 0; i + 1 < uv.size(); i += 2)
            poly.Add(surf.Value(uv[i], uv[i + 1]));
        if (!poly.IsDone()) return false;
        if (closed) poly.Close();
        if (!poly.IsDone()) return false;

        TopoDS_Shape displayShape = poly.Wire();
        if (closed)
        {
            // 평면이면 리본 면으로 채움 (곡면은 와이어 유지)
            BRepBuilderAPI_MakeFace mkFace(poly.Wire());
            if (mkFace.IsDone()) displayShape = mkFace.Face();
        }

        if (g_ctx.IsNull()) return true;   // 헤드리스(테스트) — 기하 생성만
        if (!g_overlay.IsNull())
        {
            g_ctx->Remove(g_overlay, Standard_False);
            g_overlay.Nullify();
        }
        g_overlay = new AIS_Shape(displayShape);
        g_ctx->SetColor(g_overlay, Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB), Standard_False);
        g_ctx->Display(g_overlay, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void ClearOverlayNative()
    {
        if (!g_ctx.IsNull() && !g_overlay.IsNull())
        {
            g_ctx->Remove(g_overlay, Standard_False);
            g_overlay.Nullify();
        }
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void SetViewDirectionNative(double x, double y, double z)
    {
        if (g_view.IsNull()) return;
        const double len = std::sqrt(x * x + y * y + z * z);
        if (len < 1e-12) return;
        g_view->SetProj(x / len, y / len, z / len);
        g_view->FitAll(0.01, Standard_False);
        g_view->Redraw();
    }

    bool FaceNormalNative(int faceIndex, double* out)
    {
        if (faceIndex < 0 || faceIndex >= (int)g_faces.size()) return false;
        BRepAdaptor_Surface surf(g_faces[faceIndex]);
        const Standard_Real u0 = surf.FirstUParameter(), u1 = surf.LastUParameter();
        const Standard_Real v0 = surf.FirstVParameter(), v1 = surf.LastVParameter();
        gp_Pnt p;
        gp_Vec d1u, d1v;
        surf.D1(0.5 * (u0 + u1), 0.5 * (v0 + v1), p, d1u, d1v);
        gp_Vec n = d1u.Crossed(d1v);
        if (n.SquareMagnitude() < 1e-18) return false;
        n.Normalize();
        if (g_faces[faceIndex].Orientation() == TopAbs_REVERSED) n.Reverse();
        out[0] = n.X(); out[1] = n.Y(); out[2] = n.Z();
        return true;
    }

    // ================= HLR natives (S1 항목 5) =================

    struct HlrCounts { int vis; int hid; double ms; };

    int CountEdges(const TopoDS_Shape& s)
    {
        int n = 0;
        for (TopExp_Explorer ex(s, TopAbs_EDGE); ex.More(); ex.Next()) ++n;
        return n;
    }

    HlrCounts RunHlrNative()
    {
        HlrCounts r = { 0, 0, 0.0 };
        if (g_shape.IsNull()) return r;

        const auto t0 = std::chrono::steady_clock::now();
        Handle(HLRBRep_Algo) algo = new HLRBRep_Algo();
        algo->Add(g_shape);
        algo->Projector(HLRAlgo_Projector(gp_Ax2(gp_Pnt(0.0, 0.0, 0.0), gp_Dir(1.0, 1.0, 1.0))));
        algo->Update();
        algo->Hide();

        HLRBRep_HLRToShape hlr(algo);
        r.vis = CountEdges(hlr.VCompound()) + CountEdges(hlr.Rg1LineVCompound()) + CountEdges(hlr.OutLineVCompound());
        r.hid = CountEdges(hlr.HCompound()) + CountEdges(hlr.Rg1LineHCompound()) + CountEdges(hlr.OutLineHCompound());
        const auto t1 = std::chrono::steady_clock::now();
        r.ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        return r;
    }
}
#pragma managed(pop)

namespace HeatingMold { namespace OccBridge {

bool Bridge::LoadStep(String^ path)
{
    ClearDisplayedShape();
    ClearOverlayNative();
    g_shape.Nullify();
    g_faces.clear();
    if (String::IsNullOrWhiteSpace(path)) return false;
    const std::string native = msclr::interop::marshal_as<std::string>(path);
    return LoadStepFile(native.c_str());
}

ShapeInfo^ Bridge::GetShapeInfo()
{
    const ShapeCounts c = ComputeCounts();
    ShapeInfo^ info = gcnew ShapeInfo();
    info->Solids = c.solids;
    info->Shells = c.shells;
    info->Faces = c.faces;
    info->Planes = c.planes;
    return info;
}

String^ Bridge::GetFaceType(int faceIndex)
{
    const char* name = FaceTypeName(faceIndex);
    return name == nullptr ? nullptr : gcnew String(name);
}

array<UvWire^>^ Bridge::GetFaceUvBoundary(int faceIndex)
{
    std::vector<NativeWire> nativeWires;
    if (!ExtractUvWires(faceIndex, nativeWires)) return nullptr;

    List<UvWire^>^ wires = gcnew List<UvWire^>();
    for (size_t i = 0; i < nativeWires.size(); ++i)
    {
        UvWire^ w = gcnew UvWire();
        w->IsOuter = nativeWires[i].isOuter;
        w->Closed = nativeWires[i].closed;

        array<double>^ pts = gcnew array<double>((int)nativeWires[i].pts.size());
        for (size_t k = 0; k < nativeWires[i].pts.size(); ++k)
            pts[(int)k] = nativeWires[i].pts[k];
        w->Points = pts;
        wires->Add(w);
    }
    return wires->ToArray();
}

array<double>^ Bridge::SampleUvTo3D(int faceIndex, double u, double v)
{
    double p[3];
    if (!SamplePoint(faceIndex, u, v, p)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = p[0]; r[1] = p[1]; r[2] = p[2];
    return r;
}

// ================= 뷰어 managed (S1 항목 1·6) =================

bool Bridge::InitViewer(IntPtr hwnd, int width, int height)
{
    (void)width; (void)height;
    return InitViewerNative(hwnd.ToPointer());
}

void Bridge::ResizeView(int width, int height)
{
    (void)width; (void)height;
    ResizeViewNative();
}

void Bridge::FitView()
{
    FitViewNative();
}

void Bridge::RotateStart(int x, int y)
{
    RotateStartNative(x, y);
}

void Bridge::RotateMove(int x, int y)
{
    RotateMoveNative(x, y);
}

void Bridge::RotateEnd()
{
    // StartRotation/Rotation 쌍으로 충분 — 종료 시 별도 처리 없음
}

void Bridge::ZoomView(double factor)
{
    ZoomViewNative(factor);
}

void Bridge::SetShapeColor(double r, double g, double b)
{
    SetShapeColorNative(r, g, b);
}

array<double>^ Bridge::GetViewState()
{
    double s[4];
    GetViewStateNative(s);
    array<double>^ r = gcnew array<double>(4);
    for (int i = 0; i < 4; ++i) r[i] = s[i];
    return r;
}

// ================= 오버레이 managed (S3) =================

bool Bridge::BuildWire3D(int faceIndex, array<double>^ uvFlat, bool closed)
{
    if (uvFlat == nullptr) return false;
    std::vector<double> uv(uvFlat->Length);
    for (int i = 0; i < uvFlat->Length; ++i) uv[i] = uvFlat[i];
    return BuildWire3DNative(faceIndex, uv, closed);
}

void Bridge::ClearOverlay()
{
    ClearOverlayNative();
}

void Bridge::SetViewDirection(double dx, double dy, double dz)
{
    SetViewDirectionNative(dx, dy, dz);
}

array<double>^ Bridge::GetFaceNormal(int faceIndex)
{
    double n[3];
    if (!FaceNormalNative(faceIndex, n)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = n[0]; r[1] = n[1]; r[2] = n[2];
    return r;
}

// ================= HLR managed (S1 항목 5) =================

HlrResult^ Bridge::HlrProject()
{
    const HlrCounts c = RunHlrNative();
    HlrResult^ r = gcnew HlrResult();
    r->VisibleEdges = c.vis;
    r->HiddenEdges = c.hid;
    r->Milliseconds = c.ms;
    return r;
}

}}
