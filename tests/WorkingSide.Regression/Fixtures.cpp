#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRep_Builder.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Wire.hxx>
#include <STEPControl_Writer.hxx>
#include <fstream>
#include <iterator>
#include <string>
#include <iostream>
#include <stdexcept>
#include <Geom_Line.hxx>
#include <Geom_Circle.hxx>
#include <Geom_BezierCurve.hxx>
#include <TColgp_Array1OfPnt.hxx>
#include <TopExp.hxx>
#include <map>
#include "../../src/OccBridge/ExitEdgeMatchingNative.inc"
#pragma comment(lib, "TKGeomAlgo.lib")

static void NativeExitChecks()
{
    using namespace HeatingMoldExitNative;
    auto check=[](bool value,const char* label) {
        if (!value) throw std::runtime_error(label);
        std::cout << "PASS native " << label << '\n';
    };
    const double pi=3.14159265358979323846;
    ProjectedCurve inclined;
    Handle(Geom_Curve) line=new Geom_Line(gp_Pnt(5,-7,11),gp_Dir(1,0,2));
    check(BuildProjectedCurve(line,3,40,gp_Pnt(17,-23,41),gp::DX(),gp::DY(),gp::DZ(),inclined)==Status::Hit,
        "inclined edge projection keeps original parameter");
    const double known=13.0;
    gp_Pnt2d query=Datum(inclined,line->Value(known)); query.SetX(query.X()+2);
    double t=0;
    check(NearestParameter(inclined,3,40,query,5,t)==Status::Hit && std::fabs(t-(known+2*std::sqrt(5.0)))<1e-7,
        "nearest parameter minimizes plane distance, not lifted 3D distance");
    gp_Pnt point; gp_Vec tangent; gp_Pnt2d datum;
    check(EvaluateParameter(inclined,3,40,t,point,tangent,datum)==Status::Hit &&
        point.Distance(line->Value(t))<1e-9 && datum.Distance(Datum(inclined,point))<1e-9,
        "selected 2D parameter evaluates the identical original 3D parameter");
    query=Datum(inclined,line->Value(40)); query.SetX(query.X()+0.01);
    check(NearestParameter(inclined,3,40,query,.02,t)==Status::Hit && t==40,
        "open trim endpoint participates when the extrema projector has no solution");
    check(NearestParameter(inclined,12,15,Datum(inclined,line->Value(20)),100,t)==Status::Hit && t==15,
        "cached full curve nearest query is still restricted to the supplied trim");
    check(EvaluateParameter(inclined,12,15,20,point,tangent,datum)==Status::Miss &&
        EvaluateParameter(inclined,12,15,15,point,tangent,datum)==Status::Hit,
        "saved parameter is evaluated directly without searching or changing trim");

    ProjectedCurve ellipse;
    Handle(Geom_Curve) circle=new Geom_Circle(gp_Ax2(gp_Pnt(3,5,30),gp_Dir(1,0,1),gp::DY()),20);
    check(BuildProjectedCurve(circle,.3,5.8,gp_Pnt(17,-23,41),gp::DY(),gp_Dir(-1,0,0),gp::DZ(),ellipse)==Status::Hit,
        "tilted circular curve converts to datum coordinates without reparameterizing");
    const double et=1.7;
    gp_Pnt2d ep; gp_Vec2d ed; ellipse.datum->D1(et,ep,ed);
    const gp_Pnt2d eq=ep.Translated(gp_Vec2d(-ed.Y(),ed.X()).Normalized()*.01);
    check(NearestParameter(ellipse,.3,5.8,eq,.1,t)==Status::Hit && std::fabs(t-et)<1e-7,
        "inclined curved nearest solution uses exact projected curve");

    ProjectedCurve closed;
    Handle(Geom_Curve) flatCircle=new Geom_Circle(gp_Ax2(gp_Pnt(0,0,12),gp::DZ()),20);
    check(BuildProjectedCurve(flatCircle,0,2*pi,gp::Origin(),gp::DX(),gp::DY(),gp::DZ(),closed)==Status::Hit,
        "closed periodic original edge projects successfully");
    check(NearestParameter(closed,0,2*pi,gp_Pnt2d(20,0),.01,t)==Status::Hit && std::fabs(t)<1e-9,
        "closed shared endpoints are one nearest solution");
    check(EvaluateParameter(closed,0,2*pi,2*pi,point,tangent,datum)==Status::Hit && point.Distance(flatCircle->Value(2*pi))<1e-9,
        "direct evaluation preserves the saved periodic endpoint parameter");
    check(NearestParameter(closed,0,2*pi,gp_Pnt2d(0,0),100,t)==Status::Ambiguous,
        "circle centre is an explicit ambiguous nearest solution");
    query=closed.datum->Value(4.0).Translated(gp_Vec2d(0.002,0));
    check(NearestParameter(closed,0,4.0,query,.1,t)==Status::Hit && std::fabs(t-4.0)<1e-9,
        "endpoint is compared even when the curved projector also finds interior extrema");
    std::vector<double> intersections;
    check(IntersectRay(closed,0,2*pi,gp_Pnt2d(-30,0),7,0,.05,intersections)==Status::Hit && intersections.size()==2,
        "non-unit ray returns all forward curved intersections and deduplicates closure");
    check(IntersectRay(closed,0,2*pi,gp_Pnt2d(0,0),1,0,.05,intersections)==Status::Hit && intersections.size()==1,
        "half-ray excludes backward intersections");
    check(IntersectRay(closed,0,2*pi,gp_Pnt2d(20,0),1,0,.05,intersections)==Status::Miss,
        "ray origin itself is not a forward intersection");
    check(IntersectRay(closed,0,2*pi,gp_Pnt2d(-30,20),2,0,.05,intersections)==Status::TangentOrOverlap,
        "tangent generation intersection fails explicitly");
    check(IntersectRay(closed,0,2*pi,gp_Pnt2d(-30,20.1),2,0,100,intersections)==Status::Miss,
        "large UI tolerance cannot inflate intersection geometry tolerance");
    check(IntersectRay(inclined,3,40,Datum(inclined,line->Value(0)),1,0,.05,intersections)==Status::TangentOrOverlap,
        "overlapping projected edge and ray fails explicitly");
    ProjectedCurve collapsed;
    Handle(Geom_Curve) vertical=new Geom_Line(gp_Pnt(0,0,2),gp::DZ());
    check(BuildProjectedCurve(vertical,0,10,gp::Origin(),gp::DX(),gp::DY(),gp::DZ(),collapsed)==Status::DegenerateProjection,
        "normal-aligned edge is an explicit degenerate projection");
    TColgp_Array1OfPnt poles(1,4);
    poles.SetValue(1,gp_Pnt(0,0,0)); poles.SetValue(2,gp_Pnt(20,20,10));
    poles.SetValue(3,gp_Pnt(-20,20,20)); poles.SetValue(4,gp_Pnt(0,0,30));
    ProjectedCurve folded;
    Handle(Geom_Curve) bezier=new Geom_BezierCurve(poles);
    check(BuildProjectedCurve(bezier,0,1,gp::Origin(),gp::DX(),gp::DY(),gp::DZ(),folded)==Status::Hit &&
        NearestParameter(folded,0,1,gp_Pnt2d(0,0),.01,t)==Status::Ambiguous,
        "identical endpoint datum at distinct depths is not merged");
    check(IntersectRay(folded,0,1,gp_Pnt2d(-2,0),1,0,.01,intersections)==Status::Ambiguous,
        "equal ray intersection datum at distinct depths is explicit ambiguity");

    TopoDS_Edge original=BRepBuilderAPI_MakeEdge(gp_Pnt(0,0,0),gp_Pnt(100,0,0)).Edge();
    TopoDS_Edge partial=BRepBuilderAPI_MakeEdge(gp_Pnt(20,0,0),gp_Pnt(40,0,0)).Edge();
    SourceInterval match;
    check(MatchSourceInterval(partial,original,.001,match) && std::fabs(match.first-20)<1e-7 && std::fabs(match.last-40)<1e-7,
        "sewing subedge maps to actual original partial interval, not full range");
    TopoDS_Edge backwards=BRepBuilderAPI_MakeEdge(gp_Pnt(40,0,0),gp_Pnt(20,0,0)).Edge();
    check(MatchSourceInterval(backwards,original,.001,match) && match.reversed &&
        std::fabs(match.first-20)<1e-7 && std::fabs(match.last-40)<1e-7,
        "subedge traversal sense is separate from increasing original trim bounds");
    TopoDS_Edge whole=BRepBuilderAPI_MakeEdge(gp_Pnt(0,0,0),gp_Pnt(100,0,0)).Edge();
    check(MatchSourceInterval(whole,original,.001,match) && match.first==0 && match.last==100,
        "original endpoints do not disappear when endpoint extrema lists are empty");
    TopoDS_Edge closeDepth=BRepBuilderAPI_MakeEdge(gp_Pnt(0,0,.01),gp_Pnt(100,0,.01)).Edge();
    bool ambiguous=false;
    check(!FindUniqueSourceInterval(partial,{original,closeDepth},{},.05,match,ambiguous) && ambiguous,
        "fallback requires 3D source uniqueness instead of choosing a near parallel edge");
    check(FindUniqueSourceInterval(partial,{original,closeDepth},{0},.05,match,ambiguous) && match.edge.IsSame(original),
        "sewing source history takes priority over ambiguous geometric proximity");
}

static TopoDS_Wire Rectangle(double x0,double y0,double x1,double y1,double z)
{
    BRepBuilderAPI_MakePolygon p;
    p.Add(gp_Pnt(x0,y0,z)); p.Add(gp_Pnt(x1,y0,z));
    p.Add(gp_Pnt(x1,y1,z)); p.Add(gp_Pnt(x0,y1,z)); p.Close();
    return p.Wire();
}
static TopoDS_Face Sheet(double x0,double y0,double x1,double y1,double z)
{ return BRepBuilderAPI_MakeFace(Rectangle(x0,y0,x1,y1,z)).Face(); }
static TopoDS_Compound ThreePanels()
{
    BRepBuilderAPI_Sewing sewing(1e-6);
    auto panel=[](double x0,double z0,double x1,double z1) {
        BRepBuilderAPI_MakePolygon p;
        p.Add(gp_Pnt(x0,0,z0)); p.Add(gp_Pnt(x1,0,z1)); p.Add(gp_Pnt(x1,1,z1)); p.Add(gp_Pnt(x0,1,z0)); p.Close();
        return BRepBuilderAPI_MakeFace(p.Wire()).Face();
    };
    sewing.Add(panel(0,0,1,.5)); sewing.Add(panel(1,.5,2,0)); sewing.Add(panel(2,0,3,.5));
    sewing.Perform();
    TopoDS_Compound shape; BRep_Builder builder; builder.MakeCompound(shape); builder.Add(shape,sewing.SewedShape());
    return shape;
}
// Box (60x40x20) whose top and -Y wall are split at x=30: the long boundary edge has
// different neighbours on each half, so the exit-side run breaks in the middle.
static TopoDS_Compound ExitSplit()
{
    auto quad=[](gp_Pnt a,gp_Pnt b,gp_Pnt c,gp_Pnt d) {
        BRepBuilderAPI_MakePolygon p; p.Add(a); p.Add(b); p.Add(c); p.Add(d); p.Close();
        return BRepBuilderAPI_MakeFace(p.Wire()).Face();
    };
    BRepBuilderAPI_Sewing sewing(1e-6);
    sewing.Add(quad(gp_Pnt(0,0,20),gp_Pnt(30,0,20),gp_Pnt(30,40,20),gp_Pnt(0,40,20)));   // top A
    sewing.Add(quad(gp_Pnt(30,0,20),gp_Pnt(60,0,20),gp_Pnt(60,40,20),gp_Pnt(30,40,20))); // top B
    sewing.Add(quad(gp_Pnt(0,0,0),gp_Pnt(60,0,0),gp_Pnt(60,40,0),gp_Pnt(0,40,0)));       // bottom
    sewing.Add(quad(gp_Pnt(0,0,0),gp_Pnt(0,40,0),gp_Pnt(0,40,20),gp_Pnt(0,0,20)));       // x=0
    sewing.Add(quad(gp_Pnt(60,0,0),gp_Pnt(60,40,0),gp_Pnt(60,40,20),gp_Pnt(60,0,20)));   // x=60
    sewing.Add(quad(gp_Pnt(0,40,0),gp_Pnt(60,40,0),gp_Pnt(60,40,20),gp_Pnt(0,40,20)));   // y=40
    sewing.Add(quad(gp_Pnt(0,0,0),gp_Pnt(30,0,0),gp_Pnt(30,0,20),gp_Pnt(0,0,20)));       // y=0 A
    sewing.Add(quad(gp_Pnt(30,0,0),gp_Pnt(60,0,0),gp_Pnt(60,0,20),gp_Pnt(30,0,20)));     // y=0 B
    sewing.Perform();
    TopoDS_Compound shape; BRep_Builder builder; builder.MakeCompound(shape); builder.Add(shape,sewing.SewedShape());
    return shape;
}
// Same box with the top and -Y wall split twice (x=20 and x=40): the long side is covered
// by three neighbour faces, so joining has to survive a short middle face.
static TopoDS_Compound ExitSplitThree()
{
    auto quad=[](gp_Pnt a,gp_Pnt b,gp_Pnt c,gp_Pnt d) {
        BRepBuilderAPI_MakePolygon p; p.Add(a); p.Add(b); p.Add(c); p.Add(d); p.Close();
        return BRepBuilderAPI_MakeFace(p.Wire()).Face();
    };
    BRepBuilderAPI_Sewing sewing(1e-6);
    sewing.Add(quad(gp_Pnt(0,0,20),gp_Pnt(20,0,20),gp_Pnt(20,40,20),gp_Pnt(0,40,20)));   // top A
    sewing.Add(quad(gp_Pnt(20,0,20),gp_Pnt(40,0,20),gp_Pnt(40,40,20),gp_Pnt(20,40,20))); // top B
    sewing.Add(quad(gp_Pnt(40,0,20),gp_Pnt(60,0,20),gp_Pnt(60,40,20),gp_Pnt(40,40,20))); // top C
    sewing.Add(quad(gp_Pnt(0,0,0),gp_Pnt(60,0,0),gp_Pnt(60,40,0),gp_Pnt(0,40,0)));       // bottom
    sewing.Add(quad(gp_Pnt(0,0,0),gp_Pnt(0,40,0),gp_Pnt(0,40,20),gp_Pnt(0,0,20)));       // x=0
    sewing.Add(quad(gp_Pnt(60,0,0),gp_Pnt(60,40,0),gp_Pnt(60,40,20),gp_Pnt(60,0,20)));   // x=60
    sewing.Add(quad(gp_Pnt(0,40,0),gp_Pnt(60,40,0),gp_Pnt(60,40,20),gp_Pnt(0,40,20)));   // y=40
    sewing.Add(quad(gp_Pnt(0,0,0),gp_Pnt(20,0,0),gp_Pnt(20,0,20),gp_Pnt(0,0,20)));       // y=0 A
    sewing.Add(quad(gp_Pnt(20,0,0),gp_Pnt(40,0,0),gp_Pnt(40,0,20),gp_Pnt(20,0,20)));     // y=0 B
    sewing.Add(quad(gp_Pnt(40,0,0),gp_Pnt(60,0,0),gp_Pnt(60,0,20),gp_Pnt(40,0,20)));     // y=0 C
    sewing.Perform();
    TopoDS_Compound shape; BRep_Builder builder; builder.MakeCompound(shape); builder.Add(shape,sewing.SewedShape());
    return shape;
}
// Cylinder whose top disk shares one circular boundary edge with the lateral face.
// The exit-side run must keep a single curved original edge reference.
static TopoDS_Shape CurvedExit()
{
    return BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(0,0,0),gp_Dir(0,0,1)),20,30).Shape();
}
static TopoDS_Compound SewingSubrangeExit()
{
    BRep_Builder builder; TopoDS_Compound shape; builder.MakeCompound(shape);
    builder.Add(shape,Sheet(0,0,100,30,20));
    builder.Add(shape,Sheet(20,-20,80,0,20));
    auto wall=[](double first,double last) {
        BRepBuilderAPI_MakePolygon p;
        p.Add(gp_Pnt(first,0,0));p.Add(gp_Pnt(last,0,0));p.Add(gp_Pnt(last,0,20));p.Add(gp_Pnt(first,0,20));p.Close();
        return BRepBuilderAPI_MakeFace(p.Wire()).Face();
    };
    builder.Add(shape,wall(0,20)); builder.Add(shape,wall(80,100));
    return shape;
}
int main(int argc,char** argv)
{
    if (argc!=2) return 1;
    try { NativeExitChecks(); }
    catch (const std::exception& error) { std::cerr<<"FAIL native "<<error.what()<<'\n'; return 3; }
    catch (const Standard_Failure& error) { std::cerr<<"FAIL native OCCT "<<error.GetMessageString()<<'\n'; return 3; }
    if (std::string(argv[1])=="--native-checks") return 0;
    auto write=[&](const char* name,const TopoDS_Shape& shape) {
        STEPControl_Writer writer; writer.Transfer(shape,STEPControl_AsIs);
        const std::string path=std::string(argv[1])+"/"+name+".stp";
        if (writer.Write(path.c_str())!=IFSelect_RetDone) return false;
        std::ifstream input(path);
        std::string text((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>()); input.close();
        const std::string key="REPRESENTATION('',(#11,";
        auto at=text.find(key); if (at==std::string::npos) return false;
        text.replace(at,key.size(),"REPRESENTATION('supplemental geometry',(#11,");
        std::ofstream output(path); output<<text; return output.good();
    };
    BRep_Builder builder;
    // Two coincident sheets at z=0 with opposite orientations: only the +Z one faces a +Z camera.
    TopoDS_Compound coincident; builder.MakeCompound(coincident);
    builder.Add(coincident,Sheet(-30,-30,30,30,0));
    TopoDS_Face reversedSheet=Sheet(-30,-30,30,30,0); reversedSheet.Reverse();
    builder.Add(coincident,reversedSheet);
    // Small front sheet partially covering a larger sheet far behind it.
    TopoDS_Compound stacked; builder.MakeCompound(stacked);
    builder.Add(stacked,Sheet(-30,-30,30,30,0));
    builder.Add(stacked,Sheet(-50,-50,50,50,-50));
    TopoDS_Compound layered; builder.MakeCompound(layered);
    builder.Add(layered,Sheet(-50,-30,50,30,0)); // lower full sheet
    builder.Add(layered,Sheet(-20,-10,20,10,20)); // upper partial occluder
    TopoDS_Compound ring; builder.MakeCompound(ring);
    builder.Add(ring,Sheet(0,0,100,20,0)); builder.Add(ring,Sheet(0,80,100,100,0));
    builder.Add(ring,Sheet(0,20,20,80,0)); builder.Add(ring,Sheet(80,20,100,80,0));
    TopoDS_Compound tangentBack; builder.MakeCompound(tangentBack);
    builder.Add(tangentBack,BRepPrimAPI_MakeSphere(gp_Pnt(0,0,0),20).Shape());
    builder.Add(tangentBack,Sheet(-40,-40,40,40,-30));
    TopoDS_Compound trimmed; builder.MakeCompound(trimmed);
    BRepBuilderAPI_MakeFace top(Rectangle(-50,-30,50,30,20));
    auto hole=Rectangle(-10,-10,10,10,20); hole.Reverse(); top.Add(hole);
    builder.Add(trimmed,top.Face()); builder.Add(trimmed,Sheet(-70,-50,70,50,0));
    // Solid box plus a detached sheet: the sheet has only naked boundary edges,
    // so it must never produce an exit-side candidate.
    TopoDS_Compound exitSides; builder.MakeCompound(exitSides);
    builder.Add(exitSides,BRepPrimAPI_MakeBox(gp_Pnt(0,0,0),60,40,20).Shape());
    builder.Add(exitSides,Sheet(100,0,140,40,0));
    TopoDS_Compound exitDepths; builder.MakeCompound(exitDepths);
    builder.Add(exitDepths,BRepPrimAPI_MakeBox(gp_Pnt(0,0,0),60,40,20).Shape());
    builder.Add(exitDepths,BRepPrimAPI_MakeBox(gp_Pnt(0,0,60),60,40,20).Shape());
    return write("exitSides",exitSides) && write("layers",layered) && write("ring",ring) &&
        write("trimmedVisibility",trimmed) &&
        write("tangentBack",tangentBack) && write("threePanels",ThreePanels()) &&
        write("sphere",BRepPrimAPI_MakeSphere(gp_Pnt(0,0,0),20).Shape()) &&
        write("pickCoincident",coincident) && write("pickStacked",stacked) &&
        write("exitSplit",ExitSplit()) && write("exitSplitThree",ExitSplitThree()) &&
        write("curvedExit",CurvedExit()) && write("sewingSubrangeExit",SewingSubrangeExit()) &&
        write("inclinedCurvedExit",BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(0,0,0),gp_Dir(0,1,1)),20,30).Shape()) &&
        write("exitDepths",exitDepths) ? 0 : 2;
}
