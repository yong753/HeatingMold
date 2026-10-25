// Standalone probe: replicates BRepProj_Projection::BuildSection (OCCT 7.9.3) so the failing
// stage can be identified (section not done / no edges / no wires).
#include <STEPControl_Reader.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopTools_HSequenceOfShape.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepSweep_Prism.hxx>
#include <BRepTools_TrsfModification.hxx>
#include <BRepTools_Modifier.hxx>
#include <BRepAlgoAPI_Section.hxx>
#include <Bnd_Box.hxx>
#include <BRepBndLib.hxx>
#include <ShapeAnalysis_FreeBounds.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Wire.hxx>
#include <BRep_Tool.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <Geom_CylindricalSurface.hxx>
#include <Geom_Plane.hxx>
#include <BRep_Tool.hxx>
#include <gp_Trsf.hxx>
#include <gp_Vec.hxx>
#include <Precision.hxx>
#include <Message_Report.hxx>
#include <Message.hxx>
#include <BRepTools_WireExplorer.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <IntCurvesFace_ShapeIntersector.hxx>
#include <StepData_StepModel.hxx>
#include <StepRepr_Representation.hxx>
#include <StepGeom_Axis2Placement3d.hxx>
#include <StepGeom_Direction.hxx>
#include <TCollection_HAsciiString.hxx>
#include <algorithm>
#include <cmath>
#include <string>
#include <iostream>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <GProp_PrincipalProps.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <gp_Lin.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <TopExp.hxx>
#include <IntAna_QuadQuadGeo.hxx>
#include <IntAna_ResultType.hxx>
#include <gp_Pln.hxx>
#include <gp_Cylinder.hxx>
#include <IntTools_FaceFace.hxx>
#include <IntTools_Curve.hxx>
#include <IntTools_SequenceOfCurves.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <GeomAbs_CurveType.hxx>
#include <GeomAbs_SurfaceType.hxx>
#include <stdio.h>

static Standard_Real DistanceOut(const TopoDS_Shape& S1, const TopoDS_Shape& S2)
{
  Bnd_Box B1, B2;
  BRepBndLib::Add(S1, B1);
  BRepBndLib::Add(S2, B2);
  return B1.Distance(B2);
}

static Standard_Real DistanceIn(const TopoDS_Shape& S1, const TopoDS_Shape& S2)
{
  Bnd_Box LB, SB;
  BRepBndLib::Add(S1, SB);
  BRepBndLib::Add(S2, LB);
  Standard_Real LXmin, LYmin, LZmin, LXmax, LYmax, LZmax;
  Standard_Real SXmin, SYmin, SZmin, SXmax, SYmax, SZmax;
  SB.Get(SXmin, SYmin, SZmin, SXmax, SYmax, SZmax);
  LB.Get(LXmin, LYmin, LZmin, LXmax, LYmax, LZmax);
  gp_XYZ Lmin(LXmin, LYmin, LZmin), Lmax(LXmax, LYmax, LZmax);
  gp_XYZ Smin(SXmin, SYmin, SZmin), Smax(SXmax, SYmax, SZmax);
  Lmax.Subtract(Lmin);
  Smax.Subtract(Smin);
  return Lmax.Modulus() + Smax.Modulus() + DistanceOut(S1, S2);
}

// ---- port of Bridge::TryGetSupplementalAxes (OccBridgeApi.cpp:146-194) ----
static bool TryGetSupplementalAxes(const Handle(StepData_StepModel)& model, gp_Dir out[3])
{
  if (model.IsNull()) return false;
  for (Standard_Integer i = 1; i <= model->NbEntities(); ++i)
  {
    Handle(StepRepr_Representation) rep = Handle(StepRepr_Representation)::DownCast(model->Entity(i));
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
      out[0] = gp_Dir(x); out[1] = gp_Dir(y); out[2] = gp_Dir(z);
      return true;
    }
  }
  return false;
}

// ---- port of Bridge::ComputeAutoAxes + AlignShapeToWorldAxes (OccBridgeApi.cpp:196-249) ----
static void ComputeAutoAxes(const TopoDS_Shape& shape, gp_Dir out[3])
{
  out[0] = gp::DX(); out[1] = gp::DY(); out[2] = gp::DZ();
  GProp_GProps props;
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
  for (int i = 0; i < 3; ++i) out[i] = gp_Dir(axes[i]);
}

static TopoDS_Shape AlignShape(const TopoDS_Shape& shape, const gp_Dir ax[3])
{
  gp_Trsf rotation;
  rotation.SetValues(ax[0].X(), ax[0].Y(), ax[0].Z(), 0.0,
                     ax[1].X(), ax[1].Y(), ax[1].Z(), 0.0,
                     ax[2].X(), ax[2].Y(), ax[2].Z(), 0.0);
  BRepBuilderAPI_Transform t(shape, rotation, Standard_True);
  return t.Shape();
}


static void Report(const char* label, const TopoDS_Shape& wire, const TopoDS_Shape& target,
                   const gp_Dir& D, const gp_Pnt& sa, const gp_Pnt& sb)
{
  if (wire.ShapeType() != TopAbs_EDGE && wire.ShapeType() != TopAbs_WIRE)
  { printf("%-22s : bad input type\n", label); return; }

  Standard_Real mdis = DistanceIn(wire, target);
  gp_Vec Vsup(D.XYZ() * 2 * mdis), Vinf(D.XYZ() * -mdis);
  gp_Trsf T; T.SetTranslation(Vinf);
  Handle(BRepTools_TrsfModification) Trsf = new BRepTools_TrsfModification(T);
  BRepTools_Modifier Modif(wire, Trsf);
  const TopoDS_Shape& WireBase = Modif.ModifiedShape(wire);
  BRepSweep_Prism CylSurf(WireBase, Vsup, Standard_False);
  TopoDS_Shape prism = CylSurf.Shape();

  BRepAlgoAPI_Section sec(target, prism, Standard_False);
  sec.Approximation(Standard_True);
  sec.ComputePCurveOn1(Standard_True);
  sec.ComputePCurveOn2(Standard_True);
  sec.SetUseOBB(Standard_True);
  sec.Build();

  int edges = 0;
  if (sec.IsDone())
    for (TopExp_Explorer e(sec.Shape(), TopAbs_EDGE); e.More(); e.Next()) ++edges;

  Handle(TopTools_HSequenceOfShape) hs = new TopTools_HSequenceOfShape();
  for (TopExp_Explorer e(sec.Shape(), TopAbs_EDGE); e.More(); e.Next()) hs->Append(e.Current());
  Handle(TopTools_HSequenceOfShape) wires = new TopTools_HSequenceOfShape();
  if (hs->Length() > 0)
    ShapeAnalysis_FreeBounds::ConnectEdgesToWires(hs, Precision::Confusion(), Standard_True, wires);

  // exactly the bridge's connection call (WorkingSideNative.inc:322 / OccBridgeApi.cpp:1401)
  Handle(TopTools_HSequenceOfShape) bridgeWires = new TopTools_HSequenceOfShape();
  if (hs->Length() > 0)
    ShapeAnalysis_FreeBounds::ConnectEdgesToWires(hs, 0.01, Standard_False, bridgeWires);
  int closedFlag = -1;
  if (bridgeWires->Length() > 0)
    closedFlag = (int)BRep_Tool::IsClosed(TopoDS::Wire(bridgeWires->Value(1)));

  printf("bridge-connect(tol 0.01, closed=false) wires=%d IsClosed=%d\n", bridgeWires->Length(), closedFlag);

  printf("%-22s : mdis=%9.2f prismFaces=%2d IsDone=%d HasErrors=%d sectionEdges=%2d wire(tight)=%2d\n",
    label, mdis, [&]{ int n=0; for(TopExp_Explorer e(prism,TopAbs_FACE);e.More();e.Next())++n; return n; }(),
    (int)sec.IsDone(), (int)sec.HasErrors(), edges, wires->Length());

  if (sec.HasErrors())
  {
    sec.DumpErrors(std::cout);
  }
  if (edges == 0)
  {
    // Is it the prism that misses the target, or the section?
    Bnd_Box pb, tb; BRepBndLib::Add(prism, pb); BRepBndLib::Add(target, tb);
    printf("    prism bbox   : (%8.2f,%9.2f,%9.2f) - (%8.2f,%9.2f,%9.2f)\n",
      pb.CornerMin().X(), pb.CornerMin().Y(), pb.CornerMin().Z(),
      pb.CornerMax().X(), pb.CornerMax().Y(), pb.CornerMax().Z());
    printf("    target bbox  : (%8.2f,%9.2f,%9.2f) - (%8.2f,%9.2f,%9.2f)  overlapBoxDistance=%.3f\n",
      tb.CornerMin().X(), tb.CornerMin().Y(), tb.CornerMin().Z(),
      tb.CornerMax().X(), tb.CornerMax().Y(), tb.CornerMax().Z(), pb.Distance(tb));
    if (!sec.IsDone()) printf("    -> section algorithm itself returned not-done\n");
    else printf("    -> section returned a result with zero edges\n");
  }

  // --- what the bridge checks AFTER the projection (WorkingSideNative.inc:314-338) ---
  if (wires->Length() == 1)
  {
    const TopoDS_Wire& w = TopoDS::Wire(wires->Value(1));
    GProp_GProps lp; BRepGProp::LinearProperties(w, lp);
    TopoDS_Vertex vf, vl;
    for (BRepTools_WireExplorer ex(w); ex.More(); ex.Next())
    {
      TopoDS_Vertex a, b; TopExp::Vertices(ex.Current(), a, b, Standard_True);
      if (vf.IsNull()) vf = a;
      vl = b;
    }
    const gp_Pnt pa = BRep_Tool::Pnt(vf), pb = BRep_Tool::Pnt(vl);
    auto transverse = [&](const gp_Pnt& p, const gp_Pnt& q)
    { gp_Vec v(p, q), d(D); return (v - d * v.Dot(d)).Magnitude(); };
    // source segment endpoints, same expression as the bridge (xyz[0..2], last 3)
    printf("    projected wire: len=%.3f edges=%d  start=(%.4f,%.4f,%.4f) end=(%.4f,%.4f,%.4f)\n",
      lp.Mass(), [&]{int n=0;for(TopExp_Explorer e(w,TopAbs_EDGE);e.More();e.Next())++n;return n;}(),
      pa.X(), pa.Y(), pa.Z(), pb.X(), pb.Y(), pb.Z());
    printf("    BRepCheck valid=%d\n", (int)BRepCheck_Analyzer(w).IsValid());
    printf("    transverse: start->sourceA=%.4f  end->sourceB=%.4f   (bridge tolerance 0.01)\n",
      transverse(pa, sa), transverse(pb, sb));
  }

  double bridgeLen = 0.0;
  if (bridgeWires->Length() > 0)
  {
    GProp_GProps bp; BRepGProp::LinearProperties(bridgeWires->Value(1), bp); bridgeLen = bp.Mass();
  }
  printf("RESULT %-14s secEdges=%d tight=%d bridge=%d closed=%d len=%.3f\n",
    label, (int)edges, wires->Length(), bridgeWires->Length(), closedFlag, bridgeLen);
}

int main(int argc, char** argv)
{
  OCC_CATCH_SIGNALS
  const char* step = argc > 1 ? argv[1] : "D:/0.Development/2.HeatingMold/resource/Mold.stp";
  STEPControl_Reader reader;
  if (reader.ReadFile(step) != IFSelect_RetDone) { printf("STEP read failed\n"); return 2; }
  reader.TransferRoots();
  TopoDS_Shape shape = reader.OneShape();
  gp_Dir autoAx[3];
  if (!TryGetSupplementalAxes(reader.StepModel(), autoAx)) ComputeAutoAxes(shape, autoAx);
  printf("auto axes: x=(%.4f,%.4f,%.4f) y=(%.4f,%.4f,%.4f) z=(%.4f,%.4f,%.4f)\n",
    autoAx[0].X(), autoAx[0].Y(), autoAx[0].Z(), autoAx[1].X(), autoAx[1].Y(), autoAx[1].Z(), autoAx[2].X(), autoAx[2].Y(), autoAx[2].Z());
  shape = AlignShape(shape, autoAx);   // same alignment the bridge applies before caching faces
  TopTools_IndexedMapOfShape faces;
  TopExp::MapShapes(shape, TopAbs_FACE, faces);
  printf("faces=%d\n", faces.Extent());

  int ids[4] = { 18, 74, 597, 598 };
  for (int id : ids)
  {
    const TopoDS_Face& f = TopoDS::Face(faces.FindKey(id + 1));  // bridge face id is 0-based
    BRepAdaptor_Surface sa(f);
    Bnd_Box bb; BRepBndLib::Add(f, bb);
    printf("id %3d : type %d (0=plane,1=cyl,6=bspline)  R=", id, (int)sa.GetType());
    if (sa.GetType() == GeomAbs_Cylinder) printf("%.1f", sa.Cylinder().Radius()); else printf("-");
    printf("  bbox (%8.1f,%9.1f,%9.1f)-(%8.1f,%9.1f,%9.1f)\n",
      bb.CornerMin().X(), bb.CornerMin().Y(), bb.CornerMin().Z(),
      bb.CornerMax().X(), bb.CornerMax().Y(), bb.CornerMax().Z());
  }

  BRepBuilderAPI_Sewing sewing(0.01, Standard_True, Standard_True, Standard_True, Standard_True);
  for (int id : ids) sewing.Add(faces.FindKey(id + 1));
  sewing.Perform();
  const TopoDS_Shape target = sewing.SewedShape();
  printf("target sewn: faces=%d\n", [&]{int n=0;for(TopExp_Explorer e(target,TopAbs_FACE);e.More();e.Next())++n;return n;}());

  const double z = -3606.93653298544;
  const double ax = 1751.1998456735284, ay = -1512.3210851853976;   // failing piece 87 endpoints (3D, exact from app)
  const double bx = 1751.31541159172, by = -1502.3210851853976;

  // sanity: the datum-plane endpoints must actually hit the sewn target going -Z
  {
    IntCurvesFace_ShapeIntersector hit;
    hit.Load(target, 0.01);
    struct P { const char* n; double x, y; } pts[] = { {"A", ax, ay}, {"B", bx, by} };
    for (const auto& p : pts)
    {
      hit.Perform(gp_Lin(gp_Pnt(p.x, p.y, z), gp_Dir(0, 0, -1)), 0, 1e12);
      printf("  ray (%s) hits=%d", p.n, (int)hit.IsDone());
      if (hit.IsDone() && hit.NbPnt() > 0)
        printf(" at z=%.3f", hit.Pnt(1).Z());
      printf("\n");
    }
  }
  struct Case { const char* label; double x0, x1; };
  static char labels[129][32];
  static Case cases[129];
  int nCases = 0;
  for (int i = 0; i <= 128; ++i)
  {
    const double dx = 0.11300 + 0.00005 * i;
    sprintf_s(labels[i], "dx=%.5f", dx);
    cases[nCases].label = labels[i]; cases[nCases].x0 = ax; cases[nCases].x1 = ax + dx; ++nCases;
  }
  for (const auto& c : cases)
  {
    BRepBuilderAPI_MakePolygon poly;
    poly.Add(gp_Pnt(c.x0, ay, z));
    poly.Add(gp_Pnt(c.x1, by, z));
    if (!poly.IsDone()) { printf("%s : polygon failed\n", c.label); continue; }
    Report(c.label, poly.Wire(), target, gp_Dir(0, 0, -1), gp_Pnt(c.x0, ay, z), gp_Pnt(c.x1, by, z));
  }

  // the real piece 87 segment, exact coordinates taken from the app harness
  {
    BRepBuilderAPI_MakePolygon poly;
    poly.Add(gp_Pnt(ax, ay, z));
    poly.Add(gp_Pnt(bx, by, z));
    Report("piece87 EXACT", poly.Wire(), target, gp_Dir(0, 0, -1), gp_Pnt(ax, ay, z), gp_Pnt(bx, by, z));

    // ---- which OCCT decision drops the curve? probe the pave-filler face/face step ----
    // BRepAlgoAPI_Section is a BOPAlgo_SECTION wrapper; its section curves come from
    // IntTools_FaceFace (BOPAlgo_PaveFiller::PerformFF). Run it directly, on the same two
    // faces, and sweep the fuzzy tolerance that gates it.
    {
      const gp_Dir D(0, 0, -1);
      const TopoDS_Wire w = poly.Wire();
      const Standard_Real mdis = DistanceIn(w, target);
      gp_Trsf T; T.SetTranslation(gp_Vec(D.XYZ() * -mdis));
      Handle(BRepTools_TrsfModification) Trsf = new BRepTools_TrsfModification(T);
      BRepTools_Modifier Modif(w, Trsf);
      BRepSweep_Prism prismSweep(Modif.ModifiedShape(w), gp_Vec(D.XYZ() * 2 * mdis), Standard_False);
      TopoDS_Face prismFace;
      for (TopExp_Explorer ex(prismSweep.Shape(), TopAbs_FACE); ex.More(); ex.Next())
      { prismFace = TopoDS::Face(ex.Current()); break; }
      const TopoDS_Face cylFace = TopoDS::Face(faces.FindKey(598 + 1));
      BRepAdaptor_Surface sa(prismFace);
      printf("\nface/face step (IntTools_FaceFace): prism type=%d (extrusion=%d) x cylinder 598\n",
        (int)sa.GetType(), (int)GeomAbs_SurfaceOfExtrusion);

      for (double fz : {0.0, 1e-3, 5e-3, 7.0e-3, 7.4e-3, 7.6e-3, 7.62e-3, 7.7e-3, 8.0e-3, 1e-2})
      {
        IntTools_FaceFace ff;
        ff.SetParameters(Standard_False, Standard_False, Standard_False, 0.0);
        if (fz > 0) ff.SetFuzzyValue(fz);
        ff.Perform(prismFace, cylFace, Standard_False);
        const IntTools_SequenceOfCurves& curves = ff.Lines();
        printf("  fuzzy=%-7g done=%d curves=%d points=%d tangentFaces=%d",
          fz, (int)ff.IsDone(), curves.Length(), ff.Points().Length(), (int)ff.TangentFaces());
        if (curves.Length() > 0)
        {
          const IntTools_Curve& c = curves.Value(1);
          gp_Pnt a, b; Standard_Real t1 = 0, t2 = 0;
          printf(" | first curve type=%d tol=%g", (int)c.Type(), c.Tolerance());
          if (c.Bounds(t1, t2, a, b))
            printf(" (%.3f,%.3f,%.3f)->(%.3f,%.3f,%.3f)", a.X(), a.Y(), a.Z(), b.X(), b.Y(), b.Z());
        }
        printf("\n");
      }

      // The prism of a straight segment is a PLANE, the target face is a CYLINDER: both are
      // quadrics, so OCCT's analytic solver decides first. Find which tolerance flips it.
      {
        BRepAdaptor_Surface sa2(prismFace), sc2(cylFace);
        const gp_Pln pln = sa2.Plane();
        const gp_Cylinder cyl = sc2.Cylinder();
        const gp_Ax1 axis = cyl.Axis();
        const gp_Dir nPlane = pln.Axis().Direction();
        const double distPt = pln.Distance(axis.Location());
        const double adotn = std::fabs(axis.Direction().Dot(nPlane));
        double pa, pb, pc, pd;
        pln.Coefficients(pa, pb, pc, pd);
        const gp_Pnt lo = axis.Location();
        const double signedDist = pa * lo.X() + pb * lo.Y() + pc * lo.Z() + pd;
        printf("\nanalytic classification (IntAna_QuadQuadGeo: plane x cylinder):\n");
        printf("  R=%.6f  |dist(axis,plane)|=%.6f  signed=%.6f  |dist|-R=%+.3e  |a.n|=%.3e rad (%.4f mm per 1200 mm)\n",
          cyl.Radius(), distPt, signedDist, std::fabs(signedDist) - cyl.Radius(), adotn, adotn * 1200.0);
        for (double tol : {1e-7, 1e-6, 1e-5, 1e-4, 1e-3, 5e-3, 1e-2, 5e-2})
        {
          IntAna_QuadQuadGeo qq;
          qq.Perform(pln, cyl, Precision::Angular(), tol, 300.0);
          printf("  Tolang=1e-12 Tol=%-8g done=%d type=%-2d (0=point,1=line,2=circle,3=pt+circ,4=ellipse,7=empty,8=same,9=nogeom) sol=%d",
            tol, (int)qq.IsDone(), (int)qq.TypeInter(), qq.NbSolutions());
          if (qq.TypeInter() == IntAna_Line && qq.NbSolutions() >= 1)
          {
            const gp_Lin& l = qq.Line(1);
            printf(" | dir=(%.6f,%.6f,%.6f) thru (%.3f,%.3f,%.3f)",
              l.Direction().X(), l.Direction().Y(), l.Direction().Z(),
              l.Location().X(), l.Location().Y(), l.Location().Z());
          }
          printf("\n");
        }
      }

      // same sweep on the public API the bridge actually calls
      printf("  BRepAlgoAPI_Section(sewn target, prism) edge count by fuzzy value:\n");      for (double fz : {0.0, 1e-3, 5e-3, 7.0e-3, 7.4e-3, 7.6e-3, 7.62e-3, 7.7e-3, 8.0e-3, 1e-2})
      {
        BRepAlgoAPI_Section sec(target, prismSweep.Shape(), Standard_False);
        if (fz > 0) sec.SetFuzzyValue(fz);
        sec.Approximation(Standard_True);
        sec.ComputePCurveOn1(Standard_True);
        sec.ComputePCurveOn2(Standard_True);
        sec.SetUseOBB(Standard_True);
        sec.Build();
        int ne = 0; for (TopExp_Explorer ex(sec.Shape(), TopAbs_EDGE); ex.More(); ex.Next()) ++ne;
        printf("    fuzzy=%-7g done=%d errors=%d edges=%d\n", fz, (int)sec.IsDone(), (int)sec.HasErrors(), ne);
      }
    }
  }
  return 0;
}
