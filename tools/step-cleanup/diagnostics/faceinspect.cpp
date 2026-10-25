// Inspect Mold.stp faces the same way HeatingMold's OccBridge does:
// STEPControl_Reader -> OneShape -> ComputeAutoAxes(principal axes) -> AlignShapeToWorldAxes
// -> TopExp_Explorer(shape, TopAbs_FACE) index order.
#include <cstdio>
#include <cmath>
#include <string>
#include <vector>
#include <map>
#include <memory>
#include <BRepClass3d_SolidClassifier.hxx>

#include <STEPControl_Reader.hxx>
#include <StepData_StepModel.hxx>
#include <StepRepr_Representation.hxx>
#include <StepGeom_Axis2Placement3d.hxx>
#include <StepGeom_Direction.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <TopExp_Explorer.hxx>
#include <TopExp.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_ListIteratorOfListOfShape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Solid.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Wire.hxx>
#include <BRep_Tool.hxx>
#include <BRepTools.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <GProp_PrincipalProps.hxx>
#include <Geom_CylindricalSurface.hxx>
#include <Bnd_Box.hxx>
#include <BRepBndLib.hxx>
#include <gp_Cylinder.hxx>
#include <gp_Ax3.hxx>
#include <Geom_Surface.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <GeomAbs_CurveType.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <ShapeAnalysis_Surface.hxx>
#include <GeomAPI_ProjectPointOnSurf.hxx>
#include <Extrema_ExtPS.hxx>

static gp_Dir g_autoAxes[3];

static bool TryGetSupplementalAxes(const Handle(StepData_StepModel)& model)
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
            z.Normalize(); x.Normalize();
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

static void ComputeAutoAxes(const TopoDS_Shape& shape, const Handle(StepData_StepModel)& stepModel)
{
    g_autoAxes[0] = gp::DX(); g_autoAxes[1] = gp::DY(); g_autoAxes[2] = gp::DZ();
    if (TryGetSupplementalAxes(stepModel))
    {
        printf("### axes source: supplemental geometry\n");
        return;
    }
    printf("### axes source: principal inertia axes\n");
    if (shape.IsNull()) return;
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
    axes[0].Normalize();
    axes[1] -= axes[0] * axes[1].Dot(axes[0]);
    if (axes[1].SquareMagnitude() < 1e-18) return;
    axes[1].Normalize();
    axes[2] = axes[0].Crossed(axes[1]);
    axes[2].Normalize();
    for (int i = 0; i < 3; ++i) g_autoAxes[i] = gp_Dir(axes[i]);
}

static const char* CurveTypeName(GeomAbs_CurveType t)
{
    switch (t)
    {
    case GeomAbs_Line: return "Line";
    case GeomAbs_Circle: return "Circle";
    case GeomAbs_Ellipse: return "Ellipse";
    case GeomAbs_Hyperbola: return "Hyperbola";
    case GeomAbs_Parabola: return "Parabola";
    case GeomAbs_BezierCurve: return "Bezier";
    case GeomAbs_BSplineCurve: return "BSpline";
    case GeomAbs_OffsetCurve: return "Offset";
    case GeomAbs_OtherCurve: return "Other";
    default: return "?";
    }
}

struct CylInfo
{
    int index = -1;
    double radius = 0, area = 0;
    gp_Ax3 axis;
    double u1 = 0, u2 = 0, v1 = 0, v2 = 0;
    gp_Pnt com;
    Bnd_Box box;
    int solid = -1, shell = -1;
    bool reversed = false;
    int edges = 0, wires = 0;
};

int main(int argc, char** argv)
{
    { FILE* dbg = fopen("dbg.txt", "a"); if (dbg) { fprintf(dbg, "start argc=%d\n", argc); fclose(dbg); } }
    const char* path = argc > 1 ? argv[1] : "D:/0.Development/2.HeatingMold/resource/Mold.stp";
    int wantA = argc > 2 ? atoi(argv[2]) : 79;
    int wantB = argc > 3 ? atoi(argv[3]) : 598;

    STEPControl_Reader reader;
    if (reader.ReadFile(path) != IFSelect_RetDone) { printf("read failed\n"); return 1; }
    reader.TransferRoots();
    TopoDS_Shape shape = reader.OneShape();
    if (shape.IsNull()) { printf("empty shape\n"); return 1; }

    ComputeAutoAxes(shape, reader.StepModel());

    // replicate AlignShapeToWorldAxes
    {
        const gp_Dir& x = g_autoAxes[0];
        const gp_Dir& y = g_autoAxes[1];
        const gp_Dir& z = g_autoAxes[2];
        gp_Trsf rotation;
        rotation.SetValues(
            x.X(), x.Y(), x.Z(), 0.0,
            y.X(), y.Y(), y.Z(), 0.0,
            z.X(), z.Y(), z.Z(), 0.0);
        BRepBuilderAPI_Transform aligned(shape, rotation, Standard_True);
        shape = aligned.Shape();
    }

    TopTools_IndexedMapOfShape solidMap, shellMap;
    TopExp::MapShapes(shape, TopAbs_SOLID, solidMap);
    TopExp::MapShapes(shape, TopAbs_SHELL, shellMap);
    TopTools_IndexedDataMapOfShapeListOfShape faceSolid, faceShell;
    TopExp::MapShapesAndAncestors(shape, TopAbs_FACE, TopAbs_SOLID, faceSolid);
    TopExp::MapShapesAndAncestors(shape, TopAbs_FACE, TopAbs_SHELL, faceShell);

    std::vector<TopoDS_Face> faces;
    for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
        faces.push_back(TopoDS::Face(ex.Current()));

    printf("### totals: solids=%d shells=%d faces=%zu\n", solidMap.Extent(), shellMap.Extent(), faces.size());
    Bnd_Box whole; BRepBndLib::Add(shape, whole);
    double xmin, ymin, zmin, xmax, ymax, zmax;
    whole.Get(xmin, ymin, zmin, xmax, ymax, zmax);
    printf("### whole bbox: (%.4f, %.4f, %.4f) - (%.4f, %.4f, %.4f)\n", xmin, ymin, zmin, xmax, ymax, zmax);

    std::vector<CylInfo> cyls;
    std::map<int, int> faceTypeCount;
    for (size_t i = 0; i < faces.size(); ++i)
    {
        const TopoDS_Face& f = faces[i];
        BRepAdaptor_Surface s(f, Standard_False);
        faceTypeCount[(int)s.GetType()]++;
        if (s.GetType() != GeomAbs_Cylinder) continue;
        CylInfo c;
        c.index = (int)i;
        const gp_Cylinder cy = s.Cylinder();
        c.radius = cy.Radius();
        c.axis = cy.Position();
        BRepTools::UVBounds(f, c.u1, c.u2, c.v1, c.v2);
        GProp_GProps p; BRepGProp::SurfaceProperties(f, p);
        c.area = p.Mass(); c.com = p.CentreOfMass();
        BRepBndLib::Add(f, c.box);
        c.reversed = (f.Orientation() == TopAbs_REVERSED);
        if (faceSolid.Contains(f) && !faceSolid.FindFromKey(f).IsEmpty())
        {
            TopTools_ListIteratorOfListOfShape it(faceSolid.FindFromKey(f));
            c.solid = solidMap.FindIndex(it.Value());
        }
        if (faceShell.Contains(f) && !faceShell.FindFromKey(f).IsEmpty())
        {
            TopTools_ListIteratorOfListOfShape it(faceShell.FindFromKey(f));
            c.shell = shellMap.FindIndex(it.Value());
        }
        for (TopExp_Explorer ex(f, TopAbs_EDGE); ex.More(); ex.Next()) ++c.edges;
        for (TopExp_Explorer ex(f, TopAbs_WIRE); ex.More(); ex.Next()) ++c.wires;
        cyls.push_back(c);
    }

    printf("### face types:");
    const char* names[] = {"Plane","Cylinder","Cone","Sphere","Torus","Bezier","BSpline","Revolution","Extrusion","Offset","Other"};
    for (auto& kv : faceTypeCount) printf(" %s=%d", (kv.first >= 0 && kv.first <= 10) ? names[kv.first] : "?", kv.second);
    printf("\n### cylinder faces: %zu\n", cyls.size());

    // dense listing: grouped by (radius, axis line)
    printf("\n=== ALL CYLINDERS (idx, R, axisPoint, axisDir, u-span deg, v-span, area, solid, reversed) ===\n");
    for (const CylInfo& c : cyls)
    {
        printf("#%-4d R=%8.4f axP=(%9.3f,%9.3f,%9.3f) axD=(%6.3f,%6.3f,%6.3f) u=[%7.2f..%7.2f]deg v=[%9.3f..%9.3f] A=%11.3f S=%d%s\n",
            c.index, c.radius,
            c.axis.Location().X(), c.axis.Location().Y(), c.axis.Location().Z(),
            c.axis.Direction().X(), c.axis.Direction().Y(), c.axis.Direction().Z(),
            c.u1 * 180.0 / M_PI, c.u2 * 180.0 / M_PI, c.v1, c.v2, c.area, c.solid,
            c.reversed ? " REV" : "");
    }

    // detailed compare of two requested faces
    auto detail = [&](int idx) {
        if (idx < 0 || idx >= (int)faces.size()) { printf("### face %d out of range\n", idx); return; }
        const TopoDS_Face& f = faces[idx];
        BRepAdaptor_Surface s(f, Standard_False);
        printf("\n=== FACE #%d detail ===\n", idx);
        printf("type=%s orientation=%s solid=%d shell=%d\n",
            BRepAdaptor_Surface(f, Standard_False).GetType() == GeomAbs_Cylinder ? "Cylinder" : "other",
            f.Orientation() == TopAbs_REVERSED ? "REVERSED" : "FORWARD",
            [&]{ if (faceSolid.Contains(f) && !faceSolid.FindFromKey(f).IsEmpty()) { TopTools_ListIteratorOfListOfShape it(faceSolid.FindFromKey(f)); return solidMap.FindIndex(it.Value()); } return -1; }(),
            [&]{ if (faceShell.Contains(f) && !faceShell.FindFromKey(f).IsEmpty()) { TopTools_ListIteratorOfListOfShape it(faceShell.FindFromKey(f)); return shellMap.FindIndex(it.Value()); } return -1; }());
        if (s.GetType() == GeomAbs_Cylinder)
        {
            gp_Cylinder cy = s.Cylinder();
            gp_Ax3 a = cy.Position();
            printf("cyl R=%.6f\n", cy.Radius());
            printf("  axisLoc=(%.6f, %.6f, %.6f) axisDir=(%.9f, %.9f, %.9f)\n",
                a.Location().X(), a.Location().Y(), a.Location().Z(),
                a.Direction().X(), a.Direction().Y(), a.Direction().Z());
            printf("  xDir=(%.6f,%.6f,%.6f)\n", a.XDirection().X(), a.XDirection().Y(), a.XDirection().Z());
        }
        Standard_Real u1, u2, v1, v2; BRepTools::UVBounds(f, u1, u2, v1, v2);
        printf("uv bounds u=[%.6f..%.6f] (%.3f..%.3f deg) v=[%.6f..%.6f]\n", u1, u2, u1*180/M_PI, u2*180/M_PI, v1, v2);
        GProp_GProps p; BRepGProp::SurfaceProperties(f, p);
        printf("area=%.6f mass=%.6f com=(%.6f,%.6f,%.6f)\n", p.Mass(), p.Mass(), p.CentreOfMass().X(), p.CentreOfMass().Y(), p.CentreOfMass().Z());
        Bnd_Box b; BRepBndLib::Add(f, b);
        double a1,a2,b1,b2,c1,c2; b.Get(a1,a2,b1,b2,c1,c2);
        printf("bbox=(%.6f,%.6f,%.6f)-(%.6f,%.6f,%.6f)\n", a1,b1,c1,a2,b2,c2);
        printf("tolerance=%.9f\n", BRep_Tool::Tolerance(f));
        printf("wires=%d\n", [&]{int n=0; for (TopExp_Explorer ex(f, TopAbs_WIRE); ex.More(); ex.Next()) ++n; return n;}());
        int ei = 0;
        for (TopExp_Explorer ex(f, TopAbs_EDGE); ex.More(); ex.Next(), ++ei)
        {
            TopoDS_Edge e = TopoDS::Edge(ex.Current());
            BRepAdaptor_Curve c(e);
            printf("  edge[%d] curve=%s tol=%.6f", ei, CurveTypeName(c.GetType()), BRep_Tool::Tolerance(e));
            if (c.GetType() == GeomAbs_Circle)
            {
                gp_Circ ci = c.Circle();
                printf(" R=%.6f c=(%.4f,%.4f,%.4f) n=(%.4f,%.4f,%.4f) full=%.3f", ci.Radius(),
                    ci.Location().X(), ci.Location().Y(), ci.Location().Z(),
                    ci.Axis().Direction().X(), ci.Axis().Direction().Y(), ci.Axis().Direction().Z(),
                    c.LastParameter() - c.FirstParameter());
            }
            if (c.GetType() == GeomAbs_Line)
            {
                printf(" p0=(%.4f,%.4f,%.4f) p1=(%.4f,%.4f,%.4f)",
                    c.Value(c.FirstParameter()).X(), c.Value(c.FirstParameter()).Y(), c.Value(c.FirstParameter()).Z(),
                    c.Value(c.LastParameter()).X(), c.Value(c.LastParameter()).Y(), c.Value(c.LastParameter()).Z());
            }
            printf(" closed=%d\n", BRep_Tool::IsClosed(e) ? 1 : 0);
        }
    };

    detail(wantA);
    detail(wantB);

    // geometric comparison
    if (wantA >= 0 && wantA < (int)faces.size() && wantB >= 0 && wantB < (int)faces.size())
    {
        const TopoDS_Face& fa = faces[wantA];
        const TopoDS_Face& fb = faces[wantB];
        BRepAdaptor_Surface sa(fa, Standard_False), sb(fb, Standard_False);
        printf("\n=== COMPARE #%d vs #%d ===\n", wantA, wantB);
        printf("types: %d vs %d (Cylinder=%d)\n", (int)sa.GetType(), (int)sb.GetType(), (int)GeomAbs_Cylinder);
        Handle(Geom_Surface) ha = BRep_Tool::Surface(fa), hb = BRep_Tool::Surface(fb);
        printf("surface handle identical = %s\n", ha == hb ? "YES" : "no");
        if (sa.GetType() == GeomAbs_Cylinder && sb.GetType() == GeomAbs_Cylinder)
        {
            gp_Cylinder ca = sa.Cylinder(), cb = sb.Cylinder();
            printf("radius: A=%.6f B=%.6f diff=%.6g\n", ca.Radius(), cb.Radius(), std::fabs(ca.Radius() - cb.Radius()));
            double d = std::fabs(ca.Axis().Direction().Dot(cb.Axis().Direction()));
            printf("axis dir dot |.| = %.9f\n", d);
            // distance between axis lines
            const gp_Pnt pa = ca.Location();
            gp_Vec n = ca.Axis().Direction();
            gp_Vec w(pa, cb.Location());
            gp_Vec perp = w - n * w.Dot(n);
            printf("axis line distance = %.6g\n", perp.Magnitude());
            printf("same infinite cylinder = %s\n",
                (std::fabs(ca.Radius()-cb.Radius()) < 1e-6 && perp.Magnitude() < 1e-6 && d > 1-1e-9) ? "YES" : "no");

            // sample both faces' trimmed region and test whether sample points lie on the other infinite cylinder
            auto testOn = [&](const TopoDS_Face& src, const gp_Cylinder& tgt, const char* label) {
                BRepAdaptor_Surface ss(src, Standard_False);
                Standard_Real u1,u2,v1,v2; BRepTools::UVBounds(src,u1,u2,v1,v2);
                int inside = 0, total = 0; double maxDev = 0;
                for (int i = 0; i <= 8; ++i)
                  for (int j = 0; j <= 8; ++j)
                  {
                      double u = u1 + (u2-u1)*i/8.0, v = v1 + (v2-v1)*j/8.0;
                      gp_Pnt p = ss.Value(u,v);
                      GeomAPI_ProjectPointOnSurf proj(p, new Geom_CylindricalSurface(tgt));
                      double dmin = proj.NbPoints() ? proj.LowerDistance() : 1e9;
                      if (dmin < 1e-3) ++inside;
                      if (dmin > maxDev) maxDev = dmin;
                      ++total;
                  }
                printf("%s samples: %d/%d lie on other cylinder, max dev=%.4f\n", label, inside, total, maxDev);
            };
            testOn(fa, cb, "A-on-B");
            testOn(fb, ca, "B-on-A");
        }
        // surface normals at UV midpoints (orientation-aware)
        {
            Standard_Real a1,a2,b1,b2, c1,c2,d1,d2;
            BRepTools::UVBounds(fa, a1,a2,b1,b2);
            BRepTools::UVBounds(fb, c1,c2,d1,d2);
            gp_Pnt pa, pb; gp_Vec dua, dva, dub, dvb;
            sa.D1((a1+a2)/2, (b1+b2)/2, pa, dua, dva);
            sb.D1((c1+c2)/2, (d1+d2)/2, pb, dub, dvb);
            gp_Vec na = dua.Crossed(dva); if (na.SquareMagnitude() > 1e-30) na.Normalize();
            gp_Vec nb = dub.Crossed(dvb); if (nb.SquareMagnitude() > 1e-30) nb.Normalize();
            if (fa.Orientation() == TopAbs_REVERSED) na.Reverse();
            if (fb.Orientation() == TopAbs_REVERSED) nb.Reverse();
            printf("normal at uv-mid: A=(%.6f,%.6f,%.6f) B=(%.6f,%.6f,%.6f) dot=%.6f\n",
                na.X(), na.Y(), na.Z(), nb.X(), nb.Y(), nb.Z(), na.Dot(nb));
        }
        // shared edges
        TopTools_IndexedMapOfShape ea, eb;
        TopExp::MapShapes(fa, TopAbs_EDGE, ea);
        TopExp::MapShapes(fb, TopAbs_EDGE, eb);
        int shared = 0;
        for (int i = 1; i <= ea.Extent(); ++i) if (eb.Contains(ea(i))) ++shared;
        printf("shared topological edges = %d (A has %d, B has %d)\n", shared, ea.Extent(), eb.Extent());
        // boolean common area of the two faces
        BRepAlgoAPI_Common com(fa, fb);
        if (com.IsDone())
        {
            GProp_GProps pc; BRepGProp::SurfaceProperties(com.Shape(), pc);
            printf("face-face common area = %.6f\n", pc.Mass());
        }
        else printf("face-face boolean failed\n");
    }

    // ---- body structure ----
    printf("\n=== SOLIDS ===\n");
    for (int si = 1; si <= solidMap.Extent(); ++si)
    {
        const TopoDS_Shape& s = solidMap(si);
        int nf = 0, nc = 0, nsph = 0;
        for (TopExp_Explorer ex(s, TopAbs_FACE); ex.More(); ex.Next())
        {
            ++nf;
            GeomAbs_SurfaceType t = BRepAdaptor_Surface(TopoDS::Face(ex.Current()), Standard_False).GetType();
            if (t == GeomAbs_Cylinder) ++nc;
            if (t == GeomAbs_Sphere) ++nsph;
        }
        GProp_GProps vp; BRepGProp::VolumeProperties(s, vp);
        Bnd_Box bx; BRepBndLib::Add(s, bx);
        double a1,a2,b1,b2,c1,c2; bx.Get(a1,a2,b1,b2,c1,c2);
        printf("solid %-2d faces=%3d cyl=%3d sph=%d vol=%14.3f bbox=(%.1f,%.1f,%.1f)-(%.1f,%.1f,%.1f)\n",
            si, nf, nc, nsph, vp.Mass(), a1,b1,c1,a2,b2,c2);
    }
    printf("\n=== SHELLS ===\n");
    for (int si = 1; si <= shellMap.Extent(); ++si)
    {
        const TopoDS_Shape& s = shellMap(si);
        int nf = 0;
        for (TopExp_Explorer ex(s, TopAbs_FACE); ex.More(); ex.Next()) ++nf;
        Bnd_Box bx; BRepBndLib::Add(s, bx);
        double a1,a2,b1,b2,c1,c2; bx.Get(a1,a2,b1,b2,c1,c2);
        bool closed = BRep_Tool::IsClosed(s);
        // does it belong to a solid?
        int owner = -1;
        for (int k = 1; k <= solidMap.Extent(); ++k)
        {
            TopTools_IndexedMapOfShape sm; TopExp::MapShapes(solidMap(k), TopAbs_SHELL, sm);
            if (sm.Contains(s)) { owner = k; break; }
        }
        printf("shell %-2d faces=%3d solidOwner=%-2d closed=%d bbox=(%.1f,%.1f,%.1f)-(%.1f,%.1f,%.1f)\n",
            si, nf, owner, closed ? 1 : 0, a1,b1,c1,a2,b2,c2);
    }

    // faces of shell 31 detail
    if (shellMap.Extent() >= 31)
    {
        printf("\n=== FACES OF SHELL 31 ===\n");
        const char* tn[] = {"Plane","Cylinder","Cone","Sphere","Torus","Bezier","BSpline","Revolution","Extrusion","Offset","Other"};
        int idx = 0;
        for (TopExp_Explorer ex(faces.empty() ? shape : shape, TopAbs_FACE); ex.More(); ex.Next(), ++idx)
        {
            const TopoDS_Face& f = TopoDS::Face(ex.Current());
            if (!faceShell.Contains(f)) continue;
            bool inShell31 = false;
            for (TopTools_ListIteratorOfListOfShape it(faceShell.FindFromKey(f)); it.More(); it.Next())
                if (shellMap.FindIndex(it.Value()) == 31) inShell31 = true;
            if (!inShell31) continue;
            BRepAdaptor_Surface s(f, Standard_False);
            GProp_GProps p; BRepGProp::SurfaceProperties(f, p);
            printf("  #%-4d %-14s A=%11.3f%s\n", idx, tn[(int)s.GetType()], p.Mass(),
                f.Orientation() == TopAbs_REVERSED ? " REV" : "");
        }
    }

    // duplicate report: all cylinders that are geometrically the same infinite cylinder
    printf("\n=== COINCIDENT CYLINDER GROUPS (same R + same axis line) ===\n");
    std::vector<bool> used(cyls.size(), false);
    for (size_t i = 0; i < cyls.size(); ++i)
    {
        if (used[i]) continue;
        std::vector<size_t> grp{ i };
        for (size_t j = i + 1; j < cyls.size(); ++j)
        {
            if (used[j]) continue;
            double dr = std::fabs(cyls[i].radius - cyls[j].radius);
            double dot = std::fabs(cyls[i].axis.Direction().Dot(cyls[j].axis.Direction()));
            gp_Vec w(cyls[i].axis.Location(), cyls[j].axis.Location());
            gp_Vec n = cyls[i].axis.Direction();
            double dist = (w - n * w.Dot(n)).Magnitude();
            if (dr < 1e-4 && dist < 1e-4 && dot > 1 - 1e-9) { grp.push_back(j); used[j] = true; }
        }
        used[i] = true;
        if (grp.size() < 2) continue;
        printf("group R=%.4f axisP=(%.3f,%.3f,%.3f) axisD=(%.3f,%.3f,%.3f) count=%zu\n",
            cyls[i].radius, cyls[i].axis.Location().X(), cyls[i].axis.Location().Y(), cyls[i].axis.Location().Z(),
            cyls[i].axis.Direction().X(), cyls[i].axis.Direction().Y(), cyls[i].axis.Direction().Z(), grp.size());
        for (size_t k : grp)
            printf("   #%-4d solid=%d shell=%d u=[%7.2f..%7.2f]deg v=[%9.3f..%9.3f] A=%10.3f%s\n",
                cyls[k].index, cyls[k].solid, cyls[k].shell,
                cyls[k].u1*180/M_PI, cyls[k].u2*180/M_PI, cyls[k].v1, cyls[k].v2, cyls[k].area,
                cyls[k].reversed ? " REV" : "");
    }
    // === open-shell bodies (no solid owner): redundant or unique? ===
    {
        std::vector<TopoDS_Solid> solids;
        for (int i = 1; i <= solidMap.Extent(); ++i) solids.push_back(TopoDS::Solid(solidMap(i)));
        std::vector<std::unique_ptr<BRepClass3d_SolidClassifier>> cls;
        for (auto& s : solids) cls.emplace_back(new BRepClass3d_SolidClassifier(s));

        printf("\n=== OPEN-SHELL BODY FACES vs SOLIDS (3x3 UV sample, tol 1e-4) ===\n");
        int nOn = 0, nIn = 0, nOut = 0, nFace = 0;
        double aTotal = 0, aOn = 0;
        for (size_t i = 0; i < faces.size(); ++i)
        {
            const TopoDS_Face& f = faces[i];
            if (faceSolid.Contains(f) && !faceSolid.FindFromKey(f).IsEmpty()) continue;
            ++nFace;
            int sid = -1;
            if (faceShell.Contains(f) && !faceShell.FindFromKey(f).IsEmpty())
            { TopTools_ListIteratorOfListOfShape it(faceShell.FindFromKey(f)); sid = shellMap.FindIndex(it.Value()); }
            BRepAdaptor_Surface s(f, Standard_False);
            double u1, u2, v1, v2; BRepTools::UVBounds(f, u1, u2, v1, v2);
            int on = 0, in = 0, out = 0;
            for (int a = 1; a <= 3; ++a) for (int b = 1; b <= 3; ++b)
            {
                double u = u1 + (u2 - u1) * a / 4.0, v = v1 + (v2 - v1) * b / 4.0;
                gp_Pnt p = s.Value(u, v);
                bool pOn = false, pIn = false;
                for (auto& c : cls)
                {
                    c->Perform(p, 1e-4);
                    TopAbs_State st = c->State();
                    if (st == TopAbs_ON) { pOn = true; break; }
                    if (st == TopAbs_IN) pIn = true;
                }
                if (pOn) ++on; else if (pIn) ++in; else ++out;
            }
            GProp_GProps g; BRepGProp::SurfaceProperties(f, g);
            aTotal += g.Mass();
            printf("  face#%-4zu shell=%d %-8s A=%10.3f on=%d in=%d out=%d\n", i, sid,
                names[(int)s.GetType()], g.Mass(), on, in, out);
            if (s.GetType() == GeomAbs_Plane)
            {
                gp_Pln pl = s.Plane();
                gp_Pnt o = pl.Location(); gp_Dir d = pl.Axis().Direction();
                printf("        plane o=(%.3f,%.3f,%.3f) n=(%.4f,%.4f,%.4f)\n", o.X(), o.Y(), o.Z(), d.X(), d.Y(), d.Z());
            }
            if (on >= 5) { ++nOn; aOn += g.Mass(); } else if (out >= 5) ++nOut; else ++nIn;
        }
        printf("### open-shell faces=%d : on-solid-boundary=%d (A=%.1f)  inside-solid=%d  outside/unique=%d  totalA=%.1f\n",
            nFace, nOn, aOn, nIn, nOut, aTotal);
    }
    return 0;
}
