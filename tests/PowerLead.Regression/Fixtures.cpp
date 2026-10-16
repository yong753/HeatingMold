// Build with the VS x64 native tools and the same OCCT include/lib paths as OccBridge.
// Run with one output-directory argument. No viewer or production model required.
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <STEPControl_Writer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Wire.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Vertex.hxx>
#include <TopoDS_Compound.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <TopExp_Explorer.hxx>
#include <gp_Cylinder.hxx>
#include <gp_Ax3.hxx>
#include <string>
#include <fstream>
#include <iterator>

static TopoDS_Wire Rectangle(double x0, double y0, double x1, double y1, bool slope)
{
    BRepBuilderAPI_MakePolygon p;
    p.Add(gp_Pnt(x0,y0,slope?x0*.5:0)); p.Add(gp_Pnt(x1,y0,slope?x1*.5:0));
    p.Add(gp_Pnt(x1,y1,slope?x1*.5:0)); p.Add(gp_Pnt(x0,y1,slope?x0*.5:0));
    p.Close(); return p.Wire();
}
int main(int argc, char** argv)
{
    if (argc != 2) return 1;
    auto write = [&](const char* name, const TopoDS_Shape& shape) {
        STEPControl_Writer writer; writer.Transfer(shape, STEPControl_AsIs);
        const std::string path = std::string(argv[1])+"/"+name+".stp";
        if (writer.Write(path.c_str()) != IFSelect_RetDone) return false;
        // Force the application's existing supplemental-axes loader to preserve
        // fixture world coordinates instead of PCA-aligning the sloped sheet.
        std::ifstream input(path);
        std::string text((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
        input.close();
        const std::string key = "REPRESENTATION('',(#11,";
        const size_t at = text.find(key);
        if (at == std::string::npos) return false;
        text.replace(at,key.size(),"REPRESENTATION('supplemental geometry',(#11,");
        std::ofstream output(path); output << text;
        return output.good();
    };
    BRepBuilderAPI_MakeFace sloped(Rectangle(0,0,100,60,true));
    BRepBuilderAPI_MakeFace hole(Rectangle(0,0,100,60,false));
    TopoDS_Wire inner = Rectangle(40,20,60,40,false); inner.Reverse(); hole.Add(inner);
    BRepBuilderAPI_MakeFace curved(gp_Cylinder(gp_Ax3(gp_Pnt(50,0,-100),gp_Dir(0,1,0),gp_Dir(0,0,1)),100),-.5,.5,0,60);
    BRep_Builder builder;
    TopoDS_Compound adjacent; builder.MakeCompound(adjacent);
    builder.Add(adjacent,BRepBuilderAPI_MakeFace(Rectangle(0,0,50,60,true)).Face());
    builder.Add(adjacent,BRepBuilderAPI_MakeFace(Rectangle(50,0,100,60,true)).Face());
    // U-shaped footprint: selected left arm, unselected bottom and right arms.
    TopoDS_Compound concave; builder.MakeCompound(concave);
    builder.Add(concave,BRepBuilderAPI_MakeFace(Rectangle(0,0,50,60,true)).Face());
    builder.Add(concave,BRepBuilderAPI_MakeFace(Rectangle(50,0,80,10,true)).Face());
    builder.Add(concave,BRepBuilderAPI_MakeFace(Rectangle(80,0,100,60,true)).Face());
    TopoDS_Compound narrow; builder.MakeCompound(narrow);
    builder.Add(narrow,BRepBuilderAPI_MakeFace(Rectangle(0,0,50,60,true)).Face());
    builder.Add(narrow,BRepBuilderAPI_MakeFace(Rectangle(80,0,80.001,60,true)).Face());
    TopoDS_Compound wall; builder.MakeCompound(wall);
    builder.Add(wall,BRepBuilderAPI_MakeFace(Rectangle(0,0,50,60,true)).Face());
    BRepBuilderAPI_MakePolygon vertical;
    vertical.Add(gp_Pnt(80,20,0)); vertical.Add(gp_Pnt(80,40,0));
    vertical.Add(gp_Pnt(80,40,150)); vertical.Add(gp_Pnt(80,20,150)); vertical.Close();
    builder.Add(wall,BRepBuilderAPI_MakeFace(vertical.Wire()).Face());
    // Import-like disagreement between STEP vertex coordinates and the exact
    // edge/surface curves. The STEP reader must retain CAD tolerances for it.
    TopoDS_Face noisy = BRepBuilderAPI_MakeFace(Rectangle(0,0,100,60,true)).Face();
    for (TopExp_Explorer ex(noisy,TopAbs_VERTEX); ex.More(); ex.Next()) {
        const TopoDS_Vertex vertex = TopoDS::Vertex(ex.Current());
        // Each vertex occurs twice in the wire traversal; only move it once.
        const gp_Pnt p = BRep_Tool::Pnt(vertex);
        if (std::abs(p.Z()-p.X()*.5) < 1e-7)
            builder.UpdateVertex(vertex,p.Translated(gp_Vec(0,0,.004)),.005);
    }
    TopoDS_Compound noisyBlocked; builder.MakeCompound(noisyBlocked);
    builder.Add(noisyBlocked,noisy);
    builder.Add(noisyBlocked,BRepBuilderAPI_MakeFace(Rectangle(100.003,0,100.004,60,true)).Face());
    return write("slope",sloped.Face()) && write("hole",hole.Face()) && write("curve",curved.Face()) && write("adjacent",adjacent) &&
        write("concave",concave) && write("narrow",narrow) && write("wall",wall) &&
        write("noisy",noisy) && write("noisyBlocked",noisyBlocked) ? 0 : 2;
}
