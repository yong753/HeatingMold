#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRep_Builder.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Wire.hxx>
#include <STEPControl_Writer.hxx>
#include <fstream>
#include <iterator>
#include <string>

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
int main(int argc,char** argv)
{
    if (argc!=2) return 1;
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
    return write("exitSides",exitSides) && write("layers",layered) && write("ring",ring) &&
        write("trimmedVisibility",trimmed) &&
        write("tangentBack",tangentBack) && write("threePanels",ThreePanels()) &&
        write("sphere",BRepPrimAPI_MakeSphere(gp_Pnt(0,0,0),20).Shape()) &&
        write("pickCoincident",coincident) && write("pickStacked",stacked) &&
        write("exitSplit",ExitSplit()) && write("exitSplitThree",ExitSplitThree()) ? 0 : 2;
}
