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
#include <STEPControl_Writer.hxx>
#include <STEPControl_StepModelType.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Wire.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Vertex.hxx>
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
#include <Geom_BSplineCurve.hxx>
#include <Geom_Surface.hxx>
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRep_Builder.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepProj_Projection.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <ShapeAnalysis_FreeBounds.hxx>
#include <TopTools_HSequenceOfShape.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <IntCurvesFace_ShapeIntersector.hxx>
#include <Bnd_Box.hxx>
#include <BRepBndLib.hxx>
#include <Standard_Failure.hxx>
#include <Precision.hxx>
#include <TopLoc_Location.hxx>
#include <gp_Pnt.hxx>
#include <gp_Pnt2d.hxx>
#include <gp_Ax2.hxx>
#include <gp_Circ.hxx>
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
#include <TColgp_Array1OfPnt.hxx>
#include <TColStd_Array1OfInteger.hxx>
#include <TColStd_Array1OfReal.hxx>

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
#include <GCPnts_AbscissaPoint.hxx>
#include <GCPnts_QuasiUniformDeflection.hxx>

// 메싱 (W2 — shaded 렌더링 실패 원인 대응) — TKMesh
#include <BRepMesh_IncrementalMesh.hxx>
#include <Poly_Triangulation.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <gp_Pln.hxx>
#include <gp_Ax3.hxx>
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
#include <functional>
#include <cctype>
#include <exception>
#include <limits>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <map>
#include <memory>
#include <stdexcept>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "NativeDocumentSession.h"

using namespace System;
using namespace System::Collections::Generic;

#pragma managed(push, off)
namespace
{
    using HeatingMoldNative::NativeDocumentSession;
    using HeatingMoldNative::HeatingPathSegmentOverlay;
    using HeatingMoldNative::PreparedPathSegmentGeometry;
    using HeatingMoldNative::StagedPreparedPathSegmentGeometry;
    using HeatingMoldNative::StagedWorkingPolyline;
    using HeatingMoldNative::WorkingCoordinateSystemPresentationNative;
    using HeatingMoldNative::WorkingNative;

    struct PreparedDocumentCandidate final
    {
        std::unique_ptr<NativeDocumentSession> document;
        long long baseModelRevision;
    };

    std::unique_ptr<NativeDocumentSession> g_documentSession =
        std::make_unique<NativeDocumentSession>();
    std::map<long long, PreparedDocumentCandidate> g_preparedDocumentCandidates;
    long long g_nextPreparedDocumentCandidateId = 1;
    void InvalidateWorkingSurfaceContexts(NativeDocumentSession& document);

    long long AllocatePreparedDocumentCandidateId()
    {
        const long long firstCandidateId = g_nextPreparedDocumentCandidateId > 0
            ? g_nextPreparedDocumentCandidateId
            : 1;
        long long candidateId = firstCandidateId;

        do
        {
            if (g_preparedDocumentCandidates.find(candidateId) == g_preparedDocumentCandidates.end())
            {
                g_nextPreparedDocumentCandidateId = candidateId == std::numeric_limits<long long>::max()
                    ? 1
                    : candidateId + 1;
                return candidateId;
            }

            candidateId = candidateId == std::numeric_limits<long long>::max()
                ? 1
                : candidateId + 1;
        }
        while (candidateId != firstCandidateId);

        return 0;
    }

    bool TryGetSupplementalAxes(
        NativeDocumentSession& document,
        const Handle(StepData_StepModel)& model)
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
                document.autoAxes[0] = gp_Dir(x);
                document.autoAxes[1] = gp_Dir(y);
                document.autoAxes[2] = gp_Dir(z);
                return true;
            }
        }
        return false;
    }

    void ComputeAutoAxes(
        NativeDocumentSession& document,
        const Handle(StepData_StepModel)& stepModel)
    {
        document.autoAxes[0] = gp::DX();
        document.autoAxes[1] = gp::DY();
        document.autoAxes[2] = gp::DZ();
        if (TryGetSupplementalAxes(document, stepModel)) return;
        if (document.shape.IsNull()) return;
        GProp_GProps props;
        bool hasSolid = false;
        for (TopExp_Explorer ex(document.shape, TopAbs_SOLID); ex.More(); ex.Next()) { hasSolid = true; break; }
        if (hasSolid)
            BRepGProp::VolumeProperties(document.shape, props, Standard_True, Standard_True, Standard_False);
        if (!hasSolid || std::fabs(props.Mass()) <= 1e-12)
        {
            props = GProp_GProps();
            BRepGProp::SurfaceProperties(document.shape, props, Standard_True, Standard_False);
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
        for (int i = 0; i < 3; ++i) document.autoAxes[i] = gp_Dir(axes[i]);
    }

    void AlignShapeToWorldAxes(NativeDocumentSession& document)
    {
        // Express every point in the detected model frame. Keep the origin fixed;
        // only remove the model's rotational misalignment.
        const gp_Dir& x = document.autoAxes[0];
        const gp_Dir& y = document.autoAxes[1];
        const gp_Dir& z = document.autoAxes[2];
        gp_Trsf rotation;
        rotation.SetValues(
            x.X(), x.Y(), x.Z(), 0.0,
            y.X(), y.Y(), y.Z(), 0.0,
            z.X(), z.Y(), z.Z(), 0.0);
        BRepBuilderAPI_Transform aligned(document.shape, rotation, Standard_True);
        document.shape = aligned.Shape();
        document.autoAxes[0] = gp::DX();
        document.autoAxes[1] = gp::DY();
        document.autoAxes[2] = gp::DZ();
    }

    bool GetAutoAxisNative(int axis, double* out)
    {
        if (axis < 0 || axis > 2 || out == nullptr) return false;
        out[0] = g_documentSession->autoAxes[axis].X();
        out[1] = g_documentSession->autoAxes[axis].Y();
        out[2] = g_documentSession->autoAxes[axis].Z();
        return true;
    }

    // ---- AIS 뷰어 상태 (S1 항목 1·6) — 프로세스 전역, UI 스레드 전용 ----
    Handle(Aspect_DisplayConnection) g_disp;
    Handle(OpenGl_GraphicDriver) g_driver;
    Handle(V3d_Viewer) g_viewer;
    Handle(AIS_InteractiveContext) g_ctx;
    Handle(V3d_View) g_view;
    Handle(WNT_Window) g_window;
    bool g_viewInitialized = false;
    void HighlightCircuitNative(const std::vector<int>& ids);
    Handle(AIS_RubberBand) g_selectionRubberBand;

    void ResetViewerResources()
    {
        g_viewInitialized = false;
        g_view.Nullify();
        g_ctx.Nullify();
        g_window.Nullify();
        g_viewer.Nullify();
        g_driver.Nullify();
        g_disp.Nullify();
    }

    void RebuildFaceCache(NativeDocumentSession& document)
    {
        document.faces.clear();
        document.faceMap.Clear();
        document.faceAdjacency.clear();
        document.faceAdjacencyRevision = -1;
        if (document.shape.IsNull()) return;
        for (TopExp_Explorer ex(document.shape, TopAbs_FACE); ex.More(); ex.Next())
        {
            const TopoDS_Face f = TopoDS::Face(ex.Current());
            document.faces.push_back(f);
            document.faceMap.Add(f);   // faces와 같은 순회이므로 FindIndex는 배열 인덱스 + 1입니다.
        }
    }

    void UpdateSelectedFaceColors()
    {
        if (g_ctx.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return;
        g_documentSession->presentation.faceHighlightsVisible = true;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_documentSession->presentation.aisShape);
        if (colored.IsNull()) return;

        std::set<int> current;
        for (g_ctx->InitSelected(); g_ctx->MoreSelected(); g_ctx->NextSelected())
        {
            const Handle(StdSelect_BRepOwner)& owner =
                Handle(StdSelect_BRepOwner)::DownCast(g_ctx->SelectedOwner());
            if (owner.IsNull() || !owner->HasShape() || !g_documentSession->faceMap.Contains(owner->Shape())) continue;
            current.insert(g_documentSession->faceMap.FindIndex(owner->Shape()) - 1);
        }
        for (int faceId : g_documentSession->presentation.selectedFaceIds)
            if (current.count(faceId) == 0 && faceId >= 0 && faceId < static_cast<int>(g_documentSession->faces.size()))
                colored->UnsetCustomAspects(g_documentSession->faces[faceId], Standard_True);
        for (int faceId : current)
            if (g_documentSession->presentation.selectedFaceIds.count(faceId) == 0 && faceId >= 0 && faceId < static_cast<int>(g_documentSession->faces.size()))
            {
                colored->SetCustomColor(g_documentSession->faces[faceId], Quantity_Color(0.12, 0.82, 0.38, Quantity_TOC_RGB));
                colored->SetCustomTransparency(g_documentSession->faces[faceId], 0.0);
                Handle(AIS_ColoredDrawer) faceStyle = colored->CustomAspects(g_documentSession->faces[faceId]);
                faceStyle->SetupOwnShadingAspect();
                faceStyle->SetDisplayMode(AIS_Shaded);
                faceStyle->ShadingAspect()->SetColor(Quantity_Color(0.12, 0.82, 0.38, Quantity_TOC_RGB));
                faceStyle->ShadingAspect()->SetTransparency(0.0);
                // The green face is coincident with the model face. Bias its fill
                // toward the camera to avoid depth-buffer z-fighting.
                faceStyle->ShadingAspect()->Aspect()->SetPolygonOffsets(Aspect_POM_Fill, -2.0f, -2.0f);
            }
        g_documentSession->presentation.selectedFaceIds.swap(current);
        g_ctx->Redisplay(g_documentSession->presentation.aisShape, Standard_False);
    }

    void HideFaceHighlightsNative()
    {
        if (g_ctx.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_documentSession->presentation.aisShape);
        if (colored.IsNull()) return;
        // Remove presentation overrides, not selected owners: Ctrl selection and
        // pattern regeneration must retain the current selection.
        for (int faceId : g_documentSession->presentation.selectedFaceIds)
            if (faceId >= 0 && faceId < static_cast<int>(g_documentSession->faces.size()))
                colored->UnsetCustomAspects(g_documentSession->faces[faceId], Standard_True);
        g_documentSession->presentation.selectedFaceIds.clear();
        g_documentSession->presentation.faceHighlightsVisible = false;
        g_ctx->UnhilightSelected(Standard_False);
        g_ctx->ClearDetected(Standard_False);
        g_documentSession->presentation.lastHoverFace = -1;
        g_ctx->Redisplay(g_documentSession->presentation.aisShape, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
    }

    /// <summary>W2 — STEP 로드는 메시 미생성이므로 shaded 표시를 위해 삼각분할 1회 수행</summary>
    void MeshShapeNative(NativeDocumentSession& document, double linearDeflection)
    {
        if (document.shape.IsNull()) return;
        BRepMesh_IncrementalMesh mesher(document.shape, linearDeflection, Standard_False, 0.5, Standard_False);
        (void)mesher;   // 생성자가 Perform까지 수행
    }

    /// <summary>W2/W3 — 표시 상태(모드/경계/색) 구성 + 면 선택 모드 활성화</summary>
    void ConfigureDisplay()
    {
        if (g_ctx.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return;
        g_documentSession->presentation.aisShape->Attributes()->SetFaceBoundaryDraw(Standard_True);
        g_ctx->Display(g_documentSession->presentation.aisShape, Standard_False);
        g_ctx->SetDisplayMode(g_documentSession->presentation.aisShape, AIS_Shaded, Standard_False);   // W2 — 기본 와이어프레임 무시, 음영 강제
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
        g_ctx->Activate(g_documentSession->presentation.aisShape, TopAbs_FACE, Standard_True);          // W3 — 면 서브셰이프 선택 활성화
        g_documentSession->presentation.selectedFaceIds.clear();
        g_documentSession->presentation.faceHighlightsVisible = true;
    }

    bool DisplayPreparedPathSegmentsNative(NativeDocumentSession& document)
    {
        if (document.preparedPathSegments.empty() || g_ctx.IsNull())
            return true;

        std::map<int, HeatingPathSegmentOverlay> nextPresentationPaths = document.presentation.pathSegments;
        std::map<int, HeatingPathSegmentOverlay> newPresentationPaths;
        std::map<int, Handle(AIS_Shape)> nextConnectionPaths = document.presentation.connectionPaths;
        std::map<int, Handle(AIS_Shape)> newConnectionPaths;
        std::vector<Handle(AIS_Shape)> displayedShapes;
        std::vector<Handle(AIS_Shape)> replacedShapes;

        try
        {
            for (const auto& preparedEntry : document.preparedPathSegments)
            {
                const int segmentId = preparedEntry.first;
                const auto& prepared = preparedEntry.second;
                if (segmentId < 0 || prepared.wire.IsNull())
                    return false;

                const Quantity_Color color = prepared.connectionPathSegment
                    ? Quantity_Color(1.0, 0.65, 0.05, Quantity_TOC_RGB)
                    : prepared.turnSegment
                        ? Quantity_Color(0.1, 0.65, 1.0, Quantity_TOC_RGB)
                        : Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB);
                Handle(AIS_Shape) ais = new AIS_Shape(prepared.wire);
                g_ctx->SetColor(ais, color, Standard_False);
                if (prepared.connectionPathSegment)
                {
                    g_ctx->SetWidth(ais, 3.0, Standard_False);
                    auto heatingPath = nextPresentationPaths.find(segmentId);
                    if (heatingPath != nextPresentationPaths.end())
                    {
                        replacedShapes.push_back(heatingPath->second.ais);
                        nextPresentationPaths.erase(heatingPath);
                    }

                    auto insertion = nextConnectionPaths.emplace(segmentId, ais);
                    if (!insertion.second)
                    {
                        replacedShapes.push_back(insertion.first->second);
                        insertion.first->second = ais;
                    }
                    newConnectionPaths.emplace(segmentId, ais);
                    continue;
                }

                auto connectionPath = nextConnectionPaths.find(segmentId);
                if (connectionPath != nextConnectionPaths.end())
                {
                    replacedShapes.push_back(connectionPath->second);
                    nextConnectionPaths.erase(connectionPath);
                }
                if (prepared.turnSegment)
                    ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);

                auto insertion = nextPresentationPaths.emplace(
                    segmentId,
                    HeatingPathSegmentOverlay{ ais, prepared.turnSegment, true, color, 1.0 });
                if (!insertion.second)
                {
                    replacedShapes.push_back(insertion.first->second.ais);
                    insertion.first->second = HeatingPathSegmentOverlay{ ais, prepared.turnSegment, true, color, 1.0 };
                }

                newPresentationPaths.emplace(
                    segmentId,
                    HeatingPathSegmentOverlay{ ais, prepared.turnSegment, true, color, 1.0 });
            }

            for (const auto& newPath : newPresentationPaths)
            {
                const Handle(AIS_Shape)& ais = newPath.second.ais;
                displayedShapes.push_back(ais);
                g_ctx->Display(ais, Standard_False);
                g_ctx->SetZLayer(ais, Graphic3d_ZLayerId_Topmost);
                g_ctx->Activate(ais, Standard_False);
            }

            for (const auto& newPath : newConnectionPaths)
            {
                const Handle(AIS_Shape)& ais = newPath.second;
                displayedShapes.push_back(ais);
                g_ctx->Display(ais, Standard_False);
                g_ctx->SetZLayer(ais, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(ais, Standard_False);
            }

            for (const Handle(AIS_Shape)& replacedShape : replacedShapes)
            {
                if (!replacedShape.IsNull())
                    g_ctx->Remove(replacedShape, Standard_False);
            }

            document.presentation.pathSegments.swap(nextPresentationPaths);
            document.presentation.connectionPaths.swap(nextConnectionPaths);
            return true;
        }
        catch (...)
        {
            for (const Handle(AIS_Shape)& displayedShape : displayedShapes)
            {
                try
                {
                    g_ctx->Remove(displayedShape, Standard_False);
                }
                catch (...) { }
            }

            return false;
        }
    }

    void RemovePreparedPathPresentationNative(NativeDocumentSession& document)
    {
        for (const auto& preparedPath : document.preparedPathSegments)
        {
            const int segmentId = preparedPath.first;
            if (preparedPath.second.connectionPathSegment)
            {
                auto connectionPath = document.presentation.connectionPaths.find(segmentId);
                if (connectionPath == document.presentation.connectionPaths.end())
                    continue;

                if (!g_ctx.IsNull() && !connectionPath->second.IsNull())
                {
                    try
                    {
                        g_ctx->Remove(connectionPath->second, Standard_False);
                    }
                    catch (...) { }
                }

                document.presentation.connectionPaths.erase(connectionPath);
                continue;
            }

            auto overlay = document.presentation.pathSegments.find(segmentId);
            if (overlay == document.presentation.pathSegments.end())
                continue;

            if (!g_ctx.IsNull() && !overlay->second.ais.IsNull())
            {
                try
                {
                    g_ctx->Remove(overlay->second.ais, Standard_False);
                }
                catch (...) { }
            }

            document.presentation.pathSegments.erase(overlay);
        }
    }

    bool RebuildModelPresentationNative()
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_documentSession->shape.IsNull())
            return false;

        auto& presentation = g_documentSession->presentation;
        try
        {
            if (!presentation.aisShape.IsNull())
                g_ctx->Remove(presentation.aisShape, Standard_False);
            presentation.aisShape.Nullify();
            presentation.aisShape = new AIS_ColoredShape(g_documentSession->shape);
            ConfigureDisplay();
            if (!DisplayPreparedPathSegmentsNative(*g_documentSession))
                throw std::runtime_error("PreparedPathPresentationFailed");
            g_view->FitAll(0.01, Standard_False);
            g_view->Redraw();
            g_documentSession->presentationOutOfSync = false;
            return true;
        }
        catch (...)
        {
            g_documentSession->presentationOutOfSync = true;
            RemovePreparedPathPresentationNative(*g_documentSession);
            try
            {
                if (!presentation.aisShape.IsNull())
                    g_ctx->Remove(presentation.aisShape, Standard_False);
            }
            catch (...) { }
            presentation.aisShape.Nullify();
            return false;
        }
    }

    void ClearDisplayedShape(NativeDocumentSession& document)
    {
        if (!g_ctx.IsNull())
        {
            g_ctx->ClearSelected(Standard_False);   // 이전 선택 해제 (해당 프레젠테이션 제거 전)
            g_ctx->ClearDetected(Standard_False);
        }
        if (!document.presentation.aisShape.IsNull())
        {
            if (!g_ctx.IsNull())
                g_ctx->Remove(document.presentation.aisShape, Standard_False);
            document.presentation.aisShape.Nullify();
        }
        if (!document.presentation.qualityIssueLocation.IsNull())
        {
            if (!g_ctx.IsNull())
                g_ctx->Remove(document.presentation.qualityIssueLocation, Standard_False);
            document.presentation.qualityIssueLocation.Nullify();
        }
        document.presentation.selectedFaceIds.clear();
        document.presentation.lastHoverFace = -1;
    }

    void ClearDocumentPresentation(NativeDocumentSession& document)
    {
        ClearDisplayedShape(document);

        if (!g_ctx.IsNull())
        {
            for (const auto& path : document.presentation.connectionPaths)
                g_ctx->Remove(path.second, Standard_False);
            for (const auto& path : document.presentation.pathSegments)
                g_ctx->Remove(path.second.ais, Standard_False);
            for (const auto& overlay : document.presentation.worldOverlays)
                g_ctx->Remove(overlay, Standard_False);
            for (const auto& overlay : document.presentation.exclusionOverlays)
                g_ctx->Remove(overlay, Standard_False);
            if (!document.presentation.connectionPathPreview.IsNull())
                g_ctx->Remove(document.presentation.connectionPathPreview, Standard_False);
            if (!document.presentation.connectionPointPreview.IsNull())
                g_ctx->Remove(document.presentation.connectionPointPreview, Standard_False);
            if (!document.presentation.controlPointEditPreview.IsNull())
                g_ctx->Remove(document.presentation.controlPointEditPreview, Standard_False);
            if (!document.presentation.exclusionPreview.IsNull())
                g_ctx->Remove(document.presentation.exclusionPreview, Standard_False);
        }

        document.presentation.connectionPaths.clear();
        document.presentation.pathSegments.clear();
        document.presentation.worldOverlays.clear();
        document.presentation.exclusionOverlays.clear();
        document.presentation.connectionPathPreview.Nullify();
        document.presentation.connectionPointPreview.Nullify();
        document.presentation.controlPointEditPreview.Nullify();
        document.presentation.exclusionPreview.Nullify();
        document.presentation.hoveredCircuitPieces.clear();
        document.presentation.faceHighlightsVisible = true;

        if (!g_view.IsNull())
            g_view->Redraw();
    }

    enum class NativeModelLoadStatus
    {
        FailedBeforeCommit,
        Committed,
        CommittedPresentationOutOfSync
    };

    NativeModelLoadStatus ClassifyLoadException(
        NativeDocumentSession* documentBeforeLoad,
        std::string& diagnostic)
    {
        if (g_documentSession.get() != documentBeforeLoad)
        {
            g_documentSession->presentationOutOfSync = true;
            diagnostic = "StepLoadPostCommitFailure";
            return NativeModelLoadStatus::CommittedPresentationOutOfSync;
        }

        diagnostic = "StepLoadFailedBeforeCommit";
        return NativeModelLoadStatus::FailedBeforeCommit;
    }

    bool PrepareStepFile(
        const char* path,
        std::unique_ptr<NativeDocumentSession>& candidateSession,
        long long& baseModelRevision,
        std::string& diagnostic)
    {
        baseModelRevision = g_documentSession->modelRevision;
        STEPControl_Reader reader;
        TopoDS_Shape candidateShape;
        try
        {
            if (reader.ReadFile(path) != IFSelect_RetDone)
            {
                diagnostic = "StepReadFailed";
                return false;
            }
            reader.TransferRoots();
            candidateShape = reader.OneShape();
        }
        catch (...)
        {
            diagnostic = "StepReadFailed";
            return false;
        }
        if (candidateShape.IsNull())
        {
            diagnostic = "StepContainsNoShape";
            return false;
        }

        auto preparedSession = std::make_unique<NativeDocumentSession>();
        preparedSession->shape = candidateShape;
        preparedSession->modelRevision = baseModelRevision;
        try
        {
            ComputeAutoAxes(*preparedSession, reader.StepModel());
            AlignShapeToWorldAxes(*preparedSession);
            RebuildFaceCache(*preparedSession);
            MeshShapeNative(*preparedSession, 1.0);   // 음영 표시용 삼각분할
        }
        catch (...)
        {
            diagnostic = "CandidateGeometryPreparationFailed";
            return false;
        }

        candidateSession = std::move(preparedSession);
        return true;
    }

    NativeModelLoadStatus PublishPreparedDocument(
        std::unique_ptr<NativeDocumentSession> candidateSession,
        long long baseModelRevision,
        std::string& diagnostic)
    {
        if (!candidateSession || baseModelRevision != g_documentSession->modelRevision)
        {
            diagnostic = "PreparedCandidateStale";
            return NativeModelLoadStatus::FailedBeforeCommit;
        }

        if (!candidateSession->stagedPreparedPathSegments.empty())
        {
            diagnostic = "UncommittedPreparedPathSegments";
            return NativeModelLoadStatus::FailedBeforeCommit;
        }

        if (baseModelRevision == std::numeric_limits<long long>::max())
        {
            diagnostic = "ModelRevisionExhausted";
            return NativeModelLoadStatus::FailedBeforeCommit;
        }

        // 후보 검증에서 만든 작업 context는 확정된 새 revision에 재사용하지 않습니다.
        candidateSession->workingContexts.clear();
        candidateSession->modelRevision = baseModelRevision + 1;

        // 후보 기하를 준비하는 동안 활성 문서가 바뀌지 않았을 때만 한 번에 교체합니다.
        g_documentSession.swap(candidateSession);
        bool presentationSynchronized = true;
        try
        {
            ClearDocumentPresentation(*candidateSession);
        }
        catch (...)
        {
            presentationSynchronized = false;
            diagnostic = "PreviousPresentationCleanupFailed";
        }

        try
        {
            InvalidateWorkingSurfaceContexts(*candidateSession);
        }
        catch (...)
        {
            presentationSynchronized = false;
            diagnostic = "PreviousWorkingContextCleanupFailed";
        }

        // 뷰어가 이미 초기화된 경우 즉시 표시 (S1 항목 1)
        if (!g_ctx.IsNull())
        {
            try
            {
                g_documentSession->presentation.aisShape = new AIS_ColoredShape(g_documentSession->shape);
                ConfigureDisplay();
                if (!DisplayPreparedPathSegmentsNative(*g_documentSession))
                {
                    presentationSynchronized = false;
                    diagnostic = "PreparedPathPresentationFailed";
                }
                if (!g_view.IsNull())
                {
                    g_view->FitAll(0.01, Standard_False);
                    g_view->Redraw();
                }
            }
            catch (...)
            {
                presentationSynchronized = false;
                diagnostic = "ActiveModelPresentationFailed";
                RemovePreparedPathPresentationNative(*g_documentSession);
                try
                {
                    if (!g_documentSession->presentation.aisShape.IsNull())
                        g_ctx->Remove(g_documentSession->presentation.aisShape, Standard_False);
                }
                catch (...) { }
                g_documentSession->presentation.aisShape.Nullify();
            }
        }

        g_documentSession->presentationOutOfSync = !presentationSynchronized;
        return presentationSynchronized
            ? NativeModelLoadStatus::Committed
            : NativeModelLoadStatus::CommittedPresentationOutOfSync;
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
        if (g_documentSession->shape.IsNull()) return c;
        for (TopExp_Explorer ex(g_documentSession->shape, TopAbs_SOLID); ex.More(); ex.Next()) ++c.solids;
        for (TopExp_Explorer ex(g_documentSession->shape, TopAbs_SHELL); ex.More(); ex.Next()) ++c.shells;
        c.faces = (int)g_documentSession->faces.size();
        for (size_t i = 0; i < g_documentSession->faces.size(); ++i)
            if (BRepAdaptor_Surface(g_documentSession->faces[i]).GetType() == GeomAbs_Plane) ++c.planes;
        return c;
    }

    struct NativeWire
    {
        bool isOuter;
        bool closed;
        std::vector<double> pts;   // flat: u0,v0,u1,v1,...
    };

    bool ExtractUvWires(const NativeDocumentSession& document, int faceIndex, std::vector<NativeWire>& out)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(document.faces.size())) return false;
        const TopoDS_Face& face = document.faces[faceIndex];
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
            if (id < 0 || id >= static_cast<int>(g_documentSession->faces.size())) return false;
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
        for (int faceId : selected) sewing.Add(g_documentSession->faces[faceId]);
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

    bool SamplePoint(const NativeDocumentSession& document, int faceIndex, double u, double v, double* out)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(document.faces.size())) return false;
        BRepAdaptor_Surface surf(document.faces[faceIndex]);
        const gp_Pnt p = surf.Value(u, v);
        out[0] = p.X(); out[1] = p.Y(); out[2] = p.Z();
        return true;
    }

    const char* FaceTypeName(const NativeDocumentSession& document, int faceIndex)
    {
        if (faceIndex < 0 || faceIndex >= static_cast<int>(document.faces.size())) return nullptr;
        return SurfaceTypeName(BRepAdaptor_Surface(document.faces[faceIndex]).GetType());
    }

    // ================= 뷰어 natives (S1 항목 1·6) =================

    bool InitViewerNative(void* hwnd)
    {
        if (hwnd == nullptr) return false;

        // HwndHost는 창을 다시 만들 수 있으므로 기존 OCCT 뷰가 있으면 새 HWND에 연결한다.
        if (!g_view.IsNull() && g_viewInitialized)
        {
            try
            {
                Handle(WNT_Window) window = new WNT_Window((Aspect_Handle)hwnd);
                g_view->SetWindow(window);
                if (!window->IsMapped()) window->Map();
                g_window = window;
                g_view->MustBeResized();
                g_view->Redraw();
                return true;
            }
            catch (const Standard_Failure&)
            {
                return false;
            }
        }

        // 이전 초기화가 중간에 실패한 경우 불완전한 전역 핸들을 버리고 다시 구성한다.
        ResetViewerResources();

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

            if (!g_documentSession->shape.IsNull())
            {
                if (g_documentSession->presentation.aisShape.IsNull())
                    g_documentSession->presentation.aisShape = new AIS_ColoredShape(g_documentSession->shape);
                ConfigureDisplay();
            }
            g_view->MustBeResized();
            g_view->FitAll(0.01, Standard_False);
            g_view->Redraw();
            g_viewInitialized = true;
            return true;
        }
        catch (const Standard_Failure& f)
        {
            if (diag.is_open())
            {
                diag << "[bridge] Standard_Failure: " << f.GetMessageString() << "\n";
                diag.flush();
            }
            ResetViewerResources();
            return false;
        }
    }

    void ShutdownNative()
    {
        // 후보와 활성 문서가 보유한 기하를 먼저 해제한 뒤 Viewer의 표시 context를 해제한다.
        g_preparedDocumentCandidates.clear();
        g_documentSession.reset();
        g_selectionRubberBand.Nullify();
        ResetViewerResources();
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
        if (g_ctx.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return;
        g_ctx->SetColor(g_documentSession->presentation.aisShape, Quantity_Color(r, g, b, Quantity_TOC_RGB), Standard_False);
        g_view->Redraw();
    }

    // ================= 오버레이 natives (S3) =================

    bool ProjectHeatingPathSegmentNative(int pieceId, const std::string& kind, const std::vector<double>& xyz,
        const std::vector<int>& faceIds, const gp_Dir& direction, double depth, double* result)
    {
        if (g_documentSession->shape.IsNull() || xyz.size() < 6 || xyz.size() % 3 || faceIds.empty() ||
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
                if (id < 0 || id >= static_cast<int>(g_documentSession->faces.size())) return false;
                if (unique.insert(id).second) sewing.Add(g_documentSession->faces[id]);
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
            // OCCT may reverse the resulting wire. Results must follow the source heating-path order.
            if (transverseDistance(a, sourceA) > 0.01 || transverseDistance(b, sourceB) > 0.01) std::swap(a, b);
            if (transverseDistance(a, sourceA) > 0.01 || transverseDistance(b, sourceB) > 0.01) return false;
            const gp_Vec offset = gp_Vec(direction) * depth;
            const gp_Pnt shownA = a.Translated(offset), shownB = b.Translated(offset);
            result[0] = props.Mass(); result[1] = shownA.X(); result[2] = shownA.Y(); result[3] = shownA.Z();
            result[4] = shownB.X(); result[5] = shownB.Y(); result[6] = shownB.Z();
            std::string normalizedKind = kind;
            std::transform(normalizedKind.begin(), normalizedKind.end(), normalizedKind.begin(),
                [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            // Only inter-lane turn segments are editable. Fixed same-lane joins
            // remain part of the heating path and cannot be toggled.
            const bool turnSegment = normalizedKind == "turnsegment" || normalizedKind == "turn";
            if (!g_ctx.IsNull()) {
                auto old = g_documentSession->presentation.pathSegments.find(pieceId);
                if (old != g_documentSession->presentation.pathSegments.end()) { g_ctx->Remove(old->second.ais, Standard_False); g_documentSession->presentation.pathSegments.erase(old); }
                Handle(AIS_Shape) ais = new AIS_Shape(projected);
                const Quantity_Color color = turnSegment ? Quantity_Color(0.1, 0.65, 1.0, Quantity_TOC_RGB) : Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB);
                g_ctx->SetColor(ais, color, Standard_False);
                if (turnSegment) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
                g_ctx->Display(ais, Standard_False); g_ctx->SetZLayer(ais, Graphic3d_ZLayerId_Topmost);
                // Every path segment remains selectable for circuit hover.
                g_ctx->Activate(ais, Standard_False);
                g_documentSession->presentation.pathSegments[pieceId] = { ais, turnSegment, true, color, 1.0 };
            }
            return true;
        } catch (const Standard_Failure&) { return false; }
    }

    bool SetTurnSegmentNative(int id, bool enabled, double r, double g, double b)
    {
        auto it = g_documentSession->presentation.pathSegments.find(id);
        if (it == g_documentSession->presentation.pathSegments.end() || !it->second.turnSegment || g_ctx.IsNull()) return false;
        it->second.enabled = enabled;
        it->second.color = Quantity_Color(r, g, b, Quantity_TOC_RGB);
        g_ctx->SetColor(it->second.ais, it->second.color, Standard_False);
        it->second.ais->Attributes()->LineAspect()->SetTypeOfLine(enabled ? Aspect_TOL_SOLID : Aspect_TOL_DASH);
        g_ctx->Redisplay(it->second.ais, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void ClearPathSegmentsNative()
    {
        HighlightCircuitNative(std::vector<int>());
        if (!g_ctx.IsNull()) for (const auto& piece : g_documentSession->presentation.pathSegments) g_ctx->Remove(piece.second.ais, Standard_False);
        g_documentSession->presentation.pathSegments.clear();
        g_documentSession->stagedWorkingPathSegmentOwners.clear();
    }

    void ClearConnectionPathPreviewNative()
    {
        if (!g_ctx.IsNull()) {
            if (!g_documentSession->presentation.connectionPathPreview.IsNull()) g_ctx->Remove(g_documentSession->presentation.connectionPathPreview, Standard_False);
            if (!g_documentSession->presentation.connectionPointPreview.IsNull()) g_ctx->Remove(g_documentSession->presentation.connectionPointPreview, Standard_False);
        }
        g_documentSession->presentation.connectionPathPreview.Nullify();
        g_documentSession->presentation.connectionPointPreview.Nullify();
    }

    void ClearControlPointEditPreviewNative()
    {
        if (!g_ctx.IsNull() && !g_documentSession->presentation.controlPointEditPreview.IsNull())
            g_ctx->Remove(g_documentSession->presentation.controlPointEditPreview, Standard_False);
        g_documentSession->presentation.controlPointEditPreview.Nullify();
    }

    bool SetControlPointEditPreviewNative(const std::vector<std::vector<double>>& polylines)
    {
        ClearControlPointEditPreviewNative();
        if (g_ctx.IsNull() || polylines.empty())
            return false;

        TopoDS_Compound preview;
        BRep_Builder compoundBuilder;
        compoundBuilder.MakeCompound(preview);
        for (const std::vector<double>& coordinates : polylines)
        {
            if (coordinates.size() < 6 || coordinates.size() % 3 != 0)
                return false;

            BRepBuilderAPI_MakePolygon polygon;
            for (size_t index = 0; index < coordinates.size(); index += 3)
            {
                const double x = coordinates[index];
                const double y = coordinates[index + 1];
                const double z = coordinates[index + 2];
                if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z))
                    return false;

                polygon.Add(gp_Pnt(x, y, z));
            }
            if (!polygon.IsDone())
                return false;

            compoundBuilder.Add(preview, polygon.Wire());
        }

        if (preview.IsNull())
            return false;

        Handle(AIS_Shape) overlay = new AIS_Shape(preview);
        g_ctx->SetColor(overlay, Quantity_Color(0.05, 0.85, 0.9, Quantity_TOC_RGB), Standard_False);
        g_ctx->SetWidth(overlay, 2.5, Standard_False);
        g_ctx->Display(overlay, Standard_False);
        g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
        g_ctx->Deactivate(overlay, Standard_False);
        g_documentSession->presentation.controlPointEditPreview = overlay;
        return true;
    }

    void ClearConnectionPathsNative()
    {
        ClearConnectionPathPreviewNative();
        if (!g_ctx.IsNull()) for (const auto& connectionPath : g_documentSession->presentation.connectionPaths) g_ctx->Remove(connectionPath.second, Standard_False);
        g_documentSession->presentation.connectionPaths.clear();
    }

    // Validate the complete external segment, not just its destination. Projecting
    // onto the WHOLE mold catches even elevated/unselected faces and concave re-entry.
    bool ExternalConnectionPathIsClear(const gp_Pnt& from, const gp_Pnt& to, const gp_Dir& direction)
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
        for (const TopoDS_Face& face : g_documentSession->faces) {
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
        material.Load(g_documentSession->shape, tolerance);
        material.Perform(gp_Lin(from, gp_Dir(segment)), tolerance, segment.Magnitude());
        return material.IsDone() && material.NbPnt() == 0;
    }

    bool IsSelectedBoundaryPoint(const gp_Pnt& point, const std::vector<int>& faces, double& tolerance)
    {
        // Use model-space trim-edge distance, not a fixed UV classifier epsilon
        // (UV units need not be mm). Imported edge/vertex tolerances may exceed
        // the face tolerance; never exceed the heating path's 0.01 mm sewing budget.
        const TopoDS_Vertex query = BRepBuilderAPI_MakeVertex(point).Vertex();
        bool matched = false;
        for (int id : faces) {
            const TopoDS_Face& face = g_documentSession->faces[id];
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
    void ProjectConnectionPathNative(int connectionPathId, const std::vector<double>& xyz, const std::vector<double>& actual,
        const std::vector<int>& faces, const gp_Dir& direction, double insertionDepthMm, double* result, bool previewOnly)
    {
        result[0] = -1;
        if (g_documentSession->shape.IsNull() || xyz.size() != 6 || actual.size() != 3 || faces.empty() ||
            !std::isfinite(insertionDepthMm) || insertionDepthMm < 0) return;
        for (double v : xyz) if (!std::isfinite(v)) return;
        for (double v : actual) if (!std::isfinite(v)) return;
        const gp_Pnt start(xyz[0], xyz[1], xyz[2]), destination(xyz[3], xyz[4], xyz[5]);
        const gp_Pnt expected(actual[0], actual[1], actual[2]);
        const gp_Vec axis(direction), offset = axis * insertionDepthMm;
        gp_Vec along(start, destination); along -= axis * along.Dot(axis);
        const double span = along.Magnitude();
        if (span < 0.01) return;
        along /= span;
        BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
        std::set<int> unique;
        for (int face : faces) {
            if (face < 0 || face >= static_cast<int>(g_documentSession->faces.size())) return;
            if (unique.insert(face).second) sewing.Add(g_documentSession->faces[face]);
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
            ExternalConnectionPathIsClear(expected, end, direction);
        if (!outwardBoundary) {
            // Plane destinations must remain outside the entire mold. Boundary
            // destinations are handled as surface endpoints, not axis-line hits.
            IntCurvesFace_ShapeIntersector outside;
            outside.Load(g_documentSession->shape, 1e-7);
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
        if (!ExternalConnectionPathIsClear(b, end, direction)) return;
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
            if (previewOnly) g_documentSession->presentation.connectionPathPreview = overlay;
            g_ctx->SetColor(overlay, previewOnly ? Quantity_Color(0.1, 0.9, 1.0, Quantity_TOC_RGB) :
                Quantity_Color(1.0, 0.65, 0.05, Quantity_TOC_RGB), Standard_False);
            if (previewOnly) {
                overlay->SetWidth(3.0);
                overlay->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
            }
            g_ctx->Display(overlay, Standard_False);
            g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost); g_ctx->Deactivate(overlay, Standard_False);
            if (previewOnly) {
                g_documentSession->presentation.connectionPointPreview = new AIS_Point(new Geom_CartesianPoint(end));
                g_documentSession->presentation.connectionPointPreview->SetMarker(Aspect_TOM_O_POINT);
                g_ctx->SetColor(g_documentSession->presentation.connectionPointPreview, Quantity_Color(0.1, 0.9, 1.0, Quantity_TOC_RGB), Standard_False);
                g_ctx->Display(g_documentSession->presentation.connectionPointPreview, Standard_False);
                g_ctx->SetZLayer(g_documentSession->presentation.connectionPointPreview, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(g_documentSession->presentation.connectionPointPreview, Standard_False);
            } else {
                auto old = g_documentSession->presentation.connectionPaths.find(connectionPathId);
                if (old != g_documentSession->presentation.connectionPaths.end()) g_ctx->Remove(old->second, Standard_False);
                g_documentSession->presentation.connectionPaths[connectionPathId] = overlay;
            }
        }
        result[0] = 1; result[1] = props.Mass(); result[2] = surfaceProps.Mass(); result[3] = b.Distance(end);
    }

    int PickTurnSegmentNative(int x, int y)
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);
        Handle(SelectMgr_EntityOwner) owner = g_ctx->DetectedOwner();
        if (owner.IsNull()) return -1;
        Handle(AIS_InteractiveObject) selectable = Handle(AIS_InteractiveObject)::DownCast(owner->Selectable());
        for (const auto& piece : g_documentSession->presentation.pathSegments)
            if (piece.second.turnSegment && piece.second.ais == selectable) return piece.first;
        return -1;
    }

    void HighlightCircuitNative(const std::vector<int>& ids)
    {
        if (g_ctx.IsNull()) return;
        for (int id : g_documentSession->presentation.hoveredCircuitPieces) {
            auto it = g_documentSession->presentation.pathSegments.find(id);
            if (it != g_documentSession->presentation.pathSegments.end()) {
                g_ctx->SetColor(it->second.ais, it->second.color, Standard_False);
                g_ctx->SetWidth(it->second.ais, it->second.width, Standard_False);
            }
        }
        g_documentSession->presentation.hoveredCircuitPieces.clear();
        for (int id : ids) {
            auto it = g_documentSession->presentation.pathSegments.find(id);
            if (it == g_documentSession->presentation.pathSegments.end()) continue;
            g_documentSession->presentation.hoveredCircuitPieces.insert(id);
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
        g_documentSession->presentation.exclusionOverlays.push_back(overlay);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void SetExclusionPreviewNative(const std::vector<double>& xyz)
    {
        if (!g_ctx.IsNull() && !g_documentSession->presentation.exclusionPreview.IsNull())
        {
            g_ctx->Remove(g_documentSession->presentation.exclusionPreview, Standard_False);
            g_documentSession->presentation.exclusionPreview.Nullify();
        }
        g_documentSession->presentation.exclusionPreview = MakeExclusionOverlay(xyz, Quantity_Color(0.95, 0.55, 0.05, Quantity_TOC_RGB));
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ClearExclusionOverlaysNative()
    {
        if (!g_ctx.IsNull())
        {
            for (const auto& overlay : g_documentSession->presentation.exclusionOverlays) g_ctx->Remove(overlay, Standard_False);
            if (!g_documentSession->presentation.exclusionPreview.IsNull()) g_ctx->Remove(g_documentSession->presentation.exclusionPreview, Standard_False);
        }
        g_documentSession->presentation.exclusionOverlays.clear();
        g_documentSession->presentation.exclusionPreview.Nullify();
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ClearQualityIssueLocationNative()
    {
        if (!g_documentSession)
            return;

        if (!g_documentSession->presentation.qualityIssueLocation.IsNull())
        {
            if (!g_ctx.IsNull())
                g_ctx->Remove(g_documentSession->presentation.qualityIssueLocation, Standard_False);
            g_documentSession->presentation.qualityIssueLocation.Nullify();
        }
    }

    bool ShowQualityIssueLocationNative(double x, double y, double z)
    {
        if (!g_documentSession || g_ctx.IsNull() ||
            !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z))
            return false;

        ClearQualityIssueLocationNative();
        Handle(AIS_Point) marker = new AIS_Point(new Geom_CartesianPoint(gp_Pnt(x, y, z)));
        marker->SetMarker(Aspect_TOM_O_POINT);
        g_documentSession->presentation.qualityIssueLocation = marker;
        g_ctx->SetColor(marker, Quantity_Color(1.0, 0.15, 0.05, Quantity_TOC_RGB), Standard_False);
        g_ctx->Display(marker, Standard_False);
        g_ctx->SetZLayer(marker, Graphic3d_ZLayerId_Topmost);
        g_ctx->Deactivate(marker, Standard_False);
        if (!g_view.IsNull())
            g_view->Redraw();
        return true;
    }

    void ClearOverlayNative()
    {
        ClearConnectionPathsNative();
        ClearControlPointEditPreviewNative();
        ClearQualityIssueLocationNative();
        ClearPathSegmentsNative();
        if (!g_ctx.IsNull())
        {
            for (const auto& overlay : g_documentSession->presentation.worldOverlays)
                g_ctx->Remove(overlay, Standard_False);
        }
        g_documentSession->presentation.worldOverlays.clear();
        for (const auto& staged : g_documentSession->stagedWorkingPolylines)
            if (!g_ctx.IsNull()) g_ctx->Remove(staged.second.ais,Standard_False);
        g_documentSession->stagedWorkingPolylines.clear();
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

    bool GetModelBoundsInFrameNative(const NativeDocumentSession& document,
                                     const gp_Dir& xAxis, const gp_Dir& yAxis,
                                     const gp_Dir& zAxis, double* out)
    {
        if (document.shape.IsNull() || out == nullptr) return false;
        try
        {
            Bnd_Box box;
            BRepBndLib::Add(document.shape, box);
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
        if (g_documentSession->shape.IsNull() || faceIds.empty() || xyz.size() < 6 || xyz.size() % 3 != 0 ||
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
                if (id < 0 || id >= static_cast<int>(g_documentSession->faces.size())) return false;
                if (uniqueFaces.insert(id).second) sewing.Add(g_documentSession->faces[id]);
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

            // Require projection coverage along the complete source heating path, not
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
            // mapped heating path; it is not a per-Face normal offset.
            TopoDS_Shape displayedPath = projectedWire;
            if (insertionDepth > 0.0)
            {
                gp_Trsf offset;
                offset.SetTranslation(gp_Vec(direction) * insertionDepth);
                BRepBuilderAPI_Transform transformed(projectedWire, offset, Standard_True);
                if (!transformed.IsDone()) return false;
                displayedPath = transformed.Shape();
            }

            if (!g_ctx.IsNull())
            {
                Handle(AIS_Shape) overlay = new AIS_Shape(displayedPath);
                g_ctx->SetColor(overlay, Quantity_Color(0.95, 0.15, 0.10, Quantity_TOC_RGB), Standard_False);
                g_ctx->Display(overlay, Standard_False);
                g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(overlay, Standard_False);
                g_documentSession->presentation.worldOverlays.push_back(overlay);
            }
            *outLength = length;
            *outEdgeCount = static_cast<int>(edgeCount);
            *outStatus = 8;
            return true;
        }
        catch (const Standard_Failure&) { if (outStatus) *outStatus = 99; return false; }
    }

    // ================= 면 픽킹/선택 (W3·W5) =================

    /// <summary>현재 DetectedOwner가 가리키는 면 인덱스 (g_documentSession->faces와 1:1) — 없으면 -1</summary>
    int DetectedFaceIndex()
    {
        if (g_ctx.IsNull()) return -1;
        const Handle(SelectMgr_EntityOwner)& owner = g_ctx->DetectedOwner();
        if (owner.IsNull()) return -1;
        const Handle(StdSelect_BRepOwner) brep = Handle(StdSelect_BRepOwner)::DownCast(owner);
        if (brep.IsNull() || !brep->HasShape()) return -1;
        const TopoDS_Shape& sub = brep->Shape();
        if (!g_documentSession->faceMap.Contains(sub)) return -1;
        return g_documentSession->faceMap.FindIndex(sub) - 1;
    }

    /// <summary>W5 — 호버: 동적 하이라이트. 면이 바뀔 때만 Redraw (무효화 폭주 방지)</summary>
    int HoverFaceNative(int x, int y)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return -1;
        if (!g_documentSession->presentation.faceHighlightsVisible) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);

        const int idx = DetectedFaceIndex();
        if (idx != g_documentSession->presentation.lastHoverFace)
        {
            g_documentSession->presentation.lastHoverFace = idx;
            g_view->Redraw();
        }
        return idx;
    }

    void ClearHoverNative()
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        if (g_documentSession->presentation.lastHoverFace == -1) return;
        g_documentSession->presentation.lastHoverFace = -1;
        g_ctx->ClearDetected(Standard_False);
        g_view->Redraw();
    }

    /// <summary>W5 — 클릭: 면 선택(선택 하이라이트 유지). 빈 곳이면 선택 해제 후 -1</summary>
    int SelectFaceNative(int x, int y, bool toggle)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return -1;
        g_ctx->MoveTo(x, y, g_view, Standard_False);
        const int idx = DetectedFaceIndex();
        g_documentSession->presentation.lastHoverFace = idx;
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
        if (g_ctx.IsNull() || g_view.IsNull() || g_documentSession->presentation.aisShape.IsNull()) return result;
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
            if (!g_documentSession->faceMap.Contains(brep->Shape())) continue;
            const int faceId = g_documentSession->faceMap.FindIndex(brep->Shape()) - 1;
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
                if (!brep.IsNull() && brep->HasShape() && g_documentSession->faceMap.Contains(brep->Shape()))
                {
                    const int faceId = g_documentSession->faceMap.FindIndex(brep->Shape()) - 1;
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
        g_documentSession->presentation.lastHoverFace = -1;
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

    bool PickSelectedBoundaryPointNative(const std::vector<int>& faces, int x, int y, double* out)
    {
        if (g_view.IsNull() || g_documentSession->shape.IsNull() || faces.empty() || out == nullptr) return false;
        try {
            std::set<int> unique;
            BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
            for (int id : faces) {
                if (id < 0 || id >= static_cast<int>(g_documentSession->faces.size())) return false;
                if (unique.insert(id).second) sewing.Add(g_documentSession->faces[id]);
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
            visibility.Load(g_documentSession->shape, 1e-7);
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
        if (g_documentSession->shape.IsNull()) return out;

        const gp_Dir n(nx, ny, nz);
        gp_Dir x(xx, xy, xz);
        if (std::fabs(n.Dot(x)) > 0.99)
        {
            x = n.Crossed(gp_Dir(0.0, 0.0, 1.0));
            if (x.X() * x.X() + x.Y() * x.Y() + x.Z() * x.Z() < 1e-12)
                x = n.Crossed(gp_Dir(1.0, 0.0, 0.0));
        }

        Handle(HLRBRep_Algo) algo = new HLRBRep_Algo();
        algo->Add(g_documentSession->shape);
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
#include "WorkingCoordinateSystemNative.inc"

    enum class FaceGeometryFeaturesStatusNative
    {
        Success,
        InvalidModel,
        GeometryFailure
    };

    struct FaceGeometryFeatureNative
    {
        int faceIndex;
        std::string surfaceType;
        double surfaceAreaMm2;
        gp_Pnt center;
        std::vector<int> adjacentFaceIndices;
    };

    bool CollectFaceGeometryFeaturesNative(
        NativeDocumentSession& document,
        std::vector<FaceGeometryFeatureNative>& features,
        FaceGeometryFeaturesStatusNative& status)
    {
        if (document.shape.IsNull() || document.faces.empty())
        {
            status = FaceGeometryFeaturesStatusNative::InvalidModel;
            return false;
        }

        if (!EnsureFaceAdjacencyNative(document, document.modelRevision))
        {
            status = FaceGeometryFeaturesStatusNative::GeometryFailure;
            return false;
        }

        try
        {
            std::vector<FaceGeometryFeatureNative> preparedFeatures;
            preparedFeatures.reserve(document.faces.size());

            for (int faceIndex = 0; faceIndex < static_cast<int>(document.faces.size()); ++faceIndex)
            {
                GProp_GProps properties;
                BRepGProp::SurfaceProperties(
                    document.faces[faceIndex],
                    properties,
                    Standard_True,
                    Standard_False);

                const double surfaceAreaMm2 = std::fabs(properties.Mass());
                const gp_Pnt center = properties.CentreOfMass();
                const char* surfaceType = FaceTypeName(document, faceIndex);
                if (!std::isfinite(surfaceAreaMm2) || surfaceAreaMm2 <= 0.0 ||
                    !std::isfinite(center.X()) || !std::isfinite(center.Y()) || !std::isfinite(center.Z()) ||
                    surfaceType == nullptr)
                {
                    status = FaceGeometryFeaturesStatusNative::GeometryFailure;
                    return false;
                }

                FaceGeometryFeatureNative feature;
                feature.faceIndex = faceIndex;
                feature.surfaceType = surfaceType;
                feature.surfaceAreaMm2 = surfaceAreaMm2;
                feature.center = center;
                feature.adjacentFaceIndices = GetAdjacentFaceIndicesNative(document, faceIndex);
                preparedFeatures.push_back(std::move(feature));
            }

            features.swap(preparedFeatures);
            status = FaceGeometryFeaturesStatusNative::Success;
            return true;
        }
        catch (const Standard_Failure&)
        {
            status = FaceGeometryFeaturesStatusNative::GeometryFailure;
            return false;
        }
    }

    struct StepCurveNative
    {
        CurveDefinitionNative definition;
        bool isReversed = false;
    };

    struct StepWireNative
    {
        std::vector<StepCurveNative> curves;
    };

    struct StepGroupNative
    {
        std::vector<StepWireNative> wires;
    };

    /// <summary>STEP 왕복에서 비교할 edge 길이, 방향 없는 끝점과 저장 공차입니다.</summary>
    struct StepEdgeSignatureNative
    {
        gp_Pnt firstEndpoint;
        gp_Pnt lastEndpoint;
        double lengthMm = 0.0;
        double toleranceMm = 0.0;
    };

    /// <summary>형상의 모든 edge에서 재읽기 비교용 측정값을 수집합니다.</summary>
    bool CollectStepEdgeSignaturesNative(
        const TopoDS_Shape& shape,
        std::vector<StepEdgeSignatureNative>& signatures)
    {
        if (shape.IsNull())
            return false;

        std::vector<StepEdgeSignatureNative> preparedSignatures;
        for (TopExp_Explorer explorer(shape, TopAbs_EDGE); explorer.More(); explorer.Next())
        {
            const TopoDS_Edge edge = TopoDS::Edge(explorer.Current());
            TopoDS_Vertex firstVertex;
            TopoDS_Vertex lastVertex;
            TopExp::Vertices(edge, firstVertex, lastVertex, Standard_True);
            if (firstVertex.IsNull() || lastVertex.IsNull())
                return false;

            GProp_GProps properties;
            BRepGProp::LinearProperties(edge, properties);
            const double lengthMm = properties.Mass();
            const double toleranceMm = BRep_Tool::Tolerance(edge);
            const gp_Pnt firstEndpoint = BRep_Tool::Pnt(firstVertex);
            const gp_Pnt lastEndpoint = BRep_Tool::Pnt(lastVertex);
            if (!std::isfinite(lengthMm) || lengthMm <= Precision::Confusion() ||
                !std::isfinite(toleranceMm) || toleranceMm < 0.0 ||
                !std::isfinite(firstEndpoint.X()) || !std::isfinite(firstEndpoint.Y()) ||
                !std::isfinite(firstEndpoint.Z()) || !std::isfinite(lastEndpoint.X()) ||
                !std::isfinite(lastEndpoint.Y()) || !std::isfinite(lastEndpoint.Z()))
            {
                return false;
            }

            preparedSignatures.push_back(StepEdgeSignatureNative{
                firstEndpoint,
                lastEndpoint,
                lengthMm,
                toleranceMm});
        }

        if (preparedSignatures.empty())
            return false;

        signatures.swap(preparedSignatures);
        return true;
    }

    /// <summary>STEP edge를 길이와 방향 없는 끝점 순으로 정렬합니다.</summary>
    bool StepEdgeSignatureLessNative(
        const StepEdgeSignatureNative& first,
        const StepEdgeSignatureNative& second)
    {
        auto pointLess = [](const gp_Pnt& left, const gp_Pnt& right)
        {
            if (left.X() != right.X()) return left.X() < right.X();
            if (left.Y() != right.Y()) return left.Y() < right.Y();
            return left.Z() < right.Z();
        };

        const bool firstIsReversed = pointLess(first.lastEndpoint, first.firstEndpoint);
        const bool secondIsReversed = pointLess(second.lastEndpoint, second.firstEndpoint);
        const gp_Pnt firstStart = firstIsReversed ? first.lastEndpoint : first.firstEndpoint;
        const gp_Pnt firstEnd = firstIsReversed ? first.firstEndpoint : first.lastEndpoint;
        const gp_Pnt secondStart = secondIsReversed ? second.lastEndpoint : second.firstEndpoint;
        const gp_Pnt secondEnd = secondIsReversed ? second.firstEndpoint : second.lastEndpoint;

        if (first.lengthMm != second.lengthMm)
            return first.lengthMm < second.lengthMm;
        if (pointLess(firstStart, secondStart))
            return true;
        if (pointLess(secondStart, firstStart))
            return false;
        return pointLess(firstEnd, secondEnd);
    }

    /// <summary>STEP 재읽기 edge 집합의 수, 길이와 끝점을 기대 형상과 대조합니다.</summary>
    bool MatchStepEdgeSignaturesNative(
        const std::vector<StepEdgeSignatureNative>& expected,
        const std::vector<StepEdgeSignatureNative>& actual,
        std::string& diagnostic)
    {
        if (expected.size() != actual.size())
        {
            diagnostic = "STEP 재읽기 edge 수가 출력 전 형상과 다릅니다.";
            return false;
        }

        std::vector<StepEdgeSignatureNative> expectedOrdered = expected;
        std::vector<StepEdgeSignatureNative> actualOrdered = actual;
        std::sort(expectedOrdered.begin(), expectedOrdered.end(), StepEdgeSignatureLessNative);
        std::sort(actualOrdered.begin(), actualOrdered.end(), StepEdgeSignatureLessNative);

        for (size_t edgeIndex = 0; edgeIndex < expectedOrdered.size(); ++edgeIndex)
        {
            const StepEdgeSignatureNative& expectedEdge = expectedOrdered[edgeIndex];
            const StepEdgeSignatureNative& actualEdge = actualOrdered[edgeIndex];
            const double toleranceMm = std::max(
                1e-5,
                expectedEdge.toleranceMm + actualEdge.toleranceMm);
            const bool sameDirection =
                expectedEdge.firstEndpoint.Distance(actualEdge.firstEndpoint) <= toleranceMm &&
                expectedEdge.lastEndpoint.Distance(actualEdge.lastEndpoint) <= toleranceMm;
            const bool reverseDirection =
                expectedEdge.firstEndpoint.Distance(actualEdge.lastEndpoint) <= toleranceMm &&
                expectedEdge.lastEndpoint.Distance(actualEdge.firstEndpoint) <= toleranceMm;
            if (std::fabs(expectedEdge.lengthMm - actualEdge.lengthMm) > toleranceMm ||
                (!sameDirection && !reverseDirection))
            {
                diagnostic = "STEP 재읽기에서 길이와 양 끝점이 일치하는 edge를 찾지 못했습니다.";
                return false;
            }
        }

        return true;
    }

    bool BuildStepWireNative(const StepWireNative& source, TopoDS_Wire& result,
        int& status, std::string& diagnostic)
    {
        if (source.curves.empty())
        {
            status = 1;
            diagnostic = "STEP wire에 곡선이 없습니다.";
            return false;
        }

        BRepBuilderAPI_MakeWire wireBuilder;
        for (const StepCurveNative& curve : source.curves)
        {
            TopoDS_Wire curveWire;
            if (!BuildCurveWireNative(curve.definition, curveWire))
            {
                status = 3;
                diagnostic = "곡선 정의를 OCCT wire로 만들지 못했습니다.";
                return false;
            }

            if (curve.isReversed)
                curveWire.Reverse();
            wireBuilder.Add(curveWire);
            if (!wireBuilder.IsDone())
            {
                status = 4;
                diagnostic = "연속 곡선 조각을 하나의 wire로 연결하지 못했습니다.";
                return false;
            }
        }

        result = wireBuilder.Wire();
        if (result.IsNull() || !BRepCheck_Analyzer(result).IsValid())
        {
            status = 4;
            diagnostic = "구성한 STEP wire가 유효하지 않습니다.";
            return false;
        }
        return true;
    }

    int WriteDesignStepNative(const std::string& outputPath, const std::string& sourceModelPath,
        bool includeSourceModel, const std::vector<StepGroupNative>& groups, std::string& diagnostic)
    {
        if (outputPath.empty() || groups.empty())
        {
            diagnostic = "STEP 출력 경로와 geometry 그룹이 필요합니다.";
            return 1;
        }

        STEPControl_Writer writer;
        BRep_Builder compoundBuilder;
        TopoDS_Compound expectedGeometry;
        compoundBuilder.MakeCompound(expectedGeometry);
        if (includeSourceModel)
        {
            if (sourceModelPath.empty())
            {
                diagnostic = "원본 STEP 경로가 비어 있습니다.";
                return 2;
            }

            STEPControl_Reader reader;
            if (reader.ReadFile(sourceModelPath.c_str()) != IFSelect_RetDone ||
                reader.TransferRoots() == 0 || reader.OneShape().IsNull())
            {
                diagnostic = "원본 STEP 형상을 읽거나 전송하지 못했습니다.";
                return 2;
            }
            const TopoDS_Shape sourceShape = reader.OneShape();
            if (writer.Transfer(sourceShape, STEPControl_AsIs) != IFSelect_RetDone)
            {
                diagnostic = "원본 금형 형상을 STEP 출력 모델로 전송하지 못했습니다.";
                return 5;
            }
            compoundBuilder.Add(expectedGeometry, sourceShape);
        }

        bool hasWire = false;
        for (const StepGroupNative& group : groups)
        {
            if (group.wires.empty())
            {
                diagnostic = "STEP geometry 그룹에 wire가 없습니다.";
                return 1;
            }

            TopoDS_Compound groupCompound;
            compoundBuilder.MakeCompound(groupCompound);
            for (const StepWireNative& sourceWire : group.wires)
            {
                TopoDS_Wire wire;
                int wireStatus = 0;
                if (!BuildStepWireNative(sourceWire, wire, wireStatus, diagnostic))
                    return wireStatus;

                compoundBuilder.Add(groupCompound, wire);
                compoundBuilder.Add(expectedGeometry, wire);
                hasWire = true;
            }

            if (writer.Transfer(groupCompound, STEPControl_AsIs) != IFSelect_RetDone)
            {
                diagnostic = "회로 geometry 그룹을 STEP entity로 전송하지 못했습니다.";
                return 5;
            }
        }

        if (!hasWire)
        {
            diagnostic = "STEP으로 기록할 유효 wire가 없습니다.";
            return 1;
        }
        if (writer.Write(outputPath.c_str()) != IFSelect_RetDone)
        {
            diagnostic = "OCCT STEP 파일 쓰기가 실패했습니다.";
            return 5;
        }

        std::vector<StepEdgeSignatureNative> expectedEdges;
        if (!CollectStepEdgeSignaturesNative(expectedGeometry, expectedEdges))
        {
            diagnostic = "STEP 출력 전 edge 길이와 끝점 요약을 만들지 못했습니다.";
            return 6;
        }

        STEPControl_Reader readbackReader;
        if (readbackReader.ReadFile(outputPath.c_str()) != IFSelect_RetDone ||
            readbackReader.TransferRoots() == 0)
        {
            diagnostic = "기록한 STEP 파일을 다시 읽지 못했습니다.";
            return 5;
        }

        std::vector<StepEdgeSignatureNative> actualEdges;
        if (!CollectStepEdgeSignaturesNative(readbackReader.OneShape(), actualEdges))
        {
            diagnostic = "STEP 재읽기 형상에서 edge 길이와 끝점 요약을 만들지 못했습니다.";
            return 5;
        }
        if (!MatchStepEdgeSignaturesNative(expectedEdges, actualEdges, diagnostic))
            return 5;

        return 0;
    }
}
#pragma managed(pop)

#include "OccBridgeManagedSupport.inc"
#include "OccBridgeWorkingSurfaceApi.inc"
#include "OccBridgeCurveAnalysisApi.inc"
#include "OccBridgeTopologyAreaApi.inc"
#include "OccBridgePathStagingApi.inc"
#include "OccBridgeViewerAndModelApi.inc"
