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
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRep_Builder.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepProj_Projection.hxx>
#include <ShapeAnalysis_FreeBounds.hxx>
#include <TopTools_HSequenceOfShape.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <Geom_Curve.hxx>
#include <GeomAPI_ProjectPointOnCurve.hxx>
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
#include <AIS_Point.hxx>
#include <Geom_CartesianPoint.hxx>
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
#include <Poly_Triangulation.hxx>
#include <Graphic3d_Camera.hxx>
#include <Prs3d_Drawer.hxx>
#include <Prs3d_ShadingAspect.hxx>
#include <Prs3d_PointAspect.hxx>

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
    std::set<std::pair<int,int>> g_faceAdjacency;
    long long g_faceAdjacencyRevision = -1;
    TopTools_IndexedMapOfShape g_faceMap;   // W3 픽킹: g_faces와 같은 순회로 구축 (인덱스 1:1)
    gp_Dir g_autoAxes[3] = { gp::DX(), gp::DY(), gp::DZ() };
    long long g_modelRevision = 0;
    void InvalidateWorkingContexts();

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
    // 확정된 작업영역 Face → 영역별 색상. 여러 영역을 동시에 유지한다.
    std::map<int, Quantity_Color> g_confirmedFaceColors;
    std::vector<Handle(AIS_Shape)> g_worldOverlays;
    std::map<int, Handle(AIS_Shape)> g_powerLeads;
    Handle(AIS_Shape) g_powerLeadPreview;
    Handle(AIS_Point) g_powerDestinationPreview;
    struct RoutePieceOverlay { Handle(AIS_Shape) ais; bool connector; bool enabled; Quantity_Color color; double width; };
    std::map<int, RoutePieceOverlay> g_routePieces;
    struct StagedWorkingPolyline { Handle(AIS_Shape) ais; long long contextId, revision; };
    std::map<int, StagedWorkingPolyline> g_stagedWorkingPolylines;
    std::map<int, std::pair<long long,long long>> g_stagedWorkingRouteOwners;
    // 발열 경로 인출점/끝점 마커. 경로를 지울 때 함께 제거한다.
    std::vector<Handle(AIS_Point)> g_routeEndpointMarkers;
    std::set<int> g_hoveredCircuitPieces;
    void HighlightCircuitNative(const std::vector<int>& ids);
    void ClearRouteEndpointMarkersNative();
    // 꼭지점 편집 오버레이 (WorkingSideNative.inc에서 정의, 이 파일 뒤쪽에 include됨)
    void ClearVertexHandlesNative();
    void ClearRoutePreviewNative();
    std::vector<Handle(AIS_Shape)> g_exclusionOverlays;
    Handle(AIS_Shape) g_exclusionPreview;
    // 인출 측면 후보 강조 (인출면 선택 모드에서만 표시)
    std::vector<Handle(AIS_Shape)> g_exitCandidateOverlays;
    // 후보 기본 표시는 일반 모델 외곽선처럼 보이게 두고, 호버한 후보만 강조한다.
    const Quantity_Color kExitCandidateColor(0.85, 0.85, 0.85, Quantity_TOC_RGB);
    const Quantity_Color kExitCandidateHoverColor(0.10, 0.90, 1.00, Quantity_TOC_RGB);
    const Standard_Real kExitCandidateWidth = 2.0;
    const Standard_Real kExitCandidateHoverWidth = 4.0;
    int g_exitCandidateHighlighted = -1;
    Handle(AIS_RubberBand) g_selectionRubberBand;
    int g_lastHoverFace = -1;      // W5 — 호버 하이라이트 중복 Redraw 억제
    bool g_faceHighlightsVisible = true;
    // Face currently carrying the hover colour override (-1 = none). Kept apart from
    // g_lastHoverFace because a click clears the hover colour while the pointer still
    // stays on the same face.
    int g_hoverColorFace = -1;

    void RebuildFaceCache()
    {
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

    const Quantity_Color kSelectedFaceColor(0.12, 0.82, 0.38, Quantity_TOC_RGB);   // 선택 = 초록
    const Quantity_Color kConfirmedFaceColor(0.90, 0.16, 0.16, Quantity_TOC_RGB);  // 단일 영역 기본값
    const Quantity_Color kHeatingRouteColor(1.00, 0.10, 0.10, Quantity_TOC_RGB);   // 발열 경로 = 두꺼운 빨강
    const Quantity_Color kConnectorRouteColor(0.10, 0.65, 1.00, Quantity_TOC_RGB); // 턴 커넥터 = 파랑 점선
    const Quantity_Color kRouteEndpointColor(1.00, 0.05, 0.05, Quantity_TOC_RGB);  // 인출점/끝점 마커
    const Standard_Real kHeatingRouteWidth = 3.0;

    void SetFaceColorOverride(int faceId, const Quantity_Color& color)
    {
        if (g_aisShape.IsNull() || faceId < 0 || faceId >= static_cast<int>(g_faces.size())) return;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;
        colored->SetCustomColor(g_faces[faceId], color);
        colored->SetCustomTransparency(g_faces[faceId], 0.0);
        Handle(AIS_ColoredDrawer) faceStyle = colored->CustomAspects(g_faces[faceId]);
        faceStyle->SetupOwnShadingAspect();
        faceStyle->SetDisplayMode(AIS_Shaded);
        faceStyle->ShadingAspect()->SetColor(color);
        faceStyle->ShadingAspect()->SetTransparency(0.0);
        // The colored face is coincident with the model face. Bias its fill
        // toward the camera to avoid depth-buffer z-fighting.
        faceStyle->ShadingAspect()->Aspect()->SetPolygonOffsets(Aspect_POM_Fill, -2.0f, -2.0f);
    }

    void ClearFaceColorOverride(int faceId)
    {
        if (g_aisShape.IsNull() || faceId < 0 || faceId >= static_cast<int>(g_faces.size())) return;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;
        colored->UnsetCustomAspects(g_faces[faceId], Standard_True);
    }

    /// <summary>
    /// 선택(초록)과 확정(붉은색)을 함께 반영한다. 확정된 면은 선택이 풀려도
    /// 사용자가 해제할 때까지 붉은색을 유지한다.
    /// </summary>
    void DropHoverFaceColorNative();

    const Quantity_Color* ConfirmedFaceColor(int faceId)
    {
        const auto it = g_confirmedFaceColors.find(faceId);
        return it == g_confirmedFaceColors.end() ? nullptr : &it->second;
    }

    void UpdateSelectedFaceColors()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        g_faceHighlightsVisible = true;
        DropHoverFaceColorNative();
        if (Handle(AIS_ColoredShape)::DownCast(g_aisShape).IsNull()) return;

        std::set<int> current;
        for (g_ctx->InitSelected(); g_ctx->MoreSelected(); g_ctx->NextSelected())
        {
            const Handle(StdSelect_BRepOwner)& owner =
                Handle(StdSelect_BRepOwner)::DownCast(g_ctx->SelectedOwner());
            if (owner.IsNull() || !owner->HasShape() || !g_faceMap.Contains(owner->Shape())) continue;
            current.insert(g_faceMap.FindIndex(owner->Shape()) - 1);
        }
        for (int faceId : g_selectedFaceIds)
            if (current.count(faceId) == 0)
            {
                if (const Quantity_Color* confirmed = ConfirmedFaceColor(faceId)) SetFaceColorOverride(faceId, *confirmed);
                else ClearFaceColorOverride(faceId);
            }
        for (int faceId : current)
            if (g_selectedFaceIds.count(faceId) == 0)
            {
                const Quantity_Color* confirmed = ConfirmedFaceColor(faceId);
                SetFaceColorOverride(faceId, confirmed != nullptr ? *confirmed : kSelectedFaceColor);
            }
        g_selectedFaceIds.swap(current);
        g_ctx->Redisplay(g_aisShape, Standard_False);
    }

    void RedrawFaceHighlights()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void SetConfirmedFaceHighlightColorsNative(const std::map<int, Quantity_Color>& colors)
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        if (Handle(AIS_ColoredShape)::DownCast(g_aisShape).IsNull()) return;
        DropHoverFaceColorNative();

        for (const auto& previous : g_confirmedFaceColors)
            if (colors.count(previous.first) == 0)
            {
                if (g_selectedFaceIds.count(previous.first) != 0) SetFaceColorOverride(previous.first, kSelectedFaceColor);
                else ClearFaceColorOverride(previous.first);
            }
        for (const auto& entry : colors)
            SetFaceColorOverride(entry.first, entry.second);
        g_confirmedFaceColors = colors;
        if (!colors.empty()) g_faceHighlightsVisible = true;
        g_ctx->Redisplay(g_aisShape, Standard_False);
        RedrawFaceHighlights();
    }

    void SetConfirmedFaceHighlightsNative(const std::set<int>& faceIds)
    {
        std::map<int, Quantity_Color> colors;
        for (int faceId : faceIds) colors[faceId] = kConfirmedFaceColor;
        SetConfirmedFaceHighlightColorsNative(colors);
    }

    void ClearConfirmedFaceHighlightsNative()
    {
        SetConfirmedFaceHighlightColorsNative(std::map<int, Quantity_Color>());
    }

    void HideFaceHighlightsNative()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;
        // Remove presentation overrides, not selected owners: Ctrl selection and
        // pattern regeneration must retain the current selection.
        for (int faceId : g_selectedFaceIds)
            if (faceId >= 0 && faceId < static_cast<int>(g_faces.size()) && g_confirmedFaceColors.count(faceId) == 0)
                colored->UnsetCustomAspects(g_faces[faceId], Standard_True);
        if (g_hoverColorFace >= 0 && g_hoverColorFace < static_cast<int>(g_faces.size()) &&
            g_confirmedFaceColors.count(g_hoverColorFace) == 0)
            colored->UnsetCustomAspects(g_faces[g_hoverColorFace], Standard_True);
        g_hoverColorFace = -1;
        g_selectedFaceIds.clear();
        g_confirmedFaceColors.clear();
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
        g_confirmedFaceColors.clear();
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
        g_confirmedFaceColors.clear();
        g_lastHoverFace = -1;
        g_hoverColorFace = -1;
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

    // ---- 인출 측면 후보: 외곽 경계 Edge의 반대편 인접면 ----
    // 연속 경계 Edge가 같은 원본 면을 공유하면 하나의 run(인출 측면)이 된다.

    /// <summary>run 안의 구간 하나가 시작하는 점 인덱스(0-based)와 그 구간의 반대편 면.</summary>
    struct NativeExitSegmentStart
    {
        int faceId = -1;
        int startIndex = 0;
    };

    struct NativeExitSide
    {
        int faceId = -1;
        std::vector<double> xyz;
        std::vector<NativeExitSegmentStart> segmentStarts; // 점 N개 → 구간 N-1개
    };

    double EdgeLengthNative(const TopoDS_Edge& edge)
    {
        if (edge.IsNull()) return 0.0;
        GProp_GProps props;
        BRepGProp::LinearProperties(edge, props);
        const double length = props.Mass();
        return std::isfinite(length) ? length : 0.0;
    }

    /// <summary>
    /// Sewing 사본의 경계 Edge와 기하학적으로 일치하는 원본(비선택) 면을 찾는다.
    /// 토폴로지 동일성(IsSame)을 우선하고, 없으면 길이 + 3점 투영 거리로 판정한다.
    /// 일치하는 면이 없으면 -1 (모델 끝의 나체 Edge).
    /// </summary>
    int FindAdjacentOriginalFace(const TopoDS_Edge& boundaryEdge, const std::set<int>& selected,
                                 double tolerance)
    {
        if (boundaryEdge.IsNull()) return -1;
        const double targetLength = EdgeLengthNative(boundaryEdge);
        if (targetLength <= 0.0) return -1;
        double first = 0.0, last = 0.0;
        Handle(Geom_Curve) boundaryCurve;
        try { boundaryCurve = BRep_Tool::Curve(boundaryEdge, first, last); }
        catch (const Standard_Failure&) { return -1; }
        if (boundaryCurve.IsNull() || !std::isfinite(first) || !std::isfinite(last) || last <= first)
            return -1;

        int bestFace = -1;
        double bestDistance = tolerance;
        for (int faceId = 0; faceId < static_cast<int>(g_faces.size()); ++faceId)
        {
            if (selected.count(faceId) != 0) continue;
            for (TopExp_Explorer ex(g_faces[faceId], TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge candidate = TopoDS::Edge(ex.Current());
                if (candidate.IsNull()) continue;
                // Sewing 사본이 원본 Edge를 그대로 재사용한 경우: 위상 동일성으로 즉시 확정.
                if (candidate.IsSame(boundaryEdge)) return faceId;
                const double candidateLength = EdgeLengthNative(candidate);
                if (candidateLength <= 0.0 || std::fabs(candidateLength - targetLength) > tolerance) continue;
                double cf = 0.0, cl = 0.0;
                Handle(Geom_Curve) candidateCurve;
                try { candidateCurve = BRep_Tool::Curve(candidate, cf, cl); }
                catch (const Standard_Failure&) { continue; }
                if (candidateCurve.IsNull() || !std::isfinite(cf) || !std::isfinite(cl) || cl <= cf) continue;

                double maxDistance = 0.0;
                bool matched = true;
                for (int k = 1; k <= 3; ++k)
                {
                    const gp_Pnt sample = boundaryCurve->Value(first + (last - first) * (k / 4.0));
                    GeomAPI_ProjectPointOnCurve projector(sample, candidateCurve, cf, cl);
                    if (projector.NbPoints() == 0) { matched = false; break; }
                    maxDistance = std::max(maxDistance, projector.LowerDistance());
                }
                if (matched && maxDistance <= tolerance && maxDistance < bestDistance)
                {
                    bestDistance = maxDistance;
                    bestFace = faceId;
                }
            }
        }
        return bestFace;
    }

    /// <summary>run에 점을 추가한다. 좌표는 중복 제거해 xyz에, 면 id는 구간 시작 인덱스로 기록한다.</summary>
    void AppendExitPoint(NativeExitSide& run, int faceId, const gp_Pnt& point)
    {
        bool duplicate = false;
        if (run.xyz.size() >= 3)
        {
            const size_t n = run.xyz.size();
            const gp_Pnt previous(run.xyz[n - 3], run.xyz[n - 2], run.xyz[n - 1]);
            duplicate = previous.Distance(point) <= Precision::Confusion();
        }
        if (!duplicate)
        {
            run.xyz.push_back(point.X());
            run.xyz.push_back(point.Y());
            run.xyz.push_back(point.Z());
        }
        if (run.segmentStarts.empty() || run.segmentStarts.back().faceId != faceId)
        {
            NativeExitSegmentStart start;
            start.faceId = faceId;
            start.startIndex = static_cast<int>(run.xyz.size() / 3) - 1;
            run.segmentStarts.push_back(start);
        }
    }

    /// <summary>source의 점/구간 정보를 target 끝에 이어붙인다(접촉점 중복 제거, 같은 면 구간 병합).</summary>
    void AppendExitRun(NativeExitSide& target, const NativeExitSide& source)
    {
        const int sourcePoints = static_cast<int>(source.xyz.size() / 3);
        for (size_t s = 0; s < source.segmentStarts.size(); ++s)
        {
            const int faceId = source.segmentStarts[s].faceId;
            const int from = source.segmentStarts[s].startIndex;
            const int to = (s + 1 < source.segmentStarts.size())
                ? source.segmentStarts[s + 1].startIndex
                : sourcePoints - 1;
            for (int i = from; i <= to; ++i)
                AppendExitPoint(target, faceId,
                    gp_Pnt(source.xyz[i * 3], source.xyz[i * 3 + 1], source.xyz[i * 3 + 2]));
        }
    }

    /// <summary>run의 점/구간 순서를 역전한 사본을 만든다.</summary>
    NativeExitSide ReverseExitRun(const NativeExitSide& run)
    {
        NativeExitSide reversed;
        reversed.faceId = run.faceId;
        const int points = static_cast<int>(run.xyz.size() / 3);
        reversed.xyz.resize(run.xyz.size());
        for (int i = 0; i < points; ++i)
        {
            const int source = points - 1 - i;
            reversed.xyz[i * 3] = run.xyz[source * 3];
            reversed.xyz[i * 3 + 1] = run.xyz[source * 3 + 1];
            reversed.xyz[i * 3 + 2] = run.xyz[source * 3 + 2];
        }
        const int segments = static_cast<int>(run.segmentStarts.size());
        for (int s = segments - 1; s >= 0; --s)
        {
            const int end = (s + 1 < segments) ? run.segmentStarts[s + 1].startIndex : points - 1;
            NativeExitSegmentStart start;
            start.faceId = run.segmentStarts[s].faceId;
            start.startIndex = points - 1 - end;
            reversed.segmentStarts.push_back(start);
        }
        return reversed;
    }

    /// <summary>run의 진행 방향(시작쪽 첫 유효 구간)을 반환한다.</summary>
    bool ExitFirstSegmentDirection(const NativeExitSide& run, gp_Vec& direction)
    {
        const int count = static_cast<int>(run.xyz.size() / 3);
        if (count < 2) return false;
        const gp_Pnt origin(run.xyz[0], run.xyz[1], run.xyz[2]);
        for (int i = 1; i < count; ++i)
        {
            const gp_Pnt point(run.xyz[i * 3], run.xyz[i * 3 + 1], run.xyz[i * 3 + 2]);
            if (origin.Distance(point) > 1e-9)
            {
                direction = gp_Vec(origin, point);
                return true;
            }
        }
        return false;
    }

    /// <summary>run의 진행 방향(끝쪽 마지막 유효 구간)을 반환한다.</summary>
    bool ExitLastSegmentDirection(const NativeExitSide& run, gp_Vec& direction)
    {
        const int count = static_cast<int>(run.xyz.size() / 3);
        if (count < 2) return false;
        const gp_Pnt end(run.xyz[(count - 1) * 3], run.xyz[(count - 1) * 3 + 1], run.xyz[(count - 1) * 3 + 2]);
        for (int i = count - 2; i >= 0; --i)
        {
            const gp_Pnt point(run.xyz[i * 3], run.xyz[i * 3 + 1], run.xyz[i * 3 + 2]);
            if (point.Distance(end) > 1e-9)
            {
                direction = gp_Vec(point, end);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 와이어 순서상 연속된 두 run이 같은 인출 측면의 연속인지 판정한다.
    /// 접합점이 정확히 일치하고, 접합부의 국소 진행 방향 변화가 작아야 한다.
    /// STEP 모델은 같은 측면이 여러 인접면(슬리버 포함)으로 쪼개져 Edge가 끊기고 원본 커브도
    /// BSpline으로 근사되므로 완전 공선을 요구하지 않고 꺾임 각도로 판정한다.
    /// 모서리(코너)와 되돌이는 병합하지 않는다.
    /// </summary>
    bool CanJoinExitRuns(const NativeExitSide& first, const NativeExitSide& second, bool& flipped)
    {
        flipped = false;
        if (first.xyz.size() < 6 || second.xyz.size() < 6) return false;
        const double contactTolerance = 0.01;
        const double angleToleranceDegrees = 8.0;

        const size_t firstCount = first.xyz.size();
        const gp_Pnt firstEnd(first.xyz[firstCount - 3], first.xyz[firstCount - 2], first.xyz[firstCount - 1]);
        const gp_Pnt secondStart(second.xyz[0], second.xyz[1], second.xyz[2]);
        const size_t secondCount = second.xyz.size();
        const gp_Pnt secondEnd(second.xyz[secondCount - 3], second.xyz[secondCount - 2],
                               second.xyz[secondCount - 1]);
        const bool forward = firstEnd.Distance(secondStart) <= contactTolerance;
        const bool backward = firstEnd.Distance(secondEnd) <= contactTolerance;
        if (!forward && !backward) return false;
        flipped = backward && !forward;

        gp_Vec directionFirst, directionSecond;
        if (!ExitLastSegmentDirection(first, directionFirst)) return false;
        if (!ExitFirstSegmentDirection(second, directionSecond)) return false;
        if (flipped) directionSecond.Reverse();
        const double lengthFirst = directionFirst.Magnitude();
        const double lengthSecond = directionSecond.Magnitude();
        if (lengthFirst <= 1e-9 || lengthSecond <= 1e-9) return false;
        const gp_Vec unitFirst = directionFirst / lengthFirst;
        const gp_Vec unitSecond = directionSecond / lengthSecond;
        const double limit = std::cos(angleToleranceDegrees * 3.141592653589793 / 180.0);
        return (unitFirst * unitSecond) >= limit;
    }

    /// <summary>
    /// 확정 영역 외곽 경계를 따라 인출 측면 후보(run)를 만든다.
    /// 영역 경계 추출과 동일한 0.01mm Sewing 공차/경계 분류를 사용한다.
    /// </summary>
    bool ExtractBoundaryExitSides(const std::vector<int>& faceIds, double deflection,
                                  std::vector<NativeExitSide>& sides)
    {
        sides.clear();
        if (faceIds.empty() || !std::isfinite(deflection) || deflection <= 0.0) return false;
        std::set<int> selected;
        for (int id : faceIds)
        {
            if (id < 0 || id >= static_cast<int>(g_faces.size())) return false;
            selected.insert(id);
        }

        try
        {
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

            std::vector<int> boundaryIndices; // edgeMap의 1-based 인덱스
            for (int count : uses) if (count > 2) return false; // 비다양체 선택 거부
            for (int i = 1; i <= edgeMap.Extent(); ++i)
                if (uses[i - 1] == 1) boundaryIndices.push_back(i);
            if (boundaryIndices.empty()) return false;
            std::vector<TopoDS_Edge> boundaryEdges;
            for (int index : boundaryIndices) boundaryEdges.push_back(TopoDS::Edge(edgeMap(index)));

            // 각 경계 Edge 반대편의 원본 면 (-1 = 나체 Edge). 봉합선(use==2)이 인덱스를
            // 밀지 않도록 edgeMap 인덱스로 직접 보관한다.
            std::vector<int> adjacent(edgeMap.Extent(), -1);
            for (size_t i = 0; i < boundaryEdges.size(); ++i)
                adjacent[boundaryIndices[i] - 1] =
                    FindAdjacentOriginalFace(boundaryEdges[i], selected, sewingTolerance);

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
                addVertexEdge(last, i);
            }
            for (const auto& edgesAtVertex : incident)
                if (edgesAtVertex.size() != 2) return false; // 분기/접촉 루프 거부

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
                        for (int next : incident[vi - 1])
                            if (!seen[next]) { seen[next] = true; stack.push_back(next); }
                    }
                }

                BRepBuilderAPI_MakeWire wireBuilder;
                for (int edgeIndex : component) wireBuilder.Add(boundaryEdges[edgeIndex]);
                if (!wireBuilder.IsDone()) continue;
                const TopoDS_Wire wire = wireBuilder.Wire();
                if (!BRep_Tool::IsClosed(wire)) continue;
                size_t orderedCount = 0;
                for (BRepTools_WireExplorer check(wire); check.More(); check.Next()) ++orderedCount;
                if (orderedCount != component.size()) continue;

                // 와이어 순서로 같은 인접면의 연속 Edge를 하나의 run으로 누적한다.
                std::vector<NativeExitSide> runs;
                NativeExitSide current;
                for (BRepTools_WireExplorer we(wire); we.More(); we.Next())
                {
                    const TopoDS_Edge edge = we.Current();
                    const int mapIndex = edgeMap.FindIndex(edge);
                    const int faceId = (mapIndex > 0 && mapIndex <= static_cast<int>(adjacent.size()))
                        ? adjacent[mapIndex - 1] : -1;

                    std::vector<gp_Pnt> points;
                    bool sampled = false;
                    try
                    {
                        BRepAdaptor_Curve curve(edge);
                        GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                        if (sampler.IsDone() && sampler.NbPoints() >= 2 && sampler.NbPoints() <= 100000)
                        {
                            sampled = true;
                            const bool reverse = edge.Orientation() == TopAbs_REVERSED;
                            const int count = sampler.NbPoints();
                            for (int k = 1; k <= count; ++k)
                            {
                                const gp_Pnt p = sampler.Value(reverse ? count - k + 1 : k);
                                if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z()))
                                { sampled = false; break; }
                                points.push_back(p);
                            }
                        }
                    }
                    catch (const Standard_Failure&) { sampled = false; }

                    if (faceId < 0 || !sampled)
                    {
                        // 반대편 면을 찾지 못한 구간(나체 Edge)은 후보 run을 끊는다.
                        if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));
                        current = NativeExitSide();
                        continue;
                    }
                    if (current.faceId != faceId)
                    {
                        if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));
                        current = NativeExitSide();
                        current.faceId = faceId;
                    }
                    for (const gp_Pnt& p : points) AppendExitPoint(current, faceId, p);
                }
                if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));

                // 닫힌 루프: 마지막 run과 첫 run이 같은 면이면 하나로 합친다.
                if (runs.size() >= 2 && runs.front().faceId == runs.back().faceId)
                {
                    const NativeExitSide head = runs.front();
                    AppendExitRun(runs.back(), head);
                    runs.erase(runs.begin());
                }

                // 같은 루프 안에서 와이어 순서상 연속된 run을 하나의 인출 측면으로 병합한다.
                // (STEP에서 반대편 면이 달라 중간에 끊긴 긴 변 복원. 구간별 면 id는 segmentStarts에 보존.)
                {
                    std::vector<NativeExitSide> joined;
                    for (auto& run : runs)
                    {
                        bool flipped = false;
                        if (!joined.empty() && CanJoinExitRuns(joined.back(), run, flipped))
                        {
                            const NativeExitSide tail = flipped ? ReverseExitRun(run) : run;
                            AppendExitRun(joined.back(), tail);
                        }
                        else
                        {
                            joined.push_back(std::move(run));
                        }
                    }
                    if (joined.size() >= 2)
                    {
                        bool flipped = false;
                        if (CanJoinExitRuns(joined.back(), joined.front(), flipped))
                        {
                            const NativeExitSide head = flipped ? ReverseExitRun(joined.front()) : joined.front();
                            AppendExitRun(joined.back(), head);
                            joined.erase(joined.begin());
                        }
                    }
                    runs.swap(joined);
                }
                for (auto& run : runs) sides.push_back(std::move(run));
            }
            return !sides.empty();
        }
        catch (const Standard_Failure&) { sides.clear(); return false; }
    }

    // ---- 인출 측면 후보 표시/픽 ----

    void ClearExitCandidatesNative()
    {
        if (!g_ctx.IsNull())
            for (const auto& overlay : g_exitCandidateOverlays)
                if (!overlay.IsNull()) g_ctx->Remove(overlay, Standard_False);
        g_exitCandidateOverlays.clear();
        g_exitCandidateHighlighted = -1;
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ShowExitCandidatesNative(const std::vector<std::vector<double>>& polylines)
    {
        ClearExitCandidatesNative();
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        try
        {
            for (const auto& pts : polylines)
            {
                // Keep one slot per candidate so hover indexes stay aligned with the caller's list.
                if (pts.size() < 6 || pts.size() % 3 != 0)
                {
                    g_exitCandidateOverlays.push_back(Handle(AIS_Shape)());
                    continue;
                }
                BRepBuilderAPI_MakePolygon polygon;
                bool valid = true;
                for (size_t i = 0; i + 2 < pts.size(); i += 3)
                {
                    if (!std::isfinite(pts[i]) || !std::isfinite(pts[i + 1]) || !std::isfinite(pts[i + 2]))
                    { valid = false; break; }
                    polygon.Add(gp_Pnt(pts[i], pts[i + 1], pts[i + 2]));
                }
                if (!valid || !polygon.IsDone())
                {
                    g_exitCandidateOverlays.push_back(Handle(AIS_Shape)());
                    continue;
                }
                Handle(AIS_Shape) overlay = new AIS_Shape(polygon.Wire());
                g_ctx->SetColor(overlay, kExitCandidateColor, Standard_False);
                g_ctx->SetWidth(overlay, kExitCandidateWidth, Standard_False);
                g_ctx->Display(overlay, Standard_False);
                g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(overlay, Standard_False);
                g_exitCandidateOverlays.push_back(overlay);
            }
            g_exitCandidateHighlighted = -1;
            g_view->Redraw();
        }
        catch (const Standard_Failure&) { }
    }

    /// <summary>호버한 후보만 강조하고 나머지는 기본 표시로 되돌린다.</summary>
    void HighlightExitCandidateNative(int index)
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        if (index < 0 || index >= static_cast<int>(g_exitCandidateOverlays.size())) index = -1;
        if (index == g_exitCandidateHighlighted) return;
        g_exitCandidateHighlighted = index;
        for (size_t i = 0; i < g_exitCandidateOverlays.size(); ++i)
        {
            if (g_exitCandidateOverlays[i].IsNull()) continue;
            const bool hovered = static_cast<int>(i) == index;
            g_ctx->SetColor(g_exitCandidateOverlays[i],
                            hovered ? kExitCandidateHoverColor : kExitCandidateColor, Standard_False);
            g_ctx->SetWidth(g_exitCandidateOverlays[i],
                            hovered ? kExitCandidateHoverWidth : kExitCandidateWidth, Standard_False);
        }
        g_view->Redraw();
    }

    double ScreenDistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        const double dx = bx - ax, dy = by - ay;
        const double lengthSquared = dx * dx + dy * dy;
        double t = 0.0;
        if (lengthSquared > 1e-12) t = ((px - ax) * dx + (py - ay) * dy) / lengthSquared;
        t = std::max(0.0, std::min(1.0, t));
        const double qx = ax + t * dx - px, qy = ay + t * dy - py;
        return std::sqrt(qx * qx + qy * qy);
    }

    int PickExitCandidateNative(const std::vector<std::vector<double>>& polylines, int x, int y,
                                double pixelTolerance)
    {
        if (g_view.IsNull() || polylines.empty()) return -1;
        if (!std::isfinite(pixelTolerance) || pixelTolerance <= 0.0) pixelTolerance = 12.0;
        int bestIndex = -1;
        double bestDistance = pixelTolerance;
        try
        {
            for (size_t i = 0; i < polylines.size(); ++i)
            {
                const std::vector<double>& pts = polylines[i];
                if (pts.size() < 6) continue;
                std::vector<std::pair<double, double>> screen;
                screen.reserve(pts.size() / 3);
                bool valid = true;
                for (size_t k = 0; k + 2 < pts.size(); k += 3)
                {
                    if (!std::isfinite(pts[k]) || !std::isfinite(pts[k + 1]) || !std::isfinite(pts[k + 2]))
                    { valid = false; break; }
                    Standard_Integer sx = 0, sy = 0;
                    g_view->Convert(pts[k], pts[k + 1], pts[k + 2], sx, sy);
                    screen.emplace_back(static_cast<double>(sx), static_cast<double>(sy));
                }
                if (!valid || screen.size() < 2) continue;
                for (size_t k = 1; k < screen.size(); ++k)
                {
                    const double d = ScreenDistanceToSegment(x, y, screen[k - 1].first, screen[k - 1].second,
                                                             screen[k].first, screen[k].second);
                    if (d < bestDistance) { bestDistance = d; bestIndex = static_cast<int>(i); }
                }
            }
        }
        catch (const Standard_Failure&) { return -1; }
        return bestIndex;
    }

    bool SamplePoint(int faceIndex, double u, double v, double* out)
    {        if (faceIndex < 0 || faceIndex >= (int)g_faces.size()) return false;
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
            if (first.IsNull() || last.IsNull() || !std::isfinite(props.Mass()) || props.Mass() <= Precision::Confusion() ||
                !BRepCheck_Analyzer(wires.front()).IsValid()) return false;
            size_t orderedCount = 0, edgeCount = 0;
            for (BRepTools_WireExplorer ex(wires.front()); ex.More(); ex.Next()) ++orderedCount;
            for (TopExp_Explorer ex(wires.front(), TopAbs_EDGE); ex.More(); ex.Next()) ++edgeCount;
            if (orderedCount != edgeCount) return false;
            gp_Pnt a = BRep_Tool::Pnt(first), b = BRep_Tool::Pnt(last);
            const gp_Pnt sourceA(xyz[0], xyz[1], xyz[2]);
            const gp_Pnt sourceB(xyz[xyz.size()-3], xyz[xyz.size()-2], xyz[xyz.size()-1]);
            auto transverseDistance = [&direction](const gp_Pnt& p, const gp_Pnt& q) {
                gp_Vec v(p, q); return (v - gp_Vec(direction) * v.Dot(gp_Vec(direction))).Magnitude();
            };
            // OCCT may reverse the resulting wire. Results must follow the source route order.
            if (transverseDistance(a, sourceA) > 0.01 || transverseDistance(b, sourceB) > 0.01) std::swap(a, b);
            if (transverseDistance(a, sourceA) > 0.01 || transverseDistance(b, sourceB) > 0.01) return false;
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
                const Quantity_Color color = connector ? kConnectorRouteColor : kHeatingRouteColor;
                const double width = connector ? 1.0 : kHeatingRouteWidth;
                g_ctx->SetColor(ais, color, Standard_False);
                g_ctx->SetWidth(ais, width, Standard_False);
                if (connector) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
                g_ctx->Display(ais, Standard_False); g_ctx->SetZLayer(ais, Graphic3d_ZLayerId_Topmost);
                // Every run and connector remains selectable for circuit hover.
                g_ctx->Activate(ais, Standard_False);
                g_routePieces[pieceId] = { ais, connector, true, color, width };
            }
            return true;
        } catch (const Standard_Failure&) { return false; }
    }

    bool SetConnectorNative(int id, bool enabled, double r, double g, double b)
    {
        auto it = g_routePieces.find(id);
        if (it == g_routePieces.end() || !it->second.connector || g_ctx.IsNull()) return false;
        it->second.enabled = enabled;
        it->second.color = Quantity_Color(r, g, b, Quantity_TOC_RGB);
        g_ctx->SetColor(it->second.ais, it->second.color, Standard_False);
        it->second.ais->Attributes()->LineAspect()->SetTypeOfLine(enabled ? Aspect_TOL_SOLID : Aspect_TOL_DASH);
        g_ctx->Redisplay(it->second.ais, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void ClearRouteEndpointMarkersNative()
    {
        if (!g_ctx.IsNull())
            for (const auto& marker : g_routeEndpointMarkers) g_ctx->Remove(marker, Standard_False);
        g_routeEndpointMarkers.clear();
    }

    // 인출점/끝점을 눈에 띄는 원형 점으로 표시한다. xyz는 [x,y,z,...] 평면 배열.
    void SetRouteEndpointsNative(const std::vector<double>& xyz)
    {
        ClearRouteEndpointMarkersNative();
        if (g_ctx.IsNull() || xyz.empty() || xyz.size() % 3 != 0) return;
        for (size_t i = 0; i < xyz.size(); i += 3) {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i + 1]) || !std::isfinite(xyz[i + 2])) continue;
            Handle(AIS_Point) marker = new AIS_Point(new Geom_CartesianPoint(xyz[i], xyz[i + 1], xyz[i + 2]));
            marker->SetMarker(Aspect_TOM_O_POINT);
            marker->SetColor(kRouteEndpointColor);
            Handle(Prs3d_PointAspect) pointAspect = marker->Attributes()->PointAspect();
            if (pointAspect.IsNull()) {
                pointAspect = new Prs3d_PointAspect(Aspect_TOM_O_POINT, kRouteEndpointColor, 6.0);
                marker->Attributes()->SetPointAspect(pointAspect);
            } else {
                pointAspect->SetScale(6.0);
            }
            g_ctx->Display(marker, Standard_False);
            g_ctx->SetZLayer(marker, Graphic3d_ZLayerId_Topmost);
            g_ctx->Deactivate(marker, Standard_False);
            g_routeEndpointMarkers.push_back(marker);
        }
    }

    void ClearRoutePiecesNative()
    {
        HighlightCircuitNative(std::vector<int>());
        ClearRouteEndpointMarkersNative();
        ClearVertexHandlesNative();
        ClearRoutePreviewNative();
        if (!g_ctx.IsNull()) for (const auto& piece : g_routePieces) g_ctx->Remove(piece.second.ais, Standard_False);
        g_routePieces.clear();
        g_stagedWorkingRouteOwners.clear();
    }

    void ClearPowerLeadPreviewNative()
    {
        if (!g_ctx.IsNull()) {
            if (!g_powerLeadPreview.IsNull()) g_ctx->Remove(g_powerLeadPreview, Standard_False);
            if (!g_powerDestinationPreview.IsNull()) g_ctx->Remove(g_powerDestinationPreview, Standard_False);
        }
        g_powerLeadPreview.Nullify();
        g_powerDestinationPreview.Nullify();
    }

    void ClearPowerLeadsNative()
    {
        ClearPowerLeadPreviewNative();
        if (!g_ctx.IsNull()) for (const auto& lead : g_powerLeads) g_ctx->Remove(lead.second, Standard_False);
        g_powerLeads.clear();
    }

    // Validate the complete external segment, not just its destination. Projecting
    // onto the WHOLE mold catches even elevated/unselected faces and concave re-entry.
    bool ExternalLeadIsClear(const gp_Pnt& from, const gp_Pnt& to, const gp_Dir& direction)
    {
        const double tolerance = 1e-7;
        const gp_Vec axis(direction);
        gp_Vec along(from, to); along -= axis * along.Dot(axis);
        const double span = along.Magnitude();
        if (span <= tolerance) return false;
        along /= span;
        BRepBuilderAPI_MakePolygon source; source.Add(from); source.Add(to);
        if (!source.IsDone()) return false;
        // Keep faces independent: a tolerant imported neighbor must not merge
        // away a narrow obstruction in a whole-shape projection operation.
        for (const TopoDS_Face& face : g_faces) {
            BRepProj_Projection footprint(source.Wire(), face, direction);
            for (footprint.Init(); footprint.More(); footprint.Next()) {
                for (TopExp_Explorer ex(footprint.Current(), TopAbs_EDGE); ex.More(); ex.Next()) {
                    BRepAdaptor_Curve curve(TopoDS::Edge(ex.Current()));
                    GCPnts_QuasiUniformDeflection sample(curve, 0.01);
                    if (!sample.IsDone() || sample.NbPoints() < 2 || sample.NbPoints() > 100000) return false;
                    for (int k = 1; k <= sample.NbPoints(); ++k) {
                        const double t = gp_Vec(from, sample.Value(k)).Dot(along);
                        // Only the exit point itself may touch the mold footprint.
                        if (!std::isfinite(t) || t > tolerance) return false;
                    }
                }
            }
        }
        // Also reject direct material intersections (including axis-parallel faces).
        const gp_Vec segment(from, to);
        IntCurvesFace_ShapeIntersector material;
        material.Load(g_shape, tolerance);
        material.Perform(gp_Lin(from, gp_Dir(segment)), tolerance, segment.Magnitude());
        return material.IsDone() && material.NbPnt() == 0;
    }

    bool IsSelectedBoundaryPoint(const gp_Pnt& point, const std::vector<int>& faces, double& tolerance)
    {
        // Use model-space trim-edge distance, not a fixed UV classifier epsilon
        // (UV units need not be mm). Imported edge/vertex tolerances may exceed
        // the face tolerance; never exceed the route's 0.01 mm sewing budget.
        const TopoDS_Vertex query = BRepBuilderAPI_MakeVertex(point).Vertex();
        bool matched = false;
        for (int id : faces) {
            const TopoDS_Face& face = g_faces[id];
            for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next()) {
                const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                double cadTolerance = std::max(Precision::Confusion(),
                    std::max(BRep_Tool::Tolerance(face), BRep_Tool::Tolerance(edge)));
                for (TopExp_Explorer vertex(edge, TopAbs_VERTEX); vertex.More(); vertex.Next())
                    cadTolerance = std::max(cadTolerance, BRep_Tool::Tolerance(TopoDS::Vertex(vertex.Current())));
                const double acceptedTolerance = std::min(0.01, cadTolerance);
                BRepExtrema_DistShapeShape distance(query, edge);
                if (distance.IsDone() && distance.Value() <= acceptedTolerance) {
                    tolerance = std::max(tolerance, acceptedTolerance);
                    matched = true;
                }
            }
        }
        return matched;
    }

    // No sampled/floating fallback: retain the exact OCCT surface edges and add only
    // the explicit external segment after the trimmed heating-face boundary.
    void ProjectPowerLeadNative(int id, const std::vector<double>& xyz, const std::vector<double>& actual,
        const std::vector<int>& faces, const gp_Dir& direction, double depth, double* result, bool previewOnly)
    {
        result[0] = -1;
        if (g_shape.IsNull() || xyz.size() != 6 || actual.size() != 3 || faces.empty() ||
            !std::isfinite(depth) || depth < 0) return;
        for (double v : xyz) if (!std::isfinite(v)) return;
        for (double v : actual) if (!std::isfinite(v)) return;
        const gp_Pnt start(xyz[0], xyz[1], xyz[2]), destination(xyz[3], xyz[4], xyz[5]);
        const gp_Pnt expected(actual[0], actual[1], actual[2]);
        const gp_Vec axis(direction), offset = axis * depth;
        gp_Vec along(start, destination); along -= axis * along.Dot(axis);
        const double span = along.Magnitude();
        if (span < 0.01) return;
        along /= span;
        BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
        std::set<int> unique;
        for (int face : faces) {
            if (face < 0 || face >= static_cast<int>(g_faces.size())) return;
            if (unique.insert(face).second) sewing.Add(g_faces[face]);
        }
        sewing.Perform();
        if (sewing.SewedShape().IsNull()) return;
        const gp_Pnt end = destination.Translated(offset);
        gp_Pnt b = expected;
        TopoDS_Wire surfaceWire;
        // A verified boundary endpoint directed immediately outside needs no
        // projected edge. Inward directions fail this clearance test and still
        // take the normal surface-projection path below.
        const gp_Vec startDelta(start, expected);
        double boundaryTolerance = Precision::Confusion();
        const bool outwardBoundary = IsSelectedBoundaryPoint(expected.Translated(-offset), faces, boundaryTolerance) &&
            (startDelta - axis * startDelta.Dot(axis)).Magnitude() <= boundaryTolerance &&
            ExternalLeadIsClear(expected, end, direction);
        if (!outwardBoundary) {
            // Plane destinations must remain outside the entire mold. Boundary
            // destinations are handled as surface endpoints, not axis-line hits.
            IntCurvesFace_ShapeIntersector outside;
            outside.Load(g_shape, 1e-7);
            outside.Perform(gp_Lin(destination, direction), -1e100, 1e100);
            result[0] = -2;
            if (!outside.IsDone() || outside.NbPnt() != 0) return;
            BRepBuilderAPI_MakePolygon source; source.Add(start); source.Add(destination);
            if (!source.IsDone()) return;
            BRepProj_Projection projection(source.Wire(), sewing.SewedShape(), direction);
            std::vector<TopoDS_Wire> wires;
            for (projection.Init(); projection.More(); projection.Next()) wires.push_back(projection.Current());
            result[0] = -3;
            // Multiple wires indicate a gap, re-entry or multiple surface layers.
            if (wires.size() != 1 || !BRepCheck_Analyzer(wires[0]).IsValid()) return;
            TopoDS_Vertex first, last;
            size_t ordered = 0, edges = 0;
            gp_Pnt previous;
            for (BRepTools_WireExplorer ex(wires[0]); ex.More(); ex.Next()) {
                TopoDS_Vertex a, b; TopExp::Vertices(ex.Current(), a, b, Standard_True);
                if (a.IsNull() || b.IsNull()) return;
                if (ordered && previous.Distance(BRep_Tool::Pnt(a)) > 0.01) return;
                if (!ordered) first = a;
                last = b; previous = BRep_Tool::Pnt(b); ++ordered;
            }
            for (TopExp_Explorer ex(wires[0], TopAbs_EDGE); ex.More(); ex.Next()) ++edges;
            if (!ordered || ordered != edges) return;
            gp_Pnt a = BRep_Tool::Pnt(first).Translated(offset); b = BRep_Tool::Pnt(last).Translated(offset);
            if (b.Distance(expected) < a.Distance(expected)) { std::swap(a, b); wires[0].Reverse(); }
            result[0] = -4;
            if (a.Distance(expected) > 0.01) return;
            const double exitParameter = gp_Vec(start, b).Dot(along);
            if (exitParameter <= 1e-7 || exitParameter >= span - 1e-7) return;
            // Check every curve sample stays on the projected source segment. This
            // also rejects reversed/branching excursions instead of bridging them.
            double previousParameter = -0.01;
            for (BRepTools_WireExplorer ex(wires[0]); ex.More(); ex.Next()) {
                BRepAdaptor_Curve curve(ex.Current());
                GCPnts_QuasiUniformDeflection sample(curve, 0.01);
                if (!sample.IsDone() || sample.NbPoints() > 100000) return;
                for (int k = 1; k <= sample.NbPoints(); ++k) {
                    gp_Pnt p = sample.Value(ex.Current().Orientation() == TopAbs_REVERSED ? sample.NbPoints()-k+1 : k);
                    gp_Vec v(start, p); const double t = v.Dot(along);
                    const gp_Vec residual = v - along*t - axis*v.Dot(axis);
                    if (residual.Magnitude() > 0.01 || t < previousParameter-0.01 || t > span+0.01) return;
                    previousParameter = t;
                }
            }
            gp_Trsf tr; tr.SetTranslation(offset);
            BRepBuilderAPI_Transform shifted(wires[0], tr, Standard_True);
            if (!shifted.IsDone()) return;
            surfaceWire = TopoDS::Wire(shifted.Shape());
        }
        result[0] = -5;
        if (!ExternalLeadIsClear(b, end, direction)) return;
        BRepBuilderAPI_MakeEdge external(b, end);
        if (!external.IsDone()) return;
        BRepBuilderAPI_MakeWire joined;
        if (!surfaceWire.IsNull()) joined.Add(surfaceWire);
        joined.Add(external.Edge());
        if (!joined.IsDone() || !BRepCheck_Analyzer(joined.Wire()).IsValid()) return;
        GProp_GProps surfaceProps, props;
        if (!surfaceWire.IsNull()) BRepGProp::LinearProperties(surfaceWire, surfaceProps);
        BRepGProp::LinearProperties(joined.Wire(), props);
        if (!std::isfinite(props.Mass()) || props.Mass() <= 0) return;
        if (!g_ctx.IsNull()) {
            Handle(AIS_Shape) overlay = new AIS_Shape(joined.Wire());
            // Own transient objects before any context operation can throw.
            if (previewOnly) g_powerLeadPreview = overlay;
            g_ctx->SetColor(overlay, previewOnly ? Quantity_Color(0.1, 0.9, 1.0, Quantity_TOC_RGB) :
                Quantity_Color(1.0, 0.65, 0.05, Quantity_TOC_RGB), Standard_False);
            if (previewOnly) {
                overlay->SetWidth(3.0);
                overlay->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
            }
            g_ctx->Display(overlay, Standard_False);
            g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost); g_ctx->Deactivate(overlay, Standard_False);
            if (previewOnly) {
                g_powerDestinationPreview = new AIS_Point(new Geom_CartesianPoint(end));
                g_powerDestinationPreview->SetMarker(Aspect_TOM_O_POINT);
                g_ctx->SetColor(g_powerDestinationPreview, Quantity_Color(0.1, 0.9, 1.0, Quantity_TOC_RGB), Standard_False);
                g_ctx->Display(g_powerDestinationPreview, Standard_False);
                g_ctx->SetZLayer(g_powerDestinationPreview, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(g_powerDestinationPreview, Standard_False);
            } else {
                auto old = g_powerLeads.find(id);
                if (old != g_powerLeads.end()) g_ctx->Remove(old->second, Standard_False);
                g_powerLeads[id] = overlay;
            }
        }
        result[0] = 1; result[1] = props.Mass(); result[2] = surfaceProps.Mass(); result[3] = b.Distance(end);
    }

    int PickConnectorNative(int x, int y)
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);
        Handle(SelectMgr_EntityOwner) owner = g_ctx->DetectedOwner();
        int found = -1;
        if (!owner.IsNull()) {
            Handle(AIS_InteractiveObject) selectable = Handle(AIS_InteractiveObject)::DownCast(owner->Selectable());
            for (const auto& piece : g_routePieces)
                if (piece.second.connector && piece.second.ais == selectable) { found = piece.first; break; }
        }
        // 발열 경로(런)에는 AIS 동적 하이라이트(청록)를 쓰지 않는다. 커넥터 호버 피드백만 남긴다.
        if (found < 0) g_ctx->ClearDetected(Standard_False);
        return found;
    }

    void HighlightCircuitNative(const std::vector<int>& ids)
    {
        if (g_ctx.IsNull()) return;
        for (int id : g_hoveredCircuitPieces) {
            auto it = g_routePieces.find(id);
            if (it != g_routePieces.end()) {
                g_ctx->SetColor(it->second.ais, it->second.color, Standard_False);
                g_ctx->SetWidth(it->second.ais, it->second.width, Standard_False);
            }
        }
        g_hoveredCircuitPieces.clear();
        for (int id : ids) {
            auto it = g_routePieces.find(id);
            if (it == g_routePieces.end()) continue;
            g_hoveredCircuitPieces.insert(id);
            g_ctx->SetColor(it->second.ais, Quantity_Color(1.0, 0.85, 0.0, Quantity_TOC_RGB), Standard_False);
            g_ctx->SetWidth(it->second.ais, 4.0, Standard_False);
        }
        g_view->Redraw();
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
        ClearPowerLeadsNative();
        ClearRoutePiecesNative();
        ClearVertexHandlesNative();
        ClearRoutePreviewNative();
        if (!g_ctx.IsNull())
        {
            for (const auto& overlay : g_worldOverlays)
                g_ctx->Remove(overlay, Standard_False);
        }
        g_worldOverlays.clear();
        for (const auto& staged : g_stagedWorkingPolylines)
            if (!g_ctx.IsNull()) g_ctx->Remove(staged.second.ais,Standard_False);
        g_stagedWorkingPolylines.clear();
        if (!g_view.IsNull()) g_view->Redraw();
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
                g_ctx->SetColor(overlay, kHeatingRouteColor, Standard_False);
                g_ctx->SetWidth(overlay, kHeatingRouteWidth, Standard_False);
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
    // ------------------------------------------------------------------
    // Pixel picker. A pixel is resolved by a camera ray against the whole
    // g_shape instead of AIS MoveTo, which resolves depth per sensitive
    // entity and can report a coplanar or behind face. Click, hover and
    // rectangle selection all share this single path.
    // ------------------------------------------------------------------

    enum class PickStatusNative { Ok, Miss, Error };

    std::unique_ptr<IntCurvesFace_ShapeIntersector> g_pickIntersector;
    long long g_pickRevision = -1;
    Bnd_Box g_pickBounds;
    double g_pickModelScale = 0.0;

    bool EnsurePickGeometryNative()
    {
        if (g_shape.IsNull()) return false;
        if (g_pickIntersector && g_pickRevision == g_modelRevision) return true;
        g_pickIntersector.reset();
        g_pickRevision = -1;
        try
        {
            std::unique_ptr<IntCurvesFace_ShapeIntersector> intersector =
                std::make_unique<IntCurvesFace_ShapeIntersector>();
            intersector->Load(g_shape, Precision::Confusion());
            Bnd_Box bounds;
            BRepBndLib::Add(g_shape, bounds);
            if (bounds.IsVoid() || bounds.IsOpen()) return false;
            const double scale = gp_Pnt(bounds.CornerMin()).Distance(bounds.CornerMax());
            if (!std::isfinite(scale) || !(scale > 0.0)) return false;
            g_pickBounds = bounds;
            g_pickModelScale = scale;
            g_pickIntersector = std::move(intersector);
            g_pickRevision = g_modelRevision;
            return true;
        }
        catch (const Standard_Failure&) { return false; }
    }

    // 0 = front facing (normal points back to the camera), 1 = undecidable
    // (tangent or normal failure), 2 = back facing. Undecidable hits are never
    // demoted to back facing.
    int FaceFacingRankNative(const TopoDS_Face& face, Standard_Real u, Standard_Real v,
                             const gp_Pnt& hit, const gp_Dir& rayDir, double modelScale)
    {
        try
        {
            BRepAdaptor_Surface surface(face);
            gp_Pnt p;
            gp_Vec du, dv;
            surface.D1(u, v, p, du, dv);
            if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z())) return 1;
            if (p.Distance(hit) > 1e-3 * modelScale) return 1;   // parameter space mismatch
            gp_Vec normal = du.Crossed(dv);
            if (normal.SquareMagnitude() < 1e-24) return 1;
            if (face.Orientation() == TopAbs_REVERSED) normal.Reverse();
            normal.Normalize();
            const double facing = normal.Dot(gp_Vec(rayDir));
            if (!std::isfinite(facing) || std::fabs(facing) < 1e-9) return 1;
            return facing < 0.0 ? 0 : 2;
        }
        catch (const Standard_Failure&) { return 1; }
    }

    bool BuildPickRayNative(int x, int y, gp_Pnt& origin, gp_Dir& dir, double& tMax)
    {
        if (g_view.IsNull()) return false;
        Standard_Real px = 0.0, py = 0.0, pz = 0.0, vx = 0.0, vy = 0.0, vz = 0.0;
        try { g_view->ConvertWithProj(x, y, px, py, pz, vx, vy, vz); }
        catch (const Standard_Failure&) { return false; }
        if (!std::isfinite(px) || !std::isfinite(py) || !std::isfinite(pz) ||
            !std::isfinite(vx) || !std::isfinite(vy) || !std::isfinite(vz)) return false;
        gp_Vec viewRay(vx, vy, vz);
        if (viewRay.SquareMagnitude() < 1e-18) return false;
        viewRay.Normalize();
        const Handle(Graphic3d_Camera) camera = g_view->Camera();
        if (camera.IsNull()) return false;
        const gp_Pnt planePoint(px, py, pz);
        const gp_Pnt lo = g_pickBounds.CornerMin();
        const gp_Pnt hi = g_pickBounds.CornerMax();
        const gp_Pnt center((lo.X() + hi.X()) * 0.5, (lo.Y() + hi.Y()) * 0.5, (lo.Z() + hi.Z()) * 0.5);
        const double halfDiagonal = gp_Vec(lo, hi).Magnitude() * 0.5;
        if (!(halfDiagonal > 0.0)) return false;
        if (camera->IsOrthographic())
        {
            // Start well behind the model so hits in front of the projection
            // plane are still on the positive side of the ray.
            const double span = halfDiagonal * 2.0 + camera->Distance() + 1.0;
            origin = planePoint.Translated(viewRay * -span);
            dir = gp_Dir(viewRay);
        }
        else
        {
            origin = camera->Eye();
            const gp_Vec eyeToPlane(origin, planePoint);
            if (eyeToPlane.SquareMagnitude() < 1e-18) return false;
            dir = gp_Dir(eyeToPlane);
        }
        tMax = gp_Vec(origin, center).Dot(gp_Vec(dir)) + halfDiagonal + 1.0;
        return std::isfinite(tMax) && tMax > 0.0;
    }

    PickStatusNative PickFaceAtPixelNative(int x, int y, int& faceId)
    {
        faceId = -1;
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return PickStatusNative::Error;
        if (!EnsurePickGeometryNative()) return PickStatusNative::Error;
        gp_Pnt origin;
        gp_Dir dir(0.0, 0.0, 1.0);
        double tMax = 0.0;
        if (!BuildPickRayNative(x, y, origin, dir, tMax)) return PickStatusNative::Error;
        try
        {
            IntCurvesFace_ShapeIntersector& hits = *g_pickIntersector;
            hits.Perform(gp_Lin(origin, dir), 0.0, tMax);
            if (!hits.IsDone()) return PickStatusNative::Error;

            // Nearest valid depth first. Only that depth competes, so a nearer
            // face always wins and a farther face is never searched for.
            double nearest = 1e100;
            for (int i = 1; i <= hits.NbPnt(); ++i)
            {
                const double t = hits.WParameter(i);
                if (!std::isfinite(t) || t < 0.0 || t > tMax) continue;
                const TopAbs_State state = hits.State(i);
                if (state != TopAbs_IN && state != TopAbs_ON) continue;
                if (t < nearest) nearest = t;
            }
            if (nearest > 1e99) return PickStatusNative::Miss;

            const double depthTolerance = (std::max)(1e-7, 1e-6 * g_pickModelScale);
            int bestFace = -1;
            int bestRank = 99;
            int bestState = 99;
            double bestDepth = 0.0;
            for (int i = 1; i <= hits.NbPnt(); ++i)
            {
                const double t = hits.WParameter(i);
                if (!std::isfinite(t) || t < 0.0 || t > tMax) continue;
                if (t - nearest > depthTolerance) continue;
                const TopAbs_State state = hits.State(i);
                if (state != TopAbs_IN && state != TopAbs_ON) continue;
                const TopoDS_Face& face = hits.Face(i);
                if (face.IsNull() || !g_faceMap.Contains(face)) continue;
                const int index = g_faceMap.FindIndex(face) - 1;
                if (index < 0 || index >= static_cast<int>(g_faces.size())) continue;
                const int rank = FaceFacingRankNative(face, hits.UParameter(i), hits.VParameter(i),
                                                      hits.Pnt(i), dir, g_pickModelScale);
                const int stateRank = (state == TopAbs_IN) ? 0 : 1;
                bool better = false;
                if (bestFace < 0) better = true;
                else if (rank != bestRank) better = rank < bestRank;
                else if (stateRank != bestState) better = stateRank < bestState;
                else if (t < bestDepth - 1e-12) better = true;
                else if (t <= bestDepth + 1e-12 && index < bestFace) better = true;   // deterministic tie break
                if (!better) continue;
                bestFace = index;
                bestRank = rank;
                bestState = stateRank;
                bestDepth = t;
            }
            if (bestFace < 0) return PickStatusNative::Miss;
            faceId = bestFace;
            return PickStatusNative::Ok;
        }
        catch (const Standard_Failure&) { return PickStatusNative::Error; }
    }

    bool BuildFaceOwnerMapNative(std::map<int, Handle(SelectMgr_EntityOwner)>& owners)
    {
        owners.clear();
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return false;
        const int mode = AIS_Shape::SelectionMode(TopAbs_FACE);
        if (!g_aisShape->HasSelection(mode)) return false;
        const Handle(SelectMgr_Selection) selection = g_aisShape->Selection(mode);
        if (selection.IsNull()) return false;
        const NCollection_Vector<Handle(SelectMgr_SensitiveEntity)>& entities = selection->Entities();
        for (Standard_Integer i = 0; i < entities.Length(); ++i)
        {
            const Handle(SelectMgr_SensitiveEntity)& entity = entities(i);
            if (entity.IsNull() || !entity->IsActiveForSelection()) continue;
            const Handle(Select3D_SensitiveEntity) sensitive = entity->BaseSensitive();
            if (sensitive.IsNull()) continue;
            const Handle(StdSelect_BRepOwner) brep =
                Handle(StdSelect_BRepOwner)::DownCast(sensitive->OwnerId());
            if (brep.IsNull() || !brep->HasShape()) continue;
            if (!g_faceMap.Contains(brep->Shape())) continue;
            const int faceId = g_faceMap.FindIndex(brep->Shape()) - 1;
            if (faceId < 0) continue;
            owners.emplace(faceId, sensitive->OwnerId());
        }
        return !owners.empty();
    }

    /// <summary>Drop the hover colour override. Selected/confirmed colours are owned elsewhere.</summary>
    void DropHoverFaceColorNative()
    {
        if (g_hoverColorFace < 0) return;
        const int faceId = g_hoverColorFace;
        g_hoverColorFace = -1;
        if (g_selectedFaceIds.count(faceId) == 0 && g_confirmedFaceColors.count(faceId) == 0)
            ClearFaceColorOverride(faceId);
    }

    /// <summary>Hover colour = the AIS dynamic highlight colour, so hover looks unchanged.</summary>
    Quantity_Color HoverFaceColorNative()
    {
        if (!g_ctx.IsNull())
        {
            const Handle(Prs3d_Drawer)& style = g_ctx->HighlightStyle(Prs3d_TypeOfHighlight_Dynamic);
            if (!style.IsNull()) return style->Color();
        }
        return Quantity_Color(0.0, 1.0, 1.0, Quantity_TOC_RGB);
    }

    /// <summary>World point to client pixel. Read-only diagnostic used by the regression harness.</summary>
    bool WorldToScreenNative(double x, double y, double z, int* out)
    {
        if (g_view.IsNull() || out == nullptr) return false;
        if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return false;
        try
        {
            Standard_Integer sx = 0, sy = 0;
            g_view->Convert(x, y, z, sx, sy);
            out[0] = sx;
            out[1] = sy;
            return true;
        }
        catch (const Standard_Failure&) { return false; }
    }

    /// <summary>
    /// Oriented face normal [nx,ny,nz] plus a point on the face [px,py,pz]
    /// (mid-UV surface point). Read-only diagnostic used by the regression harness.
    /// </summary>
    bool GetFaceProbeNative(int faceId, double* out)
    {
        if (out == nullptr || faceId < 0 || faceId >= static_cast<int>(g_faces.size())) return false;
        const TopoDS_Face& face = g_faces[faceId];
        if (face.IsNull()) return false;
        try
        {
            BRepAdaptor_Surface surface(face);
            const Standard_Real u = (surface.FirstUParameter() + surface.LastUParameter()) * 0.5;
            const Standard_Real v = (surface.FirstVParameter() + surface.LastVParameter()) * 0.5;
            gp_Pnt p;
            gp_Vec du, dv;
            surface.D1(u, v, p, du, dv);
            gp_Vec normal = du.Crossed(dv);
            if (normal.SquareMagnitude() < 1e-24) return false;
            if (face.Orientation() == TopAbs_REVERSED) normal.Reverse();
            normal.Normalize();
            out[0] = normal.X(); out[1] = normal.Y(); out[2] = normal.Z();
            out[3] = p.X(); out[4] = p.Y(); out[5] = p.Z();
            return true;
        }
        catch (const Standard_Failure&) { return false; }
    }

    /// <summary>W5 — 호버: 동적 하이라이트. 면이 바뀔 때만 Redraw (무효화 폭주 방지)</summary>
    int HoverFaceNative(int x, int y)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return -1;
        if (!g_faceHighlightsVisible) return -1;
        int faceId = -1;
        const PickStatusNative status = PickFaceAtPixelNative(x, y, faceId);
        const int idx = (status == PickStatusNative::Ok) ? faceId : -1;
        if (idx == g_lastHoverFace) return idx;
        const int previous = g_lastHoverFace;
        g_lastHoverFace = idx;
        if (previous == g_hoverColorFace) g_hoverColorFace = -1;
        // Selected/confirmed faces keep their own colour (SetToHilightSelected(false) semantics).
        if (previous >= 0 && g_selectedFaceIds.count(previous) == 0 && g_confirmedFaceColors.count(previous) == 0)
            ClearFaceColorOverride(previous);
        if (idx >= 0 && g_selectedFaceIds.count(idx) == 0 && g_confirmedFaceColors.count(idx) == 0)
        {
            SetFaceColorOverride(idx, HoverFaceColorNative());
            g_hoverColorFace = idx;
        }
        g_ctx->Redisplay(g_aisShape, Standard_False);
        g_view->Redraw();
        return idx;
    }

    void ClearHoverNative()
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        if (g_lastHoverFace == -1 && g_hoverColorFace == -1) return;
        g_lastHoverFace = -1;
        DropHoverFaceColorNative();
        g_ctx->ClearDetected(Standard_False);   // other modes may have left a detected owner
        if (!g_aisShape.IsNull()) g_ctx->Redisplay(g_aisShape, Standard_False);
        g_view->Redraw();
    }

    /// <summary>W5 — 클릭: 면 선택(선택 하이라이트 유지). 빈 곳이면 -1 (확정 영역이 있으면 선택 유지)</summary>
    int SelectFaceNative(int x, int y, bool toggle)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return -1;
        int faceId = -1;
        const PickStatusNative status = PickFaceAtPixelNative(x, y, faceId);
        Handle(SelectMgr_EntityOwner) owner;
        if (status == PickStatusNative::Ok)
        {
            std::map<int, Handle(SelectMgr_EntityOwner)> owners;
            if (BuildFaceOwnerMapNative(owners))
            {
                const auto it = owners.find(faceId);
                if (it != owners.end()) owner = it->second;
            }
        }
        if (status != PickStatusNative::Ok || owner.IsNull())
        {
            // Empty space keeps an existing AIS selection when a working area is
            // confirmed. A picker/owner failure must not rewrite the selection.
            g_lastHoverFace = -1;
            DropHoverFaceColorNative();
            if (status == PickStatusNative::Miss && !toggle && g_confirmedFaceColors.empty())
                g_ctx->ClearSelected(Standard_False);
            UpdateSelectedFaceColors();
            g_view->Redraw();
            return -1;
        }
        g_lastHoverFace = faceId;
        if (toggle)
        {
            if (owner->IsSelected()) g_ctx->SetSelectedState(owner, Standard_False);
            else g_ctx->AddSelect(owner);
        }
        else
        {
            g_ctx->ClearSelected(Standard_False);
            g_ctx->AddSelect(owner);
        }
        g_ctx->HilightSelected(Standard_False);
        UpdateSelectedFaceColors();   // also drops the hover colour
        g_view->Redraw();
        return faceId;
    }

    /// <summary>명시적 해제 경로(확정 영역 삭제): AIS 선택/선택 하이라이트만 해제한다.</summary>
    void ClearFaceSelectionNative()
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        g_ctx->ClearSelected(Standard_False);
        UpdateSelectedFaceColors();
        g_view->Redraw();
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
        {
            // The picker resolves face IDs, not AIS owners; map the whole mode-4
            // selection once instead of per probe.
            std::map<int, Handle(SelectMgr_EntityOwner)> faceOwners;
            if (BuildFaceOwnerMapNative(faceOwners))
                for (const auto& entry : faceOwners) owners.emplace(entry.first, entry.second);
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
                int pickedFace = -1;
                if (PickFaceAtPixelNative(x, y, pickedFace) == PickStatusNative::Ok)
                    visibleFaces.insert(pickedFace);
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
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        if (g_selectionRubberBand.IsNull())
            g_selectionRubberBand = new AIS_RubberBand(Quantity_Color(0.1, 0.55, 0.95, Quantity_TOC_RGB), Aspect_TOL_SOLID,
                                                       Quantity_Color(0.1, 0.55, 0.95, Quantity_TOC_RGB), 0.8, 1.5);
        // AIS_RubberBand is a 2D-persistent object anchored at the window's left-lower
        // corner with +Y pointing up (Graphic3d_TMF_2d / Aspect_TOTP_LEFT_LOWER), while
        // the Win32 client coordinates passed here grow downward from the top edge.
        // Flip Y so the rubber band is anchored at the press point instead of its mirror.
        Standard_Integer viewWidth = 0, viewHeight = 0;
        g_view->Window()->Size(viewWidth, viewHeight);
        const int flippedY0 = viewHeight - y0;
        const int flippedY1 = viewHeight - y1;
        g_selectionRubberBand->SetRectangle(std::min(x0, x1), std::min(flippedY0, flippedY1),
                                            std::max(x0, x1), std::max(flippedY0, flippedY1));
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

    bool PickSelectedBoundaryPointNative(const std::vector<int>& faces, int x, int y, double* out)
    {
        if (g_view.IsNull() || g_shape.IsNull() || faces.empty() || out == nullptr) return false;
        try {
            std::set<int> unique;
            BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
            for (int id : faces) {
                if (id < 0 || id >= static_cast<int>(g_faces.size())) return false;
                if (unique.insert(id).second) sewing.Add(g_faces[id]);
            }
            sewing.Perform();
            const TopoDS_Shape united = sewing.SewedShape();
            if (united.IsNull()) return false;

            // Classify the boundary on the sewn working faces, not source faces:
            // separately represented coincident seams otherwise appear twice.
            TopTools_IndexedMapOfShape edgeMap;
            std::vector<int> uses;
            for (TopExp_Explorer fx(united, TopAbs_FACE); fx.More(); fx.Next())
                for (TopExp_Explorer ex(fx.Current(), TopAbs_EDGE); ex.More(); ex.Next()) {
                    const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                    int index = edgeMap.FindIndex(edge);
                    if (!index) { edgeMap.Add(edge); uses.push_back(0); index = edgeMap.Extent(); }
                    ++uses[index - 1];
                }
            std::vector<TopoDS_Edge> edges;
            for (int i = 1; i <= edgeMap.Extent(); ++i) {
                if (uses[i - 1] > 2) return false;
                if (uses[i - 1] == 1) edges.push_back(TopoDS::Edge(edgeMap(i)));
            }
            if (edges.empty()) return false;

            double best2 = 100.0; // practical 10 px aperture
            gp_Pnt bestPoint;
            for (const TopoDS_Edge& edge : edges) {
                BRepAdaptor_Curve curve(edge);
                GCPnts_QuasiUniformDeflection samples(curve, 0.05);
                if (!samples.IsDone() || samples.NbPoints() < 2 || samples.NbPoints() > 20000) continue;
                for (int i = 1; i < samples.NbPoints(); ++i) {
                    gp_Pnt a = samples.Value(i), b = samples.Value(i + 1);
                    Standard_Integer ax, ay, bx, by;
                    g_view->Convert(a.X(), a.Y(), a.Z(), ax, ay);
                    g_view->Convert(b.X(), b.Y(), b.Z(), bx, by);
                    const double dx = static_cast<double>(bx - ax), dy = static_cast<double>(by - ay);
                    const double len2 = dx * dx + dy * dy;
                    const double rawT = len2 > 1e-12 ? ((x-ax)*dx + (y-ay)*dy)/len2 : 0.0;
                    const double t = std::max(0.0, std::min(1.0, rawT));
                    const double px = ax + t*dx, py = ay + t*dy;
                    const double d2 = (x-px)*(x-px) + (y-py)*(y-py);
                    if (d2 >= best2) continue;
                    best2 = d2;
                    // Interpolation is only a screen-space coarse estimate; refine
                    // on the actual OCCT curve with a 1-D bounded minimization.
                    const double u0 = samples.Parameter(i), u1 = samples.Parameter(i + 1);
                    double lo = std::min(u0, u1), hi = std::max(u0, u1);
                    for (int k = 0; k < 24; ++k) {
                        const double m1 = lo + (hi-lo)/3.0, m2 = hi - (hi-lo)/3.0;
                        gp_Pnt p1, p2; curve.D0(m1, p1); curve.D0(m2, p2);
                        Standard_Integer sx1, sy1, sx2, sy2;
                        g_view->Convert(p1.X(), p1.Y(), p1.Z(), sx1, sy1);
                        g_view->Convert(p2.X(), p2.Y(), p2.Z(), sx2, sy2);
                        const double d1=(x-sx1)*(x-sx1)+(y-sy1)*(y-sy1), d2=(x-sx2)*(x-sx2)+(y-sy2)*(y-sy2);
                        if (d1 < d2) hi=m2; else lo=m1;
                    }
                    curve.D0((lo+hi)*0.5, bestPoint);
                }
            }
            if (best2 >= 100.0) return false;

            // Test visibility at the candidate itself, not at the cursor: the
            // 10px aperture intentionally permits the cursor to miss the edge.
            Standard_Integer candidateX, candidateY;
            g_view->Convert(bestPoint.X(), bestPoint.Y(), bestPoint.Z(), candidateX, candidateY);
            Standard_Real rx, ry, rz, vx, vy, vz;
            g_view->ConvertWithProj(candidateX, candidateY, rx, ry, rz, vx, vy, vz);
            gp_Vec direction(vx, vy, vz);
            if (direction.SquareMagnitude() < 1e-18) return false;
            direction.Normalize();
            IntCurvesFace_ShapeIntersector visibility;
            visibility.Load(g_shape, 1e-7);
            visibility.Perform(gp_Lin(gp_Pnt(rx, ry, rz), gp_Dir(direction)), 0.0, 1e100);
            if (!visibility.IsDone() || visibility.NbPnt() == 0) return false;
            const gp_Pnt front = visibility.Pnt(1);
            Standard_Integer frontX, frontY;
            g_view->Convert(front.X(), front.Y(), front.Z(), frontX, frontY);
            if (front.Distance(bestPoint) > 0.05 &&
                (frontX-candidateX)*(frontX-candidateX)+(frontY-candidateY)*(frontY-candidateY) > 1) return false;
            out[0] = bestPoint.X(); out[1] = bestPoint.Y(); out[2] = bestPoint.Z();
            out[3] = front.Distance(gp_Pnt(rx, ry, rz));
            return true;
        }
        catch (const Standard_Failure&) { return false; }
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
#include "WorkingSideNative.inc"
}
#pragma managed(pop)

namespace HeatingMold { namespace OccBridge {

long long Bridge::GetModelRevision() { return g_modelRevision; }
void Bridge::ReleaseWorkingContext(long long id) { ReleaseWorkingNative(id); }
WorkingContext^ Bridge::CreateWorkingContext(double nx,double ny,double nz,double xx,double xy,double xz)
{
    const long long id = CreateWorkingNative(nx,ny,nz,xx,xy,xz);
    if (!id) return nullptr;
    auto* c = FindWorking(id,g_modelRevision);
    auto result = gcnew WorkingContext();
    result->Id=id; result->Revision=c->revision; result->MeshDeflection=c->deflection;
    result->Frame=gcnew array<double>{c->origin.X(),c->origin.Y(),c->origin.Z(),
        c->x.X(),c->x.Y(),c->x.Z(),c->y.X(),c->y.Y(),c->y.Z(),c->n.X(),c->n.Y(),c->n.Z()};
    result->Triangles=gcnew array<double>(static_cast<int>(c->triangles.size()));
    for (int i=0;i<result->Triangles->Length;++i) result->Triangles[i]=c->triangles[i];
    return result;
}
WorkingHit^ Bridge::QueryWorkingSurface(long long id,long long revision,double u,double v)
{
    double p[3]={}, n[3]={}; int face=-1;
    const auto status=QueryWorkingNative(id,revision,u,v,p,face,n);
    auto result=gcnew WorkingHit();
    result->ContextId=id; result->Revision=revision; result->DatumPoint=gcnew array<double>{u,v};
    result->Status=static_cast<WorkingQueryStatus>(static_cast<int>(status)); result->FaceId=face;
    if (status==WorkingStatusNative::Hit || status==WorkingStatusNative::Tangent) {
        result->Point=gcnew array<double>{p[0],p[1],p[2]};
        result->Normal=gcnew array<double>{n[0],n[1],n[2]};
    }
    return result;
}
WorkingHit^ Bridge::QueryWorkingSurfaceForFaces(long long id,long long revision,array<int>^ faceIds,double u,double v)
{
    if (faceIds==nullptr) {
        auto invalid=gcnew WorkingHit(); invalid->ContextId=id; invalid->Revision=revision;
        invalid->DatumPoint=gcnew array<double>{u,v}; invalid->Status=WorkingQueryStatus::InvalidInput; invalid->FaceId=-1; return invalid;
    }
    for each (int candidate in faceIds) if (candidate<0 || candidate>=static_cast<int>(g_faces.size())) {
        auto invalid=gcnew WorkingHit(); invalid->ContextId=id; invalid->Revision=revision;
        invalid->DatumPoint=gcnew array<double>{u,v}; invalid->Status=WorkingQueryStatus::InvalidInput; invalid->FaceId=-1; return invalid;
    }
    double p[3]={}, n[3]={}; int face=-1;
    const auto status=QueryWorkingNative(id,revision,u,v,p,face,n);
    auto result=gcnew WorkingHit();
    result->ContextId=id; result->Revision=revision; result->DatumPoint=gcnew array<double>{u,v};
    result->Status=static_cast<WorkingQueryStatus>(static_cast<int>(status)); result->FaceId=face;
    if (status==WorkingStatusNative::Hit || status==WorkingStatusNative::Tangent) {
        result->Point=gcnew array<double>{p[0],p[1],p[2]};
        result->Normal=gcnew array<double>{n[0],n[1],n[2]};
        bool selected=false;
        if (faceIds!=nullptr) for each (int candidate in faceIds) {
            if (candidate<0 || candidate>=static_cast<int>(g_faces.size())) {
                result->Status=WorkingQueryStatus::InvalidInput; result->Point=nullptr; result->Normal=nullptr; return result;
            }
            selected |= candidate==face;
        }
        result->IsSelectedFace=selected;
        if (!selected) { result->Status=WorkingQueryStatus::Miss; result->Point=nullptr; result->Normal=nullptr; }
    }
    return result;
}
WorkingHit^ Bridge::MapWorkingPointOntoFaces(long long id,long long revision,array<int>^ faceIds,double u,double v)
{
    auto result=gcnew WorkingHit();
    result->ContextId=id; result->Revision=revision; result->DatumPoint=gcnew array<double>{u,v}; result->FaceId=-1;
    if(faceIds==nullptr) { result->Status=WorkingQueryStatus::InvalidInput; return result; }
    try {
        std::vector<int> faces; for each(int candidate in faceIds) faces.push_back(candidate);
        double p[3]={}, n[3]={}; int face=-1;
        const auto status=MapWorkingPointOntoFacesNative(id,revision,faces,u,v,p,face,n);
        result->Status=static_cast<WorkingQueryStatus>(static_cast<int>(status)); result->FaceId=face;
        if(status==WorkingStatusNative::Hit||status==WorkingStatusNative::Tangent) {
            result->Point=gcnew array<double>{p[0],p[1],p[2]};
            result->Normal=gcnew array<double>{n[0],n[1],n[2]};
            result->IsSelectedFace=true;
        }
        return result;
    } catch(...) { result->Status=WorkingQueryStatus::GeometryFailure; return result; }
}
WorkingHit^ Bridge::SnapWorkingPointToFaces(double x,double y,double z,array<int>^ faceIds,double tolerance)
{
    auto result=gcnew WorkingHit(); result->ContextId=0; result->Revision=g_modelRevision; result->FaceId=-1;
    if(faceIds==nullptr) { result->Status=WorkingQueryStatus::InvalidInput; return result; }
    try {
        std::vector<int> faces; for each(int candidate in faceIds) faces.push_back(candidate);
        std::vector<double> xyz{x,y,z}; double p[3]={}; int face=-1;
        if(SnapWorkingPointToFacesNative(xyz,faces,tolerance,p,face)) {
            result->Status=WorkingQueryStatus::Hit; result->FaceId=face;
            result->Point=gcnew array<double>{p[0],p[1],p[2]}; result->IsSelectedFace=true;
        } else result->Status=WorkingQueryStatus::Miss;
        return result;
    } catch(...) { result->Status=WorkingQueryStatus::GeometryFailure; return result; }
}
bool Bridge::AreWorkingFacesAdjacent(long long id,long long revision,int faceA,int faceB)
{ return WorkingFacesAdjacentNative(id,revision,faceA,faceB); }
RoutePieceResult^ Bridge::DisplayWorkingRoutePiece(long long id,long long revision,int pieceId,String^ kind,array<double>^ flat)
{
    if (kind==nullptr || flat==nullptr) return nullptr;
    try {
        std::vector<double> xyz; for each(double v in flat) xyz.push_back(v);
        double data[7]={};
        if (!DisplayWorkingRoutePieceNative(id,revision,pieceId,msclr::interop::marshal_as<std::string>(kind),xyz,data)) return nullptr;
        auto result=gcnew RoutePieceResult(); result->Length=data[0];
        result->Start=gcnew array<double>{data[1],data[2],data[3]}; result->End=gcnew array<double>{data[4],data[5],data[6]};
        return result;
    } catch (...) { return nullptr; }
}
bool Bridge::StageWorkingPolyline(long long id,long long revision,int stageId,array<double>^ flat)
{
    if (flat==nullptr) return false;
    try { std::vector<double> xyz; for each(double v in flat) xyz.push_back(v); return StageWorkingPolylineNative(id,revision,stageId,xyz); }
    catch (...) { return false; }
}
RoutePieceResult^ Bridge::StageProjectedWorkingRoutePiece(long long id,long long revision,int stageId,String^ kind,
    array<double>^ flat,array<int>^ faceIds,double dx,double dy,double dz)
{
    if(kind==nullptr||flat==nullptr||faceIds==nullptr) return nullptr;
    try { std::vector<double> xyz;std::vector<int> faces;for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageProjectedWorkingRouteNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,gp_Dir(dx,dy,dz),data))return nullptr;
        auto r=gcnew RoutePieceResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;
    } catch(...) { return nullptr; }
}
RoutePieceResult^ Bridge::StageSurfaceMappedRoutePiece(long long id,long long revision,int stageId,String^ kind,
    array<double>^ flat,array<int>^ faceIds)
{
    if(kind==nullptr||flat==nullptr||faceIds==nullptr) return nullptr;
    try { std::vector<double> xyz;std::vector<int> faces;for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageSurfaceMappedRoutePieceNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,data))return nullptr;
        auto r=gcnew RoutePieceResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;
    } catch(...) { return nullptr; }
}
String^ Bridge::GetWorkingStageDiagnostic()
{
    return gcnew String(GetWorkingStageDiagnosticNative());
}
RoutePieceResult^ Bridge::StageProjectedWorkingPolyline(long long id,long long revision,int stageId,array<double>^ flat,array<int>^ faceIds,double dx,double dy,double dz)
{
    if(flat==nullptr||faceIds==nullptr)return nullptr;
    try {std::vector<double> xyz;std::vector<int> faces;for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageProjectedWorkingPolylineNative(id,revision,stageId,xyz,faces,gp_Dir(dx,dy,dz),data))return nullptr;
        auto r=gcnew RoutePieceResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;}catch(...){return nullptr;}
}
FacePickResult^ Bridge::PickFacePoint(int x, int y)
{
    try {
        double point[3] = {}, normal[3] = {}, uv[2] = {};
        int faceId = -1;
        const int status = PickFacePointNative(x, y, point, faceId, normal, uv);
        auto result = gcnew FacePickResult();
        result->Status = status;
        result->FaceId = faceId;
        result->Revision = g_modelRevision;
        result->Point = gcnew array<double>{point[0], point[1], point[2]};
        result->Normal = gcnew array<double>{normal[0], normal[1], normal[2]};
        result->Uv = gcnew array<double>{uv[0], uv[1]};
        return result;
    } catch (...) { return nullptr; }
}
array<array<double>^>^ Bridge::GetSharedBoundaryEdges(int exitFaceId, array<int>^ heatingFaceIds, double deflection)
{
    if (heatingFaceIds == nullptr || heatingFaceIds->Length == 0) return nullptr;
    try {
        std::vector<int> ids; for each (int v in heatingFaceIds) ids.push_back(v);
        std::vector<std::vector<double>> loops;
        if (!SharedBoundaryNative(exitFaceId, ids, deflection, loops)) return nullptr;
        auto result = gcnew array<array<double>^>(static_cast<int>(loops.size()));
        for (int i = 0; i < static_cast<int>(loops.size()); ++i) {
            auto flat = gcnew array<double>(static_cast<int>(loops[i].size()));
            for (int k = 0; k < static_cast<int>(loops[i].size()); ++k) flat[k] = loops[i][k];
            result[i] = flat;
        }
        return result;
    } catch (...) { return nullptr; }
}
bool Bridge::CommitWorkingRouteGeometry(long long id,long long revision,array<int>^ stagedRouteIds,array<int>^ finalRouteIds,array<int>^ stagedPolylineIds)
{
    if (stagedRouteIds==nullptr || finalRouteIds==nullptr || stagedPolylineIds==nullptr) return false;
    try {
        std::vector<int> sr,fr,sp; for each(int v in stagedRouteIds) sr.push_back(v);
        for each(int v in finalRouteIds) fr.push_back(v); for each(int v in stagedPolylineIds) sp.push_back(v);
        return CommitWorkingGeometryNative(id,revision,sr,fr,sp);
    } catch (...) { return false; }
}
void Bridge::DiscardWorkingRouteGeometry(long long id,long long revision,array<int>^ stagedRouteIds,array<int>^ stagedPolylineIds)
{
    try {
        std::vector<int> sr,sp;
        if (stagedRouteIds!=nullptr) for each(int v in stagedRouteIds) sr.push_back(v);
        if (stagedPolylineIds!=nullptr) for each(int v in stagedPolylineIds) sp.push_back(v);
        DiscardWorkingGeometryNative(id,revision,sr,sp);
    } catch (...) { }
}
bool Bridge::IsWorkingPointVisible(long long id,long long revision,double x,double y,double z,double tolerance)
{ return WorkingVisibleNative(id,revision,x,y,z,tolerance); }
void Bridge::ShowWorkingBoundary(array<array<double>^>^ loops)
{ ShowWorkingBoundary(loops,nullptr); }
void Bridge::ShowWorkingBoundary(array<array<double>^>^ loops,array<bool>^ holes)
{
    std::vector<std::vector<double>> values;
    std::vector<bool> flags;
    if (holes!=nullptr) for each(bool hole in holes) flags.push_back(hole);
    if (loops!=nullptr) for each(array<double>^ loop in loops) {
        std::vector<double> points;
        if (loop!=nullptr) for each(double v in loop) points.push_back(v);
        values.push_back(points);
    }
    ShowWorkingBoundaryNative(values,flags);
}
bool Bridge::ReturnToWorkingFront(long long id,long long revision)
{
    auto* ctx=FindWorking(id,revision);
    if (!ctx || g_view.IsNull()) return false;
    g_view->Camera()->SetProjectionType(Graphic3d_Camera::Projection_Orthographic);
    SetViewFrameNative(ctx->n.X(),ctx->n.Y(),ctx->n.Z(),ctx->y.X(),ctx->y.Y(),ctx->y.Z());
    return true;
}
WorkingPresentation^ Bridge::CaptureWorkingPresentation()
{
    double bounds[6]={}; bool hasBounds=false;
    const long long id=CaptureWorkingPresentationNative(bounds,hasBounds);
    if (!id) return nullptr;
    auto result=gcnew WorkingPresentation();
    result->Id=id; result->Revision=g_modelRevision;
    result->BoundaryCount=static_cast<int>(g_workingPresentations.at(id).boundaries.size());
    if (hasBounds) result->BoundaryBounds=gcnew array<double>{bounds[0],bounds[1],bounds[2],bounds[3],bounds[4],bounds[5]};
    return result;
}
bool Bridge::RestoreWorkingPresentation(long long id) { return RestoreWorkingPresentationNative(id); }
void Bridge::ReleaseWorkingPresentation(long long id) { g_workingPresentations.erase(id); }

bool Bridge::LoadStep(String^ path)
{
    ++g_modelRevision;
    InvalidateWorkingContexts();
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

bool Bridge::FaceRegionIsClosed(int faceIndex)
{
    try { return FaceRegionIsClosedNative(faceIndex); }
    catch (...) { return false; }
}

array<double>^ Bridge::GetFaceAxis(int faceIndex)
{
    double axis[3] = {0, 0, 0};
    try {
        if (!GetFaceAxisNative(faceIndex, axis)) return nullptr;
        return gcnew array<double> {axis[0], axis[1], axis[2]};
    } catch (...) {
        return nullptr;
    }
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

array<ExitSideCandidate^>^ Bridge::GetBoundaryExitSides(array<int>^ faceIds, double deflection)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    std::vector<NativeExitSide> nativeSides;
    try
    {
        if (!ExtractBoundaryExitSides(ids, deflection, nativeSides)) return nullptr;
    }
    catch (const Standard_Failure&)
    {
        return nullptr;
    }
    auto result = gcnew array<ExitSideCandidate^>((int)nativeSides.size());
    for (int i = 0; i < result->Length; ++i)
    {
        const NativeExitSide& run = nativeSides[(size_t)i];
        ExitSideCandidate^ side = gcnew ExitSideCandidate();
        side->FaceId = run.faceId;
        auto points = gcnew array<double>((int)run.xyz.size());
        for (int k = 0; k < points->Length; ++k) points[k] = run.xyz[(size_t)k];
        side->Points = points;

        // 구간별 면 id: 점 N개 → 구간 N-1개. 값이 비는 구간은 대표 면 id로 채운다.
        const int pointCount = points->Length / 3;
        auto segmentFaceIds = gcnew array<int>(pointCount > 1 ? pointCount - 1 : 0);
        for (int k = 0; k < segmentFaceIds->Length; ++k) segmentFaceIds[k] = run.faceId;
        for (size_t s = 0; s < run.segmentStarts.size(); ++s)
        {
            const int from = run.segmentStarts[s].startIndex;
            const int to = (s + 1 < run.segmentStarts.size())
                ? run.segmentStarts[s + 1].startIndex
                : pointCount - 1;
            for (int k = from; k < to && k < segmentFaceIds->Length; ++k)
                segmentFaceIds[k] = run.segmentStarts[s].faceId;
        }
        side->SegmentFaceIds = segmentFaceIds;
        result[i] = side;
    }
    return result;
}

void Bridge::ShowExitCandidates(array<array<double>^>^ polylines)
{
    std::vector<std::vector<double>> native;
    if (polylines != nullptr)
    {
        native.reserve((size_t)polylines->Length);
        for (int i = 0; i < polylines->Length; ++i)
        {
            array<double>^ flat = polylines[i];
            if (flat == nullptr || flat->Length < 6) continue;
            std::vector<double> points((size_t)flat->Length);
            for (int k = 0; k < flat->Length; ++k) points[(size_t)k] = flat[k];
            native.push_back(std::move(points));
        }
    }
    try { ShowExitCandidatesNative(native); }
    catch (const Standard_Failure&) { }
}

void Bridge::ClearExitCandidates()
{
    try { ClearExitCandidatesNative(); }
    catch (const Standard_Failure&) { }
}

void Bridge::HighlightExitCandidate(int index)
{
    try { HighlightExitCandidateNative(index); }
    catch (const Standard_Failure&) { }
}

int Bridge::PickExitCandidate(array<array<double>^>^ polylines, int x, int y, double pixelTolerance)
{
    std::vector<std::vector<double>> native;
    if (polylines != nullptr)
    {
        native.reserve((size_t)polylines->Length);
        for (int i = 0; i < polylines->Length; ++i)
        {
            array<double>^ flat = polylines[i];
            if (flat == nullptr || flat->Length < 6) { native.emplace_back(); continue; }
            std::vector<double> points((size_t)flat->Length);
            for (int k = 0; k < flat->Length; ++k) points[(size_t)k] = flat[k];
            native.push_back(std::move(points));
        }
    }
    if (native.empty()) return -1;
    try { return PickExitCandidateNative(native, x, y, pixelTolerance); }
    catch (const Standard_Failure&) { return -1; }
}

array<double>^ Bridge::SampleUvTo3D(int faceIndex, double u, double v)
{
    double p[3];
    if (!SamplePoint(faceIndex, u, v, p)) return nullptr;
    array<double>^ r = gcnew array<double>(3);
    r[0] = p[0]; r[1] = p[1]; r[2] = p[2];
    return r;
}

array<double>^ Bridge::PickSelectedBoundaryPoint(array<int>^ faceIds, int x, int y)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    double p[4];
    if (!PickSelectedBoundaryPointNative(ids, x, y, p)) return nullptr;
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

bool Bridge::SetVertexHandles(long long id, long long revision, array<double>^ xyzFlat)
{
    std::vector<double> xyz;
    if (xyzFlat != nullptr)
    {
        xyz.resize((size_t)xyzFlat->Length);
        for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    }
    return SetVertexHandlesNative(id, revision, xyz);
}

void Bridge::ClearVertexHandles()
{
    ClearVertexHandlesNative();
    if (!g_view.IsNull()) g_view->Redraw();
}

void Bridge::HighlightVertexHandle(int index)
{
    HighlightVertexHandleNative(index);
}

int Bridge::PickVertexHandle(int x, int y, double pixelTolerance)
{
    return PickVertexHandleNative(x, y, pixelTolerance);
}

bool Bridge::ShowRoutePreview(int pieceIndex, array<double>^ xyzFlat, bool invalid)
{
    std::vector<double> xyz;
    if (xyzFlat != nullptr)
    {
        xyz.resize((size_t)xyzFlat->Length);
        for (int i = 0; i < xyzFlat->Length; ++i) xyz[(size_t)i] = xyzFlat[i];
    }
    return ShowRoutePreviewNative(pieceIndex, xyz, invalid);
}

void Bridge::ClearRoutePreview()
{
    ClearRoutePreviewNative();
    if (!g_view.IsNull()) g_view->Redraw();
}

void Bridge::SetRouteEndpoints(array<double>^ xyzFlat)
{
    std::vector<double> xyz;
    if (xyzFlat != nullptr)
    {
        xyz.resize(xyzFlat->Length);
        for (int i = 0; i < xyzFlat->Length; ++i) xyz[i] = xyzFlat[i];
    }
    SetRouteEndpointsNative(xyz);
    if (!g_view.IsNull()) g_view->Redraw();
}

array<double>^ Bridge::ProjectPowerLead(int leadId, array<double>^ sourceAndDestination,
    array<double>^ actualStart, array<int>^ faceIds, double dx, double dy, double dz, double depth)
{
    return ProjectPowerLead(leadId, sourceAndDestination, actualStart, faceIds, dx, dy, dz, depth, false);
}

array<double>^ Bridge::ProjectPowerLead(int leadId, array<double>^ sourceAndDestination,
    array<double>^ actualStart, array<int>^ faceIds, double dx, double dy, double dz, double depth, bool previewOnly)
{
    // Remove the old preview without painting a blank frame. Redraw once after
    // replacement/validation, including failure so invalid positions clear it.
    const bool hadPreview = !g_powerLeadPreview.IsNull() || !g_powerDestinationPreview.IsNull();
    ClearPowerLeadPreviewNative();
    auto result = gcnew array<double>(4);
    result[0] = -1;
    if (sourceAndDestination == nullptr || actualStart == nullptr || faceIds == nullptr) {
        if (hadPreview && !g_view.IsNull()) g_view->Redraw();
        return result;
    }
    std::vector<double> xyz(sourceAndDestination->Length), actual(actualStart->Length);
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < sourceAndDestination->Length; ++i) xyz[i] = sourceAndDestination[i];
    for (int i = 0; i < actualStart->Length; ++i) actual[i] = actualStart[i];
    for (int i = 0; i < faceIds->Length; ++i) ids[i] = faceIds[i];
    double data[4] = { -9, 0, 0, 0 };
    try { ProjectPowerLeadNative(leadId, xyz, actual, ids, gp_Dir(dx, dy, dz), depth, data, previewOnly); }
    catch (const Standard_Failure&) {
        if (previewOnly) ClearPowerLeadPreviewNative();
        data[0] = -9;
    }
    if (!g_view.IsNull()) g_view->Redraw();
    for (int i = 0; i < 4; ++i) result[i] = data[i];
    return result;
}

void Bridge::ClearPowerLeads() { ClearPowerLeadsNative(); if (!g_view.IsNull()) g_view->Redraw(); }
void Bridge::ClearPowerLeadPreview()
{
    const bool hadPreview = !g_powerLeadPreview.IsNull() || !g_powerDestinationPreview.IsNull();
    ClearPowerLeadPreviewNative();
    if (hadPreview && !g_view.IsNull()) g_view->Redraw();
}
int Bridge::PickConnector(int x, int y) { return PickConnectorNative(x, y); }
int Bridge::PickRoutePiece(int x, int y)
{
    if (g_ctx.IsNull() || g_view.IsNull()) return -1;
    g_ctx->MoveTo(x, y, g_view, Standard_False);
    Handle(SelectMgr_EntityOwner) owner = g_ctx->DetectedOwner();
    int found = -1;
    if (!owner.IsNull())
    {
        Handle(AIS_InteractiveObject) selectable = Handle(AIS_InteractiveObject)::DownCast(owner->Selectable());
        for (const auto& piece : g_routePieces) if (piece.second.ais == selectable) { found = piece.first; break; }
    }
    // 회로 하이라이트(노랑)만 사용한다 — AIS 동적 하이라이트(청록)는 표시하지 않는다.
    g_ctx->ClearDetected(Standard_False);
    return found;
}
void Bridge::HighlightCircuit(array<int>^ pieceIds)
{
    std::vector<int> ids;
    if (pieceIds != nullptr) for each (int id in pieceIds) ids.push_back(id);
    HighlightCircuitNative(ids);
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

namespace { int g_panStartX = 0, g_panStartY = 0; }

void Bridge::PanStart(int x, int y)
{
    g_panStartX = x;
    g_panStartY = y;
    if (!g_view.IsNull()) g_view->Pan(0, 0, 1.0, Standard_True);
}

void Bridge::PanMove(int x, int y)
{
    if (g_view.IsNull()) return;
    g_view->Pan(x - g_panStartX, g_panStartY - y, 1.0, Standard_False);
    g_view->Redraw();
}

void Bridge::PanEnd()
{
    // Pan remembers the starting camera state; no explicit end call is needed.
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

void Bridge::RedrawView()
{
    if (!g_view.IsNull()) g_view->Redraw();
}

void Bridge::ClearOverlays()
{
    ClearOverlayNative();
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

int Bridge::HoverFace(int x, int y)
{
    return HoverFaceNative(x, y);
}

void Bridge::HideFaceHighlights()
{
    HideFaceHighlightsNative();
}

void Bridge::SetConfirmedFaceHighlights(array<int>^ faceIds)
{
    std::set<int> ids;
    if (faceIds != nullptr)
        for (int i = 0; i < faceIds->Length; ++i) ids.insert(faceIds[i]);
    SetConfirmedFaceHighlightsNative(ids);
}

void Bridge::SetConfirmedFaceHighlightColors(array<int>^ faceIds, array<double>^ rgb)
{
    std::map<int, Quantity_Color> colors;
    if (faceIds != nullptr && rgb != nullptr && rgb->Length >= faceIds->Length * 3)
        for (int i = 0; i < faceIds->Length; ++i)
            colors[faceIds[i]] = Quantity_Color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], Quantity_TOC_RGB);
    SetConfirmedFaceHighlightColorsNative(colors);
}

void Bridge::ClearConfirmedFaceHighlights()
{
    ClearConfirmedFaceHighlightsNative();
}

void Bridge::ClearFaceSelection()
{
    ClearFaceSelectionNative();
}

void Bridge::ClearHover()
{
    ClearHoverNative();
}

int Bridge::SelectFace(int x, int y, bool toggle)
{
    return SelectFaceNative(x, y, toggle);
}

array<int>^ Bridge::WorldToScreen(double x, double y, double z)
{
    int pixel[2] = {0, 0};
    if (!WorldToScreenNative(x, y, z, pixel)) return nullptr;
    array<int>^ result = gcnew array<int>(2);
    result[0] = pixel[0];
    result[1] = pixel[1];
    return result;
}

array<double>^ Bridge::GetFaceNormal(int faceId)
{
    double probe[6] = {0, 0, 0, 0, 0, 0};
    if (!GetFaceProbeNative(faceId, probe)) return nullptr;
    array<double>^ result = gcnew array<double>(6);
    for (int i = 0; i < 6; ++i) result[i] = probe[i];
    return result;
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
