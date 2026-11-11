// Mold_Item.step 절단 도구 (검사 모드 우선 구현)
//
// 목적:
//   제품(Item)은 그대로 두고, 제품의 실제 외곽에서 바깥쪽으로 margin 만큼
//   확장한 경계를 따라 덮개(Mold)만 절단한다.
//
// 현재 구현 범위:
//   inspect      - 원본을 절단하지 않고 구조/형상/tolerance/프레임 후보만 보고한다.
//   trim         - 실제 절단. 기준 평면과 margin 은 호출자가 확정해 넘긴다.
//   offset-probe - 합성 사각형으로 offset API 자체를 검증한다.
//
// 원칙:
//   - 원본 좌표를 유지한다(자동 주축 회전 금지).
//   - 입력 STEP을 덮어쓰지 않는다.
//   - tolerance를 임의로 늘리거나 최대 조각만 선택하는 fallback을 넣지 않는다.
//   - 모서리는 원호가 아니라 인접 두 직선의 연장 교점(직각)으로 만든다.
//     offset API가 만든 모서리 원호는 중간 산출물이며 최종 경계에 남기지 않는다.

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <map>
#include <set>
#include <functional>
#include <string>
#include <vector>

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepAlgoAPI_Splitter.hxx>
#include <BOPAlgo_Builder.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepClass_FaceClassifier.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <Poly_Triangulation.hxx>
#include <BRepGProp.hxx>
#include <BRepOffsetAPI_MakeOffset.hxx>
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepProj_Projection.hxx>
#include <BRep_Tool.hxx>
#include <BRepTools_WireExplorer.hxx>
#include <Bnd_Box.hxx>
#include <GProp_GProps.hxx>
#include <GProp_PrincipalProps.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_JoinType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <Geom_Circle.hxx>
#include <Geom_Line.hxx>
#include <Standard_Type.hxx>
#include <GeomAPI_ProjectPointOnSurf.hxx>
#include <GeomLProp_SLProps.hxx>
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
#include <HLRBRep_TypeOfResultingEdge.hxx>
#include <IntCurvesFace_ShapeIntersector.hxx>
#include <ShapeUpgrade_UnifySameDomain.hxx>
#include <ShapeFix_Wire.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopLoc_Location.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_ListOfShape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Shell.hxx>
#include <TopoDS_Vertex.hxx>
#include <TopoDS_Wire.hxx>
#include <BRep_Builder.hxx>
#include <gp_Ax2.hxx>
#include <gp_Ax3.hxx>
#include <gp_Lin.hxx>
#include <gp_Pln.hxx>
#include <gp_Pnt.hxx>
#include <gp_Trsf.hxx>
#include <gp_Vec.hxx>

#include <IFSelect_ReturnStatus.hxx>
#include <Standard_Failure.hxx>
#include <Interface_Static.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <STEPControl_StepModelType.hxx>
#include <TCollection_AsciiString.hxx>
#include <TCollection_ExtendedString.hxx>
#include <TDataStd_Name.hxx>
#include <TDF_Label.hxx>
#include <TDF_LabelSequence.hxx>
#include <TDocStd_Document.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <XCAFApp_Application.hxx>
#include <Quantity_Color.hxx>

namespace
{
    struct Options
    {
        std::string mode = "inspect";
        std::string input;
        std::string output;
        double margin = 100.0;
        bool haveOrigin = false, haveNormal = false, haveXAxis = false;
        gp_Pnt origin;
        gp_Dir normal{ 0.0, 0.0, 1.0 };
        gp_Dir xaxis{ 1.0, 0.0, 0.0 };
        int debugTools = 0;
        bool debugOutline = false;
    };

    void Fail(const std::string& message)
    {
        std::fprintf(stderr, "ERROR: %s\n", message.c_str());
        std::exit(2);
    }

    bool ParseTriple(const std::string& text, double out[3])
    {
        return std::sscanf(text.c_str(), "%lf,%lf,%lf", &out[0], &out[1], &out[2]) == 3;
    }

    Options ParseArgs(int argc, char** argv)
    {
        Options opt;
        for (int i = 1; i < argc; ++i)
        {
            const std::string key = argv[i];
            auto next = [&](const char* name) -> std::string {
                if (i + 1 >= argc) Fail(std::string("missing value for ") + name);
                return argv[++i];
            };
            if (key == "--mode") opt.mode = next("--mode");
            else if (key == "--input") opt.input = next("--input");
            else if (key == "--output") opt.output = next("--output");
            else if (key == "--margin") opt.margin = std::atof(next("--margin").c_str());
            else if (key == "--origin")
            {
                double v[3];
                if (!ParseTriple(next("--origin"), v)) Fail("--origin expects x,y,z");
                opt.origin.SetCoord(v[0], v[1], v[2]);
                opt.haveOrigin = true;
            }
            else if (key == "--normal")
            {
                double v[3];
                if (!ParseTriple(next("--normal"), v)) Fail("--normal expects x,y,z");
                const gp_Vec vec(v[0], v[1], v[2]);
                if (vec.SquareMagnitude() < 1e-18) Fail("--normal is zero");
                opt.normal = gp_Dir(vec);
                opt.haveNormal = true;
            }
            else if (key == "--xaxis")
            {
                double v[3];
                if (!ParseTriple(next("--xaxis"), v)) Fail("--xaxis expects x,y,z");
                const gp_Vec vec(v[0], v[1], v[2]);
                if (vec.SquareMagnitude() < 1e-18) Fail("--xaxis is zero");
                opt.xaxis = gp_Dir(vec);
                opt.haveXAxis = true;
            }
            else if (key == "--debug-tools") opt.debugTools = std::atoi(next("--debug-tools").c_str());
            else if (key == "--debug-outline") opt.debugOutline = true;
            else Fail("unknown argument: " + key);
        }
        if (opt.input.empty()) Fail("--input is required");
        return opt;
    }

    const char* ShapeTypeName(TopAbs_ShapeEnum type)
    {
        switch (type)
        {
        case TopAbs_COMPOUND: return "COMPOUND";
        case TopAbs_COMPSOLID: return "COMPSOLID";
        case TopAbs_SOLID: return "SOLID";
        case TopAbs_SHELL: return "SHELL";
        case TopAbs_FACE: return "FACE";
        case TopAbs_WIRE: return "WIRE";
        case TopAbs_EDGE: return "EDGE";
        case TopAbs_VERTEX: return "VERTEX";
        default: return "SHAPE";
        }
    }

    const char* SurfaceTypeName(GeomAbs_SurfaceType type)
    {
        switch (type)
        {
        case GeomAbs_Plane: return "Plane";
        case GeomAbs_Cylinder: return "Cylinder";
        case GeomAbs_Cone: return "Cone";
        case GeomAbs_Sphere: return "Sphere";
        case GeomAbs_Torus: return "Torus";
        case GeomAbs_BezierSurface: return "Bezier";
        case GeomAbs_BSplineSurface: return "BSpline";
        case GeomAbs_SurfaceOfRevolution: return "Revolution";
        case GeomAbs_SurfaceOfExtrusion: return "Extrusion";
        case GeomAbs_OffsetSurface: return "Offset";
        default: return "Other";
        }
    }

    const char* CurveTypeName(GeomAbs_CurveType type)
    {
        switch (type)
        {
        case GeomAbs_Line: return "Line";
        case GeomAbs_Circle: return "Circle";
        case GeomAbs_Ellipse: return "Ellipse";
        case GeomAbs_Hyperbola: return "Hyperbola";
        case GeomAbs_Parabola: return "Parabola";
        case GeomAbs_BezierCurve: return "Bezier";
        case GeomAbs_BSplineCurve: return "BSpline";
        case GeomAbs_OffsetCurve: return "Offset";
        default: return "Other";
        }
    }

    std::string ShapeTypeCounts(const TopoDS_Shape& shape)
    {
        std::string text;
        const TopAbs_ShapeEnum types[] = { TopAbs_SOLID, TopAbs_SHELL, TopAbs_FACE, TopAbs_WIRE, TopAbs_EDGE, TopAbs_VERTEX };
        for (TopAbs_ShapeEnum type : types)
        {
            int count = 0;
            for (TopExp_Explorer ex(shape, type); ex.More(); ex.Next()) ++count;
            if (count == 0) continue;
            if (!text.empty()) text += " ";
            text += ShapeTypeName(type);
            text += "=" + std::to_string(count);
        }
        return text;
    }

    std::string LabelName(const TDF_Label& label)
    {
        Handle(TDataStd_Name) name;
        if (!label.FindAttribute(TDataStd_Name::GetID(), name) || name.IsNull()) return std::string();
        const TCollection_AsciiString ascii(name->Get());
        return std::string(ascii.ToCString());
    }

    struct NamedShape
    {
        std::string name;
        std::string path;
        TopoDS_Shape shape;   // world (누적 위치 적용)
        bool isReference = false;
        TDF_Label label;      // 인스턴스(또는 단일 형상) 라벨
        TDF_Label definition; // 형상 정의가 들어 있는 라벨 (참조면 참조 대상)
    };

    void AccumulateLocation(const TopLoc_Location& loc, TopLoc_Location& acc)
    {
        acc = acc * loc;
    }

    void WalkLabel(const Handle(XCAFDoc_ShapeTool)& tool, const TDF_Label& label,
                   const TopLoc_Location& parentLoc, const std::string& parentPath,
                   std::vector<NamedShape>& out, int depth)
    {
        if (depth > 32) Fail("assembly nesting too deep (cycle?)");

        TopLoc_Location loc = parentLoc * XCAFDoc_ShapeTool::GetLocation(label);
        std::string name = LabelName(label);
        std::string path = parentPath.empty() ? name : parentPath + "/" + name;

        if (XCAFDoc_ShapeTool::IsReference(label))
        {
            TDF_Label referred;
            TopoDS_Shape shape = XCAFDoc_ShapeTool::GetShape(label);
            if (!shape.IsNull()) shape.Location(loc, false);
            NamedShape item;
            item.name = name;
            item.path = path;
            item.shape = shape;
            item.isReference = true;
            item.label = label;
            if (XCAFDoc_ShapeTool::GetReferredShape(label, referred)) item.definition = referred;
            else item.definition = label;
            out.push_back(item);
            return;  // 정의는 한 번만
        }

        if (XCAFDoc_ShapeTool::IsAssembly(label))
        {
            TDF_LabelSequence comps;
            XCAFDoc_ShapeTool::GetComponents(label, comps);
            for (Standard_Integer i = 1; i <= comps.Length(); ++i)
                WalkLabel(tool, comps.Value(i), loc, path, out, depth + 1);
            return;
        }

        TopoDS_Shape shape;
        if (!XCAFDoc_ShapeTool::GetShape(label, shape) || shape.IsNull()) return;
        shape.Location(loc, false);
        NamedShape item;
        item.name = name;
        item.path = path;
        item.shape = shape;
        item.label = label;
        item.definition = label;
        out.push_back(item);
    }

    void ReportTolerance(const TopoDS_Shape& shape)
    {
        double vmin = 0.0, vmax = 0.0, emin = 0.0, emax = 0.0, fmin = 0.0, fmax = 0.0;
        bool first = true;
        for (TopExp_Explorer ex(shape, TopAbs_VERTEX); ex.More(); ex.Next())
        {
            const double t = BRep_Tool::Tolerance(TopoDS::Vertex(ex.Current()));
            if (first) { vmin = vmax = t; emin = emax = t; fmin = fmax = t; first = false; }
            vmin = std::min(vmin, t); vmax = std::max(vmax, t);
        }
        for (TopExp_Explorer ex(shape, TopAbs_EDGE); ex.More(); ex.Next())
        {
            const double t = BRep_Tool::Tolerance(TopoDS::Edge(ex.Current()));
            emin = emin == 0.0 ? t : std::min(emin, t);
            emax = std::max(emax, t);
        }
        for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
        {
            const double t = BRep_Tool::Tolerance(TopoDS::Face(ex.Current()));
            fmin = fmin == 0.0 ? t : std::min(fmin, t);
            fmax = std::max(fmax, t);
        }
        std::printf("    tolerance vertex[%.6f..%.6f] edge[%.6f..%.6f] face[%.6f..%.6f] mm\n",
                    vmin, vmax, emin, emax, fmin, fmax);
    }

    /// 입력 형상이 스스로 선언한 최대 tolerance. 투영 붕괴 판정의 하한으로만 쓴다.
    double MaxTolerance(const TopoDS_Shape& shape)
    {
        double worst = 0.0;
        for (TopExp_Explorer ex(shape, TopAbs_VERTEX); ex.More(); ex.Next())
            worst = std::max(worst, BRep_Tool::Tolerance(TopoDS::Vertex(ex.Current())));
        for (TopExp_Explorer ex(shape, TopAbs_EDGE); ex.More(); ex.Next())
            worst = std::max(worst, BRep_Tool::Tolerance(TopoDS::Edge(ex.Current())));
        for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
            worst = std::max(worst, BRep_Tool::Tolerance(TopoDS::Face(ex.Current())));
        return worst;
    }

    void ReportBox(const TopoDS_Shape& shape, const char* label)
    {
        Bnd_Box box;
        BRepBndLib::Add(shape, box);
        if (box.IsVoid()) { std::printf("    %s bbox: <void>\n", label); return; }
        double xmin, ymin, zmin, xmax, ymax, zmax;
        box.Get(xmin, ymin, zmin, xmax, ymax, zmax);
        std::printf("    %s bbox: [%.3f,%.3f,%.3f] .. [%.3f,%.3f,%.3f]  size %.3f x %.3f x %.3f\n",
                    label, xmin, ymin, zmin, xmax, ymax, zmax,
                    xmax - xmin, ymax - ymin, zmax - zmin);
    }

    void ReportSurfaceTypes(const TopoDS_Shape& shape)
    {
        std::map<GeomAbs_SurfaceType, int> counts;
        for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
        {
            BRepAdaptor_Surface surf(TopoDS::Face(ex.Current()), Standard_False);
            counts[surf.GetType()] += 1;
        }
        std::printf("    surface types:");
        for (const auto& kv : counts) std::printf(" %s=%d", SurfaceTypeName(kv.first), kv.second);
        std::printf("\n");
    }

    void ReportCurveTypes(const TopoDS_Shape& shape)
    {
        std::map<GeomAbs_CurveType, int> counts;
        for (TopExp_Explorer ex(shape, TopAbs_EDGE); ex.More(); ex.Next())
        {
            BRepAdaptor_Curve curve(TopoDS::Edge(ex.Current()));
            counts[curve.GetType()] += 1;
        }
        std::printf("    curve types:");
        for (const auto& kv : counts) std::printf(" %s=%d", CurveTypeName(kv.first), kv.second);
        std::printf("\n");
    }

    // 열린 경계(한 면에만 붙은 edge)를 모으고, 그 rim 의 평균 평면을 보고한다.
    void ReportFreeBoundary(const TopoDS_Shape& shape)
    {        TopTools_IndexedDataMapOfShapeListOfShape edgeFace;
        TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, edgeFace);

        int freeEdges = 0;
        TopTools_ListOfShape rimEdges;
        for (Standard_Integer i = 1; i <= edgeFace.Extent(); ++i)
        {
            if (edgeFace.FindFromIndex(i).Extent() == 1)
            {
                ++freeEdges;
                rimEdges.Append(edgeFace.FindKey(i));
            }
        }
        std::printf("    free boundary edges (1 adjacent face): %d\n", freeEdges);
        if (freeEdges == 0) return;

        // rim 점들을 모아 최소자승 평면을 구한다(정확한 평면 확정이 아니라 후보 보고용).
        std::vector<gp_Pnt> pts;
        for (TopTools_ListIteratorOfListOfShape it(rimEdges); it.More(); it.Next())
        {
            for (TopExp_Explorer vx(it.Value(), TopAbs_VERTEX); vx.More(); vx.Next())
                pts.push_back(BRep_Tool::Pnt(TopoDS::Vertex(vx.Current())));
        }
        if (pts.size() < 3) return;
        gp_Pnt mean(0, 0, 0);
        for (const gp_Pnt& p : pts) mean.ChangeCoord() += p.XYZ();
        mean.ChangeCoord().Divide(static_cast<double>(pts.size()));
        double cov[3][3] = { {0,0,0},{0,0,0},{0,0,0} };
        for (const gp_Pnt& p : pts)
        {
            const double d[3] = { p.X() - mean.X(), p.Y() - mean.Y(), p.Z() - mean.Z() };
            for (int r = 0; r < 3; ++r)
                for (int c = 0; c < 3; ++c) cov[r][c] += d[r] * d[c];
        }
        // 거듭제곱 반복으로 최소 고유벡터(법선) 추정.
        gp_Vec n(1, 1, 1);
        double shift = 0.0;
        for (int r = 0; r < 3; ++r) shift = std::max(shift, cov[r][r]);
        for (int iter = 0; iter < 200; ++iter)
        {
            gp_Vec next;
            double coords[3] = { n.X(), n.Y(), n.Z() };
            double outv[3] = { 0,0,0 };
            for (int r = 0; r < 3; ++r)
            {
                double sum = 0.0;
                for (int c = 0; c < 3; ++c) sum += cov[r][c] * coords[c];
                outv[r] = shift * coords[r] - sum;   // (shift*I - C) 역반복
            }
            next.SetCoord(outv[0], outv[1], outv[2]);
            if (next.SquareMagnitude() < 1e-30) break;
            next.Normalize();
            n = next;
        }
        n.Normalize();
        std::printf("    rim least-squares plane: point(%.3f,%.3f,%.3f) normal(%.6f,%.6f,%.6f) samples=%zu\n",
                    mean.X(), mean.Y(), mean.Z(), n.X(), n.Y(), n.Z(), pts.size());

        double dmin = 1e30, dmax = -1e30;
        for (const gp_Pnt& p : pts)
        {
            const double d = gp_Vec(mean, p).Dot(n);
            dmin = std::min(dmin, d);
            dmax = std::max(dmax, d);
        }
        std::printf("    rim deviation from that plane: %.3f .. %.3f mm\n", dmin, dmax);
    }

    // 열린 경계를 연결된 loop 단위로 묶어 각 loop 의 범위와 닫힘 여부를 보고한다.
    void ReportFreeBoundaryLoops(const TopoDS_Shape& shape)
    {
        TopTools_IndexedDataMapOfShapeListOfShape edgeFace;
        TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, edgeFace);

        std::vector<TopoDS_Edge> freeEdges;
        for (Standard_Integer i = 1; i <= edgeFace.Extent(); ++i)
            if (edgeFace.FindFromIndex(i).Extent() == 1)
                freeEdges.push_back(TopoDS::Edge(edgeFace.FindKey(i)));
        if (freeEdges.empty()) return;

        // vertex -> free edge index (degenerate edge 는 vertex 가 하나뿐이므로 자기 자신만 연결)
        std::map<TopoDS_Shape, std::vector<int>, TopTools_ShapeMapHasher> byVertex;
        for (int i = 0; i < static_cast<int>(freeEdges.size()); ++i)
        {
            for (int k = 0; k < 2; ++k)
            {
                const TopoDS_Vertex v = k == 0 ? TopExp::FirstVertex(freeEdges[i], Standard_True)
                                              : TopExp::LastVertex(freeEdges[i], Standard_True);
                if (!v.IsNull()) byVertex[v].push_back(i);
            }
        }

        std::vector<int> group(freeEdges.size(), -1);
        int groupCount = 0;
        for (int seed = 0; seed < static_cast<int>(freeEdges.size()); ++seed)
        {
            if (group[seed] >= 0) continue;
            std::vector<int> stack{ seed };
            group[seed] = groupCount;
            while (!stack.empty())
            {
                const int cur = stack.back();
                stack.pop_back();
                for (int k = 0; k < 2; ++k)
                {
                    const TopoDS_Vertex v = k == 0 ? TopExp::FirstVertex(freeEdges[cur], Standard_True)
                                                  : TopExp::LastVertex(freeEdges[cur], Standard_True);
                    if (v.IsNull()) continue;
                    for (int other : byVertex[v])
                    {
                        if (group[other] >= 0) continue;
                        group[other] = groupCount;
                        stack.push_back(other);
                    }
                }
            }
            ++groupCount;
        }

        for (int g = 0; g < groupCount; ++g)
        {
            Bnd_Box box;
            int edgeCount = 0;
            std::map<TopoDS_Shape, int, TopTools_ShapeMapHasher> vertexUse;
            for (int i = 0; i < static_cast<int>(freeEdges.size()); ++i)
            {
                if (group[i] != g) continue;
                ++edgeCount;
                BRepBndLib::Add(freeEdges[i], box);
                for (TopExp_Explorer vx(freeEdges[i], TopAbs_VERTEX); vx.More(); vx.Next())
                    vertexUse[vx.Current()] += 1;
            }
            int openEnds = 0;
            for (const auto& kv : vertexUse) if (kv.second == 1) ++openEnds;
            double xmin, ymin, zmin, xmax, ymax, zmax;
            box.Get(xmin, ymin, zmin, xmax, ymax, zmax);
            std::printf("      loop %d: edges=%d openEnds=%d bbox [%.1f,%.1f,%.1f]..[%.1f,%.1f,%.1f]\n",
                        g, edgeCount, openEnds, xmin, ymin, zmin, xmax, ymax, zmax);
        }
    }

    void ReportPrincipalAxes(const TopoDS_Shape& shape, const char* label)
    {
        GProp_GProps props;
        // 솔리드가 있으면 체적 관성, 없으면 표면 관성
        bool hasSolid = false;
        for (TopExp_Explorer ex(shape, TopAbs_SOLID); ex.More(); ex.Next()) { hasSolid = true; break; }
        if (hasSolid) BRepGProp::VolumeProperties(shape, props, Standard_True, Standard_True, Standard_False);
        if (!hasSolid || std::fabs(props.Mass()) <= 1e-12)
        {
            props = GProp_GProps();
            BRepGProp::SurfaceProperties(shape, props, Standard_True, Standard_False);
        }
        if (std::fabs(props.Mass()) <= 1e-12) return;
        const GProp_PrincipalProps principal = props.PrincipalProperties();
        const gp_Pnt c = props.CentreOfMass();
        const gp_Vec axes[3] = { principal.FirstAxisOfInertia(), principal.SecondAxisOfInertia(),
                                 principal.ThirdAxisOfInertia() };
        double moments[3] = { 0.0, 0.0, 0.0 };
        principal.Moments(moments[0], moments[1], moments[2]);
        std::printf("    %s principal axes (com %.1f,%.1f,%.1f):\n", label, c.X(), c.Y(), c.Z());
        for (int i = 0; i < 3; ++i)
        {
            std::printf("      axis%d dir(%.6f,%.6f,%.6f) moment %.6e\n",
                        i, axes[i].X(), axes[i].Y(), axes[i].Z(), moments[i]);
        }
    }

    struct AxisPair { int a; int b; const char* name; };

    void ReportProjectionSurplus(const TopoDS_Shape& item, const TopoDS_Shape& mold)
    {
        Bnd_Box ib, mb;
        BRepBndLib::Add(item, ib);
        BRepBndLib::Add(mold, mb);
        if (ib.IsVoid() || mb.IsVoid()) return;
        double iLo[3], iHi[3], mLo[3], mHi[3];
        ib.Get(iLo[0], iLo[1], iLo[2], iHi[0], iHi[1], iHi[2]);
        mb.Get(mLo[0], mLo[1], mLo[2], mHi[0], mHi[1], mHi[2]);

        const AxisPair pairs[3] = { { 0, 1, "XY(top)" }, { 1, 2, "YZ" }, { 2, 0, "ZX" } };
        for (const AxisPair& pair : pairs)
        {
            std::printf("    projection %s: item[%.1f..%.1f]x[%.1f..%.1f] mold[%.1f..%.1f]x[%.1f..%.1f]\n",
                        pair.name,
                        iLo[pair.a], iHi[pair.a], iLo[pair.b], iHi[pair.b],
                        mLo[pair.a], mHi[pair.a], mLo[pair.b], mHi[pair.b]);
            std::printf("      mold surplus: -A %.2f  +A %.2f  -B %.2f  +B %.2f  (mm)\n",
                        iLo[pair.a] - mLo[pair.a], mHi[pair.a] - iHi[pair.a],
                        iLo[pair.b] - mLo[pair.b], mHi[pair.b] - iHi[pair.b]);
        }
    }

    // ------------------------------------------------------------- trim mode ---

    // 확정 기준 평면의 국소 좌표계. 국소 z=0 평면이 절단 기준 평면이다.
    struct Frame
    {
        gp_Pnt origin;
        gp_Dir normal, xdir, ydir;
        gp_Trsf toWorld, toLocal;

        Frame(const gp_Pnt& o, const gp_Dir& n, const gp_Dir& xhint)
            : origin(o), normal(n)
        {
            gp_Vec x(xhint);
            x -= gp_Vec(n) * x.Dot(gp_Vec(n));
            if (x.SquareMagnitude() < 1e-12) Fail("frame X axis is parallel to the plane normal");
            x.Normalize();
            xdir = gp_Dir(x);
            ydir = gp_Dir(gp_Vec(n).Crossed(x));
            toWorld.SetValues(xdir.X(), ydir.X(), normal.X(), origin.X(),
                              xdir.Y(), ydir.Y(), normal.Y(), origin.Y(),
                              xdir.Z(), ydir.Z(), normal.Z(), origin.Z());
            toLocal = toWorld.Inverted();
        }
        gp_Pnt World(const gp_Pnt& local) const { return local.Transformed(toWorld); }
        gp_Pnt Local(const gp_Pnt& world) const { return world.Transformed(toLocal); }
        double Height(const gp_Pnt& world) const { return gp_Vec(origin, world).Dot(gp_Vec(normal)); }
    };

    struct ProjectedCurves
    {
        TopTools_ListOfShape edges;
        int sharp = 0, smooth = 0, sewn = 0, outline = 0;
        double u0 = 1e300, u1 = -1e300, v0 = 1e300, v1 = -1e300;

        void Add(const TopoDS_Shape& compound, const Frame& frame)
        {
            if (compound.IsNull()) return;
            for (TopExp_Explorer ex(compound, TopAbs_EDGE); ex.More(); ex.Next())
            {
                const TopoDS_Edge& edge = TopoDS::Edge(ex.Current());
                if (BRep_Tool::Degenerated(edge)) continue;
                Standard_Real first = 0.0, last = 0.0;
                if (BRep_Tool::Curve(edge, first, last).IsNull()) continue;
                edges.Append(edge);
                for (int k = 0; k < 2; ++k)
                {
                    const TopoDS_Vertex v = k == 0 ? TopExp::FirstVertex(edge, Standard_True)
                                                   : TopExp::LastVertex(edge, Standard_True);
                    if (v.IsNull()) continue;
                    const gp_Pnt p = frame.Local(BRep_Tool::Pnt(v));
                    u0 = std::min(u0, p.X()); u1 = std::max(u1, p.X());
                    v0 = std::min(v0, p.Y()); v1 = std::max(v1, p.Y());
                }
            }
        }

        /// 평면에 투영된 곡선을 등록하고, 투영 평면 국소좌표 기준 범위를 갱신한다.
        void AddEdge(const TopoDS_Edge& edge, const Frame& frame)
        {
            edges.Append(edge);
            for (int k = 0; k < 2; ++k)
            {
                const TopoDS_Vertex v = k == 0 ? TopExp::FirstVertex(edge, Standard_True)
                                               : TopExp::LastVertex(edge, Standard_True);
                if (v.IsNull()) continue;
                const gp_Pnt p = frame.Local(BRep_Tool::Pnt(v));
                u0 = std::min(u0, p.X()); u1 = std::max(u1, p.X());
                v0 = std::min(v0, p.Y()); v1 = std::max(v1, p.Y());
            }
        }
    };

    /// 기준 평면 국소좌표에서 형상 정점의 u/v 범위. 투영 대상 평면의 크기를 정하는 데만 쓴다.
    void ShapeUVBounds(const TopoDS_Shape& shape, const Frame& frame,
                       double& u0, double& u1, double& v0, double& v1)
    {
        u0 = v0 = 1e300;
        u1 = v1 = -1e300;
        for (TopExp_Explorer ex(shape, TopAbs_VERTEX); ex.More(); ex.Next())
        {
            const gp_Pnt p = frame.Local(BRep_Tool::Pnt(TopoDS::Vertex(ex.Current())));
            u0 = std::min(u0, p.X()); u1 = std::max(u1, p.X());
            v0 = std::min(v0, p.Y()); v1 = std::max(v1, p.Y());
        }
        if (!(u1 > u0) || !(v1 > v0)) Fail("item has a degenerate extent on the working plane");
    }

    /// 제품 전체의 정투영 선을 종류별로 모은다(sharp/smooth/sewn/outline × 가시/은선).
    /// HLR의 3D 결과는 원본 3D 곡선이므로, 이후 기준 평면으로 직접 투영한다.
    void CollectHlrCurves(const TopoDS_Shape& shape, const Frame& frame,
                          TopTools_ListOfShape curves[4], int counts[4])
    {
        Handle(HLRBRep_Algo) algo = new HLRBRep_Algo();
        algo->Add(shape);
        algo->Projector(gp_Ax2(frame.origin, frame.normal, frame.xdir));
        algo->Update();
        algo->Hide();

        HLRBRep_HLRToShape hlr(algo);
        const HLRBRep_TypeOfResultingEdge types[4] = { HLRBRep_Sharp, HLRBRep_Rg1Line,
                                                       HLRBRep_RgNLine, HLRBRep_OutLine };
        for (int t = 0; t < 4; ++t)
        {
            counts[t] = 0;
            for (int vis = 0; vis < 2; ++vis)
            {
                const TopoDS_Shape compound = hlr.CompoundOfEdges(
                    types[t], vis == 1 ? Standard_True : Standard_False, Standard_True);
                if (compound.IsNull()) continue;
                for (TopExp_Explorer ex(compound, TopAbs_EDGE); ex.More(); ex.Next())
                {
                    const TopoDS_Edge& edge = TopoDS::Edge(ex.Current());
                    if (BRep_Tool::Degenerated(edge)) continue;
                    Standard_Real first = 0.0, last = 0.0;
                    if (BRep_Tool::Curve(edge, first, last).IsNull()) continue;
                    curves[t].Append(edge);
                    if (vis == 1) ++counts[t];
                }
            }
        }
    }

    /// 평면에 투영했을 때의 점군 퍼짐(법선 성분 제거). 0에 가까우면 경계 기여가 없다.
    double ProjectedSpread(const TopoDS_Edge& edge, const Frame& frame)
    {
        Standard_Real first = 0.0, last = 0.0;
        const Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);
        if (curve.IsNull()) return 0.0;
        gp_Pnt p0;
        curve->D0(first, p0);
        p0 = frame.Local(p0);
        double spread = 0.0;
        for (int k = 1; k <= 16; ++k)
        {
            gp_Pnt p;
            curve->D0(first + (last - first) * k / 16.0, p);
            p = frame.Local(p);
            spread = std::max(spread, std::hypot(p.X() - p0.X(), p.Y() - p0.Y()));
        }
        return spread;
    }

    /// 제품의 실루엣 모서리: 인접 두 면의 시선 방향 facing이 반대인 모서리.
    /// 이 집합이 투영 영역의 경계를 이룬다(곡면 실루엣은 HLR outline이 보완한다).
    void CollectSilhouetteEdges(const TopoDS_Shape& shape, const Frame& frame,
                                TopTools_ListOfShape& out, int& freeBoundary)
    {
        freeBoundary = 0;
        TopTools_IndexedDataMapOfShapeListOfShape edgeFaces;
        TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, edgeFaces);
        const gp_Vec view(frame.normal);
        for (int i = 1; i <= edgeFaces.Extent(); ++i)
        {
            const TopoDS_Edge& edge = TopoDS::Edge(edgeFaces.FindKey(i));
            if (BRep_Tool::Degenerated(edge)) continue;
            const TopTools_ListOfShape& faces = edgeFaces.FindFromIndex(i);
            if (faces.Extent() != 2) { ++freeBoundary; continue; }

            BRepAdaptor_Curve adaptor(edge);
            const gp_Pnt probe = adaptor.Value(0.5 * (adaptor.FirstParameter() + adaptor.LastParameter()));

            double signs[2] = { 0.0, 0.0 };
            bool resolved = true;
            int k = 0;
            for (TopTools_ListIteratorOfListOfShape it(faces); it.More() && resolved; it.Next(), ++k)
            {
                const TopoDS_Face& face = TopoDS::Face(it.Value());
                const Handle(Geom_Surface) surface = BRep_Tool::Surface(face);
                if (surface.IsNull()) { resolved = false; break; }
                GeomAPI_ProjectPointOnSurf projection(probe, surface);
                if (projection.NbPoints() == 0) { resolved = false; break; }
                Standard_Real u = 0.0, v = 0.0;
                projection.LowerDistanceParameters(u, v);
                GeomLProp_SLProps props(surface, u, v, 1, 1e-9);
                if (!props.IsNormalDefined()) { resolved = false; break; }
                gp_Vec normal = props.Normal();
                if (face.Orientation() == TopAbs_REVERSED) normal.Reverse();
                signs[k] = normal.Dot(view);
            }
            if (!resolved)
                Fail("could not resolve the facing of the faces adjacent to an item edge");
            // facing이 반대이거나 접선(0)인 모서리가 투영 경계 후보다.
            if (signs[0] * signs[1] <= 0.0) out.Append(edge);
        }
    }

    /// 3D 곡선을 확정 평면 위로 정투영한다(샘플링 아님, CAD 곡선 유지).
    void ProjectOntoPlane(const TopTools_ListOfShape& curves3d, const TopoDS_Face& target,
                          const Frame& frame, double degenerateTol, ProjectedCurves& out)
    {
        int failed = 0, degenerate = 0;
        for (TopTools_ListIteratorOfListOfShape it(curves3d); it.More(); it.Next())
        {
            const TopoDS_Edge& source = TopoDS::Edge(it.Value());
            BRepBuilderAPI_MakeWire wireMaker(source);
            if (!wireMaker.IsDone()) { ++failed; continue; }
            BRepProj_Projection projection(wireMaker.Wire(), target, frame.normal);
            bool any = false;
            if (projection.IsDone())
            {
                for (TopExp_Explorer ex(projection.Shape(), TopAbs_EDGE); ex.More(); ex.Next())
                {
                    const TopoDS_Edge& projected = TopoDS::Edge(ex.Current());
                    Standard_Real first = 0.0, last = 0.0;
                    if (BRep_Tool::Curve(projected, first, last).IsNull()) continue;
                    out.AddEdge(projected, frame);
                    any = true;
                }
            }
            if (any) continue;
            // 투영 방향과 나란한 곡선은 평면에서 점으로 붕괴한다. 경계에 기여하지 않으므로
            // 조용히 버리지 않고 계산으로 확인한 뒤 건수만 보고한다. 판정 하한은 입력 형상이
            // 스스로 선언한 tolerance이며, 임의 확대값이 아니다.
            const double spread = ProjectedSpread(source, frame);
            if (spread <= degenerateTol) { ++degenerate; continue; }
            std::printf("    [error] unprojected curve: spread=%.9f mm\n", spread);
            ++failed;
        }
        if (degenerate > 0)
            std::printf("    projection-degenerate curves (point-like, no boundary contribution): %d\n",
                        degenerate);
        if (failed > 0) Fail("projection onto the working plane failed for " +
                             std::to_string(failed) + " curve(s)");
        if (out.edges.IsEmpty()) Fail("projection onto the working plane produced no curve");
    }

    TopoDS_Face BuildPlaneFace(const gp_Pln& plane, double u0, double u1, double v0, double v1, double pad)
    {
        if (!(u1 > u0) || !(v1 > v0)) Fail("degenerate projection range");
        return BRepBuilderAPI_MakeFace(plane, u0 - pad, u1 + pad, v0 - pad, v1 + pad).Face();
    }

    /// 평면 face를 투영 곡선들로 분할한 2D 배열(arrangement)을 만든다.
    /// Splitter는 솔리드 분할용이라 평면 face를 조각내지 않고 원본을 함께 돌려주므로,
    /// 동일 평면 위의 face + 모서리를 일반 퓨즈로 처리하는 BOPAlgo_Builder를 쓴다.
    TopoDS_Shape SplitPlaneFace(const TopoDS_Face& face, const TopTools_ListOfShape& tools)
    {
        BOPAlgo_Builder builder;
        builder.SetRunParallel(Standard_False);
        builder.SetNonDestructive(Standard_False);
        builder.AddArgument(face);
        for (TopTools_ListIteratorOfListOfShape it(tools); it.More(); it.Next())
            builder.AddArgument(it.Value());
        builder.Perform();
        if (builder.HasErrors()) Fail("planar arrangement failed");
        return builder.Shape();
    }

    /// 비볼록 셀에서도 확실히 내부인 점을 찾는다. 무게중심을 그대로 쓰지 않는다.
    bool FaceInteriorPoint(const TopoDS_Face& face, const Frame& frame, gp_Pnt& out)
    {
        double u0 = 1e300, u1 = -1e300, v0 = 1e300, v1 = -1e300;
        for (TopExp_Explorer ex(face, TopAbs_VERTEX); ex.More(); ex.Next())
        {
            const gp_Pnt p = frame.Local(BRep_Tool::Pnt(TopoDS::Vertex(ex.Current())));
            u0 = std::min(u0, p.X()); u1 = std::max(u1, p.X());
            v0 = std::min(v0, p.Y()); v1 = std::max(v1, p.Y());
        }

        std::vector<gp_Pnt> candidates;
        // 면적 무게중심은 볼록 셀에서 확실한 내부점이다. 비볼록 셀에서는 아래 격자가 보완한다.
        {
            GProp_GProps props;
            BRepGProp::SurfaceProperties(face, props);
            if (props.Mass() > 0.0) candidates.push_back(props.CentreOfMass());
        }
        candidates.push_back(gp_Pnt(0.5 * (u0 + u1), 0.5 * (v0 + v1), 0.0));
        for (int pass = 0; pass < 2; ++pass)
        {
            const int steps = pass == 0 ? 5 : 15;
            for (int iy = 1; iy <= steps; ++iy)
                for (int ix = 1; ix <= steps; ++ix)
                    candidates.push_back(gp_Pnt(u0 + (u1 - u0) * ix / (steps + 1.0),
                                                v0 + (v1 - v0) * iy / (steps + 1.0), 0.0));
        }
        for (const gp_Pnt& candidate : candidates)
        {
            const gp_Pnt world = frame.World(candidate);
            BRepClass_FaceClassifier classifier(face, world, 1e-9);
            if (classifier.State() == TopAbs_IN) { out = world; return true; }
        }

        // 격자로 못 찾는 얇은/구멍 뚫린 셀은 셀 자신의 삼각분할로 내부점을 찾는다.
        // 내부점 탐색에만 쓰고, 경계 형상 자체는 삼각분할로 대체하지 않는다.
        BRepMesh_IncrementalMesh mesher(face, 0.05, Standard_False, 0.5, Standard_True);
        TopLoc_Location location;
        const Handle(Poly_Triangulation) triangulation = BRep_Tool::Triangulation(face, location);
        if (triangulation.IsNull()) return false;
        const gp_Trsf placement = location.Transformation();
        struct TriangleCandidate { double area; gp_Pnt centre; };
        std::vector<TriangleCandidate> triangles;
        triangles.reserve(static_cast<size_t>(triangulation->NbTriangles()));
        for (Standard_Integer i = 1; i <= triangulation->NbTriangles(); ++i)
        {
            Standard_Integer n1 = 0, n2 = 0, n3 = 0;
            triangulation->Triangle(i).Get(n1, n2, n3);
            const gp_Pnt a = triangulation->Node(n1).Transformed(placement);
            const gp_Pnt b = triangulation->Node(n2).Transformed(placement);
            const gp_Pnt c = triangulation->Node(n3).Transformed(placement);
            const double area = 0.5 * gp_Vec(a, b).Crossed(gp_Vec(a, c)).Magnitude();
            if (!(area > 0.0)) continue;
            const gp_XYZ centre = (a.XYZ() + b.XYZ() + c.XYZ()) * (1.0 / 3.0);
            triangles.push_back({ area, gp_Pnt(centre) });
        }
        std::sort(triangles.begin(), triangles.end(),
                  [](const TriangleCandidate& l, const TriangleCandidate& r) { return l.area > r.area; });
        const size_t limit = std::min<size_t>(triangles.size(), 8);
        for (size_t i = 0; i < limit; ++i)
        {
            BRepClass_FaceClassifier classifier(face, triangles[i].centre, 1e-9);
            if (classifier.State() == TopAbs_IN) { out = triangles[i].centre; return true; }
        }
        return false;
    }

    bool WireOrEdgeArea(const TopoDS_Shape& shape, double& area)
    {
        if (shape.IsNull()) return false;
        GProp_GProps props;
        BRepGProp::SurfaceProperties(shape, props);
        area = props.Mass();
        return true;
    }

    /// 2D 3D 혼용 금지: 모든 점은 확정 평면 위에 있다.
    TopoDS_Face FaceFromWires(const gp_Pln& plane, const std::vector<TopoDS_Wire>& wires, double& area)
    {
        if (wires.empty()) Fail("offset produced no wire");
        size_t outer = 0;
        double best = -1.0;
        std::vector<double> areas(wires.size(), 0.0);
        for (size_t i = 0; i < wires.size(); ++i)
        {
            BRepBuilderAPI_MakeFace maker(plane, wires[i], Standard_True);
            if (!maker.IsDone()) continue;
            double a = 0.0;
            if (WireOrEdgeArea(maker.Face(), a)) areas[i] = std::fabs(a);
            if (areas[i] > best) { best = areas[i]; outer = i; }
        }
        if (best <= 0.0) Fail("offset wire could not build a planar face");

        BRepBuilderAPI_MakeFace maker(plane, wires[outer], Standard_True);
        if (!maker.IsDone()) Fail("offset outer wire is not a valid planar boundary");
        for (size_t i = 0; i < wires.size(); ++i)
        {
            if (i == outer || areas[i] <= 0.0) continue;
            maker.Add(wires[i]);
        }
        if (!maker.IsDone()) Fail("offset inner wires could not be added");
        area = best;
        return maker.Face();
    }

    // 국소 좌표계 2D 와이어 목록을 뽑는다.
    std::vector<TopoDS_Wire> ShapeWires(const TopoDS_Shape& shape)
    {
        std::vector<TopoDS_Wire> wires;
        if (shape.IsNull()) return wires;
        for (TopExp_Explorer ex(shape, TopAbs_WIRE); ex.More(); ex.Next())
            wires.push_back(TopoDS::Wire(ex.Current()));
        return wires;
    }

    bool FaceContainsPoint(const TopoDS_Face& face, const gp_Pnt& point, double tol)
    {
        BRepClass_FaceClassifier classifier(face, point, tol);
        const TopAbs_State state = classifier.State();
        return state == TopAbs_IN || state == TopAbs_ON;
    }

    /// 진단 전용: 외곽 와이어를 로컬 (u,v) 순서대로 출력한다.
    void DumpOutline(const TopoDS_Face& face, const Frame& frame)
    {
        const std::vector<TopoDS_Wire> wires = ShapeWires(face);
        for (size_t w = 0; w < wires.size(); ++w)
        {
            std::printf("  wire %zu (orientation=%d):\n", w, static_cast<int>(wires[w].Orientation()));
            int index = 0;
            for (TopExp_Explorer ex(wires[w], TopAbs_EDGE); ex.More(); ex.Next(), ++index)
            {
                const TopoDS_Edge& edge = TopoDS::Edge(ex.Current());
                double first = 0.0, last = 0.0;
                Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);
                GProp_GProps props;
                BRepGProp::LinearProperties(edge, props);
                if (curve.IsNull())
                {
                    std::printf("   %2d: null curve len=%.6f\n", index, props.Mass());
                    continue;
                }
                const gp_Pnt p0 = frame.Local(curve->Value(edge.Orientation() == TopAbs_REVERSED ? last : first));
                const gp_Pnt p1 = frame.Local(curve->Value(edge.Orientation() == TopAbs_REVERSED ? first : last));
                const BRepAdaptor_Curve adaptor(edge);
                std::printf("   %2d: %-8s len=%9.4f (%9.3f,%9.3f) -> (%9.3f,%9.3f)\n", index,
                            CurveTypeName(adaptor.GetType()), props.Mass(), p0.X(), p0.Y(), p1.X(), p1.Y());
            }
        }
    }

    /// 파생 도우미 면(원본 STEP 형상이 아님)의 외곽을 offset 가능한 형태로 정리한다.
    /// 형상 변화는 허용 오차 이내여야 하며, 초과하면 명시적으로 실패한다.
    TopoDS_Face CleanOutline(const gp_Pln& plane, const TopoDS_Face& face, double& outArea)
    {
        double originalArea = 0.0;
        WireOrEdgeArea(face, originalArea);
        originalArea = std::fabs(originalArea);

        TopoDS_Shape unified = face;
        try
        {
            ShapeUpgrade_UnifySameDomain unify(face, Standard_True, Standard_True, Standard_False);
            unify.Build();
            if (!unify.Shape().IsNull()) unified = unify.Shape();
        }
        catch (const Standard_Failure&)
        {
        }

        std::vector<TopoDS_Wire> wires = ShapeWires(unified);
        if (wires.empty()) wires = ShapeWires(face);
        if (wires.empty()) Fail("outline cleanup: footprint has no wire");

        int before = 0, after = 0;
        std::vector<TopoDS_Wire> cleaned;
        for (size_t i = 0; i < wires.size(); ++i)
        {
            TopoDS_Wire wire = wires[i];
            for (TopExp_Explorer ex(wire, TopAbs_EDGE); ex.More(); ex.Next()) ++before;
            try
            {
                ShapeFix_Wire fixer(wire, face, 1.0e-4);
                fixer.SetPrecision(1.0e-4);
                fixer.SetMaxTolerance(1.0e-2);
                fixer.SetMinTolerance(1.0e-6);
                fixer.FixReorder();
                fixer.FixSmall(Standard_False, 1.0e-3);
                fixer.FixConnected(1.0e-3);
                fixer.FixSelfIntersection();
                wire = fixer.Wire();
            }
            catch (const Standard_Failure&)
            {
            }
            for (TopExp_Explorer ex(wire, TopAbs_EDGE); ex.More(); ex.Next()) ++after;
            cleaned.push_back(wire);
        }

        double area = 0.0;
        const TopoDS_Face rebuilt = FaceFromWires(plane, cleaned, area);
        if (rebuilt.IsNull()) Fail("outline cleanup could not rebuild the planar face");
        const double change = std::fabs(std::fabs(area) - originalArea);
        std::printf("    outline cleanup: edges %d -> %d, area %.6f -> %.6f (change %.9f)\n",
                    before, after, originalArea, std::fabs(area), change);
        if (change > 1.0e-3 + 1.0e-6 * originalArea)
            Fail("outline cleanup moved the boundary beyond the allowed correction");
        outArea = std::fabs(area);
        return rebuilt;
    }

    /// 확장 경계가 원본 외곽에서 실제로 margin 만큼 떨어져 있는지 표본으로 측정한다.
    /// 근사(approx) 사용 시의 편차를 숨기지 않고 보고하기 위한 검증이다.
    void ReportOffsetDeviation(const TopoDS_Face& region, const TopoDS_Face& footprint, double margin)
    {
        double minDistance = 1e300, maxDistance = -1e300;
        double worstDeviation = 0.0;
        int samples = 0;
        for (TopExp_Explorer ex(region, TopAbs_EDGE); ex.More(); ex.Next())
        {
            const TopoDS_Edge& edge = TopoDS::Edge(ex.Current());
            double first = 0.0, last = 0.0;
            Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);
            if (curve.IsNull()) continue;
            const int steps = 8;
            for (int i = 0; i <= steps; ++i)
            {
                const double t = first + (last - first) * (static_cast<double>(i) / steps);
                const gp_Pnt point = curve->Value(t);
                BRepExtrema_DistShapeShape distance(BRepBuilderAPI_MakeVertex(point), footprint);
                distance.Perform();
                if (!distance.IsDone() || distance.NbSolution() < 1) continue;
                const double d = distance.Value();
                minDistance = std::min(minDistance, d);
                maxDistance = std::max(maxDistance, d);
                worstDeviation = std::max(worstDeviation, std::fabs(d - margin));
                ++samples;
            }
        }
        if (samples == 0) Fail("offset deviation could not be measured");
        std::printf("    offset deviation: %d samples, distance %.6f..%.6f mm (target %.3f), "
                    "worst |distance-margin| = %.6f mm\n",
                    samples, minDistance, maxDistance, margin, worstDeviation);
    }

    /// 확장 경계의 모서리 원호를 인접 두 직선의 연장 교점으로 바꾼다.
    /// 사용자 확정: 모서리는 원호가 아니라 직각(연장선 교점)이어야 한다.
    /// offset API의 원호는 중간 산출물이므로 최종 경계에서는 제거한다.
    /// 직선 두 개가 만나는 모서리만 바꾸고, 그 밖의 모서리는 사유와 함께 보고한다.
    TopoDS_Face SquareCorners(const Frame& frame, const gp_Pln& plane, const TopoDS_Face& roundRegion,
                              const TopoDS_Face& footprint, double margin, double roundArea,
                              double& outArea, double& outMinDistance)
    {
        struct TraversedEdge
        {
            TopoDS_Edge edge;
            Handle(Geom_Curve) curve;
            double first = 0.0, last = 0.0;
            bool reversed = false;
            gp_Pnt start, end;
        };

        const std::vector<TopoDS_Wire> roundWires = ShapeWires(roundRegion);
        if (roundWires.size() != 1)
            Fail("sharp corners: offset boundary has " + std::to_string(roundWires.size()) + " wires");

        std::vector<TraversedEdge> edges;
        for (BRepTools_WireExplorer ex(roundWires[0]); ex.More(); ex.Next())
        {
            TraversedEdge info;
            info.edge = ex.Current();
            info.reversed = info.edge.Orientation() == TopAbs_REVERSED;
            info.curve = BRep_Tool::Curve(info.edge, info.first, info.last);
            if (info.curve.IsNull()) Fail("sharp corners: boundary edge has no 3D curve");
            info.start = info.curve->Value(info.reversed ? info.last : info.first);
            info.end = info.curve->Value(info.reversed ? info.first : info.last);
            edges.push_back(info);
        }
        const int n = static_cast<int>(edges.size());
        if (n < 3) Fail("sharp corners: offset boundary has too few edges");

        std::vector<bool> drop(n, false), startReplaced(n, false), endReplaced(n, false);
        std::vector<gp_Pnt> newStart(n), newEnd(n);
        std::vector<std::string> skipped;
        int candidates = 0, squared = 0;
        double addedAreaBound = 0.0;

        for (int i = 0; i < n; ++i)
        {
            if (edges[i].curve->DynamicType() != STANDARD_TYPE(Geom_Circle)) continue;
            const Handle(Geom_Circle) circle = Handle(Geom_Circle)::DownCast(edges[i].curve);
            if (circle.IsNull()) continue;
            // 자유 경계 모서리 원호는 반지름이 margin 이다. 제품 자체의 둥근 모서리는
            // 확장 후 반지름이 margin 과 다르므로 이 판정에 걸리지 않는다.
            if (std::fabs(circle->Radius() - margin) > 1.0e-6 * std::max(1.0, margin)) continue;
            ++candidates;

            const int p = (i + n - 1) % n;
            const int q = (i + 1) % n;
            const gp_Pnt centerLocal = frame.Local(circle->Location());
            const auto neighbourType = [&](int index) {
                const Handle(Geom_Curve)& curve = edges[index].curve;
                if (curve->DynamicType() == STANDARD_TYPE(Geom_Line)) return "Line";
                if (curve->DynamicType() == STANDARD_TYPE(Geom_Circle)) return "Circle";
                const BRepAdaptor_Curve adaptor(edges[index].edge);
                return CurveTypeName(adaptor.GetType());
            };
            std::printf("    corner at (%.3f, %.3f) neighbours=%s/%s: ",
                        centerLocal.X(), centerLocal.Y(),
                        neighbourType(p), neighbourType(q));
            const auto skip = [&](const char* reason) {
                std::printf("skipped (%s)\n", reason);
                skipped.push_back(reason);
            };
            if (drop[p] || drop[q])
            {
                skip("adjacent corner arcs");
                continue;
            }
            if (edges[p].curve->DynamicType() != STANDARD_TYPE(Geom_Line) ||
                edges[q].curve->DynamicType() != STANDARD_TYPE(Geom_Line))
            {
                skip("neighbour is not a straight line");
                continue;
            }

            // 두 직선의 교점을 작업평면 2D에서 구한다.
            const gp_Lin lineP = Handle(Geom_Line)::DownCast(edges[p].curve)->Lin().Transformed(frame.toLocal);
            const gp_Lin lineQ = Handle(Geom_Line)::DownCast(edges[q].curve)->Lin().Transformed(frame.toLocal);
            const gp_Dir dirP = lineP.Direction();
            const gp_Dir dirQ = lineQ.Direction();
            const double det = dirP.X() * dirQ.Y() - dirP.Y() * dirQ.X();
            if (std::fabs(det) < 1.0e-9) { skip("parallel neighbours"); continue; }
            const gp_Pnt originP = lineP.Location();
            const gp_Pnt originQ = lineQ.Location();
            const double t = ((originQ.X() - originP.X()) * dirQ.Y() -
                              (originQ.Y() - originP.Y()) * dirQ.X()) / det;
            const gp_Pnt localCorner(originP.X() + t * dirP.X(), originP.Y() + t * dirP.Y(), 0.0);
            const gp_Pnt corner = frame.World(localCorner);

            const gp_Pnt center = circle->Location();
            const gp_Pnt arcMid = edges[i].curve->Value(0.5 * (edges[i].first + edges[i].last));
            const gp_Vec toCorner(center, corner);
            const gp_Vec toMid(center, arcMid);
            if (toCorner.Magnitude() < margin - 1.0e-6)
            {
                skip("corner point is not outside the arc");
                continue;
            }
            if (toCorner.Dot(toMid) <= 0.0 || toCorner.Normalized().Dot(toMid.Normalized()) < 0.9)
            {
                skip("corner point is not on the outward bisector");
                continue;
            }
            if (corner.Distance(arcMid) > margin)
            {
                skip("corner point is too far from the arc");
                continue;
            }
            if (FaceContainsPoint(roundRegion, corner, 1.0e-6))
            {
                skip("corner point falls inside the round region");
                continue;
            }

            addedAreaBound += 0.5 * gp_Vec(corner, edges[p].end)
                                        .Crossed(gp_Vec(corner, edges[q].start)).Magnitude();
            newEnd[p] = corner; endReplaced[p] = true;
            newStart[q] = corner; startReplaced[q] = true;
            drop[i] = true;
            ++squared;
            std::printf("squared (%.3f, %.3f)\n", localCorner.X(), localCorner.Y());
        }

        std::printf("    sharp corners: candidates=%d squared=%d", candidates, squared);
        for (const std::string& reason : skipped) std::printf(" [skipped: %s]", reason.c_str());
        std::printf("\n");
        if (squared == 0) Fail("sharp corners: no corner could be replaced by a straight-line intersection");

        BRepBuilderAPI_MakeWire maker;
        for (int i = 0; i < n; ++i)
        {
            if (drop[i]) continue;
            if (startReplaced[i] || endReplaced[i])
            {
                if (edges[i].curve->DynamicType() != STANDARD_TYPE(Geom_Line))
                    Fail("sharp corners: a replaced endpoint belongs to a non-linear boundary edge");
                const gp_Pnt s = startReplaced[i] ? newStart[i] : edges[i].start;
                const gp_Pnt e = endReplaced[i] ? newEnd[i] : edges[i].end;
                if (s.Distance(e) <= 1.0e-9)
                    Fail("sharp corners: squaring a corner produced a zero-length boundary edge");
                maker.Add(BRepBuilderAPI_MakeEdge(s, e).Edge());
            }
            else
            {
                maker.Add(edges[i].edge);
            }
        }
        if (!maker.IsDone()) Fail("sharp corners: rebuilt boundary wire is not connected");
        const TopoDS_Wire sharpWire = maker.Wire();
        if (!BRep_Tool::IsClosed(sharpWire)) Fail("sharp corners: rebuilt boundary wire is not closed");

        double area = 0.0;
        const TopoDS_Face sharp = FaceFromWires(plane, std::vector<TopoDS_Wire>{ sharpWire }, area);
        if (sharp.IsNull()) Fail("sharp corners: planar face build failed");
        area = std::fabs(area);

        // 직각 경계는 둥근 경계를 포함해야 하고, 늘어난 면적은 모서리 삼각형 합을 넘을 수 없다.
        if (area < roundArea - 1.0e-6 || area > roundArea + addedAreaBound + 1.0e-6)
            Fail("sharp corners: area " + std::to_string(area) + " is outside the expected range " +
                 std::to_string(roundArea) + ".." + std::to_string(roundArea + addedAreaBound));
        {
            double footprintArea = 0.0;
            WireOrEdgeArea(footprint, footprintArea);
            if (!(area > std::fabs(footprintArea))) Fail("sharp corners: region is not larger than the footprint");
        }

        // 둥근 경계가 직각 경계 안에 완전히 들어가는지 표본으로 확인한다.
        int outsideSamples = 0;
        for (BRepTools_WireExplorer ex(roundWires[0]); ex.More(); ex.Next())
        {
            const TopoDS_Edge& edge = ex.Current();
            double first = 0.0, last = 0.0;
            const Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);
            if (curve.IsNull()) continue;
            for (int k = 1; k <= 3; ++k)
            {
                const double param = first + (last - first) * (0.25 * k);
                if (FaceContainsPoint(sharp, curve->Value(param), 1.0e-6)) continue;
                ++outsideSamples;
            }
        }
        if (outsideSamples > 0)
            Fail("sharp corners: the round boundary is not contained in the squared boundary (" +
                 std::to_string(outsideSamples) + " samples outside)");

        BRepCheck_Analyzer analyzer(sharp, Standard_True);
        if (!analyzer.IsValid()) Fail("sharp corners: squared boundary face is not valid");

        // 직선 구간은 원본 외곽에서 정확히 margin 떨어져 있어야 한다. 모서리는 설계상 margin 보다 멀다.
        double minDistance = 1e300, maxDistance = -1e300;
        int samples = 0;
        for (TopExp_Explorer ex(sharp, TopAbs_EDGE); ex.More(); ex.Next())
        {
            const TopoDS_Edge& edge = TopoDS::Edge(ex.Current());
            double first = 0.0, last = 0.0;
            const Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);
            if (curve.IsNull()) continue;
            for (int k = 0; k <= 8; ++k)
            {
                const double param = first + (last - first) * (static_cast<double>(k) / 8.0);
                BRepExtrema_DistShapeShape distance(BRepBuilderAPI_MakeVertex(curve->Value(param)), footprint);
                distance.Perform();
                if (!distance.IsDone() || distance.NbSolution() < 1) continue;
                minDistance = std::min(minDistance, distance.Value());
                maxDistance = std::max(maxDistance, distance.Value());
                ++samples;
            }
        }
        if (samples == 0) Fail("sharp corners: boundary distance could not be measured");
        const double tolerance = 1.0e-3 + 2.0 * MaxTolerance(footprint);
        if (std::fabs(minDistance - margin) > tolerance)
            Fail("sharp corners: the closest boundary distance " + std::to_string(minDistance) +
                 " is not the margin " + std::to_string(margin));

        std::printf("    sharp boundary: area=%.3f mm^2 (round %.3f, added <= %.3f), "
                    "distance to footprint %.6f..%.6f mm\n",
                    area, roundArea, addedAreaBound, minDistance, maxDistance);
        outArea = area;
        outMinDistance = minDistance;
        return sharp;
    }

    /// 외곽을 margin 만큼 바깥으로 확장한다. 방향은 포함관계/면적으로 확인한다.
    TopoDS_Face OffsetOutward(const gp_Pln& plane, const TopoDS_Face& footprint, double margin, double& outArea)
    {
        const std::vector<TopoDS_Wire> footprintWires = ShapeWires(footprint);
        if (footprintWires.empty()) Fail("footprint has no wire");
        if (footprintWires.size() != 1)
            Fail("footprint outline has " + std::to_string(footprintWires.size()) +
                 " wires; a single outer boundary is required");

        double footprintArea = 0.0;
        WireOrEdgeArea(footprint, footprintArea);
        footprintArea = std::fabs(footprintArea);
        if (!(footprintArea > 0.0)) Fail("footprint has zero area");

        double perimeter = 0.0;
        {
            GProp_GProps props;
            BRepGProp::LinearProperties(footprint, props);
            perimeter = props.Mass();
        }
        const double upperBound = footprintArea + perimeter * margin + M_PI * margin * margin;
        const double distanceTolerance = 1.0e-3 + 2.0 * MaxTolerance(footprint);

        std::string failures;
        const GeomAbs_JoinType joins[2] = { GeomAbs_Arc, GeomAbs_Intersection };
        for (int input = 0; input < 2; ++input)
        {
            for (int approx = 0; approx < 2; ++approx)
            {
                for (int j = 0; j < 2; ++j)
                {
                    for (int sign = 0; sign < 2; ++sign)
                    {
                        const double value = sign == 0 ? margin : -margin;
                        const std::string label = std::string(input == 0 ? "face" : "wire") +
                                                  (approx == 0 ? "/exact" : "/approx") +
                                                  "/" + (j == 0 ? "arc" : "intersection") +
                                                  "(" + (sign == 0 ? "+" : "-") +
                                                  std::to_string(std::fabs(value)) + ")";
                        try
                        {
                            BRepOffsetAPI_MakeOffset offset;
                            if (input == 0) offset.Init(footprint, joins[j], Standard_False);
                            else
                            {
                                offset.Init(joins[j], Standard_False);
                                offset.AddWire(footprintWires[0]);
                            }
                            offset.SetApprox(approx == 1 ? Standard_True : Standard_False);
                            offset.Perform(value);
                            if (!offset.IsDone())
                            {
                                failures += " " + label + ": not done;";
                                continue;
                            }

                            const std::vector<TopoDS_Wire> wires = ShapeWires(offset.Shape());
                            if (wires.empty())
                            {
                                failures += " " + label + ": no wire;";
                                continue;
                            }

                            double area = 0.0;
                            const TopoDS_Face candidate = FaceFromWires(plane, wires, area);
                            if (candidate.IsNull())
                            {
                                failures += " " + label + ": face build failed;";
                                continue;
                            }

                            bool containsAll = true;
                            for (TopExp_Explorer ex(footprint, TopAbs_VERTEX); ex.More(); ex.Next())
                            {
                                const gp_Pnt p = BRep_Tool::Pnt(TopoDS::Vertex(ex.Current()));
                                if (!FaceContainsPoint(candidate, p, 1e-6)) { containsAll = false; break; }
                            }
                            if (!containsAll)
                            {
                                failures += " " + label + ": does not contain footprint;";
                                continue;
                            }

                            const double computed = std::fabs(area);
                            if (!(computed > footprintArea) || computed > upperBound * (1.0 + 1e-6) + 1e-6)
                            {
                                failures += " " + label + ": area " + std::to_string(computed) +
                                            " outside the outward bound;";
                                continue;
                            }

                            // 확장 경계는 원본 외곽에서 정확히 margin 떨어져 있어야 한다.
                            double boundaryDistance = -1.0;
                            bool distanceKnown = false;
                            try
                            {
                                TopoDS_Compound offsetBoundary;
                                BRep_Builder builder;
                                builder.MakeCompound(offsetBoundary);
                                for (size_t i = 0; i < wires.size(); ++i) builder.Add(offsetBoundary, wires[i]);
                                BRepExtrema_DistShapeShape distance(offsetBoundary, footprintWires[0]);
                                distance.Perform();
                                if (distance.IsDone() && distance.NbSolution() > 0)
                                {
                                    boundaryDistance = distance.Value();
                                    distanceKnown = true;
                                }
                            }
                            catch (const Standard_Failure&)
                            {
                            }
                            if (distanceKnown && std::fabs(boundaryDistance - margin) > distanceTolerance)
                            {
                                failures += " " + label + ": boundary distance " +
                                            std::to_string(boundaryDistance) + " is not the margin;";
                                continue;
                            }

                            outArea = computed;
                            std::printf("    offset: %s area=%.3f mm^2 (footprint %.3f, boundary distance %.6f mm)\n",
                                        label.c_str(), computed, footprintArea, boundaryDistance);
                            if (approx == 1)
                                std::printf("    warning: the outline was approximated to lines/arcs by OCCT\n");
                            return candidate;
                        }
                        catch (const Standard_Failure& failure)
                        {
                            failures += " " + label + ": " +
                                        (failure.GetMessageString() ? failure.GetMessageString() : "exception") + ";";
                        }
                    }
                }
            }
        }
        Fail("outward offset failed:" + failures);
        return TopoDS_Face();
    }

    void ShapeExtents(const TopoDS_Shape& shape, const Frame& frame, double& lo, double& hi)
    {
        lo = 1e300; hi = -1e300;
        for (TopExp_Explorer ex(shape, TopAbs_VERTEX); ex.More(); ex.Next())
        {
            const double h = frame.Height(BRep_Tool::Pnt(TopoDS::Vertex(ex.Current())));
            lo = std::min(lo, h); hi = std::max(hi, h);
        }
        if (!(hi >= lo)) Fail("empty shape extent");
    }

    TopoDS_Shell ShellFromFaces(const std::vector<TopoDS_Face>& faces)
    {
        BRep_Builder builder;
        TopoDS_Shell shell;
        builder.MakeShell(shell);
        for (const TopoDS_Face& face : faces) builder.Add(shell, face);
        return shell;
    }

    double ShapeArea(const TopoDS_Shape& shape);
    int CountFaces(const TopoDS_Shape& shape);

    /// 결과 face 들이 실제로 원본 덮개의 곡면을 그대로 쓰는지(비파괴 boolean) 확인한다.
    /// boolean 이 곡면을 다시 근사한 경우에는 그 면 전체가 원본 덮개 위에 있는지 확인한다.
    void ReportSurfaceProvenance(const std::vector<TopoDS_Face>& kept, const TopoDS_Shape& original)
    {
        std::set<const Standard_Transient*> originalSurfaces;
        for (TopExp_Explorer ex(original, TopAbs_FACE); ex.More(); ex.Next())
            originalSurfaces.insert(BRep_Tool::Surface(TopoDS::Face(ex.Current())).get());

        std::vector<TopoDS_Face> foreignFaces;
        for (size_t i = 0; i < kept.size(); ++i)
        {
            const Handle(Geom_Surface) surface = BRep_Tool::Surface(kept[i]);
            if (surface.IsNull() || originalSurfaces.count(surface.get()) == 0) foreignFaces.push_back(kept[i]);
        }
        std::printf("[5b] surface provenance: original surfaces=%d, rebuilt=%d\n",
                    static_cast<int>(kept.size() - foreignFaces.size()),
                    static_cast<int>(foreignFaces.size()));

        const double tolerance = 1.0e-3 + 2.0 * MaxTolerance(original);
        for (size_t i = 0; i < foreignFaces.size(); ++i)
        {
            const TopoDS_Face& face = foreignFaces[i];
            double area = 0.0;
            WireOrEdgeArea(face, area);
            const BRepAdaptor_Surface adaptor(face);

            // 면 위의 표본점(정점 + 삼각분할 내부점)이 모두 원본 덮개 위에 있어야 한다.
            std::vector<gp_Pnt> samples;
            for (TopExp_Explorer ex(face, TopAbs_VERTEX); ex.More(); ex.Next())
                samples.push_back(BRep_Tool::Pnt(TopoDS::Vertex(ex.Current())));
            try
            {
                BRepMesh_IncrementalMesh mesh(face, 0.05);
                TopLoc_Location location;
                const Handle(Poly_Triangulation) triangulation = BRep_Tool::Triangulation(face, location);
                if (!triangulation.IsNull())
                {
                    int taken = 0;
                    for (int t = 1; t <= triangulation->NbTriangles() && taken < 8; ++t)
                    {
                        Standard_Integer a = 0, b = 0, c = 0;
                        triangulation->Triangle(t).Get(a, b, c);
                        gp_Pnt p0 = triangulation->Node(a), p1 = triangulation->Node(b), p2 = triangulation->Node(c);
                        if (!location.IsIdentity())
                        {
                            p0.Transform(location.Transformation());
                            p1.Transform(location.Transformation());
                            p2.Transform(location.Transformation());
                        }
                        const gp_Pnt centre((p0.X() + p1.X() + p2.X()) / 3.0, (p0.Y() + p1.Y() + p2.Y()) / 3.0,
                                            (p0.Z() + p1.Z() + p2.Z()) / 3.0);
                        BRepClass_FaceClassifier classifier(face, centre, 1e-9);
                        if (classifier.State() == TopAbs_IN) { samples.push_back(centre); ++taken; }
                    }
                }
            }
            catch (const Standard_Failure&)
            {
            }
            if (samples.size() < 2) Fail("rebuilt face has no sample point for the provenance check");

            double worstDistance = 0.0;
            for (size_t s = 0; s < samples.size(); ++s)
            {
                BRepExtrema_DistShapeShape distance(BRepBuilderAPI_MakeVertex(samples[s]), original);
                distance.Perform();
                if (!distance.IsDone() || distance.NbSolution() < 1)
                    Fail("face provenance distance could not be measured");
                worstDistance = std::max(worstDistance, distance.Value());
            }
            std::printf("    rebuilt face: type=%s area=%.3f mm^2 max distance to the original mold=%.6f mm\n",
                        SurfaceTypeName(adaptor.GetType()), std::fabs(area), worstDistance);
            if (worstDistance > tolerance)
                Fail("the trimmed mold has a face that is not part of the original mold surface (distance " +
                     std::to_string(worstDistance) + " mm)");
        }
    }

    /// face 인접(공유 edge) 기준 연결 요소 수. 절단으로 떨어져 나간 조각을 검출한다.
    int CountFaceComponents(const TopoDS_Shape& shape)
    {
        TopTools_IndexedDataMapOfShapeListOfShape edgeFaces;
        TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, edgeFaces);
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(shape, TopAbs_FACE, faces);
        if (faces.Extent() == 0) return 0;

        std::vector<int> parent(static_cast<size_t>(faces.Extent()) + 1);
        for (size_t i = 0; i < parent.size(); ++i) parent[i] = static_cast<int>(i);
        std::function<int(int)> root = [&parent](int index) {
            while (parent[static_cast<size_t>(index)] != index)
            {
                parent[static_cast<size_t>(index)] = parent[static_cast<size_t>(parent[static_cast<size_t>(index)])];
                index = parent[static_cast<size_t>(index)];
            }
            return index;
        };
        for (Standard_Integer i = 1; i <= edgeFaces.Extent(); ++i)
        {
            int firstIndex = 0;
            for (TopTools_ListIteratorOfListOfShape it(edgeFaces(i)); it.More(); it.Next())
            {
                const int index = faces.FindIndex(it.Value());
                if (index == 0) continue;
                if (firstIndex == 0) { firstIndex = index; continue; }
                const int a = root(firstIndex);
                const int b = root(index);
                if (a != b) parent[static_cast<size_t>(std::max(a, b))] = std::min(a, b);
            }
        }
        std::set<int> roots;
        for (int i = 1; i <= faces.Extent(); ++i) roots.insert(root(i));
        return static_cast<int>(roots.size());
    }

    /// 투영 셀 중 제품에 속하는 것들을 하나의 평면 face 로 합친다(내부 구멍 유지).
    TopoDS_Face MergeCells(const std::vector<TopoDS_Face>& cells)
    {
        if (cells.empty()) Fail("no inside cell to merge");
        if (cells.size() == 1) return cells.front();
        TopTools_ListOfShape first, second;
        for (size_t i = 0; i < cells.size(); ++i)
            (i % 2 == 0 ? first : second).Append(cells[i]);

        BRepAlgoAPI_Fuse fuse;
        fuse.SetArguments(first);
        if (!second.IsEmpty()) fuse.SetTools(second);
        fuse.SetRunParallel(Standard_False);
        fuse.Build();
        if (!fuse.IsDone()) Fail("footprint fuse failed");
        {
            double cellsArea = 0.0;
            for (const TopoDS_Face& cell : cells) cellsArea += ShapeArea(cell);
            std::printf("    fuse: cells=%d sumArea=%.3f fusedFaces=%d fusedArea=%.3f\n",
                        static_cast<int>(cells.size()), cellsArea,
                        CountFaces(fuse.Shape()), ShapeArea(fuse.Shape()));
        }

        ShapeUpgrade_UnifySameDomain unify(fuse.Shape(), Standard_True, Standard_True, Standard_True);
        unify.Build();
        {
            std::vector<double> areas;
            for (TopExp_Explorer ex(unify.Shape(), TopAbs_FACE); ex.More(); ex.Next())
                areas.push_back(ShapeArea(ex.Current()));
            std::sort(areas.begin(), areas.end());
            std::printf("    unify: faces=%d area=%.3f", static_cast<int>(areas.size()), ShapeArea(unify.Shape()));
            if (!areas.empty())
                std::printf(" smallest=%.6f largest=%.6f", areas.front(), areas.back());
            std::printf("\n");
        }

        TopoDS_Face result;
        int faces = 0;
        for (TopExp_Explorer ex(unify.Shape(), TopAbs_FACE); ex.More(); ex.Next())
        {
            result = TopoDS::Face(ex.Current());
            ++faces;
        }
        if (faces != 1) Fail("footprint merged into " + std::to_string(faces) + " faces (expected 1)");
        return result;
    }

    double ShapeArea(const TopoDS_Shape& shape)
    {
        GProp_GProps props;
        BRepGProp::SurfaceProperties(shape, props);
        return props.Mass();
    }

    int CountFaces(const TopoDS_Shape& shape)
    {
        int count = 0;
        for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next()) ++count;
        return count;
    }

    void ReportValidity(const TopoDS_Shape& shape)
    {
        BRepCheck_Analyzer analyzer(shape, Standard_True);
        std::printf("    BRepCheck_Analyzer: %s\n", analyzer.IsValid() ? "valid" : "INVALID");
    }
    void ReportMass(const TopoDS_Shape& shape, bool hasSolid)
    {
        if (hasSolid)
        {
            GProp_GProps props;
            BRepGProp::VolumeProperties(shape, props);
            std::printf("    volume: %.3f mm^3, surface area: ", props.Mass());
        }
        else
        {
            std::printf("    (no solid) surface area: ");
        }
        GProp_GProps surface;
        BRepGProp::SurfaceProperties(shape, surface);
        std::printf("%.3f mm^2\n", surface.Mass());
    }

    /// XDE 문서를 읽어 최상위 형상 트리를 이름 경로와 함께 펼친다.
    void LoadShapes(const std::string& path, Handle(TDocStd_Document)& doc,
                    std::vector<NamedShape>& shapes)
    {
        Handle(XCAFApp_Application) app = XCAFApp_Application::GetApplication();
        app->NewDocument("MDTV-XCAF", doc);
        if (doc.IsNull()) Fail("cannot create XDE document");

        STEPCAFControl_Reader reader;
        reader.SetNameMode(Standard_True);
        reader.SetColorMode(Standard_True);
        reader.SetLayerMode(Standard_True);
        reader.SetPropsMode(Standard_True);

        const IFSelect_ReturnStatus status = reader.ReadFile(path.c_str());
        if (status != IFSelect_RetDone) Fail("STEP read failed, status=" + std::to_string(static_cast<int>(status)));
        if (!reader.Transfer(doc)) Fail("STEP transfer to XDE failed");

        Handle(XCAFDoc_ShapeTool) tool = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
        TDF_LabelSequence freeShapes;
        tool->GetFreeShapes(freeShapes);
        std::printf("free shapes: %d\n", freeShapes.Length());

        for (Standard_Integer i = 1; i <= freeShapes.Length(); ++i)
        {
            const TDF_Label label = freeShapes.Value(i);
            const size_t before = shapes.size();
            WalkLabel(tool, label, TopLoc_Location(), std::string(), shapes, 0);
            if (shapes.size() == before)
            {
                TopoDS_Shape shape;
                if (XCAFDoc_ShapeTool::GetShape(label, shape) && !shape.IsNull())
                {
                    NamedShape item;
                    item.name = LabelName(label);
                    item.path = item.name;
                    item.shape = shape;
                    shapes.push_back(item);
                }
            }
        }
    }

    void WriteStepDocument(const Handle(TDocStd_Document)& doc, const std::string& path)
    {
        Interface_Static::SetCVal("write.step.schema", "AP214IS");
        STEPCAFControl_Writer writer;
        writer.SetNameMode(Standard_True);
        writer.SetColorMode(Standard_True);
        writer.SetLayerMode(Standard_True);
        writer.SetPropsMode(Standard_True);
        if (!writer.Transfer(doc, STEPControl_AsIs)) Fail("XDE to STEP transfer failed");

        std::filesystem::remove(path);
        const IFSelect_ReturnStatus status = writer.Write(path.c_str());
        if (status != IFSelect_RetDone)
            Fail("STEP write failed, status=" + std::to_string(static_cast<int>(status)));
        if (!std::filesystem::exists(path)) Fail("STEP write produced no file");
    }

    const NamedShape* FindByName(const std::vector<NamedShape>& shapes, const char* wanted)
    {
        for (const NamedShape& item : shapes)
        {
            std::string lower = item.name;
            std::transform(lower.begin(), lower.end(), lower.begin(),
                           [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            if (lower == wanted) return &item;
        }
        return nullptr;
    }

    void Inspect(const Options& opt)
    {
        std::printf("=== inspect: %s ===\n", opt.input.c_str());

        Handle(TDocStd_Document) doc;
        std::vector<NamedShape> shapes;
        LoadShapes(opt.input, doc, shapes);

        for (const NamedShape& item : shapes)
        {
            bool hasSolid = false;
            for (TopExp_Explorer ex(item.shape, TopAbs_SOLID); ex.More(); ex.Next()) { hasSolid = true; break; }
            std::printf("\n[shape] name='%s' path='%s'%s\n", item.name.c_str(), item.path.c_str(),
                        item.isReference ? " (reference)" : "");
            std::printf("    topology: %s\n", ShapeTypeCounts(item.shape).c_str());
            ReportBox(item.shape, "world");
            ReportMass(item.shape, hasSolid);
            ReportSurfaceTypes(item.shape);
            ReportCurveTypes(item.shape);
            ReportTolerance(item.shape);
            ReportValidity(item.shape);
            ReportFreeBoundary(item.shape);
            ReportFreeBoundaryLoops(item.shape);
            ReportPrincipalAxes(item.shape, "shape");
        }

        const NamedShape* mold = nullptr;
        const NamedShape* part = nullptr;
        for (const NamedShape& item : shapes)
        {
            std::string lower = item.name;
            std::transform(lower.begin(), lower.end(), lower.begin(),
                           [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            if (lower == "mold") mold = &item;
            else if (lower == "item") part = &item;
        }
        if (mold != nullptr && part != nullptr)
        {
            std::printf("\n[axis-aligned projection surplus]\n");
            ReportProjectionSurplus(part->shape, mold->shape);
        }
        else
        {
            std::printf("\n[warn] could not identify both 'Mold' and 'Item' by name\n");
        }
    }

    bool SamePath(const std::string& a, const std::string& b)
    {
        std::error_code ec;
        std::filesystem::path pa = std::filesystem::weakly_canonical(std::filesystem::path(a), ec);
        if (ec) pa = std::filesystem::absolute(std::filesystem::path(a), ec);
        std::error_code ec2;
        std::filesystem::path pb = std::filesystem::weakly_canonical(std::filesystem::path(b), ec2);
        if (ec2) pb = std::filesystem::absolute(std::filesystem::path(b), ec2);
        std::string sa = pa.u8string();
        std::string sb = pb.u8string();
        std::transform(sa.begin(), sa.end(), sa.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        std::transform(sb.begin(), sb.end(), sb.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return sa == sb;
    }

    /// 제품 투영영역(셀 병합) -> 100 mm 확장 -> 법선 돌출 -> 덮개와 교차.
    /// offset API 사용 패턴 자체를 검증하는 자체 시험(합성 사각형).
    void OffsetProbe()
    {
        const gp_Pln plane(gp_Pnt(0.0, 0.0, 0.0), gp_Dir(0.0, 0.0, 1.0));
        BRepBuilderAPI_MakePolygon polygon;
        polygon.Add(gp_Pnt(0.0, 0.0, 0.0));
        polygon.Add(gp_Pnt(400.0, 0.0, 0.0));
        polygon.Add(gp_Pnt(400.0, 800.0, 0.0));
        polygon.Add(gp_Pnt(0.0, 800.0, 0.0));
        polygon.Close();
        BRepBuilderAPI_MakeFace faceMaker(plane, polygon.Wire(), Standard_True);
        const TopoDS_Face face = faceMaker.Face();

        for (int j = 0; j < 2; ++j)
        {
            const GeomAbs_JoinType join = j == 0 ? GeomAbs_Arc : GeomAbs_Intersection;
            for (int s = 0; s < 2; ++s)
            {
                const double value = s == 0 ? 100.0 : -100.0;
                try
                {
                    BRepOffsetAPI_MakeOffset offset;
                    offset.Init(face, join, Standard_False);
                    offset.SetApprox(Standard_False);
                    offset.Perform(value);
                    double area = 0.0;
                    bool built = false;
                    if (offset.IsDone())
                    {
                        const std::vector<TopoDS_Wire> wires = ShapeWires(offset.Shape());
                        if (!wires.empty())
                        {
                            const TopoDS_Face candidate = FaceFromWires(plane, wires, area);
                            built = !candidate.IsNull();
                        }
                    }
                    std::printf("    probe join=%s value=%+.1f done=%d face=%d area=%.3f\n",
                                j == 0 ? "arc" : "intersection", value, offset.IsDone() ? 1 : 0,
                                built ? 1 : 0, std::fabs(area));
                }
                catch (const Standard_Failure& failure)
                {
                    std::printf("    probe join=%s value=%+.1f exception=%s\n",
                                j == 0 ? "arc" : "intersection", value,
                                failure.GetMessageString() ? failure.GetMessageString() : "?");
                }
            }
        }
    }

    void Trim(const Options& opt)
    {
        if (opt.input.empty()) Fail("--input is required");
        if (opt.output.empty()) Fail("--output is required for trim mode");
        if (!opt.haveOrigin || !opt.haveNormal)
            Fail("trim requires --origin and --normal: the reference plane is a user decision, not inferred");
        if (!(opt.margin > 0.0)) Fail("--margin must be > 0");
        if (SamePath(opt.input, opt.output)) Fail("refusing to overwrite the input STEP; choose a different --output");
        if (std::filesystem::exists(opt.output))
            std::printf("[warn] output already exists and will be replaced on success: %s\n", opt.output.c_str());

        std::printf("=== trim: %s ===\n", opt.input.c_str());
        std::printf("working plane: origin(% .4f,% .4f,% .4f) normal(% .6f,% .6f,% .6f) xaxis(% .6f,% .6f,% .6f)\n",
                    opt.origin.X(), opt.origin.Y(), opt.origin.Z(),
                    opt.normal.X(), opt.normal.Y(), opt.normal.Z(),
                    opt.xaxis.X(), opt.xaxis.Y(), opt.xaxis.Z());
        std::printf("margin: %.3f mm (planar distance on the working plane)\n", opt.margin);

        Handle(TDocStd_Document) doc;
        std::vector<NamedShape> shapes;
        LoadShapes(opt.input, doc, shapes);

        const NamedShape* mold = FindByName(shapes, "mold");
        const NamedShape* part = FindByName(shapes, "item");
        if (mold == nullptr || part == nullptr) Fail("both 'Mold' and 'Item' must exist by name");

        int moldCount = 0, itemCount = 0;
        for (const NamedShape& item : shapes)
        {
            const std::string& n = item.name;
            std::string lower = n;
            std::transform(lower.begin(), lower.end(), lower.begin(),
                           [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            if (lower == "mold") ++moldCount;
            else if (lower == "item") ++itemCount;
        }
        if (moldCount != 1 || itemCount != 1)
            Fail("expected exactly one 'Mold' and one 'Item', found " + std::to_string(moldCount) +
                 " and " + std::to_string(itemCount));

        int partSolids = 0;
        for (TopExp_Explorer ex(part->shape, TopAbs_SOLID); ex.More(); ex.Next()) ++partSolids;
        if (partSolids != 1) Fail("'Item' must be exactly one solid, found " + std::to_string(partSolids));

        const double moldAreaBefore = ShapeArea(mold->shape);
        const int moldFacesBefore = CountFaces(mold->shape);
        std::printf("mold: faces=%d area=%.3f mm^2; item: faces=%d\n",
                    moldFacesBefore, moldAreaBefore, CountFaces(part->shape));

        const Frame frame(opt.origin, opt.normal, opt.xaxis);
        {
            const gp_Pnt probe = frame.World(gp_Pnt(10.0, 20.0, 0.0));
            const double back = frame.Height(probe);
            if (std::fabs(back) > 1e-9) Fail("working plane frame is not consistent (round-trip mismatch)");
        }

        // ---- 1. 제품 투영 외곽 ----
        const gp_Pln workPlane(gp_Ax3(frame.origin, frame.normal, frame.xdir));

        // HLR: 제품 전체의 투영 선을 종류별로 수집한다. 이 중 경계 후보는 outline(곡면
        // 실루엣)뿐이고, sharp/smooth/sewn은 투영 내부선이므로 경계 도구로 쓰지 않는다.
        TopTools_ListOfShape hlrByType[4];
        int hlrCounts[4] = { 0, 0, 0, 0 };
        CollectHlrCurves(part->shape, frame, hlrByType, hlrCounts);
        std::printf("\n[1a] HLR curves: sharp=%d smooth=%d sewn=%d outline=%d\n",
                    hlrCounts[0], hlrCounts[1], hlrCounts[2], hlrCounts[3]);

        // 실루엣 모서리: 인접 두 면의 facing이 갈리는 정확한 CAD 모서리.
        TopTools_ListOfShape boundaryCurves;
        int freeBoundary = 0;
        CollectSilhouetteEdges(part->shape, frame, boundaryCurves, freeBoundary);
        std::printf("    silhouette edges=%d free-boundary edges=%d\n",
                    static_cast<int>(boundaryCurves.Extent()), freeBoundary);
        if (freeBoundary > 0) Fail("item is not a closed solid (free boundary edges found)");
        if (boundaryCurves.IsEmpty() && hlrByType[3].IsEmpty())
            Fail("no silhouette was found for the item");
        for (TopTools_ListIteratorOfListOfShape it(hlrByType[3]); it.More(); it.Next())
            boundaryCurves.Append(it.Value());

        double pu0 = 0.0, pu1 = 0.0, pv0 = 0.0, pv1 = 0.0;
        ShapeUVBounds(part->shape, frame, pu0, pu1, pv0, pv1);
        const double targetPad = 0.05 * std::max(pu1 - pu0, pv1 - pv0) + 50.0;
        const TopoDS_Face projectionTarget =
            BuildPlaneFace(workPlane, pu0, pu1, pv0, pv1, targetPad);

        // HLR의 3D 결과와 제품 모서리는 평면 밖(제품 깊이)의 원본 곡선이므로, 확정 평면 위로
        // 정투영한다.
        ProjectedCurves curves;
        ProjectOntoPlane(boundaryCurves, projectionTarget, frame, MaxTolerance(part->shape), curves);
        curves.sharp = hlrCounts[0];
        curves.smooth = hlrCounts[1];
        curves.sewn = hlrCounts[2];
        curves.outline = hlrCounts[3];
        std::printf("[1b] boundary curves: total=%d\n", static_cast<int>(curves.edges.Extent()));
        std::printf("    uv range: [%.3f..%.3f] x [%.3f..%.3f]  size %.3f x %.3f\n",
                    curves.u0, curves.u1, curves.v0, curves.v1, curves.u1 - curves.u0, curves.v1 - curves.v0);
        if (curves.edges.IsEmpty()) Fail("projection produced no curve");

        const double uPad = 0.05 * std::max(curves.u1 - curves.u0, curves.v1 - curves.v0) + 50.0;
        {
            double worst = 0.0;
            for (TopTools_ListIteratorOfListOfShape it(curves.edges); it.More(); it.Next())
            {
                const TopoDS_Edge& edge = TopoDS::Edge(it.Value());
                Standard_Real first = 0.0, last = 0.0;
                const Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);
                if (curve.IsNull()) continue;
                for (int k = 0; k <= 4; ++k)
                {
                    gp_Pnt p;
                    curve->D0(first + (last - first) * k / 4.0, p);
                    worst = std::max(worst, std::fabs(workPlane.Distance(p)));
                }
            }
            std::printf("    max curve deviation from the working plane: %.6f mm\n", worst);
            if (worst > 1e-6)
                Fail("projected footprint curve is not on the working plane");
        }
        TopoDS_Face plane = BuildPlaneFace(workPlane, curves.u0, curves.u1, curves.v0, curves.v1, uPad);
        if (opt.debugTools > 0)
        {
            const double planeArea = ShapeArea(plane);
            for (int k = 1; k <= opt.debugTools; ++k)
            {
                TopTools_ListOfShape limited;
                int taken = 0;
                for (TopTools_ListIteratorOfListOfShape it(curves.edges); it.More() && taken < k; it.Next(), ++taken)
                    limited.Append(it.Value());
                const TopoDS_Shape arr = SplitPlaneFace(plane, limited);
                int faces = 0, whole = 0;
                double total = 0.0;
                for (TopExp_Explorer ex(arr, TopAbs_FACE); ex.More(); ex.Next())
                {
                    ++faces;
                    const double a = ShapeArea(ex.Current());
                    total += a;
                    if (std::fabs(std::fabs(a) - planeArea) <= 1e-6 * planeArea) ++whole;
                }
                std::printf("[debug] tools=%d faces=%d wholePlaneFaces=%d sumArea=%.3f planeArea=%.3f\n",
                            k, faces, whole, total, planeArea);
            }
            return;
        }
        TopoDS_Shape arrangement = SplitPlaneFace(plane, curves.edges);

        double halfRange = 2.0 * std::max(curves.u1 - curves.u0, curves.v1 - curves.v0) + 4.0 * opt.margin + 1000.0;
        {
            double lo = 1e300, hi = -1e300;
            ShapeExtents(part->shape, frame, lo, hi);
            double lo2 = 1e300, hi2 = -1e300;
            ShapeExtents(mold->shape, frame, lo2, hi2);
            halfRange += std::max(std::fabs(lo), std::fabs(hi)) + std::max(std::fabs(lo2), std::fabs(hi2));
        }

        IntCurvesFace_ShapeIntersector intersector;
        intersector.Load(part->shape, 1e-7);

        int cellCount = 0, insideCount = 0;
        bool unresolvedCell = false;
        std::vector<TopoDS_Face> insideCells;
        std::vector<double> cellAreas;
        for (TopExp_Explorer ex(arrangement, TopAbs_FACE); ex.More(); ex.Next())
        {
            const TopoDS_Face cell = TopoDS::Face(ex.Current());
            ++cellCount;
            gp_Pnt interior;
            if (!FaceInteriorPoint(cell, frame, interior))
            {
                double a = 0.0;
                WireOrEdgeArea(cell, a);
                std::printf("    [warn] cell without verified interior point: area=%.9f mm^2\n",
                            std::fabs(a));
                unresolvedCell = true;
                continue;
            }
            const gp_Lin line(interior, opt.normal);
            intersector.Perform(line, -halfRange, halfRange);
            if (!intersector.IsDone()) Fail("line/shape intersection failed on a projection cell");
            const bool inside = intersector.NbPnt() > 0;
            double cellArea = 0.0;
            WireOrEdgeArea(cell, cellArea);
            cellAreas.push_back(cellArea);
            if (inside) { ++insideCount; insideCells.push_back(cell); }
            else
                std::printf("    cell outside the item: orientation=%d area=%.6f mm^2\n",
                            static_cast<int>(cell.Orientation()), std::fabs(cellArea));
        }
        {
            std::vector<double> sorted = cellAreas;
            std::sort(sorted.begin(), sorted.end(), [](double l, double r) { return l > r; });
            double total = 0.0;
            for (double a : cellAreas) total += a;
            std::printf("    cell area total=%.3f", total);
            for (size_t i = 0; i < std::min<size_t>(sorted.size(), 3); ++i)
                std::printf(" top%zu=%.3f", i + 1, sorted[i]);
            std::printf("\n");
        }
        std::printf("[2] arrangement cells=%d inside=%d%s\n", cellCount, insideCount,
                    unresolvedCell ? " (WARNING: some cells had no verified interior point)" : "");
        if (unresolvedCell) Fail("projection cell without a verified interior point; classification is incomplete");
        if (insideCount == 0) Fail("no projection cell belongs to the item");

        TopoDS_Face footprint = MergeCells(insideCells);
        {
            double area = 0.0;
            WireOrEdgeArea(footprint, area);
            std::printf("[3] footprint: area=%.3f mm^2 wires=%d\n",
                        std::fabs(area), static_cast<int>(ShapeWires(footprint).size()));
            if (!(std::fabs(area) > 0.0)) Fail("footprint has zero area");

            const std::vector<TopoDS_Wire> footprintWires = ShapeWires(footprint);
            for (size_t i = 0; i < footprintWires.size(); ++i)
            {
                int edgeCount = 0;
                double minLength = 1e300, maxLength = 0.0;
                for (TopExp_Explorer ex(footprintWires[i], TopAbs_EDGE); ex.More(); ex.Next())
                {
                    ++edgeCount;
                    GProp_GProps props;
                    BRepGProp::LinearProperties(ex.Current(), props);
                    minLength = std::min(minLength, props.Mass());
                    maxLength = std::max(maxLength, props.Mass());
                }
                std::printf("    wire %zu: edges=%d closed=%d orientation=%d length %.9f..%.3f\n",
                            i, edgeCount,
                            BRep_Tool::IsClosed(footprintWires[i]) ? 1 : 0,
                            static_cast<int>(footprintWires[i].Orientation()),
                            minLength, maxLength);
            }
            std::printf("    footprint orientation=%d\n", static_cast<int>(footprint.Orientation()));
            if (opt.debugOutline) DumpOutline(footprint, frame);
        }

        // ---- 2. 100 mm 확장 ----
        double offsetArea = 0.0;
        TopoDS_Face outline = CleanOutline(workPlane, footprint, offsetArea);
        TopoDS_Face region = OffsetOutward(workPlane, outline, opt.margin, offsetArea);
        ReportOffsetDeviation(region, outline, opt.margin);
        {
            double sharpArea = 0.0, sharpDistance = 0.0;
            const double roundArea = offsetArea;
            region = SquareCorners(frame, workPlane, region, outline, opt.margin,
                                   roundArea, sharpArea, sharpDistance);
            offsetArea = sharpArea;
        }
        {
            double footprintArea = 0.0;
            WireOrEdgeArea(footprint, footprintArea);
            if (!(offsetArea > std::fabs(footprintArea)))
                Fail("offset region is not larger than the footprint");
        }

        // ---- 3. 덮개 절단 ----
        double partLo = 0.0, partHi = 0.0, moldLo = 0.0, moldHi = 0.0;
        ShapeExtents(part->shape, frame, partLo, partHi);
        ShapeExtents(mold->shape, frame, moldLo, moldHi);
        const double lo = std::min(partLo, moldLo) - 25.0;
        const double hi = std::max(partHi, moldHi) + 25.0;
        std::printf("[4] cut range along normal: %.3f .. %.3f (height %.3f)\n", lo, hi, hi - lo);

        gp_Trsf lift;
        lift.SetTranslation(gp_Vec(frame.normal) * lo);
        const TopoDS_Shape lifted = BRepBuilderAPI_Transform(region, lift, Standard_True).Shape();
        BRepPrimAPI_MakePrism makePrism(lifted, gp_Vec(frame.normal) * (hi - lo));
        if (!makePrism.IsDone()) Fail("prism build failed");
        const TopoDS_Shape cutter = makePrism.Shape();

        BRepAlgoAPI_Common common(mold->shape, cutter);
        common.SetRunParallel(Standard_False);
        common.Build();
        if (!common.IsDone()) Fail("boolean common (mold, cut region) failed");

        std::vector<TopoDS_Face> keptFaces;
        for (TopExp_Explorer ex(common.Shape(), TopAbs_FACE); ex.More(); ex.Next())
            keptFaces.push_back(TopoDS::Face(ex.Current()));
        if (keptFaces.empty()) Fail("trim removed the whole mold");
        const TopoDS_Shape trimmed = ShellFromFaces(keptFaces);
        const double trimmedArea = ShapeArea(trimmed);
        std::printf("[5] trimmed mold: faces=%d area=%.3f mm^2 (removed %.3f mm^2)\n",
                    static_cast<int>(keptFaces.size()), trimmedArea, moldAreaBefore - trimmedArea);
        if (!(trimmedArea > 0.0) || trimmedArea > moldAreaBefore + 1e-3)
            Fail("trimmed area is not a subset of the original mold area");

        // 모든 결과 정점이 확장영역 안(경계 포함)에 있어야 한다.
        int outsideVertices = 0;
        for (size_t i = 0; i < keptFaces.size(); ++i)
        {
            for (TopExp_Explorer ex(keptFaces[i], TopAbs_VERTEX); ex.More(); ex.Next())
            {
                const gp_Pnt world = BRep_Tool::Pnt(TopoDS::Vertex(ex.Current()));
                if (FaceContainsPoint(region, world, 1e-6)) continue;
                ++outsideVertices;
            }
        }
        if (outsideVertices > 0)
            Fail("trimmed mold has " + std::to_string(outsideVertices) + " vertices outside the cut region");

        BRepCheck_Analyzer analyzer(trimmed, Standard_True);
        std::printf("[6] BRepCheck_Analyzer: %s\n", analyzer.IsValid() ? "valid" : "INVALID");
        if (!analyzer.IsValid()) Fail("trimmed shell is not valid");

        ReportSurfaceProvenance(keptFaces, mold->shape);
        const int components = CountFaceComponents(trimmed);
        int shellCount = 0;
        for (TopExp_Explorer ex(trimmed, TopAbs_SHELL); ex.More(); ex.Next()) ++shellCount;
        std::printf("[6b] trimmed mold connected components=%d, shells=%d\n", components, shellCount);
        if (components != 1) Fail("trimmed mold splits into " + std::to_string(components) + " pieces");

        // 접촉면 보존: 제품과 덮개의 공통 면적이 절단 전후로 같아야 한다.
        double contactBefore = 0.0, contactAfter = 0.0;
        {
            BRepAlgoAPI_Common contact0(part->shape, mold->shape);
            contact0.Build();
            if (!contact0.IsDone()) Fail("contact area (before) could not be computed");
            contactBefore = ShapeArea(contact0.Shape());
        }
        {
            BRepAlgoAPI_Common contact1(part->shape, trimmed);
            contact1.Build();
            if (!contact1.IsDone()) Fail("contact area (after) could not be computed");
            contactAfter = ShapeArea(contact1.Shape());
        }
        std::printf("[7] item/mold contact area: before=%.3f after=%.3f mm^2\n", contactBefore, contactAfter);
        if (std::fabs(contactBefore - contactAfter) > 1e-3 + 1e-6 * std::fabs(contactBefore))
            Fail("contact area between item and mold changed");

        // ---- 4. 저장 ----
        Handle(XCAFDoc_ShapeTool) shapeTool = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
        if (mold->definition.IsNull()) Fail("mold definition label is missing");
        TDF_LabelSequence users;
        if (XCAFDoc_ShapeTool::GetUsers(mold->definition, users) > 1)
            Fail("mold definition is shared by several instances; refusing to replace it");

        TopoDS_Shape stored = trimmed;
        const TopLoc_Location definitionLocation = XCAFDoc_ShapeTool::GetLocation(mold->definition);
        if (!definitionLocation.IsIdentity())
        {
            const gp_Trsf inverse = definitionLocation.Transformation().Inverted();
            stored = BRepBuilderAPI_Transform(stored, inverse, Standard_True).Shape();
        }
        Quantity_Color moldColor;
        bool hasMoldColor = false;
        {
            Handle(XCAFDoc_ColorTool) colorTool = XCAFDoc_DocumentTool::ColorTool(doc->Main());
            hasMoldColor = colorTool->GetColor(mold->definition, XCAFDoc_ColorGen, moldColor);
            if (!hasMoldColor) hasMoldColor = colorTool->GetColor(mold->definition, XCAFDoc_ColorSurf, moldColor);
        }

        // 기준 왕복: 수정 전 문서를 그대로 저장/재읽기하여 STEP 왕복 자체의 오차를 측정한다.
        // 출력물의 제품 편차는 이 기준값과 비교해야 하며, 절단 전후 비교에 쓰지 않는다.
        const std::string baselinePath = opt.output + ".baseline";
        double baselineItemVolume = 0.0, baselineItemArea = 0.0, baselineMoldArea = 0.0;
        int baselineItemFaces = 0;
        {
            WriteStepDocument(doc, baselinePath);
            Handle(TDocStd_Document) baselineDoc;
            std::vector<NamedShape> baselineShapes;
            LoadShapes(baselinePath, baselineDoc, baselineShapes);
            const NamedShape* baselineItem = FindByName(baselineShapes, "item");
            if (baselineItem == nullptr) Fail("baseline round trip lost the Item");
            const NamedShape* baselineMold = FindByName(baselineShapes, "mold");
            if (baselineMold == nullptr) Fail("baseline round trip lost the Mold");
            {
                GProp_GProps props;
                BRepGProp::VolumeProperties(baselineItem->shape, props);
                baselineItemVolume = props.Mass();
                GProp_GProps surface;
                BRepGProp::SurfaceProperties(baselineItem->shape, surface);
                baselineItemArea = surface.Mass();
                baselineMoldArea = ShapeArea(baselineMold->shape);
                for (TopExp_Explorer ex(baselineItem->shape, TopAbs_FACE); ex.More(); ex.Next())
                    ++baselineItemFaces;
            }
            std::printf("[8] baseline round trip: item volume=%.3f area=%.3f faces=%d, mold area=%.3f\n",
                        baselineItemVolume, baselineItemArea, baselineItemFaces, baselineMoldArea);
            std::filesystem::remove(baselinePath);
        }

        shapeTool->SetShape(mold->definition, stored);
        shapeTool->UpdateAssemblies();
        if (hasMoldColor)
        {
            Handle(XCAFDoc_ColorTool) colorTool = XCAFDoc_DocumentTool::ColorTool(doc->Main());
            colorTool->SetColor(mold->definition, moldColor, XCAFDoc_ColorGen);
        }

        const std::string tempPath = opt.output + ".tmp";
        WriteStepDocument(doc, tempPath);

        // 재읽기 검증: 제품은 "수정 전 문서의 왕복 결과"와 비교한다.
        Handle(TDocStd_Document) verifyDoc;
        std::vector<NamedShape> verifyShapes;
        LoadShapes(tempPath, verifyDoc, verifyShapes);
        const NamedShape* verifyMold = FindByName(verifyShapes, "mold");
        const NamedShape* verifyItem = FindByName(verifyShapes, "item");
        if (verifyMold == nullptr || verifyItem == nullptr) Fail("written STEP no longer contains both Mold and Item");
        const double verifyMoldArea = ShapeArea(verifyMold->shape);
        const double verifyItemArea = ShapeArea(verifyItem->shape);
        double itemVolumeBefore = 0.0, itemVolumeAfter = 0.0;
        int itemFacesBefore = 0, itemFacesAfter = 0;
        for (TopExp_Explorer ex(part->shape, TopAbs_FACE); ex.More(); ex.Next()) ++itemFacesBefore;
        for (TopExp_Explorer ex(verifyItem->shape, TopAbs_FACE); ex.More(); ex.Next()) ++itemFacesAfter;
        {
            GProp_GProps props;
            BRepGProp::VolumeProperties(part->shape, props);
            itemVolumeBefore = props.Mass();
        }
        {
            GProp_GProps props;
            BRepGProp::VolumeProperties(verifyItem->shape, props);
            itemVolumeAfter = props.Mass();
        }
        GProp_GProps itemSurface;
        BRepGProp::SurfaceProperties(part->shape, itemSurface);
        std::printf("[9] reread: mold area=%.3f (expected %.3f), item volume=%.3f (baseline %.3f), "
                    "item area=%.3f (baseline %.3f), item faces=%d (in-memory %d, baseline %d)\n",
                    verifyMoldArea, trimmedArea, itemVolumeAfter, baselineItemVolume,
                    verifyItemArea, baselineItemArea, itemFacesAfter, itemFacesBefore, baselineItemFaces);
        std::printf("    step round-trip error: item volume %.6f mm^3, item area %.6f mm^2, mold area %.6f mm^2\n",
                    itemVolumeBefore - baselineItemVolume, itemSurface.Mass() - baselineItemArea,
                    baselineMoldArea - moldAreaBefore);

        // 덮개는 같은 면들이 다시 기록되므로 기준 왕복 오차 범위 안이면 된다.
        const double roundTripMoldError = std::fabs(baselineMoldArea - moldAreaBefore);
        const double moldTolerance = 1e-2 + 2.0 * roundTripMoldError + 1e-6 * trimmedArea;
        if (std::fabs(verifyMoldArea - trimmedArea) > moldTolerance)
            Fail("reread mold area does not match the trimmed shape (round-trip error " +
                 std::to_string(roundTripMoldError) + ")");
        if (std::fabs(itemVolumeAfter - baselineItemVolume) > 1e-2 + 1e-6 * std::fabs(baselineItemVolume))
            Fail("item volume differs from the unchanged round trip: " +
                 std::to_string(itemVolumeAfter) + " vs " + std::to_string(baselineItemVolume));
        if (std::fabs(verifyItemArea - baselineItemArea) > 1e-2 + 1e-6 * std::fabs(baselineItemArea))
            Fail("item surface area differs from the unchanged round trip: " +
                 std::to_string(verifyItemArea) + " vs " + std::to_string(baselineItemArea));
        if (itemFacesAfter != baselineItemFaces || itemFacesBefore != baselineItemFaces)
            Fail("item face count changed: before=" + std::to_string(itemFacesBefore) +
                 " after=" + std::to_string(itemFacesAfter) +
                 " baseline=" + std::to_string(baselineItemFaces));

        std::filesystem::rename(tempPath, opt.output);
        std::printf("\n[done] wrote %s\n", opt.output.c_str());
    }
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    const Options opt = ParseArgs(argc, argv);
    if (opt.mode == "inspect") { Inspect(opt); return 0; }
    if (opt.mode == "trim") { Trim(opt); return 0; }
    if (opt.mode == "offset-probe") { OffsetProbe(); return 0; }
    Fail("unknown mode: " + opt.mode);
}

