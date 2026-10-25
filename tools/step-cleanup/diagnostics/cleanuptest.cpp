// Cleanup variant harness for Mold.stp
// Applies several "cleanup" strategies, prints metrics, exports each result as STEP.
#include <cstdio>
#include <cmath>
#include <string>
#include <vector>
#include <memory>

#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Solid.hxx>
#include <TopoDS_Shell.hxx>
#include <TopoDS_Compound.hxx>
#include <BRep_Builder.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_ListIteratorOfListOfShape.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <ShapeUpgrade_UnifySameDomain.hxx>
#include <ShapeFix_Shape.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepClass3d_SolidClassifier.hxx>
#include <BRepBndLib.hxx>
#include <Bnd_Box.hxx>
#include <TopAbs_State.hxx>

struct M
{
    int solids = 0, shells = 0, faces = 0, openEdges = 0, orphanFaces = 0;
    double area = 0, vol = 0;
    bool valid = false;
};

static M measure(const TopoDS_Shape& s)
{
    M m;
    TopTools_IndexedMapOfShape sm, shm, fm;
    TopExp::MapShapes(s, TopAbs_SOLID, sm);
    TopExp::MapShapes(s, TopAbs_SHELL, shm);
    TopExp::MapShapes(s, TopAbs_FACE, fm);
    m.solids = sm.Extent(); m.shells = shm.Extent(); m.faces = fm.Extent();

    GProp_GProps p; BRepGProp::SurfaceProperties(s, p); m.area = p.Mass();
    GProp_GProps v; BRepGProp::VolumeProperties(s, v); m.vol = v.Mass();

    BRepCheck_Analyzer an(s, Standard_True); m.valid = an.IsValid();

    TopTools_IndexedDataMapOfShapeListOfShape e2f;
    TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, e2f);
    for (int i = 1; i <= e2f.Extent(); ++i)
        if (e2f(i).Extent() == 1) ++m.openEdges;

    TopTools_IndexedDataMapOfShapeListOfShape f2s;
    TopExp::MapShapesAndAncestors(s, TopAbs_FACE, TopAbs_SOLID, f2s);
    for (int i = 1; i <= f2s.Extent(); ++i)
        if (f2s(i).IsEmpty()) ++m.orphanFaces;
    return m;
}

static TopoDS_Shape makeCompound(const std::vector<TopoDS_Shape>& parts)
{
    TopoDS_Compound c; BRep_Builder b; b.MakeCompound(c);
    for (const auto& p : parts) b.Add(c, p);
    return c;
}

static void report(const char* label, const TopoDS_Shape& s, double baseArea, double baseFaces)
{
    M m = measure(s);
    printf("[%-28s] solids=%2d shells=%2d faces=%3d (dFaces=%+4.0f) area=%12.1f (dA=%+11.1f) vol=%14.1f openEdges=%4d orphanFaces=%3d valid=%s\n",
        label, m.solids, m.shells, m.faces, m.faces - baseFaces, m.area, m.area - baseArea, m.vol,
        m.openEdges, m.orphanFaces, m.valid ? "YES" : "no");
}

static bool writeStep(const TopoDS_Shape& s, const char* path)
{
    STEPControl_Writer w;
    if (w.Transfer(s, STEPControl_AsIs) != IFSelect_RetDone) return false;
    return w.Write(path) == IFSelect_RetDone;
}

int main(int argc, char** argv)
{
    const char* path = argc > 1 ? argv[1] : "D:/0.Development/2.HeatingMold/resource/Mold.stp";
    const char* outDir = argc > 2 ? argv[2] : ".";

    STEPControl_Reader reader;
    if (reader.ReadFile(path) != IFSelect_RetDone) { printf("read failed\n"); return 1; }
    reader.TransferRoots();
    TopoDS_Shape shape = reader.OneShape();
    if (shape.IsNull()) { printf("empty shape\n"); return 1; }

    // ---------------------------------------------------------------- split bodies
    TopTools_IndexedMapOfShape solidMap, shellMap;
    TopExp::MapShapes(shape, TopAbs_SOLID, solidMap);
    TopExp::MapShapes(shape, TopAbs_SHELL, shellMap);

    TopTools_IndexedDataMapOfShapeListOfShape shellSolid;
    TopExp::MapShapesAndAncestors(shape, TopAbs_SHELL, TopAbs_SOLID, shellSolid);

    std::vector<TopoDS_Shape> solids, freeShells;
    for (int i = 1; i <= solidMap.Extent(); ++i) solids.push_back(solidMap(i));
    for (int i = 1; i <= shellMap.Extent(); ++i)
        if (shellSolid.FindFromKey(shellMap(i)).IsEmpty()) freeShells.push_back(shellMap(i));

    printf("=== BASELINE ===\n");
    M base = measure(shape);
    printf("  solids=%d shells=%d faces=%d freeShells=%zu area=%.1f vol=%.1f valid=%s\n",
        base.solids, base.shells, base.faces, freeShells.size(), base.area, base.vol, base.valid ? "YES" : "no");
    for (size_t i = 0; i < freeShells.size(); ++i)
    {
        M fm = measure(freeShells[i]);
        Bnd_Box bb; BRepBndLib::Add(freeShells[i], bb);
        double a1, a2, b1, b2, c1, c2; bb.Get(a1, b1, c1, a2, b2, c2);
        printf("  freeShell[%zu] faces=%d area=%.1f openEdges=%d closed=%s bbox=(%.1f,%.1f,%.1f)-(%.1f,%.1f,%.1f)\n",
            i, fm.faces, fm.area, fm.openEdges,
            (fm.openEdges == 0) ? "YES" : "no", a1, b1, c1, a2, b2, c2);
    }

    // ---------------------------------------------------------------- sewing diagnosis
    printf("\n=== SEWING DIAGNOSIS of free shells (is SURFACE.x actually a closed body?) ===\n");
    for (size_t i = 0; i < freeShells.size(); ++i)
    {
        for (double tol : {1e-4, 1e-3, 1e-2, 5e-2, 1e-1})
        {
            BRepBuilderAPI_Sewing sew(tol);
            sew.Add(freeShells[i]);
            sew.Perform();
            TopoDS_Shape sewn = sew.SewedShape();
            M m = measure(sewn);
            printf("  shell[%zu] tol=%-6g -> faces=%d shells=%d solids=%d openEdges=%d area=%.1f\n",
                i, tol, m.faces, m.shells, m.solids, m.openEdges, m.area);
        }
    }

    // ---------------------------------------------------------------- variants
    printf("\n=== VARIANTS (base area=%.1f, base faces=%d) ===\n", base.area, base.faces);

    // V1: solids only
    TopoDS_Shape v1 = makeCompound(solids);
    report("V1 solids only", v1, base.area, base.faces);
    writeStep(v1, (std::string(outDir) + "/v1_solids_only.stp").c_str());

    // V2: drop only the smallest free shell (the 4-plane bbox artifact)
    {
        std::vector<TopoDS_Shape> keep = solids;
        size_t smallest = 0; M best; best.faces = 1 << 30;
        for (size_t i = 0; i < freeShells.size(); ++i)
        {
            M fm = measure(freeShells[i]);
            if (fm.faces < best.faces) { best = fm; smallest = i; }
        }
        for (size_t i = 0; i < freeShells.size(); ++i) if (i != smallest) keep.push_back(freeShells[i]);
        TopoDS_Shape v2 = makeCompound(keep);
        report("V2 solids + big surface", v2, base.area, base.faces);
        writeStep(v2, (std::string(outDir) + "/v2_drop_bbox_planes.stp").c_str());
    }

    // V3: unify same domain per solid
    {
        std::vector<TopoDS_Shape> parts;
        int before = 0, after = 0;
        for (const auto& s : solids)
        {
            before += measure(s).faces;
            ShapeUpgrade_UnifySameDomain u(s, Standard_True, Standard_True, Standard_False);
            u.Build();
            TopoDS_Shape r = u.Shape();
            after += measure(r).faces;
            parts.push_back(r);
        }
        TopoDS_Shape v3 = makeCompound(parts);
        report("V3 unify(solids)", v3, base.area, base.faces);
        printf("     unify detail: solid faces %d -> %d\n", before, after);
        writeStep(v3, (std::string(outDir) + "/v3_unify_solids.stp").c_str());
    }

    // V4: unify same domain on everything
    {
        ShapeUpgrade_UnifySameDomain u(shape, Standard_True, Standard_True, Standard_False);
        u.Build();
        report("V4 unify(whole)", u.Shape(), base.area, base.faces);
        writeStep(u.Shape(), (std::string(outDir) + "/v4_unify_whole.stp").c_str());
    }

    // V5: V1 + ShapeFix
    {
        ShapeFix_Shape fix(v1);
        fix.Perform();
        report("V5 solids+ShapeFix", fix.Shape(), base.area, base.faces);
        writeStep(fix.Shape(), (std::string(outDir) + "/v5_solids_shapefix.stp").c_str());
    }

    // V6: V3 + ShapeFix
    {
        std::vector<TopoDS_Shape> parts;
        for (const auto& s : solids)
        {
            ShapeUpgrade_UnifySameDomain u(s, Standard_True, Standard_True, Standard_False);
            u.Build();
            parts.push_back(u.Shape());
        }
        TopoDS_Shape c = makeCompound(parts);
        ShapeFix_Shape fix(c);
        fix.Perform();
        report("V6 unify+ShapeFix", fix.Shape(), base.area, base.faces);
        writeStep(fix.Shape(), (std::string(outDir) + "/v6_unify_shapefix.stp").c_str());
    }

    // V7: sew solids together (would merge touching bodies - tests contact topology)
    {
        BRepBuilderAPI_Sewing sew(1e-4);
        for (const auto& s : solids) sew.Add(s);
        sew.Perform();
        report("V7 sew(solids)", sew.SewedShape(), base.area, base.faces);
    }

    // V8: solids + sew free shells onto them
    {
        BRepBuilderAPI_Sewing sew(1e-4);
        for (const auto& s : solids) sew.Add(s);
        for (const auto& s : freeShells) sew.Add(s);
        sew.Perform();
        report("V8 sew(all)", sew.SewedShape(), base.area, base.faces);
    }

    printf("\noutput written to %s\n", outDir);
    return 0;
}
