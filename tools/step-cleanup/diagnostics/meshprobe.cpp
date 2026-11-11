// Reproduces the working-context meshing step of OccBridge:
// BRepTools::Clean -> BRepMesh_IncrementalMesh(shape, 0.05, false, 0.1, true)
// and reports every face without triangulation.
#include <STEPControl_Reader.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Face.hxx>
#include <TopExp_Explorer.hxx>
#include <BRep_Tool.hxx>
#include <Poly_Triangulation.hxx>
#include <TopLoc_Location.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepTools.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <BRepBndLib.hxx>
#include <Bnd_Box.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <IntCurvesFace_ShapeIntersector.hxx>
#include <gp_Lin.hxx>
#include <Precision.hxx>
#include <Standard_Failure.hxx>
#include <iostream>
#include <iomanip>
#include <vector>

static const char* SurfName(GeomAbs_SurfaceType t)
{
    switch (t)
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

int main(int argc, char** argv)
{
    if (argc < 2) { std::cerr << "usage: meshprobe <step-file>\n"; return 2; }

    STEPControl_Reader reader;
    if (reader.ReadFile(argv[1]) != IFSelect_RetDone) { std::cerr << "read failed\n"; return 3; }
    reader.TransferRoots();
    TopoDS_Shape shape = reader.OneShape();
    if (shape.IsNull()) { std::cerr << "null shape\n"; return 3; }

    TopoDS_Shape work = shape;
    const double deflections[] = { 1.0, 0.5, 0.2, 0.1, 0.05 };
    for (double d : deflections)
    {
        BRepTools::Clean(work);
        BRepMesh_IncrementalMesh mesh(work, d, Standard_False, 0.1, Standard_True);
        int faces = 0, nulls = 0;
        long long tris = 0;
        for (TopExp_Explorer e(work, TopAbs_FACE); e.More(); e.Next())
        {
            TopoDS_Face f = TopoDS::Face(e.Current());
            faces++;
            TopLoc_Location loc;
            Handle(Poly_Triangulation) tr = BRep_Tool::Triangulation(f, loc);
            if (tr.IsNull())
            {
                nulls++;
                GProp_GProps props;
                BRepGProp::SurfaceProperties(f, props);
                Bnd_Box box;
                BRepBndLib::Add(f, box);
                double x1, y1, z1, x2, y2, z2;
                box.Get(x1, y1, z1, x2, y2, z2);
                BRepAdaptor_Surface surf(f);
                std::cout << "  NULL face #" << (faces - 1)
                          << " type=" << SurfName(surf.GetType())
                          << " area=" << props.Mass()
                          << " bbox=" << (x2 - x1) << "x" << (y2 - y1) << "x" << (z2 - z1) << "\n";
            }
            else
            {
                tris += tr->NbTriangles();
            }
        }
        std::cout << "deflection=" << d << " done=" << (mesh.IsDone() ? 1 : 0)
                  << " faces=" << faces << " null=" << nulls << " triangles=" << tris << "\n";
        if (nulls == 0) break;
    }

    std::cout << "intersector: ";
    try
    {
        IntCurvesFace_ShapeIntersector inter;
        inter.Load(work, Precision::Confusion());
        inter.Perform(gp_Lin(gp_Pnt(0, 0, 5000), gp_Dir(0, 0, -1)), 0.0, 1.0e6);
        std::cout << "load/perform ok, hits=" << inter.NbPnt() << "\n";
    }
    catch (const Standard_Failure& ex)
    {
        std::cout << "failed: " << (ex.GetMessageString() ? ex.GetMessageString() : "") << "\n";
    }
    return 0;
}
