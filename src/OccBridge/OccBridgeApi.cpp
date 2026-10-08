// S1 브리지 스파이크 — OCCT 7.9.3 네이티브 로직 (C++/CLI /clr:netcore)
// 네이티브 메커니즘은 2.1 사전 스모크(temp)에서 검증 완료 — 본 코드는 브리지 경유 검증용.
// 설계: OCCT 호출은 전부 native 헬퍼(#pragma managed(off))로 격리,
//       ref class 메서드는 marshaling만 담당 (C2711/C3821 회피).
#include "OccBridgeApi.h"

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
#include <BRepAdaptor_Surface.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <gp_Pnt.hxx>
#include <gp_Pnt2d.hxx>

#include <cmath>
#include <string>
#include <vector>

using namespace System;
using namespace System::Collections::Generic;

#pragma managed(push, off)
namespace
{
    TopoDS_Shape g_shape;
    std::vector<TopoDS_Face> g_faces;

    void RebuildFaceCache()
    {
        g_faces.clear();
        if (g_shape.IsNull()) return;
        for (TopExp_Explorer ex(g_shape, TopAbs_FACE); ex.More(); ex.Next())
            g_faces.push_back(TopoDS::Face(ex.Current()));
    }

    bool LoadStepFile(const char* path)
    {
        STEPControl_Reader reader;
        if (reader.ReadFile(path) != IFSelect_RetDone) return false;
        reader.TransferRoots();
        g_shape = reader.OneShape();
        if (g_shape.IsNull()) return false;
        RebuildFaceCache();
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
}
#pragma managed(pop)

namespace HeatingMold { namespace OccBridge {

bool Bridge::LoadStep(String^ path)
{
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

}}
