// Thicken an open surface shell into a solid body.
// usage: thicken <step-file> <thickness-mm> [out-step]
//  - loads the file with the same STEPControl_Reader pipeline
//  - picks the open (non-solid) shells
//  - splits each shell into connected face components
//  - decides the offset side by testing which side of each face lies inside the mold solids
//  - tries several offset configurations until a closed solid appears
//  - writes the first successful solid to out-step
#include <cstdio>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <map>
#include <functional>

#include <TopoDS.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Shell.hxx>
#include <TopoDS_Solid.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Edge.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_ListOfShape.hxx>
#include <TopTools_ListIteratorOfListOfShape.hxx>

#include <BRep_Tool.hxx>
#include <BRepTools.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <BRepBndLib.hxx>
#include <Bnd_Box.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepCheck_Result.hxx>
#include <BRepCheck_ListOfStatus.hxx>
#include <BRepClass3d_SolidClassifier.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_MakeSolid.hxx>
#include <ShapeFix_Shell.hxx>
#include <ShapeFix_Shape.hxx>
#include <ShapeFix_Solid.hxx>
#include <ShapeFix_Face.hxx>
#include <ShapeUpgrade_UnifySameDomain.hxx>
#include <ShapeAnalysis_Shell.hxx>
#include <BRepOffsetAPI_MakeThickSolid.hxx>
#include <BRepOffsetAPI_MakeOffsetShape.hxx>
#include <BRepOffset_MakeOffset.hxx>
#include <BRepOffset_Mode.hxx>
#include <BRepOffset_Error.hxx>
#include <GeomAbs_JoinType.hxx>

#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <Interface_Static.hxx>

#include <gp_Pnt.hxx>
#include <gp_Vec.hxx>

static int CountFreeEdges(const TopoDS_Shape& s)
{
    TopTools_IndexedDataMapOfShapeListOfShape map;
    TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, map);
    int n = 0;
    for (Standard_Integer i = 1; i <= map.Extent(); ++i)
        if (map.FindFromIndex(i).Extent() == 1) ++n;
    return n;
}

static int CountShape(const TopoDS_Shape& s, TopAbs_ShapeEnum type);
static double VolumeOf(const TopoDS_Shape& s);

static TopoDS_Shape RepairBody(const TopoDS_Shape& in, const char* tag)
{
    TopoDS_Shape cur = in;
    int n = CountFreeEdges(cur);
    const double tols[] = { 1.0e-4, 1.0e-3, 1.0e-2, 1.0e-1 };
    for (double tol : tols)
    {
        BRepBuilderAPI_Sewing sew(tol);
        sew.Add(cur);
        sew.Perform();
        const TopoDS_Shape sewn = sew.SewedShape();
        TopoDS_Shell shell;
        for (TopExp_Explorer sx(sewn, TopAbs_SHELL); sx.More(); sx.Next())
        {
            const TopoDS_Shell& sh = TopoDS::Shell(sx.Current());
            if (CountShape(sh, TopAbs_FACE) > 0) { shell = sh; break; }
        }
        if (shell.IsNull()) continue;
        BRepBuilderAPI_MakeSolid ms(shell);
        if (!ms.IsDone()) continue;
        const TopoDS_Solid sol = ms.Solid();
        BRepCheck_Analyzer an(sol);
        const int f = CountFreeEdges(sol);
        printf("%s repair tol=%.4f: free=%d valid=%s volume=%.1f\n", tag, tol, f,
               an.IsValid() ? "yes" : "no", VolumeOf(sol));
        if (f < n && an.IsValid() && VolumeOf(sol) > 0)
        {
            cur = sol;
            n = f;
            if (n == 0) break;
        }
    }
    const double precs[] = { 1.0e-5, 1.0e-4, 1.0e-3, 1.0e-2 };
    for (double pr : precs)
    {
        ShapeFix_Shape sfs(cur);
        sfs.SetPrecision(pr);
        sfs.SetMaxTolerance(0.5);
        sfs.Perform();
        const TopoDS_Shape fixed = sfs.Shape();
        if (fixed.IsNull() || CountShape(fixed, TopAbs_SOLID) == 0 || VolumeOf(fixed) <= 0) continue;
        BRepCheck_Analyzer af(fixed);
        const int f = CountFreeEdges(fixed);
        printf("%s shapefix prec=%.5f: free=%d valid=%s volume=%.1f\n", tag, pr, f,
               af.IsValid() ? "yes" : "no", VolumeOf(fixed));
        if (f < CountFreeEdges(cur) || (f == 0 && af.IsValid()))
        {
            cur = fixed;
            if (f == 0) break;
        }
    }
    if (CountFreeEdges(cur) > 0)
    {
        for (TopExp_Explorer sx(cur, TopAbs_SOLID); sx.More(); sx.Next())
        {
            ShapeFix_Solid sfsd(TopoDS::Solid(sx.Current()));
            sfsd.SetPrecision(1.0e-5);
            if (!sfsd.Perform()) continue;
            const TopoDS_Shape s2 = sfsd.Solid();
            if (s2.IsNull() || VolumeOf(s2) <= 0) continue;
            BRepCheck_Analyzer a2(s2);
            printf("%s solidfix: free=%d valid=%s volume=%.1f\n", tag, CountFreeEdges(s2),
                   a2.IsValid() ? "yes" : "no", VolumeOf(s2));
            if (CountFreeEdges(s2) < CountFreeEdges(cur)) cur = s2;
        }
    }
    return cur;
}

static void DumpInvalid(const char* tag, const TopoDS_Shape& s)
{
    BRepCheck_Analyzer an(s);
    printf("%s: valid=%s faces=%d edges=%d\n", tag, an.IsValid() ? "yes" : "no",
           CountShape(s, TopAbs_FACE), CountShape(s, TopAbs_EDGE));
    if (an.IsValid()) return;
    int shownF = 0, shownE = 0;
    for (TopExp_Explorer ex(s, TopAbs_FACE); ex.More(); ex.Next())
    {
        const TopoDS_Shape& f = ex.Current();
        if (an.IsValid(f)) continue;
        if (shownF++ >= 12) continue;
        Handle(BRepCheck_Result) r = an.Result(f);
        printf("  invalid face: area=%.4f status=", [&]() {
            GProp_GProps sp; BRepGProp::SurfaceProperties(f, sp); return sp.Mass(); }());
        if (!r.IsNull())
            for (BRepCheck_Status st : r->Status()) printf("%d ", (int)st);
        printf("\n");
    }
    for (TopExp_Explorer ex(s, TopAbs_EDGE); ex.More(); ex.Next())
    {
        const TopoDS_Shape& e = ex.Current();
        if (an.IsValid(e)) continue;
        if (shownE++ >= 12) continue;
        GProp_GProps lp; BRepGProp::LinearProperties(e, lp);
        Handle(BRepCheck_Result) r = an.Result(e);
        printf("  invalid edge: len=%.6f status=", lp.Mass());
        if (!r.IsNull())
            for (BRepCheck_Status st : r->Status()) printf("%d ", (int)st);
        printf("\n");
    }
    printf("  totals: invalid faces=%d invalid edges=%d\n", shownF, shownE);
}

static int CountShape(const TopoDS_Shape& s, TopAbs_ShapeEnum type)
{
    int n = 0;
    for (TopExp_Explorer ex(s, type); ex.More(); ex.Next()) ++n;
    return n;
}

static double VolumeOf(const TopoDS_Shape& s)
{
    if (s.IsNull()) return 0.0;
    GProp_GProps vp;
    BRepGProp::VolumeProperties(s, vp, Standard_True, Standard_False);
    return vp.Mass();
}

static bool IsSolid(const TopoDS_Shape& s)
{
    return !s.IsNull() && CountShape(s, TopAbs_SOLID) > 0 && VolumeOf(s) > 1e-6;
}

static void Report(const char* tag, const TopoDS_Shape& s)
{
    if (s.IsNull()) { printf("%s: <null>\n", tag); return; }
    GProp_GProps vp, sp;
    BRepGProp::VolumeProperties(s, vp, Standard_True, Standard_False);
    BRepGProp::SurfaceProperties(s, sp, Standard_True, Standard_False);
    Bnd_Box bb; BRepBndLib::Add(s, bb);
    double x1, y1, z1, x2, y2, z2; bb.Get(x1, y1, z1, x2, y2, z2);
    const bool valid = BRepCheck_Analyzer(s).IsValid();
    printf("%s: solids=%d shells=%d faces=%d volume=%.3f area=%.3f freeEdges=%d valid=%s\n",
           tag, CountShape(s, TopAbs_SOLID), CountShape(s, TopAbs_SHELL), CountShape(s, TopAbs_FACE),
           vp.Mass(), sp.Mass(), CountFreeEdges(s), valid ? "yes" : "no");
    printf("%s: bbox=(%.3f, %.3f, %.3f) - (%.3f, %.3f, %.3f)\n", tag, x1, y1, z1, x2, y2, z2);
}

// split a shell into face-connected components
static std::vector<TopoDS_Shell> SplitComponents(const TopoDS_Shell& shell)
{
    TopTools_IndexedMapOfShape faces;
    TopExp::MapShapes(shell, TopAbs_FACE, faces);
    const int n = faces.Extent();
    std::vector<int> parent(n + 1);
    for (int i = 1; i <= n; ++i) parent[i] = i;
    std::function<int(int)> find = [&](int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };

    TopTools_IndexedDataMapOfShapeListOfShape edgeFaces;
    TopExp::MapShapesAndAncestors(shell, TopAbs_EDGE, TopAbs_FACE, edgeFaces);
    for (Standard_Integer i = 1; i <= edgeFaces.Extent(); ++i)
    {
        const TopTools_ListOfShape& lf = edgeFaces.FindFromIndex(i);
        if (lf.Extent() < 2) continue;
        TopTools_ListIteratorOfListOfShape it(lf);
        const int first = faces.FindIndex(it.Value());
        for (it.Next(); it.More(); it.Next())
        {
            const int other = faces.FindIndex(it.Value());
            if (first > 0 && other > 0) parent[find(other)] = find(first);
        }
    }

    std::map<int, TopTools_IndexedMapOfShape> groups;
    for (int i = 1; i <= n; ++i) groups[find(i)].Add(faces.FindKey(i));

    std::vector<TopoDS_Shell> out;
    for (auto& kv : groups)
    {
        BRepBuilderAPI_Sewing sew(1.0e-4);
        for (Standard_Integer i = 1; i <= kv.second.Extent(); ++i) sew.Add(kv.second.FindKey(i));
        sew.Perform();
        TopoDS_Shape sewn = sew.SewedShape();
        int best = 0;
        TopoDS_Shell pick;
        for (TopExp_Explorer ex(sewn, TopAbs_SHELL); ex.More(); ex.Next())
        {
            const int c = CountShape(ex.Current(), TopAbs_FACE);
            if (c > best) { best = c; pick = TopoDS::Shell(ex.Current()); }
        }
        if (best > 0) out.push_back(pick);
    }
    return out;
}

struct Probe
{
    BRepClass3d_SolidClassifier* cls;
};

static void EdgeStats(const char* tag, const TopoDS_Shape& s)
{
    TopTools_IndexedDataMapOfShapeListOfShape map;
    TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, map);
    int freeE = 0, man = 0, nonman = 0, seamE = 0;
    double freeLen = 0.0, nonSeamLen = 0.0;
    for (Standard_Integer i = 1; i <= map.Extent(); ++i)
    {
        const int n = map.FindFromIndex(i).Extent();
        if (n == 1)
        {
            ++freeE;
            const TopoDS_Edge& e = TopoDS::Edge(map.FindKey(i));
            GProp_GProps lp; BRepGProp::LinearProperties(e, lp);
            freeLen += lp.Mass();
            bool seam = false;
            for (TopTools_ListIteratorOfListOfShape it(map.FindFromIndex(i)); it.More(); it.Next())
                if (BRep_Tool::IsClosed(e, TopoDS::Face(it.Value()))) seam = true;
            if (seam) ++seamE; else nonSeamLen += lp.Mass();
            if (std::strcmp(tag, "solid") == 0)
            {
                printf("  free edge %d: len=%.6f faces=", i, lp.Mass());
                for (TopTools_ListIteratorOfListOfShape it(map.FindFromIndex(i)); it.More(); it.Next())
                {
                    GProp_GProps fp; BRepGProp::SurfaceProperties(it.Value(), fp);
                    printf("[area=%.6f type=%d] ", fp.Mass(),
                           (int)BRepAdaptor_Surface(TopoDS::Face(it.Value()), Standard_False).GetType());
                }
                printf("\n");
            }
        }
        else if (n == 2) ++man; else ++nonman;
    }
    ShapeAnalysis_Shell sas;
    sas.LoadShells(s);
    const Standard_Boolean oriented = sas.CheckOrientedShells(s, Standard_False);
    printf("%s: edges=%d free=%d (seam=%d len=%.4f nonSeamLen=%.4f) manifold=%d nonmanifold=%d oriented=%s badEdges=%d freeEdgesSAS=%d\n",
           tag, map.Extent(), freeE, seamE, freeLen, nonSeamLen, man, nonman, oriented ? "yes" : "no",
           CountShape(sas.BadEdges(), TopAbs_EDGE), CountShape(sas.FreeEdges(), TopAbs_EDGE));
}

static bool OffsetShell(const TopoDS_Shell& shell, double offset, bool selfInter, GeomAbs_JoinType join,
                        int api, int mode, TopoDS_Shape& out, const char* label)
{
    if (api == 0)
    {
        TopTools_ListOfShape closing;
        BRepOffsetAPI_MakeThickSolid mk;
        try
        {
            mk.MakeThickSolidByJoin(shell, closing, offset, 1.0e-3, BRepOffset_Skin,
                                    Standard_False, selfInter ? Standard_True : Standard_False,
                                    join, Standard_False);
        }
        catch (const Standard_Failure& e)
        {
            printf("  %s: exception %s\n", label, e.GetMessageString());
            return false;
        }
        if (!mk.IsDone())
        {
            printf("  %s: done=no\n", label);
            return false;
        }
        out = mk.Shape();
    }
    else if (api == 1)
    {
        BRepOffset_MakeOffset mk;
        try
        {
            mk.Initialize(shell, offset, 1.0e-3, (BRepOffset_Mode)mode,
                          Standard_False, selfInter ? Standard_True : Standard_False,
                          join, Standard_True, Standard_False);
            mk.MakeThickSolid();
        }
        catch (const Standard_Failure& e)
        {
            printf("  %s: exception %s\n", label, e.GetMessageString());
            return false;
        }
        if (!mk.IsDone())
        {
            printf("  %s: done=no error=%d\n", label, (int)mk.Error());
            return false;
        }
        out = mk.Shape();
    }
    else if (api == 3)
    {
        BRepOffsetAPI_MakeThickSolid mk;
        try
        {
            mk.MakeThickSolidBySimple(shell, offset);
        }
        catch (const Standard_Failure& e)
        {
            printf("  %s: exception %s\n", label, e.GetMessageString());
            return false;
        }
        if (!mk.IsDone())
        {
            printf("  %s: done=no (simple)\n", label);
            return false;
        }
        out = mk.Shape();
    }
    else
    {
        BRepOffsetAPI_MakeOffsetShape mk;
        try
        {
            mk.PerformBySimple(shell, offset);
        }
        catch (const Standard_Failure& e)
        {
            printf("  %s: exception %s\n", label, e.GetMessageString());
            return false;
        }
        if (!mk.IsDone())
        {
            printf("  %s: done=no\n", label);
            return false;
        }
        out = mk.Shape();
    }
    if (out.IsNull())
    {
        printf("  %s: null shape\n", label);
        return false;
    }
    printf("  %s: done=yes solids=%d faces=%d volume=%.3f freeEdges=%d\n", label,
           CountShape(out, TopAbs_SOLID), CountShape(out, TopAbs_FACE), VolumeOf(out), CountFreeEdges(out));
    return true;
}

static bool OffsetOneFaceEx(const TopoDS_Face& f, double offset, double tol, GeomAbs_JoinType join)
{
    try
    {
        BRepOffsetAPI_MakeOffsetShape mk;
        mk.PerformByJoin(f, offset, tol, BRepOffset_Skin, Standard_False, Standard_False, join);
        return mk.IsDone() && !mk.Shape().IsNull();
    }
    catch (const Standard_Failure&)
    {
        return false;
    }
}

// count shared edges whose two adjacent faces use the same edge orientation
static int OrientationMismatch(const TopoDS_Shape& s)
{
    TopTools_IndexedDataMapOfShapeListOfShape map;
    TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, map);
    int bad = 0;
    for (Standard_Integer i = 1; i <= map.Extent(); ++i)
    {
        const TopTools_ListOfShape& lf = map.FindFromIndex(i);
        if (lf.Extent() != 2) continue;
        const TopoDS_Shape& e = map.FindKey(i);
        TopAbs_Orientation ori[2];
        int k = 0;
        for (TopTools_ListIteratorOfListOfShape it(lf); it.More(); it.Next(), ++k)
        {
            ori[k] = TopAbs_EXTERNAL;
            for (TopExp_Explorer fe(it.Value(), TopAbs_EDGE); fe.More(); fe.Next())
                if (fe.Current().IsSame(e)) { ori[k] = fe.Current().Orientation(); break; }
        }
        if (ori[0] == ori[1]) ++bad;
    }
    return bad;
}

// Try offsetting each face on its own, to locate faces the offsetter rejects.
static void ProbeFaces(const TopoDS_Shell& comp, double t)
{
    int idx = 0, ok = 0, fail = 0, fixedOk = 0;
    for (TopExp_Explorer ex(comp, TopAbs_FACE); ex.More(); ex.Next(), ++idx)
    {
        const TopoDS_Face& f = TopoDS::Face(ex.Current());
        if (OffsetOneFaceEx(f, -t, 1.0e-3, GeomAbs_Arc)) { ++ok; continue; }
        ++fail;
        GProp_GProps p; BRepGProp::SurfaceProperties(f, p, Standard_True, Standard_False);
        BRepAdaptor_Surface sa(f, Standard_False);
        printf("  face %d (area=%.3f type=%d wires=%d edges=%d): offset failed",
               idx, p.Mass(), (int)sa.GetType(), CountShape(f, TopAbs_WIRE), CountShape(f, TopAbs_EDGE));
        const double signs[2] = { -1.0, 1.0 };
        const double tols[3] = { 1.0e-3, 1.0e-5, 1.0e-9 };
        for (int si = 0; si < 2; ++si)
            for (int ti = 0; ti < 3; ++ti)
                for (int ji = 0; ji < 2; ++ji)
                    if (OffsetOneFaceEx(f, signs[si] * t, tols[ti],
                                        ji ? GeomAbs_Intersection : GeomAbs_Arc))
                        printf(" [works: sign=%+.0f tol=%.0e join=%s]", signs[si], tols[ti],
                               ji ? "Int" : "Arc");
        printf("\n");
    }
    printf("per-face offset: ok=%d fail=%d (repaired=%d) of %d\n", ok, fail, fixedOk, idx);
    printf("orientation mismatches on shell: %d\n", OrientationMismatch(comp));
}

static double InsideFraction(const TopoDS_Shape& s, const std::vector<BRepClass3d_SolidClassifier*>& cls)
{
    int in = 0, out = 0;
    for (TopExp_Explorer ex(s, TopAbs_FACE); ex.More(); ex.Next())
    {
        const TopoDS_Face& f = TopoDS::Face(ex.Current());
        double u1, u2, v1, v2;
        BRepTools::UVBounds(f, u1, u2, v1, v2);
        BRepAdaptor_Surface sa(f, Standard_False);
        for (int i = 1; i <= 3; ++i)
            for (int j = 1; j <= 3; ++j)
            {
                const double u = u1 + (u2 - u1) * i / 4.0;
                const double v = v1 + (v2 - v1) * j / 4.0;
                gp_Pnt p; gp_Vec d1, d2;
                try { sa.D1(u, v, p, d1, d2); } catch (const Standard_Failure&) { continue; }
                bool isIn = false;
                for (BRepClass3d_SolidClassifier* c : cls)
                {
                    c->Perform(p, 1.0e-6);
                    if (c->State() == TopAbs_IN) { isIn = true; break; }
                }
                if (isIn) ++in; else ++out;
            }
    }
    return (in + out) > 0 ? (double)in / (double)(in + out) : 0.0;
}

int main(int argc, char** argv)
{
    Interface_Static::SetCVal("xstep.cascade.unit", "MM");
    Interface_Static::SetCVal("write.step.unit", "MM");

    if (argc < 3)
    {
        printf("usage: thicken <step-file> <thickness-mm> [out-step]\n");
        return 2;
    }
    const char* inPath = argv[1];
    const double thick = std::atof(argv[2]);
    if (!(thick > 0.0)) { printf("bad thickness\n"); return 2; }
    const bool censusOnly = (argc >= 4 && std::strcmp(argv[3], "--census") == 0);
    const bool probeOnly = (argc >= 4 && std::strcmp(argv[3], "--probe") == 0);
    const bool checkOnly = (argc >= 4 && std::strcmp(argv[3], "--check") == 0);

    STEPControl_Reader reader;
    if (reader.ReadFile(inPath) != IFSelect_RetDone)
    {
        printf("read failed: %s\n", inPath);
        return 1;
    }
    reader.TransferRoots();
    TopoDS_Shape all = reader.OneShape();
    if (all.IsNull()) { printf("empty shape\n"); return 1; }
    Report("loaded", all);
    if (checkOnly) { DumpInvalid("loaded", all); return 0; }

    std::vector<TopoDS_Solid> solids;
    for (TopExp_Explorer ex(all, TopAbs_SOLID); ex.More(); ex.Next())
        solids.push_back(TopoDS::Solid(ex.Current()));
    TopTools_IndexedMapOfShape solidShells;
    for (const TopoDS_Solid& s : solids)
        for (TopExp_Explorer ex(s, TopAbs_SHELL); ex.More(); ex.Next())
            solidShells.Add(ex.Current());
    printf("mold solids=%d\n", (int)solids.size());

    std::vector<TopoDS_Shell> freeShells;
    for (TopExp_Explorer ex(all, TopAbs_SHELL); ex.More(); ex.Next())
        if (solidShells.FindIndex(ex.Current()) == 0)
            freeShells.push_back(TopoDS::Shell(ex.Current()));
    printf("free shells=%d\n", (int)freeShells.size());

    std::vector<BRepClass3d_SolidClassifier*> classifiers;
    for (const TopoDS_Solid& s : solids) classifiers.push_back(new BRepClass3d_SolidClassifier(s));
    auto insideAny = [&](const gp_Pnt& p) {
        for (BRepClass3d_SolidClassifier* c : classifiers)
        {
            c->Perform(p, 1.0e-6);
            if (c->State() == TopAbs_IN) return true;
        }
        return false;
    };

    const double thicknesses[3] = { thick, thick * 0.5, thick * 0.25 };
    bool wrote = false;
    std::string outPath = (argc >= 4) ? std::string(argv[3]) : (std::string(inPath) + "_thick.stp");

    for (size_t si = 0; si < freeShells.size(); ++si)
    {
        if (argc >= 5 && std::atoi(argv[4]) > 0 && (int)si + 1 != std::atoi(argv[4])) continue;
        printf("\n=== free shell %zu ===\n", si + 1);
        TopoDS_Shell raw = freeShells[si];
        Report("shell", raw);
        {
            ShapeFix_Shell fix;
            fix.Init(raw);
            fix.Perform();
            if (!fix.Shell().IsNull()) raw = fix.Shell();
        }
        EdgeStats("shell-fixed", raw);

        std::vector<TopoDS_Shell> comps = SplitComponents(raw);
        printf("connected components=%d\n", (int)comps.size());

        size_t ci = 0;
        for (const TopoDS_Shell& comp : comps)
        {
            ++ci;
            printf("\n-- component %zu (faces=%d) --\n", ci, CountShape(comp, TopAbs_FACE));
            Report("comp", comp);
            EdgeStats("comp", comp);
            TopoDS_Shell work = comp;
            {
                BRepBuilderAPI_Sewing sew(1.0e-2);
                sew.Add(work);
                sew.Perform();
                int best = -1;
                TopoDS_Shell pick;
                for (TopExp_Explorer sx(sew.SewedShape(), TopAbs_SHELL); sx.More(); sx.Next())
                {
                    const int c = CountShape(sx.Current(), TopAbs_FACE);
                    if (c > best) { best = c; pick = TopoDS::Shell(sx.Current()); }
                }
                if (best > 0)
                {
                    printf("presew: faces=%d free=%d (raw free=%d)\n", best, CountFreeEdges(pick), CountFreeEdges(work));
                    if (CountFreeEdges(pick) < CountFreeEdges(work)) work = pick;
                }
            }
            if (censusOnly) continue;
            if (probeOnly)
            {
                ProbeFaces(work, thick);
                ShapeUpgrade_UnifySameDomain uni(work, Standard_True, Standard_True, Standard_False);
                uni.Build();
                TopoDS_Shape us = uni.Shape();
                Report("unified", us);
                for (TopExp_Explorer sx(us, TopAbs_SHELL); sx.More(); sx.Next())
                {
                    const TopoDS_Shell& sh = TopoDS::Shell(sx.Current());
                    if (CountShape(sh, TopAbs_FACE) < 2) continue;
                    EdgeStats("unified-shell", sh);
                    ProbeFaces(sh, thick);
                    break;
                }
                continue;
            }

            int plusIn = 0, minusIn = 0, tested = 0;
            for (TopExp_Explorer ex(work, TopAbs_FACE); ex.More(); ex.Next())
            {
                const TopoDS_Face& f = TopoDS::Face(ex.Current());
                double u1, u2, v1, v2;
                BRepTools::UVBounds(f, u1, u2, v1, v2);
                BRepAdaptor_Surface sa(f, Standard_False);
                gp_Pnt p; gp_Vec du, dv;
                sa.D1(0.5 * (u1 + u2), 0.5 * (v1 + v2), p, du, dv);
                gp_Vec n = du.Crossed(dv);
                if (n.Magnitude() < 1e-12) continue;
                n.Normalize();
                if (f.Orientation() == TopAbs_REVERSED) n.Reverse();
                ++tested;
                if (insideAny(p.Translated(n * thick))) ++plusIn;
                if (insideAny(p.Translated(n * -thick))) ++minusIn;
            }
            const double dir = (plusIn >= minusIn) ? 1.0 : -1.0;
            printf("side probe: tested=%d +n=%d -n=%d -> %s\n", tested, plusIn, minusIn,
                   dir > 0 ? "+normal" : "-normal");

            for (double t : thicknesses)
            {
                std::vector<std::pair<double, TopoDS_Shape>> cands;
                for (int sgn = 0; sgn < 2; ++sgn)
                {
                    const double offset = ((sgn == 0) ? dir : -dir) * t;
                    bool hit = false;
                    for (int variant = 0; variant < 4 && !hit; ++variant)
                    {
                        for (int api = 3; api >= 0 && !hit; --api)
                        {
                            for (int mode = 0; mode < 3 && !hit; ++mode)
                            {
                                if ((api == 0 || api == 3) && mode != 0) continue;
                                const bool selfInter = (variant & 1) != 0;
                                const GeomAbs_JoinType join = (variant & 2) ? GeomAbs_Intersection : GeomAbs_Arc;
                                const char* apiName = (api == 0) ? "thicksolid" : (api == 1 ? "offset+thick" : (api == 2 ? "simple" : "thicksimple"));
                                const char* modeName = (mode == 0) ? "Skin" : (mode == 1 ? "Pipe" : "RectoVerso");
                                char label[200];
                                std::snprintf(label, sizeof(label), "t=%.2f off=%+.2f self=%d join=%s api=%s mode=%s",
                                              t, offset, selfInter ? 1 : 0,
                                              (variant & 2) ? "Intersection" : "Arc", apiName, modeName);
                                TopoDS_Shape res;
                                if (!OffsetShell(work, offset, selfInter, join, api, mode, res, label)) continue;
                                const double rawVol = VolumeOf(res);
                                if (CountShape(res, TopAbs_SOLID) == 0 || std::fabs(rawVol) < 1e-6) continue;
                                hit = true;
                                TopoDS_Shape solid = (rawVol < 0.0) ? res.Reversed() : res;
                                const double frac = InsideFraction(solid, classifiers);
                                printf("  candidate %s: volume=%.1f faces=%d inside=%.3f\n",
                                       label, VolumeOf(solid), CountShape(solid, TopAbs_FACE), frac);
                                cands.push_back(std::make_pair(frac, solid));
                            }
                        }
                    }
                }
                if (cands.empty()) continue;

                size_t best = 0;
                for (size_t k = 1; k < cands.size(); ++k)
                    if (cands[k].first > cands[best].first) best = k;
                TopoDS_Shape res = cands[best].second;
                res = RepairBody(res, "solid");
                Report("solid", res);
                EdgeStats("solid", res);
                {
                    int closedShells = 0, openShells = 0;
                    for (TopExp_Explorer sx(res, TopAbs_SHELL); sx.More(); sx.Next())
                    {
                        if (BRep_Tool::IsClosed(sx.Current())) ++closedShells; else ++openShells;
                    }
                    printf("solid: closed shells=%d open shells=%d\n", closedShells, openShells);
                }
                if (!wrote)
                {
                    STEPControl_Writer writer;
                    writer.Transfer(res, STEPControl_AsIs);
                    const IFSelect_ReturnStatus st = writer.Write(outPath.c_str());
                    printf("write %s: %s\n", outPath.c_str(), st == IFSelect_RetDone ? "ok" : "failed");
                    wrote = (st == IFSelect_RetDone);
                }
                break;
            }
            if (!wrote) printf("component %zu: no solid produced\n", ci);
        }
    }

    for (BRepClass3d_SolidClassifier* c : classifiers) delete c;
    printf("\n%s\n", wrote ? "RESULT: solid written" : "RESULT: no solid produced");
    return wrote ? 0 : 3;
}
