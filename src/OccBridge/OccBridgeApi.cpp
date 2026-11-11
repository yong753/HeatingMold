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
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_ListOfShape.hxx>
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
#include <Prs3d_LineAspect.hxx>
#include <Graphic3d_AspectLine3d.hxx>
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

// 사각형 선택 가시성(face ID 이미지 캐시) — TKService/TKV3d
#include <Image_PixMap.hxx>
#include <Image_AlienPixMap.hxx>
#include <Image_Format.hxx>
#include <V3d_ImageDumpOptions.hxx>
#include <Graphic3d_BufferType.hxx>
#include <Graphic3d_TypeOfShadingModel.hxx>
#include <Graphic3d_AspectFillArea3d.hxx>
#include <NCollection_Array1.hxx>

// HLR (S1 항목 5) — TKHLR
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
#include <HLRAlgo_Projector.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <GCPnts_QuasiUniformDeflection.hxx>

// 메싱 (W2 — shaded 렌더링 실패 원인 대응) — TKMesh
#include <BRepMesh_IncrementalMesh.hxx>
#include <Poly_Triangulation.hxx>
#include <Poly_Triangle.hxx>
// 제외영역 차감 채움 메시 — 삼각분할 표시(AIS_Triangulation) + 노드별 색
#include <AIS_Triangulation.hxx>
#include <TColStd_HArray1OfInteger.hxx>
#include <Aspect_PolygonOffsetMode.hxx>
#include <Graphic3d_Camera.hxx>
#include <Graphic3d_CView.hxx>
#include <Graphic3d_TypeOfBackground.hxx>
#include <Prs3d_Drawer.hxx>
#include <Prs3d_ShadingAspect.hxx>
#include <Prs3d_PointAspect.hxx>

// 면 픽킹 (W3) — 서브셰이프 선택
#include <TopTools_IndexedMapOfShape.hxx>
#include <SelectMgr_EntityOwner.hxx>
#include <StdSelect_BRepOwner.hxx>
#include <StdSelect_ViewerSelector3d.hxx>

#include <chrono>
#include <algorithm>
#include <cmath>
#include <cctype>
#include <limits>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>
#include "ProjectedExitCurveNative.inc"
#include "ExitEdgeMatchingNative.inc"
#include "BoundaryLoopTraversalNative.inc"

using namespace System;
using namespace System::Collections::Generic;

#pragma managed(push, off)
namespace
{
    TopoDS_Shape g_shape;
    std::vector<TopoDS_Face> g_faces;
    // g_faces와 같은 순서의 토폴로지 지문. 같은 STEP을 다시 읽었을 때 FaceId를
    // 재매핑하는 영속 참조로 사용한다. 원본 런타임 인덱스를 그대로 믿지 않는다.
    std::vector<std::string> g_faceTopologySignatures;
    std::set<std::pair<int,int>> g_faceAdjacency;
    long long g_faceAdjacencyRevision = -1;
    TopTools_IndexedMapOfShape g_faceMap;   // W3 픽킹: g_faces와 같은 순회로 구축 (인덱스 1:1)
    // Edge의 인접 Face 목록. Edge topology signature에 인접 Face signature를 넣어
    // 같은 형상이 겹쳐 있는 경우에도 원본 Edge를 구분하기 위해 사용한다.
    TopTools_IndexedDataMapOfShapeListOfShape g_edgeFaceMap;
    gp_Dir g_autoAxes[3] = { gp::DX(), gp::DY(), gp::DZ() };
    long long g_modelRevision = 0;
    // 형상/삼각분할 세대. 작업면 생성처럼 모델 revision은 그대로인데 mesh만 다시
    // 만들어지는 경로가 있으므로, mesh에 의존하는 선택 캐시는 이 값으로 무효화한다.
    long long g_meshEpoch = 0;
    void InvalidateWorkingContexts();
    // 사각형 선택 가시성 캐시/프리뷰 (FaceIdSelectionNative.inc 에서 정의)
    void ReleaseFaceIdCacheNative();
    void AbortRectanglePreviewNative();
    void InvalidateFaceSelectionCachesNative();
    /// <summary>선택 적용 결과. Failed는 기존 선택을 건드리지 않았다는 뜻이다.</summary>
    enum FaceApplyStatusNative
    {
        FaceApply_Unchanged = 0,
        FaceApply_Applied = 1,
        FaceApply_Failed = -1
    };

    bool TryGetSupplementalAxes(const Handle(StepData_StepModel)& model, gp_Dir outAxes[3])
    {
        if (model.IsNull() || outAxes == nullptr) return false;
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
                outAxes[0] = gp_Dir(x);
                outAxes[1] = gp_Dir(y);
                outAxes[2] = gp_Dir(z);
                return true;
            }
        }
        return false;
    }

    void ComputeAutoAxesForShape(const Handle(StepData_StepModel)& stepModel,
                                 const TopoDS_Shape& shape, gp_Dir outAxes[3],
                                 bool legacyPrincipalAxes = false)
    {
        outAxes[0] = gp::DX(); outAxes[1] = gp::DY(); outAxes[2] = gp::DZ();
        if (TryGetSupplementalAxes(stepModel, outAxes)) return;
        if (shape.IsNull()) return;
        // The model's own coordinates are authoritative. Principal-axis alignment
        // is retained only to reproduce projects saved with import policy 1.
        if (!legacyPrincipalAxes) return;
        GProp_GProps props;
        bool hasSolid = false;
        for (TopExp_Explorer ex(shape, TopAbs_SOLID); ex.More(); ex.Next()) { hasSolid = true; break; }
        if (hasSolid)
            BRepGProp::VolumeProperties(shape, props, Standard_True, Standard_True, Standard_False);
        if (!hasSolid || std::fabs(props.Mass()) <= 1e-12)
        {
            props = GProp_GProps();
            BRepGProp::SurfaceProperties(shape, props, Standard_True, Standard_False);
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
        for (int i = 0; i < 3; ++i) outAxes[i] = gp_Dir(axes[i]);
    }

    void ComputeAutoAxes(const Handle(StepData_StepModel)& stepModel)
    {
        ComputeAutoAxesForShape(stepModel, g_shape, g_autoAxes);
    }

    TopoDS_Shape AlignShapeToWorldAxesForShape(const TopoDS_Shape& shape, const gp_Dir axes[3])
    {
        // Express every point in the detected model frame. Keep the origin fixed;
        // only remove the model's rotational misalignment.
        const gp_Dir& x = axes[0];
        const gp_Dir& y = axes[1];
        const gp_Dir& z = axes[2];
        gp_Trsf rotation;
        rotation.SetValues(
            x.X(), x.Y(), x.Z(), 0.0,
            y.X(), y.Y(), y.Z(), 0.0,
            z.X(), z.Y(), z.Z(), 0.0);
        BRepBuilderAPI_Transform aligned(shape, rotation, Standard_True);
        return aligned.Shape();
    }

    void AlignShapeToWorldAxes()
    {
        g_shape = AlignShapeToWorldAxesForShape(g_shape, g_autoAxes);
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
    struct RoutePieceOverlay {
        Handle(AIS_Shape) ais; bool connector; bool enabled; Quantity_Color color; double width;
        long long contextId = 0, revision = 0;
        // 확정 표시에 실제로 사용한 순서 보존 3D 점 체인. 내보내기는 AIS 형상에서
        // topology를 다시 추론하지 않고 이 체인을 그대로 사용한다.
        std::vector<double> sourceXyz;
        // sourceXyz의 각 점이 어느 원본 Face에서 매핑됐는지 점 단위로 보존한다.
        // WithFaces 스테이징이 아니면 비어 있다.
        std::vector<int> sourceSupportFaces;
    };
    std::map<int, RoutePieceOverlay> g_routePieces;
    struct StagedWorkingPolyline { Handle(AIS_Shape) ais; long long contextId, revision; };
    std::map<int, StagedWorkingPolyline> g_stagedWorkingPolylines;
    std::map<long long, std::vector<StagedWorkingPolyline>> g_committedWorkingPolylines;
    std::map<int, std::pair<long long,long long>> g_stagedWorkingRouteOwners;
    // 발열 경로 인출점/끝점 마커. 경로를 지울 때 함께 제거한다.
    std::vector<Handle(AIS_Point)> g_routeEndpointMarkers;
    std::set<int> g_hoveredCircuitPieces;
    void HighlightCircuitNative(const std::vector<int>& ids);
    void ClearRouteEndpointMarkersNative();
    // 꼭지점 편집 오버레이 (WorkingSideNative.inc에서 정의, 이 파일 뒤쪽에 include됨)
    void ClearVertexHandlesNative();
    void ClearRoutePreviewNative();
    // 제외영역 채움 메시 점유 면 조회 (AreaHighlightNative.inc에서 정의, 뒤쪽 include)
    bool IsFaceInAreaHighlightNative(int faceId);
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
    // 인출면 선택 모드에서 커서 아래 후보 면이 받는 호버 색 override (-1 = 없음).
    // g_hoverColorFace와 분리해 W5 호버/선택 색과 서로 덮어쓰지 않게 한다.
    int g_exitFaceHover = -1;
    // 활성 작업영역의 인출면 지속 강조 (-1 = 없음). 영역 전환 시 교체된다.
    int g_exitFaceHighlightFace = -1;
    Quantity_Color g_exitFaceHighlightColor(1.00, 0.75, 0.75, Quantity_TOC_RGB);
    // 활성 작업영역에서 선택된 인출 경계 edge의 지속 강조 (폴리라인).
    Handle(AIS_Shape) g_exitEdgeHighlight;
    Handle(AIS_RubberBand) g_selectionRubberBand;
    int g_lastHoverFace = -1;      // W5 — 호버 하이라이트 중복 Redraw 억제
    bool g_faceHighlightsVisible = true;
    // Face currently carrying the hover colour override (-1 = none). Kept apart from
    // g_lastHoverFace because a click clears the hover colour while the pointer still
    // stays on the same face.
    int g_hoverColorFace = -1;


    std::string SignatureNumber(double value)
    {
        if (!std::isfinite(value)) return "nan";
        std::ostringstream out;
        out << std::setprecision(12) << value;
        return out.str();
    }

    void AppendSignaturePoint(std::ostringstream& out, const char* name, const gp_Pnt& point)
    {
        out << name << '=' << SignatureNumber(point.X()) << ','
            << SignatureNumber(point.Y()) << ',' << SignatureNumber(point.Z()) << ';';
    }

    std::string FaceTopologySignature(const TopoDS_Face& face)
    {
        try
        {
            std::ostringstream out;
            BRepAdaptor_Surface surface(face);
            out << "t=" << static_cast<int>(surface.GetType()) << ';';
            switch (surface.GetType())
            {
            case GeomAbs_Plane:
            {
                const gp_Pln plane = surface.Plane();
                AppendSignaturePoint(out, "loc", plane.Location());
                const gp_Dir normal = plane.Axis().Direction();
                out << "n=" << SignatureNumber(normal.X()) << ','
                    << SignatureNumber(normal.Y()) << ',' << SignatureNumber(normal.Z()) << ';';
                break;
            }
            case GeomAbs_Cylinder:
            {
                const gp_Cylinder cylinder = surface.Cylinder();
                AppendSignaturePoint(out, "loc", cylinder.Axis().Location());
                const gp_Dir axis = cylinder.Axis().Direction();
                out << "ax=" << SignatureNumber(axis.X()) << ','
                    << SignatureNumber(axis.Y()) << ',' << SignatureNumber(axis.Z())
                    << ";r=" << SignatureNumber(cylinder.Radius()) << ';';
                break;
            }
            case GeomAbs_Cone:
            {
                const gp_Cone cone = surface.Cone();
                AppendSignaturePoint(out, "loc", cone.Axis().Location());
                const gp_Dir axis = cone.Axis().Direction();
                out << "ax=" << SignatureNumber(axis.X()) << ','
                    << SignatureNumber(axis.Y()) << ',' << SignatureNumber(axis.Z())
                    << ";r=" << SignatureNumber(cone.RefRadius())
                    << ";a=" << SignatureNumber(cone.SemiAngle()) << ';';
                break;
            }
            case GeomAbs_Sphere:
            {
                const gp_Sphere sphere = surface.Sphere();
                AppendSignaturePoint(out, "loc", sphere.Location());
                out << "r=" << SignatureNumber(sphere.Radius()) << ';';
                break;
            }
            case GeomAbs_Torus:
            {
                const gp_Torus torus = surface.Torus();
                AppendSignaturePoint(out, "loc", torus.Location());
                const gp_Dir axis = torus.Axis().Direction();
                out << "ax=" << SignatureNumber(axis.X()) << ','
                    << SignatureNumber(axis.Y()) << ',' << SignatureNumber(axis.Z())
                    << ";r=" << SignatureNumber(torus.MajorRadius())
                    << ";m=" << SignatureNumber(torus.MinorRadius()) << ';';
                break;
            }
            default: break;
            }

            GProp_GProps props;
            BRepGProp::SurfaceProperties(face, props);
            out << "a=" << SignatureNumber(std::fabs(props.Mass())) << ';';
            AppendSignaturePoint(out, "c", props.CentreOfMass());

            Bnd_Box box;
            BRepBndLib::Add(face, box);
            if (!box.IsVoid())
            {
                double xmin, ymin, zmin, xmax, ymax, zmax;
                box.Get(xmin, ymin, zmin, xmax, ymax, zmax);
                out << "bb=" << SignatureNumber(xmin) << ',' << SignatureNumber(ymin) << ','
                    << SignatureNumber(zmin) << ',' << SignatureNumber(xmax) << ','
                    << SignatureNumber(ymax) << ',' << SignatureNumber(zmax) << ';';
            }

            int wires = 0, edges = 0, vertices = 0;
            for (TopExp_Explorer ex(face, TopAbs_WIRE); ex.More(); ex.Next()) ++wires;
            for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next()) ++edges;
            for (TopExp_Explorer ex(face, TopAbs_VERTEX); ex.More(); ex.Next()) ++vertices;
            out << "w=" << wires << ";e=" << edges << ";v=" << vertices << ';';

            std::vector<std::string> edgeSignatures;
            for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge& edge = TopoDS::Edge(ex.Current());
                std::ostringstream edgeOut;
                edgeOut << "eo=" << static_cast<int>(edge.Orientation()) << ';';
                BRepAdaptor_Curve curve(edge);
                edgeOut << "ct=" << static_cast<int>(curve.GetType()) << ';';
                GProp_GProps edgeProps;
                BRepGProp::LinearProperties(edge, edgeProps);
                edgeOut << "el=" << SignatureNumber(std::fabs(edgeProps.Mass())) << ';';
                TopoDS_Vertex first, last;
                TopExp::Vertices(edge, first, last, Standard_False);
                if (!first.IsNull()) AppendSignaturePoint(edgeOut, "p0", BRep_Tool::Pnt(first));
                if (!last.IsNull()) AppendSignaturePoint(edgeOut, "p1", BRep_Tool::Pnt(last));
                edgeSignatures.push_back(edgeOut.str());
            }
            std::sort(edgeSignatures.begin(), edgeSignatures.end());
            for (const std::string& edge : edgeSignatures) out << edge << '|';
            return out.str();
        }
        catch (...) { return "error"; }
    }

    std::vector<std::string> BuildFaceTopologySignatures(const std::vector<TopoDS_Face>& faces)
    {
        std::vector<std::string> signatures;
        signatures.reserve(faces.size());
        for (const TopoDS_Face& face : faces)
            signatures.push_back(FaceTopologySignature(face));
        return signatures;
    }

    void BuildFaceCache(const TopoDS_Shape& shape,
                        std::vector<TopoDS_Face>& faces,
                        TopTools_IndexedMapOfShape& faceMap,
                        TopTools_IndexedDataMapOfShapeListOfShape& edgeFaceMap,
                        std::vector<std::string>& signatures)
    {
        faces.clear();
        faceMap.Clear();
        signatures.clear();
        if (shape.IsNull()) return;
        for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
        {
            const TopoDS_Face f = TopoDS::Face(ex.Current());
            faces.push_back(f);
            faceMap.Add(f);   // faces 순회와 동일 → FindIndex == faces 인덱스 + 1
        }
        signatures = BuildFaceTopologySignatures(faces);
        edgeFaceMap.Clear();
        TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, edgeFaceMap);
    }

    void RebuildFaceTopologySignatures()
    {
        g_faceTopologySignatures = BuildFaceTopologySignatures(g_faces);
    }

    void RebuildFaceCache()
    {
        BuildFaceCache(g_shape, g_faces, g_faceMap, g_edgeFaceMap, g_faceTopologySignatures);
    }

    const Quantity_Color kSelectedFaceColor(0.12, 0.82, 0.38, Quantity_TOC_RGB);   // 선택 = 초록
    const Quantity_Color kConfirmedFaceColor(0.90, 0.16, 0.16, Quantity_TOC_RGB);  // 단일 영역 기본값
    const Quantity_Color kHeatingRouteColor(1.00, 0.10, 0.10, Quantity_TOC_RGB);   // 발열 경로 = 두꺼운 빨강
    const Quantity_Color kConnectorRouteColor(1.00, 0.10, 0.10, Quantity_TOC_RGB); // 턴 커넥터 = 발열선과 같은 빨강 점선
    const Quantity_Color kRouteEndpointColor(1.00, 0.05, 0.05, Quantity_TOC_RGB);  // 인출점/끝점 마커
    const Standard_Real kHeatingRouteWidth = 3.0;
    const Standard_Real kAreaFillTransparency = 0.5;  // 발열면 채움 투명도 (0=불투명, 1=완전 투명)

    /// <summary>
    /// 면 색상 override. <paramref name="keepBaseFaceBoundary"/>가 true이면 면 채움에만
    /// 영역 색을 적용하고 경계선은 모델 기본 스타일을 유지한다. 확정 영역 색상이
    /// 공유 Edge까지 물들이는 현상을 막되, 기본 CAD Edge 표시 자체는 없애지 않는다.
    /// </summary>
    void SetFaceColorOverride(int faceId, const Quantity_Color& color, Standard_Real transparency = 0.0,
                              bool keepBaseFaceBoundary = false)
    {
        if (g_aisShape.IsNull() || faceId < 0 || faceId >= static_cast<int>(g_faces.size())) return;
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;
        colored->SetCustomColor(g_faces[faceId], color);
        colored->SetCustomTransparency(g_faces[faceId], transparency);
        Handle(AIS_ColoredDrawer) faceStyle = colored->CustomAspects(g_faces[faceId]);
        faceStyle->SetupOwnShadingAspect();
        faceStyle->SetDisplayMode(AIS_Shaded);
        faceStyle->ShadingAspect()->SetColor(color);
        faceStyle->ShadingAspect()->SetTransparency(transparency);
        // The colored face is coincident with the model face. Bias its fill
        // toward the camera to avoid depth-buffer z-fighting.
        faceStyle->ShadingAspect()->Aspect()->SetPolygonOffsets(Aspect_POM_Fill, -2.0f, -2.0f);
        if (keepBaseFaceBoundary)
        {
            // AIS_Shape::setColor는 ShadingAspect뿐 아니라 FaceBoundaryAspect까지 같은
            // 색으로 덮어쓴다. 확정 영역은 채움만 영역 색을 쓰고, 경계선은 모델 기본
            // 선 스타일을 복사해 원래 Edge 표시를 유지한다.
            const Handle(Prs3d_LineAspect)& baseBoundary =
                g_aisShape->Attributes()->FaceBoundaryAspect();
            if (!baseBoundary.IsNull())
            {
                const Handle(Graphic3d_AspectLine3d)& source = baseBoundary->Aspect();
                faceStyle->SetFaceBoundaryAspect(
                    new Prs3d_LineAspect(source->Color(), source->Type(), source->Width()));
            }
        }
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
    void DropExitFaceHoverNative();

    const Quantity_Color* ConfirmedFaceColor(int faceId)
    {
        const auto it = g_confirmedFaceColors.find(faceId);
        return it == g_confirmedFaceColors.end() ? nullptr : &it->second;
    }

    /// <summary>현재 활성 영역에서 지속 강조 중인 인출면인가.</summary>
    bool IsExitFaceHighlightNative(int faceId)
    {
        return faceId >= 0 && faceId == g_exitFaceHighlightFace;
    }

    /// <summary>선택 소유를 제외한 나머지 소유자(확정 → 인출면 강조 → 없음) 순으로 색을 되돌린다.</summary>
    void ApplyRemainingFaceColorNative(int faceId)
    {
        if (const Quantity_Color* confirmed = ConfirmedFaceColor(faceId))
        {
            SetFaceColorOverride(faceId, *confirmed, kAreaFillTransparency, true);
            return;
        }
        if (IsExitFaceHighlightNative(faceId))
        {
            SetFaceColorOverride(faceId, g_exitFaceHighlightColor);
            return;
        }
        ClearFaceColorOverride(faceId);
    }

    /// <summary>선택 중이 아니면 소유자 규칙으로 복원한다. 선택 중인 면은 선택색을 유지한다.</summary>
    void RestoreFaceColorNative(int faceId)
    {
        if (g_selectedFaceIds.count(faceId) != 0) return;
        ApplyRemainingFaceColorNative(faceId);
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
                ApplyRemainingFaceColorNative(faceId);
        for (int faceId : current)
            if (g_selectedFaceIds.count(faceId) == 0)
            {
                const Quantity_Color* confirmed = ConfirmedFaceColor(faceId);
                SetFaceColorOverride(faceId,
                    confirmed != nullptr ? *confirmed : kSelectedFaceColor,
                    confirmed != nullptr ? kAreaFillTransparency : 0.0,
                    confirmed != nullptr);
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
                else if (IsExitFaceHighlightNative(previous.first)) SetFaceColorOverride(previous.first, g_exitFaceHighlightColor);
                else ClearFaceColorOverride(previous.first);
            }
        for (const auto& entry : colors)
            SetFaceColorOverride(entry.first, entry.second, kAreaFillTransparency, true);
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

    /// <summary>활성 영역 인출면 하나를 영역 톤 색으로 지속 강조한다. 이전 인출면 강조는 교체된다.</summary>
    void SetExitFaceHighlightNative(int faceId, const Quantity_Color& color)
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        if (faceId >= static_cast<int>(g_faces.size())) faceId = -1;
        const int previous = g_exitFaceHighlightFace;
        if (previous == faceId)
        {
            g_exitFaceHighlightColor = color;
            if (faceId >= 0 && g_selectedFaceIds.count(faceId) == 0 &&
                g_confirmedFaceColors.count(faceId) == 0 &&
                g_hoverColorFace != faceId && g_exitFaceHover != faceId)
                SetFaceColorOverride(faceId, color);
            g_ctx->Redisplay(g_aisShape, Standard_False);
            if (!g_view.IsNull()) g_view->Redraw();
            return;
        }
        g_exitFaceHighlightFace = -1;
        if (previous >= 0) RestoreFaceColorNative(previous);
        g_exitFaceHighlightFace = faceId;
        g_exitFaceHighlightColor = color;
        if (faceId >= 0 && g_selectedFaceIds.count(faceId) == 0 &&
            g_confirmedFaceColors.count(faceId) == 0 &&
            g_hoverColorFace != faceId && g_exitFaceHover != faceId)
            SetFaceColorOverride(faceId, color);
        g_ctx->Redisplay(g_aisShape, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
    }

    void ClearExitFaceHighlightNative()
    {
        const int faceId = g_exitFaceHighlightFace;
        g_exitFaceHighlightFace = -1;
        if (faceId < 0) return;
        RestoreFaceColorNative(faceId);
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        g_ctx->Redisplay(g_aisShape, Standard_False);
        if (!g_view.IsNull()) g_view->Redraw();
    }

    /// <summary>활성 영역에서 선택된 인출 경계 edge 강조를 제거한다.</summary>
    void ClearExitEdgeHighlightNative()
    {
        if (!g_ctx.IsNull() && !g_exitEdgeHighlight.IsNull())
            g_ctx->Remove(g_exitEdgeHighlight, Standard_False);
        g_exitEdgeHighlight.Nullify();
        if (!g_view.IsNull()) g_view->Redraw();
    }

    /// <summary>인출 경계 edge 하나를 원색(무광) 폴리라인으로 지속 표시한다. 이전 표시는 교체된다.</summary>
    void SetExitEdgeHighlightNative(const std::vector<double>& points, const Quantity_Color& color)
    {
        ClearExitEdgeHighlightNative();
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        if (points.size() < 6 || points.size() % 3 != 0) return;
        try
        {
            BRepBuilderAPI_MakePolygon polygon;
            for (size_t i = 0; i + 2 < points.size(); i += 3)
            {
                if (!std::isfinite(points[i]) || !std::isfinite(points[i + 1]) || !std::isfinite(points[i + 2]))
                    return;
                polygon.Add(gp_Pnt(points[i], points[i + 1], points[i + 2]));
            }
            if (!polygon.IsDone()) return;
            Handle(AIS_Shape) overlay = new AIS_Shape(polygon.Wire());
            overlay->SetDisplayMode(AIS_WireFrame);
            g_ctx->SetColor(overlay, color, Standard_False);
            g_ctx->SetWidth(overlay, kExitCandidateHoverWidth, Standard_False);
            g_ctx->Display(overlay, Standard_False);
            g_ctx->SetZLayer(overlay, Graphic3d_ZLayerId_Topmost);
            g_ctx->Deactivate(overlay, Standard_False);
            g_exitEdgeHighlight = overlay;
            g_view->Redraw();
        }
        catch (const Standard_Failure&) { }
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
        if (g_exitFaceHover >= 0 && g_exitFaceHover < static_cast<int>(g_faces.size()) &&
            g_confirmedFaceColors.count(g_exitFaceHover) == 0)
            colored->UnsetCustomAspects(g_faces[g_exitFaceHover], Standard_True);
        g_exitFaceHover = -1;
        if (g_exitFaceHighlightFace >= 0 && g_exitFaceHighlightFace < static_cast<int>(g_faces.size()) &&
            g_confirmedFaceColors.count(g_exitFaceHighlightFace) == 0)
            colored->UnsetCustomAspects(g_faces[g_exitFaceHighlightFace], Standard_True);
        g_exitFaceHighlightFace = -1;
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
        // 삼각분할이 바뀌면 face-ID 이미지/투영 캐시는 무효다.
        InvalidateFaceSelectionCachesNative();
        BRepMesh_IncrementalMesh mesher(g_shape, linearDeflection, Standard_False, 0.5, Standard_False);
        (void)mesher;   // 생성자가 Perform까지 수행
        InvalidateFaceSelectionCachesNative();
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
        AbortRectanglePreviewNative();
        ReleaseFaceIdCacheNative();
        if (!g_ctx.IsNull())
        {
            g_ctx->ClearSelected(Standard_False);   // 이전 선택 해제 (해당 프레젠테이션 제거 전)
            g_ctx->ClearDetected(Standard_False);
        }
        if (!g_ctx.IsNull() && !g_exitEdgeHighlight.IsNull())
            g_ctx->Remove(g_exitEdgeHighlight, Standard_False);
        g_exitEdgeHighlight.Nullify();
        if (!g_ctx.IsNull() && !g_aisShape.IsNull())
        {
            g_ctx->Remove(g_aisShape, Standard_False);
            g_aisShape.Nullify();
        }
        g_selectedFaceIds.clear();
        g_confirmedFaceColors.clear();
        g_lastHoverFace = -1;
        g_hoverColorFace = -1;
        g_exitFaceHover = -1;
        g_exitFaceHighlightFace = -1;
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

    // 원본 Edge를 다시 가져온 모델에서 찾기 위한 안정 signature. 인접 Face signature까지
    // 포함해 동일 형상이 겹친 경우를 구분한다. 실패 시 "error"를 반환한다.
    std::string EdgeTopologySignature(const TopoDS_Edge& edge,
        const TopTools_IndexedDataMapOfShapeListOfShape& edgeFaceMap,
        const TopTools_IndexedMapOfShape& faceMap,
        const std::vector<std::string>& faceSignatures)
    {
        std::ostringstream out;
        try
        {
            BRepAdaptor_Curve curve(edge);
            out << "curve=" << static_cast<int>(curve.GetType())
                << ";closed=" << (curve.IsClosed() ? 1 : 0) << ';';
            const double first = curve.FirstParameter();
            const double last = curve.LastParameter();
            out << "range=" << SignatureNumber(first) << ',' << SignatureNumber(last) << ';';
            for (int i = 0; i <= 4; ++i)
            {
                const double t = first + (last - first) * (static_cast<double>(i) / 4.0);
                AppendSignaturePoint(out, "s", curve.Value(t));
            }
            GProp_GProps props;
            BRepGProp::LinearProperties(edge, props);
            out << "len=" << SignatureNumber(std::fabs(props.Mass())) << ';';
            std::vector<std::string> adjacent;
            if (edgeFaceMap.Contains(edge))
            {
                for (const TopoDS_Shape& faceShape : edgeFaceMap.FindFromKey(edge))
                {
                    const int index = faceMap.FindIndex(faceShape);
                    adjacent.push_back(index > 0 && index <= static_cast<int>(faceSignatures.size())
                        ? faceSignatures[static_cast<size_t>(index - 1)]
                        : std::string("?"));
                }
            }
            std::sort(adjacent.begin(), adjacent.end());
            for (const std::string& value : adjacent) out << "f=" << value << ';';
        }
        catch (...)
        {
            return "error";
        }
        return out.str();
    }

    std::string EdgeTopologySignature(const TopoDS_Edge& edge)
    {
        return EdgeTopologySignature(edge, g_edgeFaceMap, g_faceMap, g_faceTopologySignatures);
    }

    struct StoredExitEdge
    {
        TopoDS_Edge edge;
        long long revision = 0;
        int faceId = -1;
        int supportFaceId = -1;
        std::string signature;
        struct Binding { double first, last; int faceId, supportFaceId; };
        std::vector<Binding> bindings;
    };

    // 후보 조회 때마다 새로 만들어지는 sewing Edge를 영속 참조로 쓰지 않는다.
    // 원본 Edge 사본과 revision을 보관하고, 모델이 바뀔 때만 비운다.
    std::map<long long, StoredExitEdge> g_exitEdges;
    TopTools_IndexedMapOfShape g_exitSourceEdges;
    TopTools_IndexedMapOfShape g_exitSourceVertices;

    void ClearExitEdgeStoreNative()
    {
        g_exitEdges.clear();
        g_exitSourceEdges.Clear();
        g_exitSourceVertices.Clear();
    }

    // 추출 중에는 기존 등록표의 복사본에만 키를 배정한다. managed 결과 구성까지
    // 성공한 호출자만 Publish로 게시하므로, 중간 실패는 기존 키/binding을 바꾸지 않는다.
    // 전제: UI 스레드에서 동기 호출. 지원하지 않는 동시 native 호출에 대한 원자성은 보장하지 않는다.
    struct ExitEdgeStoreSnapshot
    {
        std::map<long long, StoredExitEdge> edges;
        TopTools_IndexedMapOfShape sourceEdges;
        TopTools_IndexedMapOfShape sourceVertices;
    };

    void BeginExitEdgeStaging(ExitEdgeStoreSnapshot& staged)
    {
        staged.edges = g_exitEdges;
        staged.sourceEdges = g_exitSourceEdges;
        staged.sourceVertices = g_exitSourceVertices;
    }

    void PublishExitEdgeStaging(ExitEdgeStoreSnapshot& staged)
    {
        g_exitEdges.swap(staged.edges);
        g_exitSourceEdges.Exchange(staged.sourceEdges);
        g_exitSourceVertices.Exchange(staged.sourceVertices);
    }

    long long StageOriginalExitEdge(ExitEdgeStoreSnapshot& staged,
        const HeatingMoldExitNative::SourceInterval& source, int faceId, int supportFaceId)
    {
        const long long key = staged.sourceEdges.Add(source.edge);
        auto entry = staged.edges.find(key);
        if (entry == staged.edges.end()) {
            StoredExitEdge stored;
            stored.edge = source.edge; stored.revision = g_modelRevision;
            stored.faceId = faceId; stored.supportFaceId = supportFaceId;
            stored.signature = EdgeTopologySignature(source.edge);
            entry = staged.edges.emplace(key, stored).first;
            TopoDS_Vertex first, last;
            TopExp::Vertices(source.edge, first, last, Standard_False);
            if (!first.IsNull()) staged.sourceVertices.Add(first);
            if (!last.IsNull()) staged.sourceVertices.Add(last);
        }
        for (const auto& binding : entry->second.bindings)
            if (binding.first == source.first && binding.last == source.last &&
                binding.faceId == faceId && binding.supportFaceId == supportFaceId) return key;
        entry->second.bindings.push_back({source.first, source.last, faceId, supportFaceId});
        return key;
    }

    // 아래 정의는 뒤쪽 인출 측면 추출과 공유한다.
    int FindSupportFaceForEdge(const TopoDS_Edge& edge, const std::set<int>& selected);
    double EdgeLengthNative(const TopoDS_Edge& edge);
    std::string g_exitSideDiagnostic;

    /// <summary>경계 일부 점 구간이 대응하는 원본 CAD Edge 참조.</summary>
    struct NativeExitEdgeRef
    {
        long long revision = 0;
        long long key = -1;
        int faceId = -1;
        double firstParameter = 0.0;
        double lastParameter = 0.0;
        bool reversed = false;
        int startIndex = 0; // 경계 polyline의 점 인덱스
        int endIndex = 0;   // 포함
        std::string signature; // 재가져오기 안정 원본 Edge signature (실패 시 "error")
    };

    struct NativeBoundaryLoop
    {
        bool closed = false;
        std::vector<double> xyz;
        std::vector<NativeExitEdgeRef> edgeRefs;
    };

    /// <summary>
    /// 외곽 경계 슬롯 하나. 순회 결과의 입력 인덱스가 직접 이 기록을 가리키므로
    /// 재조립된 Edge를 다시 조회하지 않는다. edgeMapIndex는 전체 Edge 목록 기준이며
    /// 외곽 슬롯 번호와 같다고 가정하지 않는다.
    /// </summary>
    struct BoundaryEdgeInput
    {
        TopoDS_Edge workingEdge;
        int edgeMapIndex = 0; // 1-based edgeMap index
        HeatingMoldExitNative::SourceInterval source;
        bool sourceValid = false;
        bool sourceAmbiguous = false;
        int relatedFace = -1; // 영역: 소유 Face, 인출 후보: 반대편 인접 Face
    };

    /// <summary>
    /// 선택 면의 원본 위상만으로 통합 경계를 분류할 수 있는지 판정한다.
    /// 원본에서 모든 비퇴화 Edge가 1~2개 선택 면에만 쓰이고, 선택 면들이
    /// Edge 공유로 하나의 연결 성분이면 재봉합이 필요 없다.
    /// 이미 공유된 원본 내부 Edge를 다시 Sewing하면 작은 내부 Edge 주변이
    /// 병합되어 한 Edge를 여러 면이 공유하는 비다양체가 만들어질 수 있는데,
    /// 그것은 원본 결함이 아니라 임시 병합의 부작용이다.
    /// </summary>
    bool BuildOriginalSelectedRegionShape(const std::set<int>& selected,
                                          TopoDS_Shape& shape,
                                          std::vector<TopoDS_Face>& faces,
                                          std::string& reason)
    {
        faces.clear();
        reason.clear();
        if (selected.empty()) { reason = "empty selection"; return false; }
        BRep_Builder builder;
        TopoDS_Compound compound;
        builder.MakeCompound(compound);
        for (int faceId : selected)
        {
            if (faceId < 0 || faceId >= static_cast<int>(g_faces.size()))
            {
                reason = "face id out of range";
                return false;
            }
            builder.Add(compound, g_faces[faceId]);
        }
        for (TopExp_Explorer ex(compound, TopAbs_FACE); ex.More(); ex.Next())
            faces.push_back(TopoDS::Face(ex.Current()));
        if (faces.size() != selected.size()) { reason = "selected face count changed"; return false; }
        const int faceCount = static_cast<int>(faces.size());

        TopTools_IndexedMapOfShape edgeMap;
        std::vector<std::vector<int>> owners; // edgeMap index-1 -> 이 Edge를 쓰는 face slot
        int exterior = 0;
        for (int faceIndex = 0; faceIndex < faceCount; ++faceIndex)
        {
            for (TopExp_Explorer ex(faces[static_cast<size_t>(faceIndex)], TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                if (BRep_Tool::Degenerated(edge)) continue;
                int index = edgeMap.FindIndex(edge);
                if (index == 0)
                {
                    edgeMap.Add(edge);
                    owners.emplace_back();
                    index = edgeMap.Extent();
                }
                owners[static_cast<size_t>(index - 1)].push_back(faceIndex);
            }
        }
        if (edgeMap.Extent() == 0) { reason = "no original edge"; return false; }

        std::vector<int> parent(static_cast<size_t>(faceCount));
        for (int i = 0; i < faceCount; ++i) parent[static_cast<size_t>(i)] = i;
        const auto rootOf = [&parent](int node)
        {
            while (parent[static_cast<size_t>(node)] != node)
                node = parent[static_cast<size_t>(node)];
            return node;
        };
        for (const std::vector<int>& list : owners)
        {
            if (list.size() > 2)
            {
                reason = "original edge shared by more than two selected faces";
                return false;
            }
            if (list.size() == 1) { ++exterior; continue; }
            if (list[0] == list[1]) continue; // 같은 면의 seam은 두 면을 잇지 않는다
            const int left = rootOf(list[0]);
            const int right = rootOf(list[1]);
            if (left != right) parent[static_cast<size_t>(left)] = right;
        }
        if (exterior == 0) { reason = "original topology has no exterior edge"; return false; }
        const int root = rootOf(0);
        for (int i = 1; i < faceCount; ++i)
            if (rootOf(i) != root)
            {
                reason = "selected faces are disconnected in original topology";
                return false;
            }
        shape = compound;
        return true;
    }

    /// <summary>
    /// 병합하지 않은 경우 경계 Edge는 원본 Edge 그 자체다. history를 비워 두면
    /// 기하 검색이 모든 원본을 후보로 삼아 아주 짧은 슬리버 Edge에서 모호성이
    /// 생긴다. 동일 Edge 하나만 후보로 고정하고, 대체 Edge로 넘어가지 않는다.
    /// </summary>
    std::vector<std::vector<int>> IdentitySourceHistory(
        const std::vector<TopoDS_Edge>& boundaries, const std::vector<TopoDS_Edge>& originals,
        const TopTools_IndexedMapOfShape& boundariesMap)
    {
        std::vector<std::vector<int>> inverse(static_cast<size_t>(boundariesMap.Extent()));
        for (const TopoDS_Edge& boundary : boundaries)
        {
            const int index = boundariesMap.FindIndex(boundary) - 1;
            if (index < 0) continue;
            for (int k = 0; k < static_cast<int>(originals.size()); ++k)
                if (originals[static_cast<size_t>(k)].IsSame(boundary))
                {
                    inverse[static_cast<size_t>(index)].push_back(k);
                    break;
                }
        }
        return inverse;
    }

    bool ExtractSelectedRegionBoundary(const std::vector<int>& faceIds, double deflection,
                                       std::vector<NativeBoundaryLoop>& loops,
                                       ExitEdgeStoreSnapshot& staged, std::string& diagnostic)
    {
        loops.clear();
        diagnostic.clear();
        if (faceIds.empty() || !std::isfinite(deflection) || deflection <= 0.0)
        {
            diagnostic = "region-boundary: invalid input";
            return false;
        }
        std::set<int> selected;
        for (int id : faceIds)
        {
            if (id < 0 || id >= static_cast<int>(g_faces.size()))
            {
                diagnostic = "region-boundary: face id out of range";
                return false;
            }
            selected.insert(id);
        }
        BeginExitEdgeStaging(staged);

        // 원본 위상이 이미 완결된 영역이면 병합하지 않는다. 병합은 원본에 없는
        // 다중 공유 Edge를 만들 수 있고, 그 결과는 원본 결함이 아니다.
        // 병합이 필요할 때는 비다양체 허용을 끄고, 이미 두 면이 공유하는 원본
        // 내부 Edge를 병합 후보에서 제외한 뒤 선택 면 보존을 검증한다.
        // 0.01 mm는 새로 연결하는 이음의 판단 거리이며, 원본 CAD 정점 공차를
        // 깎는 값이 아니다. 더 큰 간극은 의도적으로 연결하지 않는다.
        const double sewingTolerance = 0.01;
        TopoDS_Shape workingShape;
        std::vector<TopoDS_Face> workingFaces;
        BRepBuilderAPI_Sewing sewing(sewingTolerance, Standard_True, Standard_False, Standard_True, Standard_True);
        bool sewed = false;
        std::string originalTopologyReason;
        if (!BuildOriginalSelectedRegionShape(selected, workingShape, workingFaces, originalTopologyReason))
        {
            for (int faceId : selected) sewing.Add(g_faces[faceId]);
            sewing.Perform();
            workingShape = sewing.SewedShape();
            if (workingShape.IsNull())
            {
                diagnostic = "region-boundary: temporary sewing produced no shape";
                return false;
            }
            workingFaces.clear();
            for (TopExp_Explorer ex(workingShape, TopAbs_FACE); ex.More(); ex.Next())
                workingFaces.push_back(TopoDS::Face(ex.Current()));
            if (workingFaces.size() != selected.size())
            {
                // 병합이 선택 면을 삭제/대체하면 원본 위상 보존 실패다.
                diagnostic = "region-boundary: temporary sewing changed the selected face set";
                return false;
            }
            sewed = true;
        }

        // Count edge uses by topological identity after sewing. Internal edges
        // used by two selected working faces disappear from the union boundary.
        int degenerateWorkingEdges = 0;
        int degenerateOriginalEdges = 0;
        const auto logDegenerateBoundaryEdges = [](int workingCount, int originalCount)
        {
            char path[MAX_PATH] = { 0 };
            const DWORD length = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", path, MAX_PATH);
            if (length == 0 || length >= MAX_PATH) return;
            std::ofstream log(path, std::ios::app);
            if (!log) return;
            log << "region-boundary: skipped degenerate edges working=" << workingCount
                << " original=" << originalCount << "\n";
        };

        TopTools_IndexedMapOfShape edgeMap;
        std::vector<int> uses;
        for (const TopoDS_Face& face : workingFaces)
            for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                if (BRep_Tool::Degenerated(edge))
                {
                    // A pole or apex collapsed to a point is not a real exterior
                    // boundary and has no source interval to match.
                    ++degenerateWorkingEdges;
                    continue;
                }
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
        for (int count : uses)
            if (count > 2)
            {
                diagnostic = sewed
                    ? "region-boundary: temporary seam join produced a non-manifold edge"
                    : "region-boundary: selected faces are non-manifold";
                return false;
            }
        for (int i = 1; i <= edgeMap.Extent(); ++i)
            if (uses[i - 1] == 1) boundaryEdges.push_back(TopoDS::Edge(edgeMap(i)));
        if (boundaryEdges.empty())
        {
            diagnostic = "region-boundary: selected faces have no exterior edge";
            return false;
        }

        // Sewing 사본 경계 Edge를 선택 면의 원본 경계 Edge로 되돌린다.
        // 표시용 샘플과 독립된 정확 경계 판정의 정본이며, 매칭 실패는
        // 폴리곤 근사로 대체하지 않고 추출 실패로 처리한다.
        // 원본 CAD edge 매칭 허용 거리(mm). 길이 차이도 같은 값을 쓴다.
        const double geometryMatchTolerance = 0.1;
        TopTools_IndexedMapOfShape originalEdgeMap;
        std::vector<int> originalEdgeUses;
        for (int faceId : selected)
        {
            for (TopExp_Explorer ex(g_faces[faceId], TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge originalEdge = TopoDS::Edge(ex.Current());
                if (BRep_Tool::Degenerated(originalEdge))
                {
                    ++degenerateOriginalEdges;
                    continue;
                }
                int index = originalEdgeMap.FindIndex(originalEdge);
                if (index == 0)
                {
                    originalEdgeMap.Add(originalEdge);
                    originalEdgeUses.push_back(0);
                    index = originalEdgeMap.Extent();
                }
                ++originalEdgeUses[index - 1];
            }
        }
        if (degenerateWorkingEdges > 0 || degenerateOriginalEdges > 0)
            logDegenerateBoundaryEdges(degenerateWorkingEdges, degenerateOriginalEdges);
        std::vector<TopoDS_Edge> originalBoundaryEdges;
        for (int i = 1; i <= originalEdgeMap.Extent(); ++i)
            if (originalEdgeUses[i - 1] == 1)
                originalBoundaryEdges.push_back(TopoDS::Edge(originalEdgeMap(i)));
        if (originalBoundaryEdges.empty())
        {
            diagnostic = "region-boundary: no original boundary edges";
            return false;
        }
        // 병합하지 않았으면 원본 Edge가 곧 작업 Edge이므로 history가 필요 없다.
        // 이때 FindUniqueSourceInterval은 동일 Edge fast path로 정확한 원본 구간을 준다.
        std::vector<std::vector<int>> inverse;
        if (sewed)
            inverse = HeatingMoldExitNative::SewingSourceInverse(sewing, originalBoundaryEdges, edgeMap);
        else
            inverse = IdentitySourceHistory(boundaryEdges, originalBoundaryEdges, edgeMap);
        std::vector<HeatingMoldExitNative::SourceInterval> sourceMatches(static_cast<size_t>(edgeMap.Extent()));
        std::vector<bool> sourceValid(static_cast<size_t>(edgeMap.Extent()), false);
        std::vector<bool> sourceAmbiguous(static_cast<size_t>(edgeMap.Extent()), false);
        for (const TopoDS_Edge& boundaryEdge : boundaryEdges)
        {
            const int index = edgeMap.FindIndex(boundaryEdge) - 1;
            if (index < 0 || index >= static_cast<int>(sourceMatches.size()))
            {
                diagnostic = "region-boundary: boundary edge missing from the working edge map";
                return false;
            }
            bool ambiguous = false;
            sourceValid[index] = HeatingMoldExitNative::FindUniqueSourceInterval(boundaryEdge,
                originalBoundaryEdges, inverse[index], geometryMatchTolerance, sourceMatches[index], ambiguous);
            sourceAmbiguous[index] = ambiguous;
            if (!sourceValid[index])
            {
                diagnostic = "region-boundary: " + std::string(ambiguous ? "ambiguous" : "missing") +
                    "-source len=" + std::to_string(EdgeLengthNative(boundaryEdge)) +
                    " join=" + (sewed ? "seam" : "original") +
                    " candidates=" + std::to_string(inverse[index].size()) +
                    " origin=" + originalTopologyReason;
                return false;
            }
        }

        // 외곽 슬롯마다 원본 대응을 직접 보관한다. 이후 단계는 새 Edge를 다시
        // 조회하지 않고 이 기록의 workingEdge와 source를 그대로 사용한다.
        std::vector<BoundaryEdgeInput> boundaryInputs;
        boundaryInputs.reserve(boundaryEdges.size());
        for (const TopoDS_Edge& boundaryEdge : boundaryEdges)
        {
            BoundaryEdgeInput input;
            input.workingEdge = boundaryEdge;
            input.edgeMapIndex = edgeMap.FindIndex(boundaryEdge);
            const size_t index = static_cast<size_t>(input.edgeMapIndex - 1);
            input.source = sourceMatches[index];
            input.sourceValid = sourceValid[index];
            input.sourceAmbiguous = sourceAmbiguous[index];
            input.relatedFace = FindSupportFaceForEdge(input.source.edge, selected);
            if (input.relatedFace < 0)
            {
                diagnostic = "region-boundary: no owning face for a boundary edge";
                return false;
            }
            boundaryInputs.push_back(input);
        }
        // Wire 재조립 없이 기존 정점 연결로 외곽 루프의 순서와 방향만 정한다.
        std::vector<TopoDS_Edge> boundaryWorkingEdges;
        boundaryWorkingEdges.reserve(boundaryInputs.size());
        for (const BoundaryEdgeInput& input : boundaryInputs)
            boundaryWorkingEdges.push_back(input.workingEdge);
        const auto logOrderFailure = [&](const std::string& reason)
        {
            char path[MAX_PATH] = { 0 };
            const DWORD length = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", path, MAX_PATH);
            if (length == 0 || length >= MAX_PATH) return;
            std::ofstream log(path, std::ios::app);
            if (!log) return;
            log << "region-boundary: order-failure " << reason << "\n";
            TopTools_IndexedMapOfShape vertexMap;
            std::vector<std::vector<int>> incident;
            for (int i = 0; i < static_cast<int>(boundaryWorkingEdges.size()); ++i)
            {
                TopoDS_Vertex first, last;
                TopExp::Vertices(boundaryWorkingEdges[static_cast<size_t>(i)], first, last, Standard_True);
                const auto add = [&](const TopoDS_Vertex& vertex, int slot)
                {
                    int index = vertexMap.FindIndex(vertex);
                    if (index == 0)
                    {
                        vertexMap.Add(vertex);
                        incident.emplace_back();
                        index = vertexMap.Extent();
                    }
                    incident[static_cast<size_t>(index - 1)].push_back(i * 2 + slot);
                };
                if (!first.IsNull()) add(first, 0);
                if (!last.IsNull()) add(last, 1);
            }
            for (int v = 1; v <= vertexMap.Extent(); ++v)
            {
                if (incident[static_cast<size_t>(v - 1)].size() == 2) continue;
                const TopoDS_Vertex vertex = TopoDS::Vertex(vertexMap(v));
                const gp_Pnt p = BRep_Tool::Pnt(vertex);
                log << "region-boundary: vertex-incidence count="
                    << incident[static_cast<size_t>(v - 1)].size()
                    << " tol=" << BRep_Tool::Tolerance(vertex)
                    << " p=(" << p.X() << "," << p.Y() << "," << p.Z() << ") edges=";
                for (int code : incident[static_cast<size_t>(v - 1)])
                    log << (code / 2) << ":" << (code % 2) << " ";
                log << "\n";
            }
        };

        std::vector<HeatingMoldBoundaryNative::BoundaryComponent> components;
        int failedComponents = 0;
        std::string orderFailure;
        if (!HeatingMoldBoundaryNative::OrderBoundaryLoops(
                boundaryWorkingEdges, components, failedComponents, orderFailure))
        {
            logOrderFailure(orderFailure);
            diagnostic = "region-boundary: " + orderFailure;
            return false;
        }
        if (failedComponents > 0)
        {
            logOrderFailure(orderFailure);
            diagnostic = "region-boundary: " + orderFailure;
            return false;
        }

        const auto logBoundaryGap = [](const std::string& text)
        {
            char path[MAX_PATH] = { 0 };
            const DWORD length = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", path, MAX_PATH);
            if (length == 0 || length >= MAX_PATH) return;
            std::ofstream log(path, std::ios::app);
            if (!log) return;
            log << text << "\n";
        };

        std::vector<NativeBoundaryLoop> prepared;
        for (const HeatingMoldBoundaryNative::BoundaryComponent& component : components)
        {
            NativeBoundaryLoop loop;
            loop.closed = true;
            gp_Pnt firstPoint, previousPoint;
            bool hasPoint = false;
            int stepIndex = 0;
            const BoundaryEdgeInput* previousInput = nullptr;
            const HeatingMoldBoundaryNative::BoundaryStep* previousStep = nullptr;
            for (const HeatingMoldBoundaryNative::BoundaryStep& step : component.steps)
            {
                const BoundaryEdgeInput& input = boundaryInputs[static_cast<size_t>(step.inputIndex)];
                const int startPoint = static_cast<int>(loop.xyz.size() / 3);
                int endPoint = startPoint - 1;
                const bool curveReversed = input.workingEdge.Orientation() == TopAbs_REVERSED;
                const bool sampleReverse = curveReversed != step.reverse;
                BRepAdaptor_Curve curve(input.workingEdge);
                GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                if (!sampler.IsDone() || sampler.NbPoints() < 2 || sampler.NbPoints() > 100000)
                {
                    diagnostic = "region-boundary: boundary sampling failed";
                    return false;
                }
                // 정점 identity로 연결된 두 Edge의 곡선 끝점은 해당 공유 정점의
                // tolerance 안에서 어긋날 수 있다. 봉합 공차(0.01)는 서로 다른
                // Edge를 합치는 기준이고, 이미 공유된 정점의 연속성 기준이 아니다.
                double allowedGap = sewingTolerance;
                if (previousInput != nullptr && previousStep != nullptr)
                {
                    TopoDS_Vertex prevFirst, prevLast, curFirst, curLast;
                    TopExp::Vertices(previousInput->workingEdge, prevFirst, prevLast, Standard_True);
                    TopExp::Vertices(input.workingEdge, curFirst, curLast, Standard_True);
                    const TopoDS_Vertex& prevExit = previousStep->reverse ? prevFirst : prevLast;
                    const TopoDS_Vertex& curEntry = step.reverse ? curLast : curFirst;
                    if (!prevExit.IsNull() && !curEntry.IsNull() && prevExit.IsSame(curEntry))
                        allowedGap = std::max(sewingTolerance, BRep_Tool::Tolerance(prevExit));
                }
                for (int k = 1; k <= sampler.NbPoints(); ++k)
                {
                    const gp_Pnt p = sampler.Value(sampleReverse ? sampler.NbPoints() - k + 1 : k);
                    if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z()))
                    {
                        diagnostic = "region-boundary: non-finite boundary sample";
                        return false;
                    }
                    if (k == 1 && hasPoint)
                    {
                        const double gap = previousPoint.Distance(p);
                        if (gap > allowedGap)
                        {
                            if (previousInput != nullptr && previousStep != nullptr)
                            {
                                TopoDS_Vertex prevFirst, prevLast, curFirst, curLast;
                                TopExp::Vertices(previousInput->workingEdge, prevFirst, prevLast, Standard_True);
                                TopExp::Vertices(input.workingEdge, curFirst, curLast, Standard_True);
                                const TopoDS_Vertex& prevExit = previousStep->reverse ? prevFirst : prevLast;
                                const TopoDS_Vertex& curEntry = step.reverse ? curLast : curFirst;
                                const bool shared = !prevExit.IsNull() && !curEntry.IsNull() &&
                                    prevExit.IsSame(curEntry);
                                double vertexDistance = -1.0;
                                if (!prevExit.IsNull() && !curEntry.IsNull())
                                    vertexDistance = BRep_Tool::Pnt(prevExit).Distance(BRep_Tool::Pnt(curEntry));
                                std::ostringstream message;
                                message << "region-boundary: gap step=" << stepIndex
                                        << " prevEdge=" << previousStep->inputIndex
                                        << " edge=" << step.inputIndex
                                        << " dist=" << gap
                                        << " allowed=" << allowedGap
                                        << " sewing=" << sewingTolerance
                                        << " sharedVertex=" << (shared ? 1 : 0)
                                        << " vertexDist=" << vertexDistance
                                        << " prevVertexTol="
                                        << (prevExit.IsNull() ? -1.0 : BRep_Tool::Tolerance(prevExit))
                                        << " curVertexTol="
                                        << (curEntry.IsNull() ? -1.0 : BRep_Tool::Tolerance(curEntry))
                                        << " prevEdgeTol=" << BRep_Tool::Tolerance(previousInput->workingEdge)
                                        << " curEdgeTol=" << BRep_Tool::Tolerance(input.workingEdge)
                                        << " prevDegenerated="
                                        << (BRep_Tool::Degenerated(previousInput->workingEdge) ? 1 : 0)
                                        << " curDegenerated="
                                        << (BRep_Tool::Degenerated(input.workingEdge) ? 1 : 0)
                                        << " prevLen=" << EdgeLengthNative(previousInput->workingEdge)
                                        << " curLen=" << EdgeLengthNative(input.workingEdge)
                                        << " prevP=(" << previousPoint.X() << "," << previousPoint.Y()
                                        << "," << previousPoint.Z() << ")"
                                        << " curP=(" << p.X() << "," << p.Y() << "," << p.Z() << ")";
                                logBoundaryGap(message.str());
                            }
                            diagnostic = "region-boundary: boundary sample is discontinuous";
                            return false;
                        }
                        if (gap > sewingTolerance)
                        {
                            std::ostringstream message;
                            message << "region-boundary: gap within vertex tolerance step=" << stepIndex
                                    << " prevEdge="
                                    << (previousStep != nullptr ? previousStep->inputIndex : -1)
                                    << " edge=" << step.inputIndex
                                    << " dist=" << gap
                                    << " allowed=" << allowedGap;
                            logBoundaryGap(message.str());
                        }
                    }
                    if (!hasPoint) { firstPoint = p; hasPoint = true; }
                    previousPoint = p;
                    if (loop.xyz.size() >= 3 &&
                        gp_Pnt(loop.xyz[loop.xyz.size() - 3], loop.xyz[loop.xyz.size() - 2],
                               loop.xyz[loop.xyz.size() - 1]).Distance(p) <= Precision::Confusion()) continue;
                    loop.xyz.push_back(p.X()); loop.xyz.push_back(p.Y()); loop.xyz.push_back(p.Z());
                    endPoint = static_cast<int>(loop.xyz.size() / 3) - 1;
                }
                // 표시점 중복 제거로 유효한 원본 Edge 구간이 사라지면 조용히
                // 건너뛰지 않는다. 참조 구간은 루프 전체를 빈틈없이 덮어야 한다.
                if (endPoint < startPoint)
                {
                    diagnostic = "region-boundary: boundary edge produced no polyline segment";
                    return false;
                }
                const int referenceStart = startPoint > 0 ? startPoint - 1 : 0;
                if (endPoint <= referenceStart)
                {
                    diagnostic = "region-boundary: boundary edge produced no reference interval";
                    return false;
                }
                NativeExitEdgeRef ref;
                ref.revision = g_modelRevision;
                ref.key = StageOriginalExitEdge(staged, input.source, input.relatedFace, input.relatedFace);
                const auto stagedEdge = staged.edges.find(ref.key);
                if (stagedEdge != staged.edges.end()) ref.signature = stagedEdge->second.signature;
                ref.faceId = input.relatedFace;
                ref.firstParameter = input.source.first;
                ref.lastParameter = input.source.last;
                ref.reversed = (input.source.reversed != curveReversed) != step.reverse;
                ref.startIndex = referenceStart;
                ref.endIndex = endPoint;
                loop.edgeRefs.push_back(ref);
                previousInput = &input;
                previousStep = &step;
                ++stepIndex;
            }
            if (loop.edgeRefs.empty())
            {
                diagnostic = "region-boundary: boundary loop has no original references";
                return false;
            }
            double closureGap = sewingTolerance;
            if (!component.steps.empty())
            {
                const HeatingMoldBoundaryNative::BoundaryStep& firstStep = component.steps.front();
                const HeatingMoldBoundaryNative::BoundaryStep& lastStep = component.steps.back();
                TopoDS_Vertex lastFirst, lastLast, firstFirst, firstLast;
                TopExp::Vertices(
                    boundaryInputs[static_cast<size_t>(lastStep.inputIndex)].workingEdge,
                    lastFirst, lastLast, Standard_True);
                TopExp::Vertices(
                    boundaryInputs[static_cast<size_t>(firstStep.inputIndex)].workingEdge,
                    firstFirst, firstLast, Standard_True);
                const TopoDS_Vertex& lastExit = lastStep.reverse ? lastFirst : lastLast;
                const TopoDS_Vertex& firstEntry = firstStep.reverse ? firstLast : firstFirst;
                if (!lastExit.IsNull() && !firstEntry.IsNull() && lastExit.IsSame(firstEntry))
                    closureGap = std::max(sewingTolerance, BRep_Tool::Tolerance(lastExit));
            }
            if (!hasPoint || previousPoint.Distance(firstPoint) > closureGap)
            {
                diagnostic = "region-boundary: boundary loop is not closed";
                return false;
            }
            if (loop.xyz.size() < 9)
            {
                diagnostic = "region-boundary: too few boundary points";
                return false;
            }
            prepared.push_back(std::move(loop));
        }
        if (prepared.empty())
        {
            diagnostic = "region-boundary: no boundary loop";
            return false;
        }
        loops = std::move(prepared);
        return true;
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
        std::vector<NativeExitEdgeRef> edgeRefs;
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
    /// Sewing 사본 경계 Edge에 대응하는 원본(선택 면) Edge를 찾는다.
    /// 후보는 선택 면 집합의 실제 외곽 Edge로 제한한다(내부 공유 Edge/이음선 제외).
    /// </summary>
    /// <summary>
    /// 주어진 원본 Edge를 실제로 소유한 선택 면을 찾는다.
    /// 경계 Hit의 실제 선택 지지면을 반환하기 위한 위상 조회다. 2D 검색에는 사용하지 않는다.
    /// </summary>
    int FindSupportFaceForEdge(const TopoDS_Edge& edge, const std::set<int>& selected)
    {
        if (edge.IsNull()) return -1;
        for (int faceId : selected)
        {
            for (TopExp_Explorer ex(g_faces[faceId], TopAbs_EDGE); ex.More(); ex.Next())
                if (TopoDS::Edge(ex.Current()).IsSame(edge)) return faceId;
        }
        return -1;
    }

    /// <summary>
    /// Sewing 사본의 경계 Edge와 기하학적으로 일치하는 원본(비선택) 면을 찾는다.
    /// 토폴로지 동일성(IsSame)을 우선하고, 없으면 길이 + 3점 투영 거리로 판정한다.
    /// 일치하는 면이 없으면 -1 (모델 끝의 나체 Edge).
    /// </summary>
    int FindAdjacentOriginalFace(const TopoDS_Edge& boundaryEdge, const std::set<int>& selected,
                                  double tolerance, const TopoDS_Edge* original = nullptr)
    {
        if (boundaryEdge.IsNull()) return -1;
        std::set<int> topologyFaces, geometryFaces;
        for (int faceId = 0; faceId < static_cast<int>(g_faces.size()); ++faceId)
        {
            if (selected.count(faceId) != 0) continue;
            for (TopExp_Explorer ex(g_faces[faceId], TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge candidate = TopoDS::Edge(ex.Current());
                if (candidate.IsNull()) continue;
                if (candidate.IsSame(boundaryEdge) || (original && candidate.IsSame(*original)))
                { topologyFaces.insert(faceId); continue; }
                HeatingMoldExitNative::SourceInterval match;
                if (HeatingMoldExitNative::MatchSourceInterval(boundaryEdge, candidate, tolerance, match))
                    geometryFaces.insert(faceId);
            }
        }
        if (topologyFaces.size() == 1) return *topologyFaces.begin();
        if (!topologyFaces.empty()) return -2; // non-manifold/ambiguous
        return geometryFaces.size() == 1 ? *geometryFaces.begin() : (geometryFaces.empty() ? -1 : -2);
    }

    /// <summary>run에 점을 추가하고 실제로 저장된 점 인덱스를 반환한다. 좌표는 중복 제거해 xyz에, 면 id는 구간 시작 인덱스로 기록한다.</summary>
    int AppendExitPoint(NativeExitSide& run, int faceId, const gp_Pnt& point)
    {
        int index = static_cast<int>(run.xyz.size() / 3);
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
        else
        {
            index -= 1;
        }
        if (run.segmentStarts.empty() || run.segmentStarts.back().faceId != faceId)
        {
            NativeExitSegmentStart start;
            start.faceId = faceId;
            start.startIndex = index;
            run.segmentStarts.push_back(start);
        }
        return index;
    }

    /// <summary>source의 점/구간/원본 Edge 참조를 target 끝에 이어붙인다(접촉점 중복 제거, 같은 면 구간 병합).</summary>
    void AppendExitRun(NativeExitSide& target, const NativeExitSide& source)
    {
        const int sourcePoints = static_cast<int>(source.xyz.size() / 3);
        std::vector<int> pointMap(static_cast<size_t>(sourcePoints), -1);
        for (size_t s = 0; s < source.segmentStarts.size(); ++s)
        {
            const int faceId = source.segmentStarts[s].faceId;
            const int from = source.segmentStarts[s].startIndex;
            const int to = (s + 1 < source.segmentStarts.size())
                ? source.segmentStarts[s + 1].startIndex
                : sourcePoints - 1;
            for (int i = from; i <= to; ++i)
                pointMap[static_cast<size_t>(i)] = AppendExitPoint(target, faceId,
                    gp_Pnt(source.xyz[i * 3], source.xyz[i * 3 + 1], source.xyz[i * 3 + 2]));
        }
        for (const NativeExitEdgeRef& sourceRef : source.edgeRefs)
        {
            if (sourceRef.startIndex < 0 || sourceRef.endIndex < 0 ||
                sourceRef.startIndex >= sourcePoints || sourceRef.endIndex >= sourcePoints)
                continue;
            NativeExitEdgeRef ref = sourceRef;
            ref.startIndex = pointMap[static_cast<size_t>(sourceRef.startIndex)];
            ref.endIndex = pointMap[static_cast<size_t>(sourceRef.endIndex)];
            if (ref.startIndex < 0 || ref.endIndex <= ref.startIndex) continue;
            target.edgeRefs.push_back(ref);
        }
    }

    /// <summary>run의 점/구간/원본 Edge 참조 순서를 역전한 사본을 만든다.</summary>
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
        const int refs = static_cast<int>(run.edgeRefs.size());
        for (int r = refs - 1; r >= 0; --r)
        {
            NativeExitEdgeRef ref = run.edgeRefs[r];
            const int oldStart = ref.startIndex;
            const int oldEnd = ref.endIndex;
            ref.reversed = !ref.reversed;
            ref.startIndex = points - 1 - oldEnd;
            ref.endIndex = points - 1 - oldStart;
            reversed.edgeRefs.push_back(ref);
        }
        return reversed;
    }

    /// <summary>run의 진행 방향(시작쪽 첫 유효 구간)을 반환한다.</summary>
    // The boundary turns by the dihedral angle wherever the neighbouring face
    // changes, so a 3D angle gate rejects legitimate splits of one straight
    // side. Judge in the working-plane projection instead, and use a chord
    // window so a short sampling segment cannot set the direction.

    const double kExitJunctionAngle2dDegrees = 15.0;
    const double kExitJunctionWindow = 1.0;
    const double kExitPlaneDirectionEpsilon = 1e-7;

    bool ExitReferenceCoverage(const NativeExitSide& run)
    {
        const int count = static_cast<int>(run.xyz.size() / 3);
        if (count < 2 || run.edgeRefs.empty()) return false;
        std::vector<int> coverage(static_cast<size_t>(count - 1), 0);
        for (const auto& ref : run.edgeRefs) {
            if (ref.key <= 0 || ref.revision != g_modelRevision || ref.faceId < 0 ||
                ref.startIndex < 0 || ref.endIndex >= count || ref.endIndex <= ref.startIndex ||
                ref.lastParameter <= ref.firstParameter) return false;
            for (int i = ref.startIndex; i < ref.endIndex; ++i) ++coverage[i];
        }
        for (int covered : coverage) if (covered != 1) return false;
        for (const auto& start : run.segmentStarts) if (start.faceId < 0) return false;
        return true;
    }

    gp_Vec ProjectExitDirection(const gp_Vec& direction, const gp_Vec& viewNormal)
    {
        const double length = viewNormal.Magnitude();
        if (length <= 1e-12) return direction;
        const gp_Vec unit = viewNormal / length;
        return direction - unit * (direction * unit);
    }

    /// <summary>
    /// Chord direction over a window (mm) measured from the requested end of the
    /// run. Sampling noise shorter than the window is ignored; a run shorter than
    /// the window falls back to its farthest sample.
    /// </summary>
    bool ExitWindowDirection(const NativeExitSide& run, bool fromEnd, gp_Vec& direction)
    {
        const int count = static_cast<int>(run.xyz.size() / 3);
        if (count < 2) return false;
        const int anchor = fromEnd ? count - 1 : 0;
        const gp_Pnt base(run.xyz[anchor * 3], run.xyz[anchor * 3 + 1], run.xyz[anchor * 3 + 2]);
        int farthest = -1;
        for (int step = 1; step < count; ++step)
        {
            const int i = fromEnd ? count - 1 - step : step;
            const gp_Pnt point(run.xyz[i * 3], run.xyz[i * 3 + 1], run.xyz[i * 3 + 2]);
            const double distance = base.Distance(point);
            if (distance <= 1e-9) continue;
            if (distance >= kExitJunctionWindow)
            {
                farthest = i;
                break;
            }
            farthest = i;
        }
        if (farthest < 0) return false;
        const gp_Pnt point(run.xyz[farthest * 3], run.xyz[farthest * 3 + 1], run.xyz[farthest * 3 + 2]);
        direction = fromEnd ? gp_Vec(point, base) : gp_Vec(base, point);
        return true;
    }

    /// <summary>
    /// 와이어 순서상 연속된 두 run이 같은 인출 측면의 연속인지 판정한다.
    /// 접합점이 정확히 일치하고, 접합부의 국소 진행 방향 변화가 작아야 한다.
    /// STEP 모델은 같은 측면이 여러 인접면(슬리버 포함)으로 쪼개져 Edge가 끊기고 원본 커브도
    /// BSpline으로 근사되므로 완전 공선을 요구하지 않고 꺾임 각도로 판정한다.
    /// 모서리(코너)와 되돌이는 병합하지 않는다.
    /// </summary>
    bool CanJoinExitRuns(const NativeExitSide& first, const NativeExitSide& second,
                         const gp_Vec& viewNormal, double contactTolerance, bool& flipped)
    {
        flipped = false;
        if (first.xyz.size() < 6 || second.xyz.size() < 6) return false;
        if (!(contactTolerance > 0.0)) return false;

        const size_t firstCount = first.xyz.size();
        const gp_Pnt firstEnd(first.xyz[firstCount - 3], first.xyz[firstCount - 2], first.xyz[firstCount - 1]);
        const gp_Pnt secondStart(second.xyz[0], second.xyz[1], second.xyz[2]);
        const size_t secondCount = second.xyz.size();
        const gp_Pnt secondEnd(second.xyz[secondCount - 3], second.xyz[secondCount - 2],
                               second.xyz[secondCount - 1]);
        const double forwardDistance = firstEnd.Distance(secondStart);
        const double backwardDistance = firstEnd.Distance(secondEnd);
        const double contact = forwardDistance < backwardDistance ? forwardDistance : backwardDistance;
        const bool forward = forwardDistance <= contactTolerance;
        const bool backward = backwardDistance <= contactTolerance;
        if (!forward && !backward)
        {
            g_exitSideDiagnostic += "join contact=" + std::to_string(contact) + " reject(contact)\n";
            return false;
        }
        flipped = backward && !forward;

        gp_Vec directionFirst, directionSecond;
        if (!ExitWindowDirection(first, true, directionFirst)) return false;
        if (!ExitWindowDirection(second, false, directionSecond)) return false;
        if (flipped) directionSecond.Reverse();
        directionFirst = ProjectExitDirection(directionFirst, viewNormal);
        directionSecond = ProjectExitDirection(directionSecond, viewNormal);
        const double lengthFirst = directionFirst.Magnitude();
        const double lengthSecond = directionSecond.Magnitude();
        if (lengthFirst <= kExitPlaneDirectionEpsilon || lengthSecond <= kExitPlaneDirectionEpsilon)
        {
            g_exitSideDiagnostic += "join reject(degenerate-projection)\n";
            return false;
        }
        const gp_Vec unitFirst = directionFirst / lengthFirst;
        const gp_Vec unitSecond = directionSecond / lengthSecond;
        const double cosine = unitFirst * unitSecond;
        double clamped = cosine;
        if (clamped < -1.0) clamped = -1.0;
        if (clamped > 1.0) clamped = 1.0;
        const double degrees = std::acos(clamped) * 180.0 / 3.141592653589793;
        const double limit = std::cos(kExitJunctionAngle2dDegrees * 3.141592653589793 / 180.0);
        g_exitSideDiagnostic += "join contact=" + std::to_string(contact) +
            " angle2d=" + std::to_string(degrees) + (cosine >= limit ? " pass\n" : " reject(angle)\n");
        return cosine >= limit;
    }

    /// <summary>
    /// 확정 영역 외곽 경계를 따라 인출 측면 후보(run)를 만든다.
    /// 영역 경계 추출과 동일한 0.01mm Sewing 공차/경계 분류를 사용한다.
    /// </summary>
    bool ExtractBoundaryExitSides(const std::vector<int>& faceIds, double deflection,
                                  const gp_Vec& viewNormal, std::vector<NativeExitSide>& sides,
                                  ExitEdgeStoreSnapshot& staged)
    {
        sides.clear();
        g_exitSideDiagnostic.clear();
        if (faceIds.empty() || !std::isfinite(deflection) || deflection <= 0.0) return false;
        std::set<int> selected;
        for (int id : faceIds)
        {
            if (id < 0 || id >= static_cast<int>(g_faces.size())) return false;
            selected.insert(id);
        }
        BeginExitEdgeStaging(staged);

        try
        {
            const double sewingTolerance = 0.01;
            // 원본 CAD edge 매칭 허용 거리(mm). 길이 차이도 같은 값을 쓴다.
            const double geometryMatchTolerance = 0.1;
            std::string faceList;
            for (int faceId : selected) { faceList += std::to_string(faceId); faceList += ','; }
            g_exitSideDiagnostic += "faces=" + faceList + "\n";
            // 영역 경계와 동일한 원본 우선 정책을 쓴다. 원본 위상이 완결이면
            // 병합하지 않으므로 같은 비다양체 오판이 발생하지 않는다.
            TopoDS_Shape workingShape;
            std::vector<TopoDS_Face> workingFaces;
            BRepBuilderAPI_Sewing sewing(sewingTolerance, Standard_True, Standard_False, Standard_True, Standard_True);
            bool sewed = false;
            std::string originalTopologyReason;
            if (!BuildOriginalSelectedRegionShape(selected, workingShape, workingFaces, originalTopologyReason))
            {
                for (int faceId : selected) sewing.Add(g_faces[faceId]);
                sewing.Perform();
                workingShape = sewing.SewedShape();
                if (workingShape.IsNull()) return false;
                workingFaces.clear();
                for (TopExp_Explorer ex(workingShape, TopAbs_FACE); ex.More(); ex.Next())
                    workingFaces.push_back(TopoDS::Face(ex.Current()));
                if (workingFaces.size() != selected.size()) return false;
                sewed = true;
            }

            TopTools_IndexedMapOfShape edgeMap;
            std::vector<int> uses;
            for (const TopoDS_Face& face : workingFaces)
                for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next())
                {
                    const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                    // 영역 경계와 같은 퇴화 Edge 분류를 쓴다.
                    if (BRep_Tool::Degenerated(edge)) continue;
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
            for (int count : uses)
                if (count > 2) // 비다양체 선택 거부
                {
                    g_exitSideDiagnostic += sewed
                        ? "reject(non-manifold-after-seam-join)\n"
                        : "reject(non-manifold)\n";
                    return false;
                }
            for (int i = 1; i <= edgeMap.Extent(); ++i)
                if (uses[i - 1] == 1) boundaryIndices.push_back(i);
            if (boundaryIndices.empty()) return false;
            std::vector<TopoDS_Edge> boundaryEdges;
            for (int index : boundaryIndices) boundaryEdges.push_back(TopoDS::Edge(edgeMap(index)));

            // 원본 CAD Edge 참조 후보: 선택 면 집합에서 한 번만 사용된 Edge = 실제 외곽 경계.
            // Sewing 사본의 Edge 인덱스는 영속 ID로 쓰지 않는다.
            TopTools_IndexedMapOfShape originalEdgeMap;
            std::vector<int> originalEdgeUses;
            for (int faceId : selected)
            {
                for (TopExp_Explorer ex(g_faces[faceId], TopAbs_EDGE); ex.More(); ex.Next())
                {
                    const TopoDS_Edge edge = TopoDS::Edge(ex.Current());
                    // 퇴화 Edge는 원본 외곽 후보가 아니며 source 구간도 없다.
                    if (BRep_Tool::Degenerated(edge)) continue;
                    int index = originalEdgeMap.FindIndex(edge);
                    if (index == 0)
                    {
                        originalEdgeMap.Add(edge);
                        originalEdgeUses.push_back(0);
                        index = originalEdgeMap.Extent();
                    }
                    ++originalEdgeUses[index - 1];
                }
            }
            std::vector<TopoDS_Edge> originalBoundaryEdges;
            for (int i = 1; i <= originalEdgeMap.Extent(); ++i)
                if (originalEdgeUses[i - 1] == 1)
                    originalBoundaryEdges.push_back(TopoDS::Edge(originalEdgeMap(i)));

            // 각 경계 Edge 반대편의 원본 면 (-1 = 나체 Edge). 봉합선(use==2)이 인덱스를
            // 밀지 않도록 edgeMap 인덱스로 직접 보관한다.
            std::vector<std::vector<int>> inverse;
            if (sewed)
                inverse = HeatingMoldExitNative::SewingSourceInverse(sewing, originalBoundaryEdges, edgeMap);
            else
                inverse = IdentitySourceHistory(boundaryEdges, originalBoundaryEdges, edgeMap);
            std::vector<HeatingMoldExitNative::SourceInterval> sourceMatches(static_cast<size_t>(edgeMap.Extent()));
            std::vector<bool> sourceValid(static_cast<size_t>(edgeMap.Extent()), false);
            std::vector<int> adjacent(edgeMap.Extent(), -1);
            for (size_t i = 0; i < boundaryEdges.size(); ++i)
            {
                const int index = boundaryIndices[i] - 1;
                bool ambiguous = false;
                sourceValid[index] = HeatingMoldExitNative::FindUniqueSourceInterval(boundaryEdges[i], originalBoundaryEdges,
                    inverse[index], geometryMatchTolerance, sourceMatches[index], ambiguous);
                const int match = FindAdjacentOriginalFace(boundaryEdges[i], selected, geometryMatchTolerance,
                    sourceValid[index] ? &sourceMatches[index].edge : nullptr);
                adjacent[index] = match;
                g_exitSideDiagnostic += "edge" + std::to_string(i) +
                    " len=" + std::to_string(EdgeLengthNative(boundaryEdges[i])) +
                    " adjacent=" + std::to_string(match) +
                    (sourceValid[index] ? " source=bound\n" : (ambiguous ? " reject(ambiguous-source)\n" : " reject(missing-source)\n"));
            }

            // 외곽 슬롯마다 원본 대응과 반대편 면을 직접 보관한다. 순회 단계는
            // 재조립된 Edge가 아니라 이 기록을 그대로 사용한다.
            std::vector<BoundaryEdgeInput> boundaryInputs;
            boundaryInputs.reserve(boundaryEdges.size());
            for (size_t i = 0; i < boundaryEdges.size(); ++i)
            {
                BoundaryEdgeInput input;
                input.workingEdge = boundaryEdges[i];
                input.edgeMapIndex = boundaryIndices[i];
                input.source = sourceMatches[static_cast<size_t>(input.edgeMapIndex - 1)];
                input.sourceValid = sourceValid[static_cast<size_t>(input.edgeMapIndex - 1)];
                input.relatedFace = adjacent[static_cast<size_t>(input.edgeMapIndex - 1)];
                boundaryInputs.push_back(input);
            }

            std::vector<TopoDS_Edge> boundaryWorkingEdges;
            boundaryWorkingEdges.reserve(boundaryInputs.size());
            for (const BoundaryEdgeInput& input : boundaryInputs)
                boundaryWorkingEdges.push_back(input.workingEdge);
            std::vector<HeatingMoldBoundaryNative::BoundaryComponent> components;
            int failedComponents = 0;
            std::string orderFailure;
            if (!HeatingMoldBoundaryNative::OrderBoundaryLoops(
                    boundaryWorkingEdges, components, failedComponents, orderFailure))
            {
                g_exitSideDiagnostic += "reject(" + orderFailure + ")\n";
                return false;
            }
            if (failedComponents > 0)
            {
                // 일부 경계만 후보로 게시하면 인출 후보와 실제 영역 경계가
                // 서로 다른 결과가 된다. 계산 실패는 부분 성공으로 숨기지 않는다.
                g_exitSideDiagnostic += orderFailure;
                sides.clear();
                return false;
            }

            for (const HeatingMoldBoundaryNative::BoundaryComponent& component : components)
            {
                // 순회 순서로 같은 인접면의 연속 Edge를 하나의 run으로 누적한다.
                std::vector<NativeExitSide> runs;
                NativeExitSide current;
                for (const HeatingMoldBoundaryNative::BoundaryStep& step : component.steps)
                {
                    const BoundaryEdgeInput& input = boundaryInputs[static_cast<size_t>(step.inputIndex)];
                    const int faceId = input.relatedFace;

                    if (faceId < 0 || !input.sourceValid) {
                        // A naked/unmapped interval is a break, never an unbound bridge
                        // hidden inside an otherwise valid candidate.
                        if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));
                        current = NativeExitSide();
                        continue;
                    }
                    const auto& source = input.source;
                    const bool curveReversed = input.workingEdge.Orientation() == TopAbs_REVERSED;
                    const bool reversed = (source.reversed != curveReversed) != step.reverse;

                    std::vector<gp_Pnt> points;
                    bool sampled = false;
                    try
                    {
                        double originalFirst = 0.0, originalLast = 0.0;
                        const Handle(Geom_Curve) originalCurve = BRep_Tool::Curve(source.edge, originalFirst, originalLast);
                        const TopoDS_Edge displayEdge = BRepBuilderAPI_MakeEdge(originalCurve, source.first, source.last).Edge();
                        BRepAdaptor_Curve curve(displayEdge);
                        GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                        if (sampler.IsDone() && sampler.NbPoints() >= 2 && sampler.NbPoints() <= 100000)
                        {
                            sampled = true;
                            const int count = sampler.NbPoints();
                            for (int k = 1; k <= count; ++k)
                            {
                                const gp_Pnt p = sampler.Value(reversed ? count - k + 1 : k);
                                if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z()))
                                { sampled = false; break; }
                                points.push_back(p);
                            }
                        }
                    }
                    catch (const Standard_Failure&) { sampled = false; }

                    if (!sampled)
                    {
                        // 반대편 면을 찾지 못한 구간(나체 Edge)은 후보 run을 끊는다.
                        if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));
                        current = NativeExitSide();
                        continue;
                    }
                    const bool disconnected = current.xyz.size() >= 3 &&
                        gp_Pnt(current.xyz[current.xyz.size() - 3], current.xyz[current.xyz.size() - 2],
                            current.xyz[current.xyz.size() - 1]).Distance(points.front()) > HeatingMoldExitNative::GeometryEpsilon;
                    if (current.faceId >= 0 && (current.faceId != faceId || disconnected))
                    {
                        if (current.xyz.size() >= 6) runs.push_back(std::move(current));
                        current = NativeExitSide();
                    }
                    // 이 Sewing Edge에 대응하는 원본 Edge 참조를 함께 보존한다.
                    NativeExitSide piece;
                    piece.faceId = faceId;
                    for (const gp_Pnt& p : points) AppendExitPoint(piece, faceId, p);
                    const int supportFace = FindSupportFaceForEdge(source.edge, selected);
                    if (supportFace < 0) {
                        g_exitSideDiagnostic += "reject(missing-support-face)\n";
                        if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));
                        current = NativeExitSide(); continue;
                    }
                    NativeExitEdgeRef ref;
                    ref.revision = g_modelRevision;
                    ref.key = StageOriginalExitEdge(staged, source, faceId, supportFace);
                    const auto stagedEdge = staged.edges.find(ref.key);
                    if (stagedEdge != staged.edges.end()) ref.signature = stagedEdge->second.signature;
                    ref.faceId = faceId;
                    ref.firstParameter = source.first;
                    ref.lastParameter = source.last;
                    ref.reversed = reversed;
                    ref.startIndex = 0;
                    ref.endIndex = static_cast<int>(piece.xyz.size() / 3) - 1;
                    piece.edgeRefs.push_back(ref);
                    current.faceId = faceId;
                    AppendExitRun(current, piece);
                }
                if (current.faceId >= 0 && current.xyz.size() >= 6) runs.push_back(std::move(current));

                // 닫힌 루프: 마지막 run과 첫 run이 같은 면이면 하나로 합친다.
                if (runs.size() >= 2 && runs.front().faceId == runs.back().faceId &&
                    gp_Pnt(runs.back().xyz[runs.back().xyz.size() - 3], runs.back().xyz[runs.back().xyz.size() - 2],
                        runs.back().xyz[runs.back().xyz.size() - 1]).Distance(
                            gp_Pnt(runs.front().xyz[0], runs.front().xyz[1], runs.front().xyz[2])) <= HeatingMoldExitNative::GeometryEpsilon)
                {
                    const NativeExitSide head = runs.front();
                    AppendExitRun(runs.back(), head);
                    runs.erase(runs.begin());
                }

                // 같은 루프 안에서 와이어 순서상 연속된 run을 하나의 인출 측면으로 병합한다.
                // (STEP에서 반대편 면이 달라 중간에 끊긴 긴 변 복원. 구간별 면 id는 segmentStarts에 보존.)
                {
                    std::vector<NativeExitSide> joined;
                    for (auto& run : runs) joined.push_back(std::move(run));
                    const double contactTolerance = HeatingMoldExitNative::GeometryEpsilon;
                    bool merged = true;
                    while (merged && joined.size() >= 2)
                    {
                        merged = false;
                        for (size_t i = 0; i + 1 < joined.size(); ++i)
                        {
                            bool flipped = false;
                            if (!CanJoinExitRuns(joined[i], joined[i + 1], viewNormal, contactTolerance, flipped))
                                continue;
                            const NativeExitSide tail = flipped ? ReverseExitRun(joined[i + 1]) : joined[i + 1];
                            AppendExitRun(joined[i], tail);
                            joined.erase(joined.begin() + static_cast<std::ptrdiff_t>(i + 1));
                            merged = true;
                            if (i > 0) --i;
                        }
                        if (joined.size() >= 2)
                        {
                            bool flipped = false;
                            if (CanJoinExitRuns(joined.back(), joined.front(), viewNormal, contactTolerance, flipped))
                            {
                                const NativeExitSide head = flipped ? ReverseExitRun(joined.front()) : joined.front();
                                AppendExitRun(joined.back(), head);
                                joined.erase(joined.begin());
                                merged = true;
                            }
                        }
                    }
                    runs.swap(joined);
                }
                for (auto& run : runs) {
                    if (!ExitReferenceCoverage(run))
                    {
                        // 원본 참조가 끊긴 후보를 나머지 후보와 섞어 게시하지 않는다.
                        g_exitSideDiagnostic += "reject(incomplete-original-reference-coverage)\n";
                        sides.clear();
                        return false;
                    }
                    sides.push_back(std::move(run));
                }
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
        DropExitFaceHoverNative();
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

    // 표시 wire의 순서를 유지한 채 3D 점 체인을 만든다. Edge 방향(TopAbs_REVERSED)을
    // 반영하고 연속 중복점만 제거한다. 내보내기는 이 체인을 재사용해야 하므로
    // 실패하면 표시 자체를 만들지 않는 호출측 정책을 따른다.
    bool SampleWireOrderedNative(const TopoDS_Wire& wire, double deflection,
        std::vector<double>& out)
    {
        out.clear();
        if (wire.IsNull() || !std::isfinite(deflection) || deflection <= 0) return false;
        try
        {
            auto append = [&out](const gp_Pnt& p) {
                if (out.size() >= 3)
                {
                    const double dx = p.X() - out[out.size() - 3];
                    const double dy = p.Y() - out[out.size() - 2];
                    const double dz = p.Z() - out[out.size() - 1];
                    if (dx * dx + dy * dy + dz * dz <= 1e-14) return;
                }
                out.push_back(p.X()); out.push_back(p.Y()); out.push_back(p.Z());
            };
            auto appendChecked = [&append, &out](const gp_Pnt& p) {
                if (!std::isfinite(p.X()) || !std::isfinite(p.Y()) || !std::isfinite(p.Z()))
                {
                    out.clear();
                    return false;
                }
                append(p);
                return true;
            };
            for (BRepTools_WireExplorer ex(wire); ex.More(); ex.Next())
            {
                BRepAdaptor_Curve curve(ex.Current());
                GCPnts_QuasiUniformDeflection sampler(curve, deflection);
                if (!sampler.IsDone() || sampler.NbPoints() < 2) { out.clear(); return false; }
                if (ex.Current().Orientation() == TopAbs_REVERSED)
                {
                    for (Standard_Integer i = sampler.NbPoints(); i >= 1; --i)
                        if (!appendChecked(sampler.Value(i))) return false;
                }
                else
                {
                    for (Standard_Integer i = 1; i <= sampler.NbPoints(); ++i)
                        if (!appendChecked(sampler.Value(i))) return false;
                }
            }
            if (out.size() < 6) { out.clear(); return false; }
            return true;
        }
        catch (...) { out.clear(); return false; }
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
            // OCCT may reverse the resulting wire. Results and the preserved export chain
            // must both follow the source route order.
            bool reversed = false;
            if (transverseDistance(a, sourceA) > 0.01 || transverseDistance(b, sourceB) > 0.01)
            {
                std::swap(a, b);
                reversed = true;
            }
            if (transverseDistance(a, sourceA) > 0.01 || transverseDistance(b, sourceB) > 0.01) return false;
            const gp_Vec offset = gp_Vec(direction) * depth;
            const gp_Pnt shownA = a.Translated(offset), shownB = b.Translated(offset);
            result[0] = props.Mass(); result[1] = shownA.X(); result[2] = shownA.Y(); result[3] = shownA.Z();
            result[4] = shownB.X(); result[5] = shownB.Y(); result[6] = shownB.Z();
            // 표시 wire를 그대로 샘플링해 깊이 이동까지 반영한 3D 체인을 보존한다.
            std::vector<double> shownXyz;
            if (!SampleWireOrderedNative(wires.front(), 0.1, shownXyz)) return false;
            if (depth > 0)
            {
                const gp_Vec shift = gp_Vec(direction) * depth;
                for (size_t i = 0; i + 2 < shownXyz.size(); i += 3) {
                    shownXyz[i] += shift.X(); shownXyz[i + 1] += shift.Y(); shownXyz[i + 2] += shift.Z();
                }
            }
            if (reversed)
            {
                for (size_t lo = 0, hi = shownXyz.size() - 3; lo < hi; lo += 3, hi -= 3)
                {
                    std::swap(shownXyz[lo], shownXyz[hi]);
                    std::swap(shownXyz[lo + 1], shownXyz[hi + 1]);
                    std::swap(shownXyz[lo + 2], shownXyz[hi + 2]);
                }
            }
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
                g_routePieces[pieceId] = { ais, connector, true, color, width, 0, 0, shownXyz };
            }
            return true;
        } catch (const Standard_Failure&) { return false; }
    }

    bool SetConnectorNative(int id, bool enabled, double r, double g, double b, bool redraw = true)
    {
        auto it = g_routePieces.find(id);
        if (it == g_routePieces.end() || !it->second.connector || g_ctx.IsNull()) return false;
        it->second.enabled = enabled;
        it->second.color = Quantity_Color(r, g, b, Quantity_TOC_RGB);
        g_ctx->SetColor(it->second.ais, it->second.color, Standard_False);
        it->second.ais->Attributes()->LineAspect()->SetTypeOfLine(enabled ? Aspect_TOL_SOLID : Aspect_TOL_DASH);
        g_ctx->Redisplay(it->second.ais, Standard_False);
        if (redraw && !g_view.IsNull()) g_view->Redraw();
        return true;
    }

    /// <summary>스테이징/확정 경로 조각의 색을 한 번에 바꾼다. 모든 조각을 먼저 검증하고
    /// 하나라도 실패하면 어떤 색도 바꾸지 않는다(부분 적용 금지).</summary>
    struct WorkingNative;
    WorkingNative* FindWorking(long long id, long long revision);
    bool SetRoutePieceColorsNative(long long contextId, long long revision,
        const std::vector<int>& pieceIds, const std::vector<double>& rgb, bool redraw)
    {
        if (g_ctx.IsNull() || pieceIds.empty() || pieceIds.size() * 3 != rgb.size()) return false;
        if (!FindWorking(contextId, revision)) return false;
        for (double value : rgb)
            if (!std::isfinite(value) || value < 0.0 || value > 1.0) return false;
        std::set<int> unique;
        for (int pieceId : pieceIds)
        {
            const auto found = g_routePieces.find(pieceId);
            if (found == g_routePieces.end() || !unique.insert(pieceId).second ||
                found->second.contextId != contextId || found->second.revision != revision)
                return false;
        }
        // Overlay.color까지 함께 갱신해야 호버 해제 시 원래 영역 색으로 복원된다.
        for (size_t i = 0; i < pieceIds.size(); ++i)
        {
            RoutePieceOverlay& overlay = g_routePieces[pieceIds[i]];
            const Quantity_Color color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], Quantity_TOC_RGB);
            overlay.color = color;
            g_ctx->SetColor(overlay.ais, color, Standard_False);
        }
        if (redraw && !g_view.IsNull()) g_view->Redraw();
        return true;
    }

    void ClearRouteEndpointMarkersNative()
    {
        if (!g_ctx.IsNull())
            for (const auto& marker : g_routeEndpointMarkers) g_ctx->Remove(marker, Standard_False);
        g_routeEndpointMarkers.clear();
    }

    // 인출점/끝점을 눈에 띄는 원형 점으로 표시한다. xyz는 [x,y,z,...] 평면 배열.
    bool SetRouteEndpointsNative(const std::vector<double>& xyz)
    {
        if (xyz.size() % 3 != 0 || (g_ctx.IsNull() && !xyz.empty())) return false;
        for (double value : xyz) if (!std::isfinite(value)) return false;
        std::vector<Handle(AIS_Point)> next;
        try {
            next.reserve(xyz.size() / 3);
            for (size_t i = 0; i < xyz.size(); i += 3) {
                Handle(AIS_Point) marker = new AIS_Point(new Geom_CartesianPoint(xyz[i], xyz[i + 1], xyz[i + 2]));
                marker->SetMarker(Aspect_TOM_O_POINT);
                marker->SetColor(kRouteEndpointColor);
                Handle(Prs3d_PointAspect) pointAspect = marker->Attributes()->PointAspect();
                if (pointAspect.IsNull()) {
                    pointAspect = new Prs3d_PointAspect(Aspect_TOM_O_POINT, kRouteEndpointColor, 6.0);
                    marker->Attributes()->SetPointAspect(pointAspect);
                } else pointAspect->SetScale(6.0);
                next.push_back(marker);
            }
            for (const auto& marker : next) {
                g_ctx->Display(marker, Standard_False);
                g_ctx->SetZLayer(marker, Graphic3d_ZLayerId_Topmost);
                g_ctx->Deactivate(marker, Standard_False);
            }
        } catch (...) {
            if (!g_ctx.IsNull()) for (const auto& marker : next) { try { g_ctx->Remove(marker, Standard_False); } catch (...) {} }
            return false;
        }
        g_routeEndpointMarkers.swap(next);
        bool success = true;
        if (!g_ctx.IsNull()) for (const auto& marker : next) {
            try { g_ctx->Remove(marker, Standard_False); } catch (...) { success = false; }
        }
        return success;
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
        g_committedWorkingPolylines.clear();
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

    bool GetModelBoundsInFrameForShape(const TopoDS_Shape& shape,
                                       const gp_Dir& xAxis, const gp_Dir& yAxis,
                                       const gp_Dir& zAxis, double* out)
    {
        if (shape.IsNull() || out == nullptr) return false;
        try
        {
            Bnd_Box box;
            BRepBndLib::Add(shape, box);
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

    bool GetModelBoundsInFrameNative(const gp_Dir& xAxis, const gp_Dir& yAxis,
                                     const gp_Dir& zAxis, double* out)
    {
        return GetModelBoundsInFrameForShape(g_shape, xAxis, yAxis, zAxis, out);
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
        RestoreFaceColorNative(faceId);
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

    /// <summary>인출면 선택 호버 색 override를 걷어낸다. 선택/확정 색은 각자 소유한다.</summary>
    void DropExitFaceHoverNative()
    {
        if (g_exitFaceHover < 0) return;
        const int faceId = g_exitFaceHover;
        g_exitFaceHover = -1;
        RestoreFaceColorNative(faceId);
    }

    /// <summary>인출면 선택 모드 — 커서 아래 후보 면만 호버색으로 강조한다.</summary>
    void HighlightExitFaceNative(int faceId)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return;
        if (faceId < 0 || faceId >= static_cast<int>(g_faces.size())) faceId = -1;
        if (faceId == g_exitFaceHover) return;
        DropExitFaceHoverNative();
        g_exitFaceHover = faceId;
        if (faceId >= 0 && g_selectedFaceIds.count(faceId) == 0 &&
            g_confirmedFaceColors.count(faceId) == 0 &&
            !IsExitFaceHighlightNative(faceId))
            SetFaceColorOverride(faceId, HoverFaceColorNative());
        g_ctx->Redisplay(g_aisShape, Standard_False);
        g_view->Redraw();
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
        int idx = (status == PickStatusNative::Ok) ? faceId : -1;
        // 지속 강조 중인 인출면은 소유 색을 유지하도록 호버 대상에서 뺀다.
        // 클리핑 메시가 있는 Face도 같은 Face를 다시 선택할 수 있어야 하므로 호버를 막지 않는다.
        if (idx >= 0 && IsExitFaceHighlightNative(idx)) idx = -1;
        if (idx == g_lastHoverFace) return idx;
        const int previous = g_lastHoverFace;
        g_lastHoverFace = idx;
        if (previous == g_hoverColorFace) g_hoverColorFace = -1;
        // Selected/confirmed/exit-highlight owners keep their own colour (SetToHilightSelected(false) semantics).
        if (previous >= 0) RestoreFaceColorNative(previous);
        if (idx >= 0 && g_selectedFaceIds.count(idx) == 0 && g_confirmedFaceColors.count(idx) == 0 &&
            !IsExitFaceHighlightNative(idx))
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

    /// <summary>현재 AIS 선택에서 면 ID 집합을 수집한다.</summary>
    std::set<int> CollectSelectedFaceIdsNative()
    {
        std::set<int> selected;
        if (g_ctx.IsNull()) return selected;
        for (g_ctx->InitSelected(); g_ctx->MoreSelected(); g_ctx->NextSelected())
        {
            const Handle(StdSelect_BRepOwner) brep =
                Handle(StdSelect_BRepOwner)::DownCast(g_ctx->SelectedOwner());
            if (brep.IsNull() || !brep->HasShape()) continue;
            if (!g_faceMap.Contains(brep->Shape())) continue;
            selected.insert(g_faceMap.FindIndex(brep->Shape()) - 1);
        }
        return selected;
    }

    // 사각형 선택 가시성/프리뷰 (FaceIdSelectionNative.inc 에서 정의, 아래에서 include)
    bool QueryVisibleFaceIdsNative(int x0, int y0, int x1, int y1, std::vector<int>& out);
    bool QueryRectangleFaceIdsNative(int x0, int y0, int x1, int y1, std::vector<int>& out);
    void BeginRectanglePreviewNative();
    void PreviewFacesRectangleNative(int x0, int y0, int x1, int y1);
    void EndRectanglePreviewNative(bool cancel);
    FaceApplyStatusNative ApplySelectedFaceIdsNative(const std::vector<int>& faceIds, std::vector<int>& applied);
    bool IsRectanglePreviewActiveNative();
    void ReleaseFaceIdCacheNative();
    void AbortRectanglePreviewNative();
    // 마우스를 놓은 뒤에만 호출되는 국소 고해상도 보충 (FaceIdSelectionRefinementNative.inc)
    void RefineRectangleSelectionNative(int x0, int y0, int x1, int y1,
                                        const std::vector<int>& baseFaceIds,
                                        std::set<int>& supplemental);
    // 진단 전용: 지정 영역을 8배로 다시 렌더해 디코드되는 face id를 반환한다.
    std::vector<int> ProbeFaceIdRegionNative(int x0, int y0, int x1, int y1);

    /// <summary>사각형과 일부라도 교차하는 모델 FACE를 선택한다. 화면에서 가려진 면도
    /// OCCT 선택 판정에 포함된다(제품 정책). false면 선택 판정/적용 실패이며 기존 선택을
    /// 그대로 둔다. true + 빈 result는 유효한 빈 선택이다.</summary>
    bool SelectFacesRectangleNative(int x0, int y0, int x1, int y1, std::vector<int>& result)
    {
        result.clear();
        if (g_ctx.IsNull() || g_view.IsNull() || g_aisShape.IsNull()) return false;

        std::vector<int> ids;
        BeginRectanglePreviewNative();   // 활성 세션이 없으면 기준 선택을 보관한다
        if (!QueryRectangleFaceIdsNative(x0, y0, x1, y1, ids))
        {
            EndRectanglePreviewNative(true);
            return false;
        }

        if (ApplySelectedFaceIdsNative(ids, result) == FaceApply_Failed)
        {
            EndRectanglePreviewNative(true);   // 드래그 시작 전 선택으로 복구
            result.clear();
            return false;
        }
        EndRectanglePreviewNative(false);
        return true;
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
        // 프리뷰 세션이 활성화면 33ms 틱이 밴드와 면 강조를 한 번에 그린다(중복 Redraw 방지).
        if (!g_view.IsNull() && !IsRectanglePreviewActiveNative()) g_view->Redraw();
    }

    void ClearSelectionRectangleNative()
    {
        // 실제 화면 갱신은 호출 측(프리뷰 종료/확정)이 한 번만 수행한다.
        if (g_ctx.IsNull() || g_selectionRubberBand.IsNull()) return;
        g_ctx->Remove(g_selectionRubberBand, Standard_False);
        g_selectionRubberBand.Nullify();
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
                         std::vector<NativeHlrLine>& out, double deflection, bool& failed)
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
                // 부분 투상선을 성공으로 저장하지 않는다.
                if (!sampler.IsDone() || sampler.NbPoints() < 2)
                {
                    failed = true;
                    out.clear();
                    return;
                }
                NativeHlrLine line;
                line.visible = visible;
                for (Standard_Integer i = 1; i <= sampler.NbPoints(); ++i)
                {
                    const gp_Pnt p = curve.Value(sampler.Parameter(i));
                    if (!std::isfinite(p.X()) || !std::isfinite(p.Y()))
                    {
                        failed = true;
                        out.clear();
                        return;
                    }
                    line.pts.push_back(p.X());
                    line.pts.push_back(p.Y());
                }
                out.push_back(line);
            }
            catch (const Standard_Failure&)
            {
                failed = true;
                out.clear();
                return;
            }
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
        bool failed = false;
        CollectHlrEdges(hlr.VCompound(), true, out, defl, failed);
        if (failed) return {};
        CollectHlrEdges(hlr.Rg1LineVCompound(), true, out, defl, failed);
        if (failed) return {};
        CollectHlrEdges(hlr.OutLineVCompound(), true, out, defl, failed);
        if (failed) return {};
        if (hiddenToo)
        {
            CollectHlrEdges(hlr.HCompound(), false, out, defl, failed);
            if (failed) return {};
            CollectHlrEdges(hlr.Rg1LineHCompound(), false, out, defl, failed);
            if (failed) return {};
            CollectHlrEdges(hlr.OutLineHCompound(), false, out, defl, failed);
            if (failed) return {};
        }
        return out;
    }
#include "WorkingSideNative.inc"
#include "AreaHighlightNative.inc"
#include "FaceIdSelectionNative.inc"
#include "FaceIdSelectionRefinementNative.inc"

#include "ExitEdgeQueriesNative.inc"
#include "ProjectRestoreNative.inc"
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
WorkingContext^ Bridge::CreateWorkingContextWithFrame(array<double>^ frame)
{
    if (frame==nullptr || frame->Length!=12) return nullptr;
    for each(double value in frame) if (!std::isfinite(value)) return nullptr;
    const long long id = CreateWorkingNativeAtFrame(
        frame[0],frame[1],frame[2], frame[3],frame[4],frame[5],
        frame[6],frame[7],frame[8], frame[9],frame[10],frame[11]);
    if (!id) return nullptr;
    auto* c = FindWorking(id,g_modelRevision);
    if (c == nullptr) return nullptr;
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
        std::vector<double> xyz; xyz.reserve(static_cast<size_t>(flat->Length)); for each(double v in flat) xyz.push_back(v);
        double data[7]={};
        if (!DisplayWorkingRoutePieceNative(id,revision,pieceId,msclr::interop::marshal_as<std::string>(kind),xyz,data)) return nullptr;
        auto result=gcnew RoutePieceResult(); result->Length=data[0];
        result->Start=gcnew array<double>{data[1],data[2],data[3]}; result->End=gcnew array<double>{data[4],data[5],data[6]};
        return result;
    } catch (...) { return nullptr; }
}
array<array<double>^>^ Bridge::GetCommittedRouteChains(long long id,long long revision,array<int>^ pieceIds)
{
    if (pieceIds==nullptr || pieceIds->Length==0) return nullptr;
    try {
        std::vector<int> ids; ids.reserve(pieceIds->Length);
        for each(int pieceId in pieceIds) ids.push_back(pieceId);
        std::vector<std::vector<double>> chains;
        if (!GetCommittedRouteChainsNative(id,revision,ids,chains) || chains.size()!=ids.size()) return nullptr;
        auto result=gcnew array<array<double>^>(static_cast<int>(chains.size()));
        for (size_t i=0;i<chains.size();++i) {
            auto points=gcnew array<double>(static_cast<int>(chains[i].size()));
            for (size_t k=0;k<chains[i].size();++k) points[static_cast<int>(k)]=chains[i][k];
            result[static_cast<int>(i)]=points;
        }
        return result;
    } catch (...) { return nullptr; }
}
array<CommittedRouteChain^>^ Bridge::GetCommittedRouteChainsDetailed(long long id,long long revision,array<int>^ pieceIds)
{
    if (pieceIds==nullptr || pieceIds->Length==0) return nullptr;
    try {
        std::vector<int> ids; ids.reserve(pieceIds->Length);
        for each(int pieceId in pieceIds) ids.push_back(pieceId);
        std::vector<std::vector<double>> chains;
        std::vector<std::vector<int>> faces;
        if (!GetCommittedRouteChainsDetailedNative(id,revision,ids,chains,faces) ||
            chains.size()!=ids.size() || faces.size()!=ids.size()) return nullptr;
        auto result=gcnew array<CommittedRouteChain^>(static_cast<int>(chains.size()));
        for (size_t i=0;i<chains.size();++i) {
            auto chain=gcnew CommittedRouteChain();
            auto points=gcnew array<double>(static_cast<int>(chains[i].size()));
            for (size_t k=0;k<chains[i].size();++k) points[static_cast<int>(k)]=chains[i][k];
            chain->Points=points;
            auto support=gcnew array<int>(static_cast<int>(faces[i].size()));
            for (size_t k=0;k<faces[i].size();++k) support[static_cast<int>(k)]=faces[i][k];
            chain->SupportFaceIds=support;
            result[static_cast<int>(i)]=chain;
        }
        return result;
    } catch (...) { return nullptr; }
}
bool Bridge::StageWorkingPolyline(long long id,long long revision,int stageId,array<double>^ flat)
{
    if (flat==nullptr) return false;
    try { std::vector<double> xyz; xyz.reserve(static_cast<size_t>(flat->Length)); for each(double v in flat) xyz.push_back(v); return StageWorkingPolylineNative(id,revision,stageId,xyz); }
    catch (...) { return false; }
}
RoutePieceResult^ Bridge::StageProjectedWorkingRoutePiece(long long id,long long revision,int stageId,String^ kind,
    array<double>^ flat,array<int>^ faceIds,double dx,double dy,double dz)
{
    if(kind==nullptr||flat==nullptr||faceIds==nullptr) return nullptr;
    try { std::vector<double> xyz;std::vector<int> faces;xyz.reserve(static_cast<size_t>(flat->Length));faces.reserve(static_cast<size_t>(faceIds->Length));for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageProjectedWorkingRouteNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,gp_Dir(dx,dy,dz),data))return nullptr;
        auto r=gcnew RoutePieceResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;
    } catch(...) { return nullptr; }
}
RoutePieceResult^ Bridge::StageSurfaceMappedRoutePiece(long long id,long long revision,int stageId,String^ kind,
    array<double>^ flat,array<int>^ faceIds)
{
    if(kind==nullptr||flat==nullptr||faceIds==nullptr) return nullptr;
    try { std::vector<double> xyz;std::vector<int> faces;xyz.reserve(static_cast<size_t>(flat->Length));faces.reserve(static_cast<size_t>(faceIds->Length));for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageSurfaceMappedRoutePieceNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,data))return nullptr;
        auto r=gcnew RoutePieceResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;
    } catch(...) { return nullptr; }
}
RoutePieceResult^ Bridge::StageSurfaceMappedRoutePieceWithFaces(long long id,long long revision,int stageId,String^ kind,
    array<double>^ flat,array<int>^ supportFaceIds)
{
    if(kind==nullptr||flat==nullptr||supportFaceIds==nullptr) return nullptr;
    try { std::vector<double> xyz;std::vector<int> faces;xyz.reserve(static_cast<size_t>(flat->Length));faces.reserve(static_cast<size_t>(supportFaceIds->Length));for each(double v in flat)xyz.push_back(v);for each(int v in supportFaceIds)faces.push_back(v);
        double data[7]={};if(!StageSurfaceMappedRoutePieceWithFacesNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,data))return nullptr;
        auto r=gcnew RoutePieceResult();r->Length=data[0];r->Start=gcnew array<double>{data[1],data[2],data[3]};r->End=gcnew array<double>{data[4],data[5],data[6]};return r;
    } catch(...) { return nullptr; }
}
String^ Bridge::GetWorkingStageDiagnostic()
{
    return gcnew String(GetWorkingStageDiagnosticNative());
}
RoutePieceResult^ Bridge::StageProjectedWorkingPolyline(long long id,long long revision,int stageId,String^ kind,array<double>^ flat,array<int>^ faceIds,double dx,double dy,double dz)
{
    if(flat==nullptr||faceIds==nullptr)return nullptr;
    try {std::vector<double> xyz;std::vector<int> faces;xyz.reserve(static_cast<size_t>(flat->Length));faces.reserve(static_cast<size_t>(faceIds->Length));for each(double v in flat)xyz.push_back(v);for each(int v in faceIds)faces.push_back(v);
        double data[7]={};if(!StageProjectedWorkingPolylineNative(id,revision,stageId,msclr::interop::marshal_as<std::string>(kind),xyz,faces,gp_Dir(dx,dy,dz),data))return nullptr;
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
bool Bridge::ConsumeCommitRedrawFailure()
{
    const bool failed = g_lastCommitRedrawFailed;
    g_lastCommitRedrawFailed = false;
    return failed;
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
    ClearLiveModelForRestore();
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

array<System::String^>^ Bridge::GetFaceTopologySignatures()
{
    try
    {
        const int count = static_cast<int>(g_faces.size());
        auto result = gcnew array<System::String^>(count);
        for (int i = 0; i < count; ++i)
        {
            result[i] = i < static_cast<int>(g_faceTopologySignatures.size())
                ? gcnew System::String(g_faceTopologySignatures[i].c_str())
                : System::String::Empty;
        }
        return result;
    }
    catch (...) { return nullptr; }
}

ProjectRestoreCandidateInfo^ Bridge::BeginProjectRestoreCandidate(String^ path, int importPolicyVersion)
{
    if (String::IsNullOrWhiteSpace(path)) return nullptr;
    try
    {
        const std::string native = msclr::interop::marshal_as<std::string>(path);
        std::string error;
        const int id = BeginProjectRestoreCandidateNative(native, importPolicyVersion, error);
        if (id <= 0) return nullptr;
        const auto it = g_projectRestoreCandidates.find(id);
        if (it == g_projectRestoreCandidates.end() || !it->second)
        {
            DiscardProjectRestoreCandidateNative(id);
            return nullptr;
        }
        const auto& candidate = *it->second;
        auto info = gcnew ProjectRestoreCandidateInfo();
        info->CandidateId = id;
        info->FaceSignatures = gcnew array<System::String^>(static_cast<int>(candidate.faceSignatures.size()));
        for (int i = 0; i < static_cast<int>(candidate.faceSignatures.size()); ++i)
            info->FaceSignatures[i] = gcnew System::String(candidate.faceSignatures[i].c_str());
        info->EdgeSignatures = gcnew array<System::String^>(static_cast<int>(candidate.edgeSignatures.size()));
        for (int i = 0; i < static_cast<int>(candidate.edgeSignatures.size()); ++i)
            info->EdgeSignatures[i] = gcnew System::String(candidate.edgeSignatures[i].c_str());
        return info;
    }
    catch (...) { return nullptr; }
}

bool Bridge::CommitProjectRestoreCandidate(int candidateId)
{
    return CommitProjectRestoreCandidateNative(candidateId);
}

void Bridge::DiscardProjectRestoreCandidate(int candidateId)
{
    DiscardProjectRestoreCandidateNative(candidateId);
}

System::String^ Bridge::GetProjectRestoreCandidateError()
{
    return gcnew System::String(g_projectRestoreCandidateError.c_str());
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
    System::String^ failureReason;
    return Bridge::GetSelectedRegionBoundary(faceIds, deflection, failureReason);
}

array<RegionBoundaryLoop^>^ Bridge::GetSelectedRegionBoundary(array<int>^ faceIds, double deflection,
                                                             System::String^% failureReason)
{
    failureReason = nullptr;
    if (faceIds == nullptr || faceIds->Length == 0)
    {
        failureReason = "region-boundary: no face selected";
        return nullptr;
    }
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    std::vector<NativeBoundaryLoop> nativeLoops;
    ExitEdgeStoreSnapshot staged;
    std::string diagnostic;
    try
    {
        if (!ExtractSelectedRegionBoundary(ids, deflection, nativeLoops, staged, diagnostic))
        {
            failureReason = diagnostic.empty()
                ? gcnew System::String("region-boundary: extraction failed")
                : msclr::interop::marshal_as<System::String^>(diagnostic);
            return nullptr;
        }
    }
    catch (const Standard_Failure&)
    {
        failureReason = "region-boundary: native exception";
        return nullptr;
    }
    catch (const std::exception&)
    {
        failureReason = "region-boundary: native exception";
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
        auto references = gcnew array<ExitEdgeReference^>((int)nativeLoops[(size_t)i].edgeRefs.size());
        for (int k = 0; k < references->Length; ++k)
        {
            const NativeExitEdgeRef& source = nativeLoops[(size_t)i].edgeRefs[(size_t)k];
            ExitEdgeReference^ reference = gcnew ExitEdgeReference();
            reference->Revision = source.revision;
            reference->EdgeKey = source.key;
            reference->EdgeSignature = gcnew System::String(source.signature.c_str());
            reference->FaceId = source.faceId;
            reference->FirstParameter = source.firstParameter;
            reference->LastParameter = source.lastParameter;
            reference->Reversed = source.reversed;
            reference->StartIndex = source.startIndex;
            reference->EndIndex = source.endIndex;
            references[k] = reference;
        }
        loop->EdgeReferences = references;
        result[i] = loop;
    }
    // managed 결과가 모두 준비된 뒤에만 원본 참조 등록을 게시한다.
    PublishExitEdgeStaging(staged);
    return result;
}

array<ExitSideCandidate^>^ Bridge::GetBoundaryExitSides(array<int>^ faceIds, double deflection)
{
    // Default working normal is the model +Z axis (legacy behaviour).
    return Bridge::GetBoundaryExitSides(faceIds, deflection, 0.0, 0.0, 1.0);
}

System::String^ Bridge::GetExitSideDiagnostic()
{
    return msclr::interop::marshal_as<System::String^>(g_exitSideDiagnostic);
}

array<ExitSideCandidate^>^ Bridge::GetBoundaryExitSides(array<int>^ faceIds, double deflection,
                                                       double nx, double ny, double nz)
{
    if (faceIds == nullptr || faceIds->Length == 0) return nullptr;
    std::vector<int> ids(faceIds->Length);
    for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
    const gp_Vec viewNormal(nx, ny, nz);
    std::vector<NativeExitSide> nativeSides;
    ExitEdgeStoreSnapshot staged;
    try
    {
        if (!ExtractBoundaryExitSides(ids, deflection, viewNormal, nativeSides, staged)) return nullptr;
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

        auto edgeRefs = gcnew array<ExitEdgeReference^>((int)run.edgeRefs.size());
        for (int r = 0; r < edgeRefs->Length; ++r)
        {
            const NativeExitEdgeRef& source = run.edgeRefs[(size_t)r];
            ExitEdgeReference^ reference = gcnew ExitEdgeReference();
            reference->Revision = source.revision;
            reference->EdgeKey = source.key;
            reference->EdgeSignature = gcnew System::String(source.signature.c_str());
            reference->FaceId = source.faceId;
            reference->FirstParameter = source.firstParameter;
            reference->LastParameter = source.lastParameter;
            reference->Reversed = source.reversed;
            reference->StartIndex = source.startIndex;
            reference->EndIndex = source.endIndex;
            edgeRefs[r] = reference;
        }
        side->EdgeReferences = edgeRefs;
        result[i] = side;
    }
    // managed 결과가 모두 준비된 뒤에만 원본 참조 등록을 게시한다.
    PublishExitEdgeStaging(staged);
    return result;
}

static NativeExitEdgeRef CopyExitReference(ExitEdgeReference^ reference)
{
    NativeExitEdgeRef native;
    native.revision = reference->Revision; native.key = reference->EdgeKey;
    if (reference->EdgeSignature != nullptr)
        native.signature = msclr::interop::marshal_as<std::string>(reference->EdgeSignature);
    native.faceId = reference->FaceId;
    native.firstParameter = reference->FirstParameter; native.lastParameter = reference->LastParameter;
    native.reversed = reference->Reversed;
    native.startIndex = reference->StartIndex; native.endIndex = reference->EndIndex;
    return native;
}
bool Bridge::CommitWorkingRouteGeometryDelta(long long id, long long revision, array<int>^ stagedRouteIds,
    array<int>^ finalRouteIds, array<int>^ stagedPolylineIds, array<int>^ retainedRouteIds, array<int>^ deletedRouteIds)
{
    if (stagedRouteIds == nullptr || finalRouteIds == nullptr || stagedPolylineIds == nullptr ||
        retainedRouteIds == nullptr || deletedRouteIds == nullptr) return false;
    try {
        std::vector<int> staged, finals, lines, retained, deleted;
        for each (int value in stagedRouteIds) staged.push_back(value);
        for each (int value in finalRouteIds) finals.push_back(value);
        for each (int value in stagedPolylineIds) lines.push_back(value);
        for each (int value in retainedRouteIds) retained.push_back(value);
        for each (int value in deletedRouteIds) deleted.push_back(value);
        return CommitWorkingGeometryDeltaNative(id, revision, staged, finals, lines, retained, deleted);
    } catch (...) { return false; }
}

static ExitEdgeHit^ ManagedExitHit(long long id, long long revision, long long edgeKey, const ExitEdgeHitNative& native)
{
    auto result = gcnew ExitEdgeHit();
    result->Status = static_cast<ExitEdgeQueryStatus>(native.status);
    result->FaceId = native.faceId;
    result->ContextId = id; result->ContextRevision = revision;
    result->EdgeKey = edgeKey; result->Revision = revision;
    if (native.status != ExitEdgeStatusNative::Hit) return result;
    result->VertexKey = native.vertexKey;
    result->Parameter = native.parameter;
    result->Point = gcnew array<double>{native.point.X(), native.point.Y(), native.point.Z()};
    result->Tangent = gcnew array<double>{native.tangent.X(), native.tangent.Y(), native.tangent.Z()};
    result->DatumPoint = gcnew array<double>{native.datum.X(), native.datum.Y()};
    return result;
}

static ExitEdgeHit^ FailedExitHit(ExitEdgeQueryStatus status)
{
    auto result = gcnew ExitEdgeHit();
    result->Status = status;
    result->FaceId = -1;
    return result;
}

ExitEdgeHit^ Bridge::EvaluateExitEdge(long long id, long long revision,
    long long edgeRevision, long long edgeKey, double u, double v, double tolerance)
{
    if (!std::isfinite(u) || !std::isfinite(v) || !std::isfinite(tolerance) || tolerance < 0.0)
        return FailedExitHit(ExitEdgeQueryStatus::InvalidInput);
    try {
        return ManagedExitHit(id, revision, edgeKey,
            EvaluateExitEdgeLegacyNative(id, revision, edgeRevision, edgeKey, u, v, tolerance));
    } catch (...) { return FailedExitHit(ExitEdgeQueryStatus::GeometryFailure); }
}

ExitEdgeHit^ Bridge::ProjectExitEdge(long long id, long long revision, ExitEdgeReference^ reference,
    double u, double v, double tolerance)
{
    if (reference == nullptr) return FailedExitHit(ExitEdgeQueryStatus::MissingReference);
    try {
        return ManagedExitHit(id, revision, reference->EdgeKey,
            ProjectExitEdgeNative(id, revision, CopyExitReference(reference), u, v, tolerance));
    } catch (...) { return FailedExitHit(ExitEdgeQueryStatus::GeometryFailure); }
}

ExitEdgeHit^ Bridge::EvaluateExitEdgeParameter(long long id, long long revision, ExitEdgeReference^ reference,
    double parameter)
{
    if (reference == nullptr) return FailedExitHit(ExitEdgeQueryStatus::MissingReference);
    try {
        return ManagedExitHit(id, revision, reference->EdgeKey,
            EvaluateExitEdgeParameterNative(id, revision, CopyExitReference(reference), parameter));
    } catch (...) { return FailedExitHit(ExitEdgeQueryStatus::GeometryFailure); }
}

ExitEdgeRayResult^ Bridge::IntersectExitEdgeRay(long long id, long long revision, ExitEdgeReference^ reference,
    double u, double v, double du, double dv, double tolerance)
{
    auto result = gcnew ExitEdgeRayResult();
    result->Hits = gcnew array<ExitEdgeHit^>(0);
    if (reference == nullptr) { result->Status = ExitEdgeQueryStatus::MissingReference; return result; }
    try {
        std::vector<ExitEdgeHitNative> hits;
        result->Status = static_cast<ExitEdgeQueryStatus>(IntersectExitEdgeRayNative(id, revision, CopyExitReference(reference),
            u, v, du, dv, tolerance, hits));
        if (result->Status != ExitEdgeQueryStatus::Hit) return result;
        result->Hits = gcnew array<ExitEdgeHit^>(static_cast<int>(hits.size()));
        for (int i = 0; i < result->Hits->Length; ++i)
            result->Hits[i] = ManagedExitHit(id, revision, reference->EdgeKey, hits[i]);
        return result;
    } catch (...) { result->Status = ExitEdgeQueryStatus::GeometryFailure; return result; }
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

void Bridge::HighlightExitFace(int faceId)
{
    try { HighlightExitFaceNative(faceId); }
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

bool Bridge::SetConnectorEnabled(int pieceId, bool enabled, double r, double g, double b, bool redraw)
{
    return SetConnectorNative(pieceId, enabled, r, g, b, redraw);
}

bool Bridge::SetRoutePieceColors(long long id, long long revision, array<int>^ pieceIds,
    array<double>^ rgb, bool redraw)
{
    if (pieceIds == nullptr || rgb == nullptr) return false;
    std::vector<int> ids((size_t)pieceIds->Length);
    std::vector<double> colors((size_t)rgb->Length);
    for (int i = 0; i < pieceIds->Length; ++i) ids[(size_t)i] = pieceIds[i];
    for (int i = 0; i < rgb->Length; ++i) colors[(size_t)i] = rgb[i];
    return SetRoutePieceColorsNative(id, revision, ids, colors, redraw);
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
    SetRouteEndpointsNoRedraw(xyzFlat);
    if (!g_view.IsNull()) g_view->Redraw();
}
bool Bridge::SetRouteEndpointsNoRedraw(array<double>^ xyzFlat)
{
    try {
        std::vector<double> xyz;
        if (xyzFlat != nullptr) { xyz.reserve(xyzFlat->Length); for each (double value in xyzFlat) xyz.push_back(value); }
        return SetRouteEndpointsNative(xyz);
    } catch (...) { return false; }
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
bool Bridge::TryRedrawView()
{
    if (g_view.IsNull()) return false;
    try { g_view->Redraw(); return true; } catch (...) { return false; }
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

array<int>^ Bridge::SelectFacesInRectangle(int x0, int y0, int x1, int y1)
{
    std::vector<int> ids;
    if (!SelectFacesRectangleNative(x0, y0, x1, y1, ids)) return nullptr;   // 실패 ≠ 유효한 빈 선택
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

void Bridge::BeginFaceRectanglePreview()
{
    BeginRectanglePreviewNative();
}

void Bridge::PreviewFacesInRectangle(int x0, int y0, int x1, int y1)
{
    PreviewFacesRectangleNative(x0, y0, x1, y1);
}

void Bridge::CancelFaceRectanglePreview()
{
    EndRectanglePreviewNative(true);
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

bool Bridge::StageAreaHighlightMesh(int areaId, long long modelRevision, long long workingId,
    array<int>^ faceIds, array<double>^ rects, array<double>^ frame, array<double>^ color)
{
    if (faceIds == nullptr || rects == nullptr || frame == nullptr || color == nullptr) return false;
    if (faceIds->Length == 0 || rects->Length == 0 || frame->Length != 9 || color->Length < 3) return false;
    try
    {
        std::vector<int> ids(static_cast<size_t>(faceIds->Length));
        for (int i = 0; i < faceIds->Length; ++i) ids[(size_t)i] = faceIds[i];
        std::vector<double> nativeRects(static_cast<size_t>(rects->Length));
        for (int i = 0; i < rects->Length; ++i) nativeRects[(size_t)i] = rects[i];
        std::vector<double> nativeFrame(static_cast<size_t>(frame->Length));
        for (int i = 0; i < frame->Length; ++i) nativeFrame[(size_t)i] = frame[i];
        double nativeColor[3] = {color[0], color[1], color[2]};
        return StageAreaHighlightMeshNative(areaId, modelRevision, workingId, ids, nativeRects,
            nativeFrame, nativeColor);
    }
    catch (...) { return false; }
}

bool Bridge::CommitAreaHighlight(int areaId)
{
    try { return CommitAreaHighlightNative(areaId); }
    catch (...) { return false; }
}

void Bridge::DiscardAreaHighlight(int areaId)
{
    try { DiscardAreaHighlightNative(areaId); }
    catch (...) { }
}

void Bridge::ClearAreaHighlight(int areaId)
{
    try { ClearAreaHighlightNative(areaId); }
    catch (...) { }
}

void Bridge::ClearAllAreaHighlights()
{
    try { ClearAllAreaHighlightsNative(); }
    catch (...) { }
}

int Bridge::GetAreaHighlightTriangleCount(int areaId)
{
    try { return GetAreaHighlightTriangleCountNative(areaId); }
    catch (...) { return -1; }
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

void Bridge::SetExitFaceHighlight(int faceId, double r, double g, double b)
{
    SetExitFaceHighlightNative(faceId, Quantity_Color(r, g, b, Quantity_TOC_RGB));
}

void Bridge::ClearExitFaceHighlight()
{
    ClearExitFaceHighlightNative();
}

void Bridge::SetExitEdgeHighlight(array<double>^ points, double r, double g, double b)
{
    std::vector<double> native;
    if (points != nullptr && points->Length >= 6 && points->Length % 3 == 0)
    {
        native.resize(static_cast<size_t>(points->Length));
        for (int i = 0; i < points->Length; ++i) native[static_cast<size_t>(i)] = points[i];
    }
    try { SetExitEdgeHighlightNative(native, Quantity_Color(r, g, b, Quantity_TOC_RGB)); }
    catch (const Standard_Failure&) { }
}

void Bridge::ClearExitEdgeHighlight()
{
    try { ClearExitEdgeHighlightNative(); }
    catch (const Standard_Failure&) { }
}

void Bridge::ClearFaceSelection()
{
    ClearFaceSelectionNative();
}

array<int>^ Bridge::GetSelectedFaceIds()
{
    const std::set<int> ids = CollectSelectedFaceIdsNative();
    array<int>^ result = gcnew array<int>((int)ids.size());
    int index = 0;
    for (int id : ids) result[index++] = id;
    return result;
}

long long Bridge::GetFaceIdBuildCount()
{
    return g_faceIdBuildCount;
}

array<double>^ Bridge::GetFaceSelectionRefineStats()
{
    array<double>^ stats = gcnew array<double>(10);
    stats[0] = static_cast<double>(g_faceRefineStats.Status);
    stats[1] = static_cast<double>(g_faceRefineStats.CandidateFaces);
    stats[2] = static_cast<double>(g_faceRefineStats.CandidateTiles);
    stats[3] = static_cast<double>(g_faceRefineStats.RenderPasses);
    stats[4] = static_cast<double>(g_faceRefineStats.OutputPixels);
    stats[5] = static_cast<double>(g_faceRefineStats.ProjectedTriangles);
    stats[6] = static_cast<double>(g_faceRefineStats.CandidatePixels);
    stats[7] = g_faceRefineStats.ProjectMs;
    stats[8] = g_faceRefineStats.RenderMs;
    stats[9] = g_faceRefineStats.TotalMs;
    return stats;
}

array<int>^ Bridge::DebugProbeFaceIdRegion(int x0, int y0, int x1, int y1)
{
    std::vector<int> ids = ProbeFaceIdRegionNative(x0, y0, x1, y1);
    array<int>^ result = gcnew array<int>(static_cast<int>(ids.size()));
    for (int i = 0; i < static_cast<int>(ids.size()); ++i) result[i] = ids[i];
    return result;
}

array<int>^ Bridge::DebugGetLastRectangleBaseFaceIds()
{
    array<int>^ result = gcnew array<int>(static_cast<int>(g_faceRefineLastBaseIds.size()));
    for (int i = 0; i < static_cast<int>(g_faceRefineLastBaseIds.size()); ++i)
        result[i] = g_faceRefineLastBaseIds[i];
    return result;
}

void Bridge::DebugSetFaceRefineDumpPath(System::String^ path)
{
    if (System::String::IsNullOrEmpty(path))
    {
        g_faceRefineDumpPath.clear();
        return;
    }
    System::IntPtr ptr = System::Runtime::InteropServices::Marshal::StringToHGlobalAnsi(path);
    try
    {
        g_faceRefineDumpPath = static_cast<const char*>(ptr.ToPointer());
    }
    finally
    {
        System::Runtime::InteropServices::Marshal::FreeHGlobal(ptr);
    }
}

bool Bridge::SetCameraState(array<double>^ eye, array<double>^ center, array<double>^ up, double scale)
{
    if (g_view.IsNull()) return false;
    if (eye == nullptr || center == nullptr || up == nullptr) return false;
    if (eye->Length != 3 || center->Length != 3 || up->Length != 3) return false;
    if (!(scale > 0.0)) return false;

    Standard_Integer width = 0, height = 0;
    if (!g_view->Window().IsNull()) g_view->Window()->Size(width, height);

    try
    {
        Handle(Graphic3d_Camera) camera = new Graphic3d_Camera();
        camera->SetEyeAndCenter(gp_Pnt(eye[0], eye[1], eye[2]),
                                gp_Pnt(center[0], center[1], center[2]));
        camera->SetUp(gp_Dir(up[0], up[1], up[2]));
        camera->SetProjectionType(Graphic3d_Camera::Projection_Orthographic);
        camera->SetScale(scale);
        if (width > 0 && height > 0)
            camera->SetAspect(static_cast<Standard_Real>(width) / static_cast<Standard_Real>(height));
        g_view->SetCamera(camera);
        if (!g_view.IsNull()) g_view->Redraw();
        return true;
    }
    catch (const Standard_Failure&)
    {
        return false;
    }
}

array<int>^ Bridge::SelectFaceIds(array<int>^ faceIds)
{
    if (faceIds == nullptr) return nullptr;
    std::vector<int> requested;
    requested.reserve(faceIds->Length);
    for each (int id in faceIds)
        if (id >= 0) requested.push_back(id);

    std::vector<int> applied;
    if (!SelectFaceIdsNative(requested, applied)) return nullptr;

    array<int>^ result = gcnew array<int>((int)applied.size());
    for (size_t i = 0; i < applied.size(); ++i) result[(int)i] = applied[i];
    return result;
}

System::String^ Bridge::GetDisplayDiagnostic()
{
    int routes = 0, displayed = 0, connectors = 0, enabled = 0;
    for (const auto& piece : g_routePieces)
    {
        ++routes;
        if (!g_ctx.IsNull() && g_ctx->IsDisplayed(piece.second.ais)) ++displayed;
        if (piece.second.connector) ++connectors;
        if (piece.second.enabled) ++enabled;
    }
    const int selected = (int)CollectSelectedFaceIdsNative().size();
    const int markers = (int)g_routeEndpointMarkers.size();
    const int worldOverlays = (int)g_worldOverlays.size();
    return System::String::Format(
        System::Globalization::CultureInfo::InvariantCulture,
        "routes={0} displayed={1} connectors={2} enabled={3} markers={4} selected={5} worldOverlays={6}",
        routes, displayed, connectors, enabled, markers, selected, worldOverlays);
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
