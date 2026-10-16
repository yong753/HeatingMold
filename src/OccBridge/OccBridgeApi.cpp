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
#include <TopExp.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <TopAbs_Orientation.hxx>
#include <BRep_Tool.hxx>
#include <BRepTools.hxx>
#include <BRepTools_WireExplorer.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRep_Builder.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepProj_Projection.hxx>
#include <ShapeAnalysis_FreeBounds.hxx>
#include <TopTools_HSequenceOfShape.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepClass_FaceClassifier.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <GeomLib_IsPlanarSurface.hxx>
#include <GeomAPI_ProjectPointOnSurf.hxx>
#include <IntCurvesFace_ShapeIntersector.hxx>
#include <Bnd_Box.hxx>
#include <BRepBndLib.hxx>
#include <Standard_Failure.hxx>
#include <Precision.hxx>
#include <TopLoc_Location.hxx>
#include <gp_Pnt.hxx>
#include <gp_Pnt2d.hxx>
#include <gp_Ax2.hxx>
#include <gp_Dir.hxx>
#include <gp_Lin.hxx>
#include <gp_Trsf.hxx>
#include <gp.hxx>
#include <gp_Vec.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <GProp_PrincipalProps.hxx>
#include <StepData_StepModel.hxx>
#include <StepRepr_Representation.hxx>
#include <StepGeom_Axis2Placement3d.hxx>
#include <StepGeom_Direction.hxx>
#include <TCollection_HAsciiString.hxx>

// 뷰어 (S1 항목 1·6) — WNT_Window/Aspect_DisplayConnection=TKService, OpenGl_GraphicDriver=TKOpenGl, V3d_*/AIS_*=TKV3d
#include <Aspect_DisplayConnection.hxx>
#include <OpenGl_GraphicDriver.hxx>
#include <V3d_Viewer.hxx>
#include <V3d_View.hxx>
#include <AIS_InteractiveContext.hxx>
#include <AIS_ColoredShape.hxx>
#include <AIS_ColoredDrawer.hxx>
#include <AIS_SelectionScheme.hxx>
#include <AIS_Shape.hxx>
#include <AIS_InteractiveObject.hxx>
#include <AIS_RubberBand.hxx>
#include <WNT_Window.hxx>
#include <Quantity_Color.hxx>
#include <Graphic3d_ZLayerId.hxx>
#include <Graphic3d_ZLayerSettings.hxx>
#include <Graphic3d_Vec2.hxx>
#include <Aspect_TypeOfLine.hxx>
#include <OpenGl_Context.hxx>
#include <Message.hxx>
#include <Message_PrinterOStream.hxx>

// HLR (S1 항목 5) — TKHLR
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
#include <HLRAlgo_Projector.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <GCPnts_QuasiUniformDeflection.hxx>

// 메싱 (W2 — shaded 렌더링 실패 원인 대응) — TKMesh
#include <BRepMesh_IncrementalMesh.hxx>
#include <Prs3d_Drawer.hxx>
#include <Prs3d_ShadingAspect.hxx>

// 면 픽킹 (W3) — 서브셰이프 선택
#include <TopTools_IndexedMapOfShape.hxx>
#include <SelectMgr_EntityOwner.hxx>
#include <StdSelect_BRepOwner.hxx>

#include <chrono>
#include <algorithm>
#include <cmath>
#include <cctype>
#include <limits>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

using namespace System;
using namespace System::Collections::Generic;

#pragma managed(push, off)
namespace
{
    TopoDS_Shape g_shape;
    std::vector<TopoDS_Face> g_faces;
    std::map<int, std::unique_ptr<IntCurvesFace_ShapeIntersector>> g_surfaceIntersectors;
    std::map<int, Bnd_Box> g_surfaceBounds;
    std::map<int, double> g_surfaceTolerances;
    TopTools_IndexedMapOfShape g_faceMap;   // W3 픽킹: g_faces와 같은 순회로 구축 (인덱스 1:1)
    gp_Dir g_autoAxes[3] = { gp::DX(), gp::DY(), gp::DZ() };

    bool TryGetSupplementalAxes(const Handle(StepData_StepModel)& model)
    {
        if (model.IsNull()) return false;
        for (Standard_Integer i = 1; i <= model->NbEntities(); ++i)
        {
            Handle(StepRepr_Representation) rep =
                Handle(StepRepr_Representation)::DownCast(model->Entity(i));
            if (rep.IsNull() || rep->Name().IsNull() ||
                std::string(rep->Name()->ToCString()) != "supplemental geometry") continue;

            for (Standard_Integer j = 1; j <= rep->NbItems(); ++j)
            {
                Handle(StepGeom_Axis2Placement3d) placement =
                    Handle(StepGeom_Axis2Placement3d)::DownCast(rep->ItemsValue(j));
                if (placement.IsNull()) continue;
                // STEP defaults for omitted AXIS / REF_DIRECTION are +Z / +X.
                gp_Vec z(0.0, 0.0, 1.0), x(1.0, 0.0, 0.0);
                if (placement->HasAxis())
                {
                    const Handle(StepGeom_Direction) d = placement->Axis();
                    if (d.IsNull() || d->NbDirectionRatios() < 3) continue;
                    z.SetCoord(d->DirectionRatiosValue(1), d->DirectionRatiosValue(2), d->DirectionRatiosValue(3));
                }
                if (placement->HasRefDirection())
                {
                    const Handle(StepGeom_Direction) d = placement->RefDirection();
                    if (d.IsNull() || d->NbDirectionRatios() < 3) continue;
                    x.SetCoord(d->DirectionRatiosValue(1), d->DirectionRatiosValue(2), d->DirectionRatiosValue(3));
                }
                const double zScale = std::max(std::fabs(z.X()), std::max(std::fabs(z.Y()), std::fabs(z.Z())));
                const double xScale = std::max(std::fabs(x.X()), std::max(std::fabs(x.Y()), std::fabs(x.Z())));
                if (zScale <= 1e-30 || xScale <= 1e-30) continue;
                z /= zScale; x /= xScale;
                z.Normalize();
                x.Normalize();
                x -= z * x.Dot(z);
                if (x.SquareMagnitude() < 1e-18) continue;
                x.Normalize();
                gp_Vec y = z.Crossed(x);
                if (y.SquareMagnitude() < 1e-12) continue;
                y.Normalize();
                g_autoAxes[0] = gp_Dir(x);
                g_autoAxes[1] = gp_Dir(y);
                g_autoAxes[2] = gp_Dir(z);
                return true;
            }
        }
        return false;
    }

    void ComputeAutoAxes(const Handle(StepData_StepModel)& stepModel)
    {
        g_autoAxes[0] = gp::DX(); g_autoAxes[1] = gp::DY(); g_autoAxes[2] = gp::DZ();
        if (TryGetSupplementalAxes(stepModel)) return;
        if (g_shape.IsNull()) return;
        GProp_GProps props;
        bool hasSolid = false;
        for (TopExp_Explorer ex(g_shape, TopAbs_SOLID); ex.More(); ex.Next()) { hasSolid = true; break; }
        if (hasSolid)
            BRepGProp::VolumeProperties(g_shape, props, Standard_True, Standard_True, Standard_False);
        if (!hasSolid || std::fabs(props.Mass()) <= 1e-12)
        {
            props = GProp_GProps();
            BRepGProp::SurfaceProperties(g_shape, props, Standard_True, Standard_False);
        }
        if (std::fabs(props.Mass()) <= 1e-12) return;
        const GProp_PrincipalProps principal = props.PrincipalProperties();
        gp_Vec axes[3] = { principal.FirstAxisOfInertia(), principal.SecondAxisOfInertia(), principal.ThirdAxisOfInertia() };
        for (int i = 0; i < 3; ++i)
        {
            if (axes[i].SquareMagnitude() < 1e-18) return;
            axes[i].Normalize();
            const double ax = std::fabs(axes[i].X()), ay = std::fabs(axes[i].Y()), az = std::fabs(axes[i].Z());
            const double dominant = ax >= ay && ax >= az ? axes[i].X() : (ay >= az ? axes[i].Y() : axes[i].Z());
            if (dominant < 0.0) axes[i].Reverse();
        }
        // Remove numerical non-orthogonality and enforce a right-handed work frame.
        axes[0].Normalize();
        axes[1] -= axes[0] * axes[1].Dot(axes[0]);
        if (axes[1].SquareMagnitude() < 1e-18) return;
        axes[1].Normalize();
        axes[2] = axes[0].Crossed(axes[1]);
        axes[2].Normalize();
        for (int i = 0; i < 3; ++i) g_autoAxes[i] = gp_Dir(axes[i]);
    }

    void AlignShapeToWorldAxes()
    {
        // Express every point in the detected model frame. Keep the origin fixed;
        // only remove the model's rotational misalignment.
        const gp_Dir& x = g_autoAxes[0];
        const gp_Dir& y = g_autoAxes[1];
        const gp_Dir& z = g_autoAxes[2];
        gp_Trsf rotation;
        rotation.SetValues(
            x.X(), x.Y(), x.Z(), 0.0,
            y.X(), y.Y(), y.Z(), 0.0,
            z.X(), z.Y(), z.Z(), 0.0);
        BRepBuilderAPI_Transform aligned(g_shape, rotation, Standard_True);
        g_shape = aligned.Shape();
        g_autoAxes[0] = gp::DX();
        g_autoAxes[1] = gp::DY();
        g_autoAxes[2] = gp::DZ();
    }

    bool GetAutoAxisNative(int axis, double* out)
    {
        if (axis < 0 || axis > 2 || out == nullptr) return false;
        out[0] = g_autoAxes[axis].X(); out[1] = g_autoAxes[axis].Y(); out[2] = g_autoAxes[axis].Z();
        return true;
    }

    // ---- AIS 뷰어 상태 (S1 항목 1·6) — 프로세스 전역, UI 스레드 전용 ----
    Handle(Aspect_DisplayConnection) g_disp;
    Handle(OpenGl_GraphicDriver) g_driver;
    Handle(V3d_Viewer) g_viewer;
    Handle(AIS_InteractiveContext) g_ctx;
    Handle(V3d_View) g_view;
    Handle(WNT_Window) g_window;
    Handle(AIS_Shape) g_aisShape;
    std::set<int> g_selectedFaceIds;
    std::map<int, Handle(AIS_Shape)> g_overlays;   // S3 사행 오버레이 — 면별 유지 (다른 면 생성 시에도 보존)
    std::vector<Handle(AIS_Shape)> g_worldOverlays;
    struct RoutePieceOverlay { Handle(AIS_Shape) ais; bool connector; bool enabled; };
    std::map<int, RoutePieceOverlay> g_routePieces;
    std::vector<Handle(AIS_Shape)> g_exclusionOverlays;
    Handle(AIS_Shape) g_exclusionPreview;
    Handle(AIS_RubberBand) g_selectionRubberBand;
    int g_lastHoverFace = -1;      // W5 — 호버 하이라이트 중복 Redraw 억제
    bool g_faceHighlightsVisible = true;

    void RebuildFaceCache()
    {
        g_surfaceIntersectors.clear();
        g_surfaceBounds.clear();
        g_surfaceTolerances.clear();
        g_faces.clear();
        g_faceMap.Clear();
        if (g_shape.IsNull()) return;
        for (TopExp_Explorer ex(g_shape, TopAbs_FACE); ex.More(); ex.Next())
        {
            const TopoDS_Face f = TopoDS::Face(ex.Current());
            g_faces.push_back(f);
            g_faceMap.Add(f);   // g_faces와 동일 순회 → FindIndex == g_faces 인덱스 + 1
        }
    }

    void UpdateSelectedFaceColors()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        g_faceHighlightsVisible = true;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;

        std::set<int> current;
        for (g_ctx->InitSelected(); g_ctx->MoreSelected(); g_ctx->NextSelected())
        {
            const Handle(StdSelect_BRepOwner)& owner =
                Handle(StdSelect_BRepOwner)::DownCast(g_ctx->SelectedOwner());
            if (owner.IsNull() || !owner->HasShape() || !g_faceMap.Contains(owner->Shape())) continue;
            current.insert(g_faceMap.FindIndex(owner->Shape()) - 1);
        }
        for (int faceId : g_selectedFaceIds)
            if (current.count(faceId) == 0 && faceId >= 0 && faceId < static_cast<int>(g_faces.size()))
                colored->UnsetCustomAspects(g_faces[faceId], Standard_True);
        for (int faceId : current)
            if (g_selectedFaceIds.count(faceId) == 0 && faceId >= 0 && faceId < static_cast<int>(g_faces.size()))
            {
                colored->SetCustomColor(g_faces[faceId], Quantity_Color(0.12, 0.82, 0.38, Quantity_TOC_RGB));
                colored->SetCustomTransparency(g_faces[faceId], 0.0);
                Handle(AIS_ColoredDrawer) faceStyle = colored->CustomAspects(g_faces[faceId]);
                faceStyle->SetupOwnShadingAspect();
                faceStyle->SetDisplayMode(AIS_Shaded);
                faceStyle->ShadingAspect()->SetColor(Quantity_Color(0.12, 0.82, 0.38, Quantity_TOC_RGB));
                faceStyle->ShadingAspect()->SetTransparency(0.0);
                // The green face is coincident with the model face. Bias its fill
                // toward the camera to avoid depth-buffer z-fighting.
                faceStyle->ShadingAspect()->Aspect()->SetPolygonOffsets(Aspect_POM_Fill, -2.0f, -2.0f);
            }
        g_selectedFaceIds.swap(current);
        g_ctx->Redisplay(g_aisShape, Standard_False);
    }

    void HideFaceHighlightsNative()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;
        // Remove presentation overrides, not selected owners: Ctrl selection and
        // pattern regeneration must retain the current selection.
        for (int faceId : g_selectedFaceIds)
            if (faceId >= 0 && faceId < static_cast<int>(g_faces.size()))
                colored->UnsetCustomAspects(g_faces[faceId], Standard_True);
        g_selectedFaceIds.clear();
        g_faceHighlightsVisible = false;
        g_ctx->UnhilightSelected(Standard_False);
        g_ctx->ClearDetected(Standard_False);
        g_lastHoverFace = -1;
        g_ctx->Redisplay(g_aisShape, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
    }

    /// <summary>W2 — STEP 로드는 메시 미생성이므로 shaded 표시를 위해 삼각분할 1회 수행</summary>
    void MeshShapeNative(double linearDeflection)
    {
        if (g_shape.IsNull()) return;
        BRepMesh_IncrementalMesh mesher(g_shape, linearDeflection, Standard_False, 0.5, Standard_False);
        (void)mesher;   // 생성자가 Perform까지 수행
    }

    /// <summary>W2/W3 — 표시 상태(모드/경계/색) 구성 + 면 선택 모드 활성화</summary>
    void ConfigureDisplay()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        g_aisShape->Attributes()->SetFaceBoundaryDraw(Standard_True);
        g_ctx->Display(g_aisShape, Standard_False);
        g_ctx->SetDisplayMode(g_aisShape, AIS_Shaded, Standard_False);   // W2 — 기본 와이어프레임 무시, 음영 강제
        // Face 선택은 커서 호버와 독립된 LocalSelected 스타일로 유지하고, 면 채움색도 구분한다.
        Handle(Prs3d_Drawer) selectedStyle = g_ctx->HighlightStyle(Prs3d_TypeOfHighlight_LocalSelected);
        if (selectedStyle.IsNull())
        {
            selectedStyle = new Prs3d_Drawer();
            g_ctx->SetHighlightStyle(Prs3d_TypeOfHighlight_LocalSelected, selectedStyle);
        }
        selectedStyle->SetupOwnShadingAspect();
        selectedStyle->SetDisplayMode(AIS_Shaded);
        selectedStyle->SetColor(Quantity_Color(0.12, 0.82, 0.38, Quantity_TOC_RGB));
        selectedStyle->SetTransparency(1.0);
        selectedStyle->ShadingAspect()->Aspect()->SetPolygonOffsets(Aspect_POM_Fill, -2.0f, -2.0f);
        selectedStyle->ShadingAspect()->SetColor(Quantity_Color(0.12, 0.82, 0.38, Quantity_TOC_RGB));
        selectedStyle->ShadingAspect()->SetTransparency(1.0);
        g_ctx->SetToHilightSelected(Standard_False);
        g_ctx->Activate(g_aisShape, TopAbs_FACE, Standard_True);          // W3 — 면 서브셰이프 선택 활성화
        g_selectedFaceIds.clear();
        g_faceHighlightsVisible = true;
    }

    void ClearDisplayedShape()
    {
        if (!g_ctx.IsNull())
        {
            g_ctx->ClearSelected(Standard_False);   // 이전 선택 해제 (해당 프레젠테이션 제거 전)
            g_ctx->ClearDetected(Standard_False);
        }
        if (!g_ctx.IsNull() && !g_aisShape.IsNull())
        {
            g_ctx->Remove(g_aisShape, Standard_False);
            g_aisShape.Nullify();
        }
        g_selectedFaceIds.clear();
        g_lastHoverFace = -1;
    }

    bool LoadStepFile(const char* path)
    {
        STEPControl_Reader reader;
        if (reader.ReadFile(path) != IFSelect_RetDone) return false;
        reader.TransferRoots();
        g_shape = reader.OneShape();
        if (g_shape.IsNull()) return false;
        ComputeAutoAxes(reader.StepModel());
        AlignShapeToWorldAxes();
        RebuildFaceCache();
        MeshShapeNative(1.0);   // W2 — shaded 표시용 삼각분할 (선형 편향 1mm)

        // 뷰어가 이미 초기화된 경우 즉시 표시 (S1 항목 1)
        if (!g_ctx.IsNull())
        {
            ClearDisplayedShape();
            g_aisShape = new AIS_ColoredShape(g_shape);
            ConfigureDisplay();
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

    struct NativeBoundaryLoop
    {
        bool closed = false;
        std::vector<double> xyz;
    };

    bool ExtractSelectedRegionBoundary(const std::vector<int>& faceIds, double deflection,
                                       std::vector<NativeBoundaryLoop>& loops)
    {
        if (faceIds.empty() || !std::isfinite(deflection) || deflection <= 0.0) return false;
        std::set<int> selected;
        for (int id : faceIds)
        {
            if (id < 0 || id >= static_cast<int>(g_faces.size())) return false;
            selected.insert(id);
        }

        // Sew only a temporary working copy: STEP/source topology is untouched.
        // This reconciles geometrically coincident but separately represented
        // edges before classifying the union boundary.
        // STEP's stored vertex/edge tolerances in Mold.stp reach ~0.1 mm and
        // are too permissive for seam matching. Use a separate bounded 0.01 mm
        // geometric sewing tolerance; larger gaps are deliberately not joined.
        const double sewingTolerance = 0.01;
        BRepBuilderAPI_Sewing sewing(sewingTolerance, Standard_True, Standard_True, Standard_True, Standard_True);
        for (int faceId : selected) sewing.Add(g_faces[faceId]);
        sewing.Perform();
        const TopoDS_Shape workingShape = sewing.SewedShape();
        if (workingShape.IsNull()) return false;

        std::vector<TopoDS_Face> workingFaces;
        for (TopExp_Explorer ex(workingShape, TopAbs_FACE); ex.More(); ex.Next())
            workingFaces.push_back(TopoDS::Face(ex.Current()));
        if (workingFaces.empty()) return false;

        // Count edge uses by topological identity after sewing. Internal edges
        // used by two selected working faces disappear from the union boundary.
        TopTools_IndexedMapOfShape edgeMap;
        std::vector<int> uses;
        for (const TopoDS_Face& face : workingFaces)
            for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                int index = edgeMap.FindIndex(edge);
                if (index == 0)
                {
                    edgeMap.Add(edge);
                    uses.push_back(0);
                    index = edgeMap.Extent();
                }
                ++uses[index - 1];
            }

        std::vector<TopoDS_Edge> boundaryEdges;
        for (int count : uses) if (count > 2) return false; // non-manifold selection
        for (int i = 1; i <= edgeMap.Extent(); ++i)
            if (uses[i - 1] == 1) boundaryEdges.push_back(TopoDS::Edge(edgeMap(i)));
        if (boundaryEdges.empty()) return false;

        TopTools_IndexedMapOfShape vertexMap;
        std::vector<std::vector<int>> incident;
        auto addVertexEdge = [&](const TopoDS_Vertex& vertex, int edgeIndex)
        {
            if (vertex.IsNull()) return;
            int index = vertexMap.FindIndex(vertex);
            if (index == 0)
            {
                vertexMap.Add(vertex);
                incident.emplace_back();
                index = vertexMap.Extent();
            }
            incident[index - 1].push_back(edgeIndex);
        };
        for (int i = 0; i < static_cast<int>(boundaryEdges.size()); ++i)
        {
            TopoDS_Vertex first, last;
            TopExp::Vertices(boundaryEdges[i], first, last, Standard_True);
            addVertexEdge(first, i);
            // Count both ends even for a one-edge closed loop; its graph degree is 2.
            addVertexEdge(last, i);
        }
        for (const auto& edgesAtVertex : incident)
            if (edgesAtVertex.size() != 2) return false; // reject branches/touching cycles

        std::vector<bool> seen(boundaryEdges.size(), false);
        for (int seed = 0; seed < static_cast<int>(boundaryEdges.size()); ++seed)
        {
            if (seen[seed]) continue;
            std::vector<int> component, stack{ seed };
            seen[seed] = true;
            while (!stack.empty())
            {
                int edgeIndex = stack.back(); stack.pop_back();
                component.push_back(edgeIndex);
                TopoDS_Vertex ends[2];
                TopExp::Vertices(boundaryEdges[edgeIndex], ends[0], ends[1], Standard_True);
                for (const TopoDS_Vertex& vertex : ends)
                {
                    const int vi = vertexMap.FindIndex(vertex);
                    if (vi <= 0) continue;
                    for (int adjacent : incident[vi - 1])
                        if (!seen[adjacent]) { seen[adjacent] = true; stack.push_back(adjacent); }
                }
            }

            BRepBuilderAPI_MakeWire wireBuilder;
            for (int edgeIndex : component) wireBuilder.Add(boundaryEdges[edgeIndex]);
            if (!wireBuilder.IsDone()) return false;
            const TopoDS_Wire wire = wireBuilder.Wire();
            NativeBoundaryLoop loop;
            loop.closed = BRep_Tool::IsClosed(wire);
            if (!loop.closed) return false;
            size_t orderedEdgeCount = 0;
            for (BRepTools_WireExplorer check(wire); check.More(); check.Next()) ++orderedEdgeCount;
            if (orderedEdgeCount != component.size()) return false;
            gp_Pnt firstPoint, previousPoint;
            bool hasPoint = false;
            for (BRepTools_WireExplorer we(wire); we.More(); we.Next())
            {
                const TopoDS_Edge edge = we.Current();
                BRepAdaptor_Curve curve(edge);
                GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                if (!sampler.IsDone() || sampler.NbPoints() < 2 || sampler.NbPoints() > 100000) return false;
                const bool reverse = edge.Orientation() == TopAbs_REVERSED;
                for (int k = 1; k <= sampler.NbPoints(); ++k)
                {
                    const gp_Pnt p = sampler.Value(reverse ? sampler.NbPoints() - k + 1 : k);
                    if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z())) return false;
                    if (k == 1 && hasPoint && previousPoint.Distance(p) > sewingTolerance) return false;
                    if (!hasPoint) { firstPoint = p; hasPoint = true; }
                    previousPoint = p;
                    if (loop.xyz.size() >= 3 &&
                        gp_Pnt(loop.xyz[loop.xyz.size() - 3], loop.xyz[loop.xyz.size() - 2],
                               loop.xyz[loop.xyz.size() - 1]).Distance(p) <= Precision::Confusion()) continue;
                    loop.xyz.push_back(p.X()); loop.xyz.push_back(p.Y()); loop.xyz.push_back(p.Z());
                }
            }
            if (!hasPoint || previousPoint.Distance(firstPoint) > sewingTolerance) return false;
            if (loop.xyz.size() < 9) return false;
            loops.push_back(std::move(loop));
        }
        return !loops.empty();
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

            // Overlay Z-layer (Topmost) with depth test off -> the pattern is never hidden by the solid.
            Graphic3d_ZLayerSettings aTopSettings = g_viewer->ZLayerSettings(Graphic3d_ZLayerId_Topmost);
            aTopSettings.SetEnableDepthTest(Standard_False);
            aTopSettings.SetEnableDepthWrite(Standard_False);
            g_viewer->SetZLayerSettings(Graphic3d_ZLayerId_Topmost, aTopSettings);

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
                g_aisShape = new AIS_ColoredShape(g_shape);
                ConfigureDisplay();
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

    void ZoomViewAtPointNative(int x, int y, int deltaY)
    {
        if (g_view.IsNull() || deltaY == 0) return;
        g_view->StartZoomAtPoint(x, y);
        g_view->ZoomAtPoint(x, y, x, y - deltaY);
        g_view->Redraw();
    }

    void SetShapeColorNative(double r, double g, double b)
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        g_ctx->SetColor(g_aisShape, Quantity_Color(r, g, b, Quantity_TOC_RGB), Standard_False);
        g_view->Redraw();
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

        // 같은 면을 다시 생성한 경우에만 그 면의 기존 오버레이를 교체하고,
        // 다른 면의 오버레이는 그대로 유지한다 (누적 표시).
        auto it = g_overlays.find(faceIndex);
        if (it != g_overlays.end())
        {
            g_ctx->Remove(it->second, Standard_False);
            g_overlays.erase(it);
        }
        Handle(AIS_Shape) overlay = new AIS_Shape(displayShape);
        g_ctx->SetColor(overlay, Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB), Standard_False);
        g_ctx->Display(overlay, Standard_False);
        g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);   // always on top (no depth test)
        g_ctx->Deactivate(overlay, Standard_False);   // 오버레이가 면 픽킹/호버를 가로채지 않도록 선택 비활성
        g_overlays[faceIndex] = overlay;
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    bool BuildPolyline3DNative(const std::vector<double>& xyz, bool closed, bool redraw)
    {
        if (xyz.size() < 6 || xyz.size() % 3 != 0) return false;
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i = 0; i + 2 < xyz.size(); i += 3)
            poly.Add(gp_Pnt(xyz[i], xyz[i + 1], xyz[i + 2]));
        if (!poly.IsDone()) return false;
        if (closed) poly.Close();
        if (!poly.IsDone()) return false;
        if (g_ctx.IsNull()) return true;
        Handle(AIS_Shape) overlay = new AIS_Shape(poly.Wire());
        g_ctx->SetColor(overlay, Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB), Standard_False);
        g_ctx->Display(overlay, Standard_False);
        g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
        g_ctx->Deactivate(overlay, Standard_False);
        g_worldOverlays.push_back(overlay);
        if (redraw && !g_view.IsNull()) g_view->Redraw();
        return true;
    }

    bool ProjectRoutePieceNative(int pieceId, const std::string& kind, const std::vector<double>& xyz,
        const std::vector<int>& faceIds, const gp_Dir& direction, double depth, double* result)
    {
        if (g_shape.IsNull() || xyz.size() < 6 || xyz.size() % 3 || faceIds.empty() ||
            !std::isfinite(depth) || depth < 0 || result == nullptr) return false;
        try
        {
            BRepBuilderAPI_MakePolygon source;
            for (size_t i = 0; i < xyz.size(); i += 3) {
                if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i+1]) || !std::isfinite(xyz[i+2])) return false;
                source.Add(gp_Pnt(xyz[i], xyz[i+1], xyz[i+2]));
            }
            if (!source.IsDone()) return false;
            BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
            std::set<int> unique;
            for (int id : faceIds) {
                if (id < 0 || id >= static_cast<int>(g_faces.size())) return false;
                if (unique.insert(id).second) sewing.Add(g_faces[id]);
            }
            sewing.Perform();
            const TopoDS_Shape target = sewing.SewedShape();
            if (target.IsNull()) return false;
            BRepProj_Projection projection(source.Wire(), target, direction);
            std::vector<TopoDS_Wire> wires;
            for (projection.Init(); projection.More(); projection.Next()) wires.push_back(projection.Current());
            if (wires.empty()) return false;
            TopoDS_Shape projected = wires.size() == 1 ? TopoDS_Shape(wires.front()) : TopoDS_Shape();
            if (projected.IsNull()) return false;
            if (depth > 0) {
                gp_Trsf tr; tr.SetTranslation(gp_Vec(direction) * depth);
                BRepBuilderAPI_Transform shifted(projected, tr, Standard_True);
                if (!shifted.IsDone()) return false;
                projected = shifted.Shape();
            }
            GProp_GProps props;
            BRepGProp::LinearProperties(wires.front(), props);
            TopoDS_Vertex first, last;
            for (BRepTools_WireExplorer ex(wires.front()); ex.More(); ex.Next()) {
                TopoDS_Vertex a, b; TopExp::Vertices(ex.Current(), a, b, Standard_True);
                if (first.IsNull()) first = a;
                last = b;
            }
            if (first.IsNull() || last.IsNull() || !std::isfinite(props.Mass())) return false;
            const gp_Pnt a = BRep_Tool::Pnt(first), b = BRep_Tool::Pnt(last);
            const gp_Vec offset = gp_Vec(direction) * depth;
            const gp_Pnt shownA = a.Translated(offset), shownB = b.Translated(offset);
            result[0] = props.Mass(); result[1] = shownA.X(); result[2] = shownA.Y(); result[3] = shownA.Z();
            result[4] = shownB.X(); result[5] = shownB.Y(); result[6] = shownB.Z();
            std::string normalizedKind = kind;
            std::transform(normalizedKind.begin(), normalizedKind.end(), normalizedKind.begin(),
                [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            // Only inter-lane U-turns are editable. Fixed same-lane connectors
            // stay part of the route and must not respond to connector picking.
            const bool connector = normalizedKind == "turnconnector" || normalizedKind == "turn";
            if (!g_ctx.IsNull()) {
                auto old = g_routePieces.find(pieceId);
                if (old != g_routePieces.end()) { g_ctx->Remove(old->second.ais, Standard_False); g_routePieces.erase(old); }
                Handle(AIS_Shape) ais = new AIS_Shape(projected);
                const Quantity_Color color = connector ? Quantity_Color(0.1, 0.65, 1.0, Quantity_TOC_RGB) : Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB);
                g_ctx->SetColor(ais, color, Standard_False);
                if (connector) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
                g_ctx->Display(ais, Standard_False); g_ctx->SetZLayer(ais, Graphic3d_ZLayerId_Topmost);
                if (connector) g_ctx->Activate(ais, Standard_False); else g_ctx->Deactivate(ais, Standard_False);
                g_routePieces[pieceId] = { ais, connector, true };
            }
            return true;
        } catch (const Standard_Failure&) { return false; }
    }

    bool SetConnectorNative(int id, bool enabled, double r, double g, double b)
    {
        auto it = g_routePieces.find(id);
        if (it == g_routePieces.end() || !it->second.connector || g_ctx.IsNull()) return false;
        it->second.enabled = enabled;
        g_ctx->SetColor(it->second.ais, Quantity_Color(r, g, b, Quantity_TOC_RGB), Standard_False);
        it->second.ais->Attributes()->LineAspect()->SetTypeOfLine(enabled ? Aspect_TOL_SOLID : Aspect_TOL_DASH);
        g_ctx->Redisplay(it->second.ais, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void ClearRoutePiecesNative()
    {
        if (!g_ctx.IsNull()) for (const auto& piece : g_routePieces) g_ctx->Remove(piece.second.ais, Standard_False);
        g_routePieces.clear();
    }

    int PickConnectorNative(int x, int y)
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);
        Handle(SelectMgr_EntityOwner) owner = g_ctx->DetectedOwner();
        if (owner.IsNull()) return -1;
        Handle(AIS_InteractiveObject) selectable = Handle(AIS_InteractiveObject)::DownCast(owner->Selectable());
        for (const auto& piece : g_routePieces)
            if (piece.second.connector && piece.second.ais == selectable) return piece.first;
        return -1;
    }

    Handle(AIS_Shape) MakeExclusionOverlay(const std::vector<double>& xyz, const Quantity_Color& color)
    {
        if (xyz.size() < 6 || xyz.size() % 3 != 0 || g_ctx.IsNull()) return Handle(AIS_Shape)();
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i = 0; i + 2 < xyz.size(); i += 3)
        {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i + 1]) || !std::isfinite(xyz[i + 2])) return Handle(AIS_Shape)();
            poly.Add(gp_Pnt(xyz[i], xyz[i + 1], xyz[i + 2]));
        }
        if (!poly.IsDone()) return Handle(AIS_Shape)();
        Handle(AIS_Shape) overlay = new AIS_Shape(poly.Wire());
        g_ctx->SetColor(overlay, color, Standard_False);
        g_ctx->SetWidth(overlay, 2.0, Standard_False);
        g_ctx->Display(overlay, Standard_False);
        g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
        g_ctx->Deactivate(overlay, Standard_False);
        return overlay;
    }

    bool AddExclusionOverlayNative(const std::vector<double>& xyz)
    {
        Handle(AIS_Shape) overlay = MakeExclusionOverlay(xyz, Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB));
        if (overlay.IsNull()) return false;
        g_exclusionOverlays.push_back(overlay);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void SetExclusionPreviewNative(const std::vector<double>& xyz)
    {
        if (!g_ctx.IsNull() && !g_exclusionPreview.IsNull())
        {
            g_ctx->Remove(g_exclusionPreview, Standard_False);
            g_exclusionPreview.Nullify();
        }
        g_exclusionPreview = MakeExclusionOverlay(xyz, Quantity_Color(0.95, 0.55, 0.05, Quantity_TOC_RGB));
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ClearExclusionOverlaysNative()
    {
        if (!g_ctx.IsNull())
        {
            for (const auto& overlay : g_exclusionOverlays) g_ctx->Remove(overlay, Standard_False);
            if (!g_exclusionPreview.IsNull()) g_ctx->Remove(g_exclusionPreview, Standard_False);
        }
        g_exclusionOverlays.clear();
        g_exclusionPreview.Nullify();
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ClearOverlayNative()
    {
        ClearRoutePiecesNative();
        if (!g_ctx.IsNull())
        {
            for (const auto& kv : g_overlays)
                g_ctx->Remove(kv.second, Standard_False);
            for (const auto& overlay : g_worldOverlays)
                g_ctx->Remove(overlay, Standard_False);
        }
        g_overlays.clear();
        g_worldOverlays.clear();
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

    void SetViewFrameNative(double dx, double dy, double dz, double ux, double uy, double uz)
    {
        if (g_view.IsNull()) return;
        gp_Vec direction(dx, dy, dz), up(ux, uy, uz);
        if (direction.SquareMagnitude() < 1e-18 || up.SquareMagnitude() < 1e-18) return;
        direction.Normalize(); up.Normalize();
        up -= direction * up.Dot(direction);
        if (up.SquareMagnitude() < 1e-12) return;
        up.Normalize();
        g_view->SetProj(direction.X(), direction.Y(), direction.Z());
        g_view->SetUp(up.X(), up.Y(), up.Z());
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

    bool FaceSurfaceIsPlanarNative(int faceIndex, double tolerance)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(g_faces.size()) ||
            !std::isfinite(tolerance) || tolerance <= 0.0) return false;
        Handle(Geom_Surface) surface = BRep_Tool::Surface(g_faces[faceIndex]);
        if (surface.IsNull()) return false;
        return GeomLib_IsPlanarSurface(surface, tolerance).IsPlanar();
    }

    bool ProjectPointToFaceNative(int faceIndex, double x, double y, double z, double* out)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(g_faces.size()) || out == nullptr ||
            !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return false;
        try
        {
            const TopoDS_Face& face = g_faces[faceIndex];
            TopLoc_Location location;
            Handle(Geom_Surface) surface = BRep_Tool::Surface(face, location);
            if (surface.IsNull()) return false;
            Standard_Real uMin, uMax, vMin, vMax;
            BRepTools::UVBounds(face, uMin, uMax, vMin, vMax);
            if (!std::isfinite(uMin) || !std::isfinite(uMax) || !std::isfinite(vMin) || !std::isfinite(vMax) ||
                uMax <= uMin || vMax <= vMin) return false;

            gp_Pnt query(x, y, z);
            query.Transform(location.Transformation().Inverted());
            GeomAPI_ProjectPointOnSurf projection(query, surface, uMin, uMax, vMin, vMax, Precision::Confusion());
            if (!projection.IsDone() || projection.NbPoints() == 0) return false;
            Standard_Real u, v;
            projection.LowerDistanceParameters(u, v);
            BRepClass_FaceClassifier classifier(face, gp_Pnt2d(u, v), 1.0e-6);
            if (classifier.State() != TopAbs_IN && classifier.State() != TopAbs_ON) return false;

            gp_Pnt projected = projection.NearestPoint();
            projected.Transform(location.Transformation());
            out[0] = projected.X(); out[1] = projected.Y(); out[2] = projected.Z();
            return true;
        }
        catch (const Standard_Failure&)
        {
            return false;
        }
    }

    bool IntersectSelectedFacesNative(const std::vector<int>& faceIds, double x, double y, double z,
                                      double nx, double ny, double nz, double* out)
    {
        if (out == nullptr || faceIds.empty() ||
            !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) ||
            !std::isfinite(nx) || !std::isfinite(ny) || !std::isfinite(nz)) return false;
        // Scale before normalization to avoid overflow/underflow for finite normals.
        const double scale = std::max(std::fabs(nx), std::max(std::fabs(ny), std::fabs(nz)));
        if (scale == 0.0) return false;
        try
        {
            const gp_Lin line(gp_Pnt(x, y, z), gp_Dir(nx / scale, ny / scale, nz / scale));
            // The caller starts on the whole-model +Z datum and casts toward -Z.
            const double limit = 1.0e12;
            double nearest = limit;
            bool found = false;
            for (int faceId : faceIds)
            {
                if (faceId < 0 || faceId >= static_cast<int>(g_faces.size())) continue;
                try
                {
                    auto bound = g_surfaceBounds.find(faceId);
                    if (bound == g_surfaceBounds.end())
                    {
                        Bnd_Box box;
                        BRepBndLib::Add(g_faces[faceId], box);
                        bound = g_surfaceBounds.emplace(faceId, box).first;
                        double tolerance = std::max(Precision::Confusion(), BRep_Tool::Tolerance(g_faces[faceId]));
                        for (TopExp_Explorer edge(g_faces[faceId], TopAbs_EDGE); edge.More(); edge.Next())
                            tolerance = std::max(tolerance, BRep_Tool::Tolerance(TopoDS::Edge(edge.Current())));
                        for (TopExp_Explorer vertex(g_faces[faceId], TopAbs_VERTEX); vertex.More(); vertex.Next())
                            tolerance = std::max(tolerance, BRep_Tool::Tolerance(TopoDS::Vertex(vertex.Current())));
                        // Imported STEP tolerances can be ~0.1 mm; do not let
                        // that broad tolerance expand trimmed-face hit domains.
                        g_surfaceTolerances[faceId] = std::min(tolerance, 0.01);
                    }
                    if (!bound->second.IsVoid() && bound->second.IsOut(line)) continue;
                    auto& cached = g_surfaceIntersectors[faceId];
                    if (!cached)
                    {
                        cached = std::make_unique<IntCurvesFace_ShapeIntersector>();
                        cached->Load(g_faces[faceId], g_surfaceTolerances[faceId]);
                    }
                    auto& intersector = *cached;
                    intersector.Perform(line, 0.0, limit);
                    if (!intersector.IsDone()) continue;
                    for (Standard_Integer i = 1; i <= intersector.NbPnt(); ++i)
                    {
                        const double t = intersector.WParameter(i);
                        const gp_Pnt& hit = intersector.Pnt(i);
                        if (!std::isfinite(t) || !std::isfinite(hit.X()) ||
                            !std::isfinite(hit.Y()) || !std::isfinite(hit.Z())) continue;
                        if (intersector.State(i) != TopAbs_IN && intersector.State(i) != TopAbs_ON) continue;
                        if (t < 0.0 || t > limit || (found && t >= nearest)) continue;
                        nearest = t;
                        out[0] = hit.X(); out[1] = hit.Y(); out[2] = hit.Z(); out[3] = faceId;
                        found = true;
                    }
                }
                catch (const Standard_Failure&) { }
            }
            return found;
        }
        catch (const Standard_Failure&)
        {
            return false;
        }
    }

    bool GetModelBoundsInFrameNative(const gp_Dir& xAxis, const gp_Dir& yAxis,
                                     const gp_Dir& zAxis, double* out)
    {
        if (g_shape.IsNull() || out == nullptr) return false;
        try
        {
            Bnd_Box box;
            BRepBndLib::Add(g_shape, box);
            if (box.IsVoid()) return false;
            double xmin, ymin, zmin, xmax, ymax, zmax;
            box.Get(xmin, ymin, zmin, xmax, ymax, zmax);
            const double largest = std::numeric_limits<double>::max();
            double lo[3] = { largest, largest, largest };
            double hi[3] = { -largest, -largest, -largest };
            const gp_Dir* axes[3] = { &xAxis, &yAxis, &zAxis };
            for (int mask = 0; mask < 8; ++mask)
            {
                const gp_Pnt p((mask & 1) ? xmax : xmin,
                               (mask & 2) ? ymax : ymin,
                               (mask & 4) ? zmax : zmin);
                const gp_Vec v(gp::Origin(), p);
                for (int axis = 0; axis < 3; ++axis)
                {
                    const double coordinate = v.Dot(gp_Vec(*axes[axis]));
                    lo[axis] = std::min(lo[axis], coordinate);
                    hi[axis] = std::max(hi[axis], coordinate);
                }
            }
            for (int i = 0; i < 3; ++i)
            {
                if (!std::isfinite(lo[i]) || !std::isfinite(hi[i])) return false;
                out[2 * i] = lo[i]; out[2 * i + 1] = hi[i];
            }
            return true;
        }
        catch (const Standard_Failure&) { return false; }
    }

    bool ProjectPathWireOntoSelectedFacesNative(const std::vector<int>& faceIds,
        const std::vector<double>& xyz, const gp_Dir& direction, double insertionDepth, double* outLength,
        int* outEdgeCount, int* outStatus, int* outWireCount)
    {
        if (outStatus) *outStatus = 1;
        if (outWireCount) *outWireCount = 0;
        if (g_shape.IsNull() || faceIds.empty() || xyz.size() < 6 || xyz.size() % 3 != 0 ||
            !std::isfinite(insertionDepth) || insertionDepth < 0.0 ||
            outLength == nullptr || outEdgeCount == nullptr || outStatus == nullptr || outWireCount == nullptr) return false;
        try
        {
            // Build the source as one true OCCT wire in the fixed datum plane.
            BRepBuilderAPI_MakePolygon sourceBuilder;
            for (size_t i = 0; i < xyz.size(); i += 3)
            {
                if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i + 1]) || !std::isfinite(xyz[i + 2]))
                    return false;
                sourceBuilder.Add(gp_Pnt(xyz[i], xyz[i + 1], xyz[i + 2]));
            }
            if (!sourceBuilder.IsDone()) return false;
            const TopoDS_Wire sourceWire = sourceBuilder.Wire();
            *outStatus = 2;

            // Sew only a temporary target copy so projected curves can cross
            // geometrically matching Face seams without changing the STEP model.
            BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
            std::set<int> uniqueFaces;
            for (int id : faceIds)
            {
                if (id < 0 || id >= static_cast<int>(g_faces.size())) return false;
                if (uniqueFaces.insert(id).second) sewing.Add(g_faces[id]);
            }
            sewing.Perform();
            const TopoDS_Shape target = sewing.SewedShape();
            if (target.IsNull()) return false;
            *outStatus = 3;

            // BRepProj_Projection creates actual OCCT edges on the selected
            // trimmed faces. Its output is connected into wires at shared seams.
            BRepProj_Projection projection(sourceWire, target, direction);
            std::vector<TopoDS_Wire> wires;
            for (projection.Init(); projection.More(); projection.Next())
                wires.push_back(projection.Current());
            if (wires.empty()) { *outStatus = 4; return false; }

            // The projection algorithm's initial connection uses OCCT's very
            // tight confusion tolerance. Reconnect its edges at the same bounded
            // 0.01 mm seam tolerance used for the temporary target sewing.
            Handle(TopTools_HSequenceOfShape) projectedEdges = new TopTools_HSequenceOfShape();
            for (const TopoDS_Wire& wire : wires)
                for (BRepTools_WireExplorer explorer(wire); explorer.More(); explorer.Next())
                    projectedEdges->Append(explorer.Current());
            Handle(TopTools_HSequenceOfShape) connectedWires = new TopTools_HSequenceOfShape();
            ShapeAnalysis_FreeBounds::ConnectEdgesToWires(projectedEdges, 0.01, Standard_False, connectedWires);
            *outWireCount = connectedWires->Length();
            *outStatus = 4;
            if (connectedWires->Length() != 1) return false;
            const TopoDS_Wire projectedWire = TopoDS::Wire(connectedWires->Value(1));
            if (BRep_Tool::IsClosed(projectedWire)) return false;
            *outStatus = 5;
            if (!BRepCheck_Analyzer(projectedWire).IsValid()) return false;

            // Require projection coverage along the complete source route, not
            // merely one surviving fragment. Probe each source edge at both
            // endpoints and its midpoint against the selected trimmed faces.
            IntCurvesFace_ShapeIntersector coverage;
            coverage.Load(target, 0.01);
            size_t sourceEdgeCount = 0;
            for (BRepTools_WireExplorer sourceExplorer(sourceWire); sourceExplorer.More(); sourceExplorer.Next())
            {
                const TopoDS_Edge sourceEdge = sourceExplorer.Current();
                BRepAdaptor_Curve sourceCurve(sourceEdge);
                const double first = sourceCurve.FirstParameter();
                const double last = sourceCurve.LastParameter();
                if (!std::isfinite(first) || !std::isfinite(last) || last <= first) return false;
                for (double fraction : { 0.0, 0.5, 1.0 })
                {
                    const gp_Pnt sourcePoint = sourceCurve.Value(first + (last - first) * fraction);
                    const gp_Lin ray(sourcePoint, direction);
                    coverage.Perform(ray, 0.0, 1.0e12);
                    bool hit = false;
                    if (coverage.IsDone())
                        for (Standard_Integer hitIndex = 1; hitIndex <= coverage.NbPnt(); ++hitIndex)
                            if (coverage.State(hitIndex) == TopAbs_IN || coverage.State(hitIndex) == TopAbs_ON)
                            { hit = true; break; }
                    if (!hit) { *outStatus = 51; return false; }
                }
                ++sourceEdgeCount;
            }
            if (sourceEdgeCount == 0) return false;

            size_t edgeCount = 0;
            size_t totalWireEdges = 0;
            for (TopExp_Explorer edgeExplorer(projectedWire, TopAbs_EDGE); edgeExplorer.More(); edgeExplorer.Next())
                ++totalWireEdges;
            gp_Pnt previousPoint;
            bool havePrevious = false;
            TopoDS_Vertex firstVertex, lastVertex;
            for (BRepTools_WireExplorer explorer(projectedWire); explorer.More(); explorer.Next())
            {
                const TopoDS_Edge edge = explorer.Current();
                TopoDS_Vertex startVertex, endVertex;
                TopExp::Vertices(edge, startVertex, endVertex, Standard_True);
                if (startVertex.IsNull() || endVertex.IsNull()) return false;
                const gp_Pnt startPoint = BRep_Tool::Pnt(startVertex);
                const gp_Pnt endPoint = BRep_Tool::Pnt(endVertex);
                if (!std::isfinite(startPoint.X()) || !std::isfinite(startPoint.Y()) || !std::isfinite(startPoint.Z()) ||
                    !std::isfinite(endPoint.X()) || !std::isfinite(endPoint.Y()) || !std::isfinite(endPoint.Z())) return false;
                if (havePrevious && previousPoint.Distance(startPoint) > 0.01) return false;
                if (!havePrevious) firstVertex = startVertex;
                lastVertex = endVertex;
                previousPoint = endPoint;
                havePrevious = true;
                ++edgeCount;
            }
            *outStatus = 6;
            if (edgeCount == 0 || edgeCount != totalWireEdges) return false;

            // Check that the connected result reaches both ends of the source
            // wire; otherwise a clipped leading/trailing segment is not success.
            TopoDS_Vertex sourceStart, sourceEnd;
            for (BRepTools_WireExplorer sourceExplorer(sourceWire); sourceExplorer.More(); sourceExplorer.Next())
            {
                TopoDS_Vertex edgeStart, edgeEnd;
                TopExp::Vertices(sourceExplorer.Current(), edgeStart, edgeEnd, Standard_True);
                if (sourceStart.IsNull()) sourceStart = edgeStart;
                sourceEnd = edgeEnd;
            }
            if (sourceStart.IsNull() || sourceEnd.IsNull()) return false;
            auto findForwardHit = [&](const gp_Pnt& point, gp_Pnt& hitPoint)
            {
                coverage.Perform(gp_Lin(point, direction), 0.0, 1.0e12);
                if (!coverage.IsDone()) return false;
                double nearest = 1.0e12;
                bool found = false;
                for (Standard_Integer i = 1; i <= coverage.NbPnt(); ++i)
                    if ((coverage.State(i) == TopAbs_IN || coverage.State(i) == TopAbs_ON) &&
                        coverage.WParameter(i) >= 0.0 && coverage.WParameter(i) < nearest)
                    { nearest = coverage.WParameter(i); hitPoint = coverage.Pnt(i); found = true; }
                return found;
            };
            gp_Pnt mappedStart, mappedEnd;
            if (!findForwardHit(BRep_Tool::Pnt(sourceStart), mappedStart) ||
                !findForwardHit(BRep_Tool::Pnt(sourceEnd), mappedEnd)) return false;
            const gp_Pnt wireStart = BRep_Tool::Pnt(firstVertex), wireEnd = BRep_Tool::Pnt(lastVertex);
            if (!((wireStart.Distance(mappedStart) <= 0.01 && wireEnd.Distance(mappedEnd) <= 0.01) ||
                  (wireStart.Distance(mappedEnd) <= 0.01 && wireEnd.Distance(mappedStart) <= 0.01))) return false;

            GProp_GProps properties;
            BRepGProp::LinearProperties(projectedWire, properties);
            const double length = properties.Mass();
            *outStatus = 7;
            if (!std::isfinite(length) || length <= 0.0) return false;

            // Insertion depth is a global -projection-axis displacement of the
            // mapped route; it is not a per-Face normal offset.
            TopoDS_Shape displayedRoute = projectedWire;
            if (insertionDepth > 0.0)
            {
                gp_Trsf offset;
                offset.SetTranslation(gp_Vec(direction) * insertionDepth);
                BRepBuilderAPI_Transform transformed(projectedWire, offset, Standard_True);
                if (!transformed.IsDone()) return false;
                displayedRoute = transformed.Shape();
            }

            if (!g_ctx.IsNull())
            {
                Handle(AIS_Shape) overlay = new AIS_Shape(displayedRoute);
                g_ctx->SetColor(overlay, Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB), Standard_False);
                g_ctx->Display(overlay, Standard_False);
                g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(overlay, Standard_False);
                g_worldOverlays.push_back(overlay);
            }
            *outLength = length;
            *outEdgeCount = static_cast<int>(edgeCount);
            *outStatus = 8;
            return true;
        }
        catch (const Standard_Failure&) { if (outStatus) *outStatus = 99; return false; }
    }

    // ================= 면 픽킹/선택 (W3·W5) =================

    /// <summary>현재 DetectedOwner가 가리키는 면 인덱스 (g_faces와 1:1) — 없으면 -1</summary>
    int DetectedFaceIndex()
    {
        if (g_ctx.IsNull()) return -1;
        const Handle(SelectMgr_EntityOwner)& owner = g_ctx->DetectedOwner();
        if (owner.IsNull()) return -1;
        const Handle(StdSelect_BRepOwner) brep = Handle(StdSelect_BRepOwner)::DownCast(owner);
        if (brep.IsNull() || !brep->HasShape()) return -1;
        const TopoDS_Shape& sub = brep->Shape();
        if (!g_faceMap.Contains(sub)) return -1;
        return g_faceMap.FindIndex(sub) - 1;
    }

    /// <summary>W5 — 호버: 동적 하이라이트. 면이 바뀔 때만 Redraw (무효화 폭주 방지)</summary>
    int HoverFaceNative(int x, int y)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return -1;
        if (!g_faceHighlightsVisible) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);

        const int idx = DetectedFaceIndex();
        if (idx != g_lastHoverFace)
        {
            g_lastHoverFace = idx;
            g_view->Redraw();
        }
        return idx;
    }

    void ClearHoverNative()
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        if (g_lastHoverFace == -1) return;
        g_lastHoverFace = -1;
        g_ctx->ClearDetected(Standard_False);
        g_view->Redraw();
    }

    /// <summary>W5 — 클릭: 면 선택(선택 하이라이트 유지). 빈 곳이면 선택 해제 후 -1</summary>
    int SelectFaceNative(int x, int y, bool toggle)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);
        const int idx = DetectedFaceIndex();
        g_lastHoverFace = idx;
        if (idx < 0 && !toggle)
            g_ctx->ClearSelected(Standard_False);
        else if (idx >= 0)
            g_ctx->SelectDetected(toggle ? AIS_SelectionScheme_XOR : AIS_SelectionScheme_Replace);
        UpdateSelectedFaceColors();
        g_view->Redraw();
        return idx;
    }

    std::vector<int> SelectFacesRectangleNative(int x0, int y0, int x1, int y1, bool toggle)
    {
        std::vector<int> result;
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return result;
        const int minX = std::min(x0, x1), maxX = std::max(x0, x1);
        const int minY = std::min(y0, y1), maxY = std::max(y0, y1);
        Standard_Integer viewWidth = 0, viewHeight = 0;
        g_view->Window()->Size(viewWidth, viewHeight);
        const int clippedMinX = std::max(0, minX), clippedMaxX = std::min(viewWidth - 1, maxX);
        const int clippedMinY = std::max(0, minY), clippedMaxY = std::min(viewHeight - 1, maxY);
        if (clippedMinX > clippedMaxX || clippedMinY > clippedMaxY) return result;
        std::map<int, Handle(SelectMgr_EntityOwner)> owners;
        std::set<int> previouslySelected;

        // Keep currently selected owners so XOR selection can preserve faces
        // outside the rectangle while replacing the selection deterministically.
        for (g_ctx->InitSelected(); g_ctx->MoreSelected(); g_ctx->NextSelected())
        {
            Handle(SelectMgr_EntityOwner) owner = g_ctx->SelectedOwner();
            const Handle(StdSelect_BRepOwner) brep = Handle(StdSelect_BRepOwner)::DownCast(owner);
            if (brep.IsNull() || !brep->HasShape()) continue;
            if (!g_faceMap.Contains(brep->Shape())) continue;
            const int faceId = g_faceMap.FindIndex(brep->Shape()) - 1;
            previouslySelected.insert(faceId);
            owners[faceId] = owner;
        }

        // OCCT rectangle selection includes sensitive faces behind the visible
        // surface. Probe the rectangle and keep only the frontmost detected face
        // at each screen location. Bound work for very large window selections.
        const int64_t width = static_cast<int64_t>(clippedMaxX) - clippedMinX + 1;
        const int64_t height = static_cast<int64_t>(clippedMaxY) - clippedMinY + 1;
        const int64_t area = width * height;
        constexpr int maxProbeCount = 2500;
        int step = std::max(1, static_cast<int>(std::ceil(std::sqrt(static_cast<double>(area) / maxProbeCount))));
        while (((width + step - 1) / step) * ((height + step - 1) / step) > maxProbeCount) ++step;
        std::set<int> visibleFaces;
        for (int y = clippedMinY; ; y = std::min(clippedMaxY, y + step))
        {
            for (int x = clippedMinX; ; x = std::min(clippedMaxX, x + step))
            {
                g_ctx->MoveTo(x, y, g_view, Standard_False);
                Handle(SelectMgr_EntityOwner) owner = g_ctx->DetectedOwner();
                const Handle(StdSelect_BRepOwner) brep = Handle(StdSelect_BRepOwner)::DownCast(owner);
                if (!brep.IsNull() && brep->HasShape() && g_faceMap.Contains(brep->Shape()))
                {
                    const int faceId = g_faceMap.FindIndex(brep->Shape()) - 1;
                    visibleFaces.insert(faceId);
                    owners[faceId] = owner;
                }
                if (x == clippedMaxX) break;
            }
            if (y == clippedMaxY) break;
        }

        std::set<int> targetFaces = toggle ? previouslySelected : std::set<int>();
        for (int faceId : visibleFaces)
        {
            if (toggle && previouslySelected.count(faceId) != 0) targetFaces.erase(faceId);
            else targetFaces.insert(faceId);
        }
        g_ctx->ClearSelected(Standard_False);
        for (int faceId : targetFaces)
        {
            auto it = owners.find(faceId);
            if (it != owners.end()) g_ctx->AddSelect(it->second);
        }
        g_ctx->HilightSelected(Standard_False);
        UpdateSelectedFaceColors();
        g_ctx->ClearDetected(Standard_False);
        g_lastHoverFace = -1;
        result.assign(targetFaces.begin(), targetFaces.end());
        g_view->Redraw();
        return result;
    }

    void SetSelectionRectangleNative(int x0, int y0, int x1, int y1)
    {
        if (g_ctx.IsNull()) return;
        if (g_selectionRubberBand.IsNull())
            g_selectionRubberBand = new AIS_RubberBand(Quantity_Color(0.1, 0.55, 0.95, Quantity_TOC_RGB), Aspect_TOL_SOLID,
                                                       Quantity_Color(0.1, 0.55, 0.95, Quantity_TOC_RGB), 0.8, 1.5);
        g_selectionRubberBand->SetRectangle(std::min(x0, x1), std::min(y0, y1), std::max(x0, x1), std::max(y0, y1));
        if (!g_ctx->IsDisplayed(g_selectionRubberBand)) g_ctx->Display(g_selectionRubberBand, Standard_False);
        else g_ctx->Redisplay(g_selectionRubberBand, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ClearSelectionRectangleNative()
    {
        if (!g_ctx.IsNull() && !g_selectionRubberBand.IsNull())
            g_ctx->Remove(g_selectionRubberBand, Standard_False);
        g_selectionRubberBand.Nullify();
        if (!g_view.IsNull()) g_view->Redraw();
    }

    bool ScreenPointOnPlaneNative(int x, int y, const gp_Pnt& origin, const gp_Dir& normal, double* out)
    {
        if (g_view.IsNull() || out == nullptr) return false;
        Standard_Real rx, ry, rz, vx, vy, vz;
        g_view->ConvertWithProj(x, y, rx, ry, rz, vx, vy, vz);
        gp_Vec ray(vx, vy, vz);
        if (ray.SquareMagnitude() < 1e-18) return false;
        ray.Normalize();
        const gp_Vec n(normal);
        const double denom = ray.Dot(n);
        if (std::fabs(denom) < 1e-10) return false;
        const gp_Pnt rayOrigin(rx, ry, rz);
        const double t = gp_Vec(rayOrigin, origin).Dot(n) / denom;
        const gp_Pnt hit = rayOrigin.Translated(ray * t);
        out[0] = hit.X(); out[1] = hit.Y(); out[2] = hit.Z();
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

    // ================= HLR 기하 (M4 DXF 뷰) =================

    struct NativeHlrLine { bool visible; std::vector<double> pts; };

    void CollectHlrEdges(const TopoDS_Shape& shape, bool visible,
                         std::vector<NativeHlrLine>& out, double deflection)
    {
        if (shape.IsNull()) return;
        for (TopExp_Explorer ex(shape, TopAbs_EDGE); ex.More(); ex.Next())
        {
            const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
            if (BRep_Tool::Degenerated(edge)) continue;
            try
            {
                BRepAdaptor_Curve curve(edge);
                GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                if (!sampler.IsDone() || sampler.NbPoints() < 2) continue;
                NativeHlrLine line;
                line.visible = visible;
                for (Standard_Integer i = 1; i <= sampler.NbPoints(); ++i)
                {
                    const gp_Pnt p = curve.Value(sampler.Parameter(i));
                    line.pts.push_back(p.X());
                    line.pts.push_back(p.Y());
                }
                out.push_back(line);
            }
            catch (const Standard_Failure&) { }
        }
    }

    std::vector<NativeHlrLine> RunHlrGeometryNative(
        double nx, double ny, double nz, double xx, double xy, double xz, bool hiddenToo)
    {
        std::vector<NativeHlrLine> out;
        if (g_shape.IsNull()) return out;

        const gp_Dir n(nx, ny, nz);
        gp_Dir x(xx, xy, xz);
        if (std::fabs(n.Dot(x)) > 0.99)
        {
            x = n.Crossed(gp_Dir(0.0, 0.0, 1.0));
            if (x.X() * x.X() + x.Y() * x.Y() + x.Z() * x.Z() < 1e-12)
                x = n.Crossed(gp_Dir(1.0, 0.0, 0.0));
        }

        Handle(HLRBRep_Algo) algo = new HLRBRep_Algo();
        algo->Add(g_shape);
        algo->Projector(HLRAlgo_Projector(gp_Ax2(gp_Pnt(0.0, 0.0, 0.0), n, x)));
        algo->Update();
        algo->Hide();

        HLRBRep_HLRToShape hlr(algo);
        const double defl = 1.0;
        CollectHlrEdges(hlr.VCompound(), true, out, defl);
        CollectHlrEdges(hlr.Rg1LineVCompound(), true, out, defl);
        CollectHlrEdges(hlr.OutLineVCompound(), true, out, defl);
        if (hiddenToo)
        {
            CollectHlrEdges(hlr.HCompound(), false, out, defl);
            CollectHlrEdges(hlr.Rg1LineHCompound(), false, out, defl);
            CollectHlrEdges(hlr.OutLineHCompound(), false, out, defl);
        }
        return out;
    }
}
#pragma managed(pop)

namespace HeatingMold { namespace OccBridge {

bool Bridge::LoadStep(String^ path)
{
    g_surfaceIntersectors.clear();
    g_surfaceBounds.clear();
    g_surfaceTolerances.clear();
    ClearDisplayedShape();
    ClearOverlayNative();
    ClearExclusionOverlaysNative();
    g_shape.Nullify();
    g_faces.clear();
    g_faceMap.Clear();
    g_autoAxes[0] = gp::DX(); g_autoAxes[1] = gp::DY(); g_autoAxes[2] = gp::DZ();
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

array<RegionBoundaryLoop^>^ Bridge::GetSelectedRegionBoundary(array<int>^ faceIds, double deflection)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    std::vector<NativeBoundaryLoop> nativeLoops;
    try
    {
        if (!ExtractSelectedRegionBoundary(ids, deflection, nativeLoops)) return nullptr;
    }
    catch (const Standard_Failure&)
    {
        return nullptr;
    }
    auto result = gcnew array<RegionBoundaryLoop^>((int)nativeLoops.size());
    for (int i = 0; i < result->Length; ++i)
    {
        RegionBoundaryLoop^ loop = gcnew RegionBoundaryLoop();
        loop->Closed = nativeLoops[(size_t)i].closed;
        auto points = gcnew array<double>((int)nativeLoops[(size_t)i].xyz.size());
        for (int k = 0; k < points->Length; ++k) points[k] = nativeLoops[(size_t)i].xyz[(size_t)k];
        loop->Points = points;
        result[i] = loop;
    }
    return result;
}

array<double>^ Bridge::GetModelBoundsInFrame(double xx, double xy, double xz,
                                              double yx, double yy, double yz,
                                              double zx, double zy, double zz)
{
    double bounds[6];
    try
    {
        if (!GetModelBoundsInFrameNative(gp_Dir(xx, xy, xz), gp_Dir(yx, yy, yz),
                                         gp_Dir(zx, zy, zz), bounds)) return nullptr;
    }
    catch (const Standard_Failure&) { return nullptr; }
    auto result = gcnew array<double>(6);
    for (int i = 0; i < 6; ++i) result[i] = bounds[i];
    return result;
}

array<double>^ Bridge::SampleUvTo3D(int faceIndex, double u, double v)
{
    double p[3];
    if (!SamplePoint(faceIndex, u, v, p)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = p[0]; r[1] = p[1]; r[2] = p[2];
    return r;
}

array<double>^ Bridge::ProjectPointToFace(int faceIndex, double x, double y, double z)
{
    double p[3];
    if (!ProjectPointToFaceNative(faceIndex, x, y, z, p)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = p[0]; r[1] = p[1]; r[2] = p[2];
    return r;
}

array<double>^ Bridge::IntersectSelectedFaces(array<int>^ faceIds, double x, double y, double z,
                                              double nx, double ny, double nz)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    double p[4];
    if (!IntersectSelectedFacesNative(ids, x, y, z, nx, ny, nz, p)) return nullptr;
    array<double>^ result = gcnew array<double>(4);
    for (int i = 0; i < 4; ++i) result[i] = p[i];
    return result;
}

array<double>^ Bridge::ProjectPathWireOntoSelectedFaces(array<int>^ faceIds, array<double>^ xyzFlat,
                                                        double dx, double dy, double dz, double insertionDepth)
{
    if (faceIds == nullptr || faceIds->Length == 0 || xyzFlat == nullptr) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    std::vector<double> xyz(xyzFlat->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    double length = 0.0;
    int edgeCount = 0, status = 0, wireCount = 0;
    bool success = false;
    try
    {
        success = ProjectPathWireOntoSelectedFacesNative(ids, xyz, gp_Dir(dx, dy, dz), insertionDepth,
            &length, &edgeCount, &status, &wireCount);
    }
    catch (const Standard_Failure&) { status = 99; }
    auto result = gcnew array<double>(5);
    result[0] = success ? 1.0 : 0.0;
    result[1] = length;
    result[2] = edgeCount;
    result[3] = wireCount;
    result[4] = status;
    return result;
}

RoutePieceResult^ Bridge::ProjectRoutePiece(int pieceId, String^ kind, array<double>^ xyzFlat,
    array<int>^ faceIds, double dx, double dy, double dz, double depth)
{
    if (kind == nullptr || xyzFlat == nullptr || faceIds == nullptr) return nullptr;
    std::vector<double> xyz(xyzFlat->Length);
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[i] = xyzFlat[i];
    for (int i = 0; i < faceIds->Length; ++i) ids[i] = faceIds[i];
    double data[7];
    try { if (!ProjectRoutePieceNative(pieceId, msclr::interop::marshal_as<std::string>(kind), xyz, ids, gp_Dir(dx, dy, dz), depth, data)) return nullptr; }
    catch (const Standard_Failure&) { return nullptr; }
    auto result = gcnew RoutePieceResult();
    result->Length = data[0];
    result->Start = gcnew array<double>(3); result->End = gcnew array<double>(3);
    for (int i = 0; i < 3; ++i) { result->Start[i] = data[i + 1]; result->End[i] = data[i + 4]; }
    return result;
}

bool Bridge::SetConnectorEnabled(int pieceId, bool enabled, double r, double g, double b)
{
    return SetConnectorNative(pieceId, enabled, r, g, b);
}

void Bridge::ClearRoutePieces() { ClearRoutePiecesNative(); if (!g_view.IsNull()) g_view->Redraw(); }
int Bridge::PickConnector(int x, int y) { return PickConnectorNative(x, y); }

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

void Bridge::ZoomViewAtPoint(int x, int y, int deltaY)
{
    ZoomViewAtPointNative(x, y, deltaY);
}

void Bridge::SetShapeColor(double r, double g, double b)
{
    SetShapeColorNative(r, g, b);
}

// ================= 오버레이 managed (S3) =================

bool Bridge::BuildWire3D(int faceIndex, array<double>^ uvFlat, bool closed)
{
    if (uvFlat == nullptr) return false;
    std::vector<double> uv(uvFlat->Length);
    for (int i = 0; i < uvFlat->Length; ++i) uv[i] = uvFlat[i];
    return BuildWire3DNative(faceIndex, uv, closed);
}

bool Bridge::BuildPolyline3D(array<double>^ xyzFlat, bool closed, bool redraw)
{
    if (xyzFlat == nullptr) return false;
    std::vector<double> xyz(xyzFlat->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[i] = xyzFlat[i];
    return BuildPolyline3DNative(xyz, closed, redraw);
}

void Bridge::RedrawView()
{
    if (!g_view.IsNull()) g_view->Redraw();
}

void Bridge::ClearOverlays()
{
    ClearOverlayNative();
}

void Bridge::SetViewDirection(double dx, double dy, double dz)
{
    SetViewDirectionNative(dx, dy, dz);
}

void Bridge::SetViewFrame(double dx, double dy, double dz, double ux, double uy, double uz)
{
    SetViewFrameNative(dx, dy, dz, ux, uy, uz);
}

array<double>^ Bridge::GetAutoAxis(int axis)
{
    double v[3];
    if (!GetAutoAxisNative(axis, v)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = v[0]; r[1] = v[1]; r[2] = v[2];
    return r;
}

array<int>^ Bridge::SelectFacesInRectangle(int x0, int y0, int x1, int y1, bool toggle)
{
    const std::vector<int> ids = SelectFacesRectangleNative(x0, y0, x1, y1, toggle);
    array<int>^ result = gcnew array<int>((int)ids.size());
    for (int i = 0; i < result->Length; ++i) result[i] = ids[(size_t)i];
    return result;
}

void Bridge::SetSelectionRectangle(int x0, int y0, int x1, int y1)
{
    SetSelectionRectangleNative(x0, y0, x1, y1);
}

void Bridge::ClearSelectionRectangle()
{
    ClearSelectionRectangleNative();
}

array<double>^ Bridge::ScreenPointOnPlane(int x, int y, double ox, double oy, double oz, double nx, double ny, double nz)
{
    gp_Vec n(nx, ny, nz);
    if (n.SquareMagnitude() < 1e-18) return nullptr;
    n.Normalize();
    double p[3];
    if (!ScreenPointOnPlaneNative(x, y, gp_Pnt(ox, oy, oz), gp_Dir(n), p)) return nullptr;
    array<double>^ result = gcnew array<double>(3);
    result[0] = p[0]; result[1] = p[1]; result[2] = p[2];
    return result;
}

bool Bridge::AddExclusionOverlay(array<double>^ xyzFlat)
{
    if (xyzFlat == nullptr) return false;
    std::vector<double> xyz(xyzFlat->Length);
    for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    return AddExclusionOverlayNative(xyz);
}

void Bridge::SetExclusionPreview(array<double>^ xyzFlat)
{
    std::vector<double> xyz;
    if (xyzFlat != nullptr)
    {
        xyz.resize(xyzFlat->Length);
        for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    }
    SetExclusionPreviewNative(xyz);
}

void Bridge::ClearExclusionOverlays()
{
    ClearExclusionOverlaysNative();
}

array<double>^ Bridge::GetFaceNormal(int faceIndex)
{
    double n[3];
    if (!FaceNormalNative(faceIndex, n)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = n[0]; r[1] = n[1]; r[2] = n[2];
    return r;
}

bool Bridge::IsFaceSurfacePlanar(int faceIndex, double tolerance)
{
    return FaceSurfaceIsPlanarNative(faceIndex, tolerance);
}

int Bridge::HoverFace(int x, int y)
{
    return HoverFaceNative(x, y);
}

void Bridge::HideFaceHighlights()
{
    HideFaceHighlightsNative();
}

void Bridge::ClearHover()
{
    ClearHoverNative();
}

int Bridge::SelectFace(int x, int y, bool toggle)
{
    return SelectFaceNative(x, y, toggle);
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

array<HlrPolyline^>^ Bridge::HlrEdges(double nx, double ny, double nz,
                                     double xx, double xy, double xz, bool hiddenToo)
{
    const std::vector<NativeHlrLine> lines = RunHlrGeometryNative(nx, ny, nz, xx, xy, xz, hiddenToo);
    List<HlrPolyline^>^ result = gcnew List<HlrPolyline^>();
    for (size_t i = 0; i < lines.size(); ++i)
    {
        HlrPolyline^ poly = gcnew HlrPolyline();
        poly->Visible = lines[i].visible;
        array<double>^ pts = gcnew array<double>((int)lines[i].pts.size());
        for (size_t k = 0; k < lines[i].pts.size(); ++k)
            pts[(int)k] = lines[i].pts[k];
        poly->Points = pts;
        result->Add(poly);
    }
    return result->ToArray();
}

}}
