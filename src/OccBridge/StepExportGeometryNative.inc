// STEP export geometry building for piece-level output.
//
// Each route piece (heating lane or turn connector) becomes ONE approximated
// centreline curve and ONE swept wire solid.  The approximation is allowed to
// deviate from the committed sample points; the deviation, length and ordering
// are verified explicitly instead of silently trusting the fit.
//
// This file is plain OCCT code so it can also be compiled stand-alone by the
// diagnostics probe.  It is included by OccBridgeApi.cpp before
// StepExportNative.inc.

#ifndef STEP_EXPORT_INCLUDES_DONE
#include <algorithm>
#include <cmath>
#include <limits>
#include <string>
#include <vector>

#include <Standard_Failure.hxx>
#include <Standard_Real.hxx>
#include <gp_Pnt.hxx>
#include <gp_Vec.hxx>
#include <gp_Dir.hxx>
#include <gp_Circ.hxx>
#include <gp_Ax2.hxx>
#include <gp_Lin.hxx>
#include <TColgp_Array1OfPnt.hxx>
#include <TColStd_Array1OfReal.hxx>
#include <TColStd_Array1OfInteger.hxx>
#include <Geom_Curve.hxx>
#include <Geom_Line.hxx>
#include <Geom_TrimmedCurve.hxx>
#include <Geom_BSplineCurve.hxx>
#include <GeomAdaptor_Curve.hxx>
#include <GeomAPI_ProjectPointOnCurve.hxx>
#include <AppDef_MultiLine.hxx>
#include <AppDef_Variational.hxx>
#include <AppParCurves_Constraint.hxx>
#include <AppParCurves_ConstraintCouple.hxx>
#include <AppParCurves_HArray1OfConstraintCouple.hxx>
#include <AppParCurves_MultiBSpCurve.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepOffsetAPI_MakePipeShell.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Wire.hxx>
#include <TopExp_Explorer.hxx>
#include <TopAbs.hxx>
#include <TopAbs_ShapeEnum.hxx>

#endif

namespace step_export_geometry
{
    // Deviation/length policy for one piece approximation.  These are explicit
    // product limits, not values that may be relaxed silently on failure.
    const Standard_Real PieceDeviationTolerance = 0.10;  // mm
    const Standard_Real PieceLengthTolerance = 1.0;      // mm
    const Standard_Integer PiecePoleLimit = 32;
    const Standard_Integer PieceFitDegree = 6;
    const Standard_Integer PieceFitSegments = 24;
    const Standard_Real PieceFitTolerance = 0.05;
    const Standard_Integer PieceFitIterations = 2;
    const Standard_Real PieceStraightTolerance = 0.02;   // mm
    const Standard_Integer PieceCurveSamples = 2000;

    struct NativeStepExportPiece
    {
        std::wstring name;
        std::vector<double> points;
    };

    struct NativeStepExportArea
    {
        std::wstring name;
        std::wstring areaName;
        int r = 0;
        int g = 0;
        int b = 0;
        std::vector<NativeStepExportPiece> pieces;

        // 발열영역 표시 정보. faceIds/frame/ring 중 하나라도 비어 있으면 표시
        // 정보가 없는 영역이며 면 색만 적용하지 않는다.
        std::vector<int> faceIds;
        std::vector<double> frame;  // origin(3), axisX(3), axisY(3)
        std::vector<double> ring;   // u,v 쌍 flat

        bool HasRegion() const
        {
            return !faceIds.empty() && frame.size() == 9 && ring.size() >= 6;
        }
    };

    struct NativeStepExportBuiltPiece
    {
        std::wstring name;
        TopoDS_Edge centreline;
        TopoDS_Shape wire;
    };

    inline Standard_Real PointSegmentDistance(const gp_Pnt& p,
                                              const gp_Pnt& a,
                                              const gp_Pnt& b)
    {
        gp_Vec ab(a, b);
        const Standard_Real len2 = ab.SquareMagnitude();
        if (len2 <= 1e-18)
        {
            return p.Distance(a);
        }
        gp_Vec ap(a, p);
        Standard_Real t = ap.Dot(ab) / len2;
        if (t < 0.0) t = 0.0;
        if (t > 1.0) t = 1.0;
        const gp_Pnt q = a.Translated(gp_Vec(ab.X() * t, ab.Y() * t, ab.Z() * t));
        return p.Distance(q);
    }

    inline Standard_Real PolylineLength(const std::vector<gp_Pnt>& pts)
    {
        Standard_Real total = 0.0;
        for (size_t i = 1; i < pts.size(); ++i)
        {
            total += pts[i - 1].Distance(pts[i]);
        }
        return total;
    }

    inline bool DecodePoints(const std::vector<double>& flat,
                             std::vector<gp_Pnt>& pts)
    {
        pts.clear();
        if (flat.size() < 6 || (flat.size() % 3) != 0)
        {
            return false;
        }
        pts.reserve(flat.size() / 3);
        for (size_t i = 0; i + 2 < flat.size(); i += 3)
        {
            const double x = flat[i];
            const double y = flat[i + 1];
            const double z = flat[i + 2];
            if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z))
            {
                return false;
            }
            const gp_Pnt p(x, y, z);
            if (!pts.empty())
            {
                if (pts.back().Distance(p) <= 1e-9)
                {
                    continue; // exact duplicate sample
                }
            }
            pts.push_back(p);
        }
        return pts.size() >= 2;
    }

    inline bool NearlyStraight(const std::vector<gp_Pnt>& pts,
                               Standard_Real& chordLength)
    {
        chordLength = 0.0;
        if (pts.size() < 3)
        {
            if (pts.size() == 2)
            {
                chordLength = pts[0].Distance(pts[1]);
                return chordLength > 1e-9;
            }
            return false;
        }
        gp_Vec chord(pts.front(), pts.back());
        const Standard_Real len = chord.Magnitude();
        if (len <= 1e-9)
        {
            return false;
        }
        const gp_Dir dir(chord);
        const gp_Vec unit(dir);
        for (size_t i = 1; i + 1 < pts.size(); ++i)
        {
            const gp_Vec v(pts.front(), pts[i]);
            const gp_Vec cross = v.Crossed(unit);
            if (cross.Magnitude() > PieceStraightTolerance)
            {
                return false;
            }
            const Standard_Real t = v.Dot(unit);
            if (t < -PieceStraightTolerance || t > len + PieceStraightTolerance)
            {
                return false;
            }
        }
        chordLength = len;
        return true;
    }

    // Verifies the built curve against the original polyline: deviation in both
    // directions, length difference, monotone progression and exact endpoints.
    inline bool VerifyPieceCurve(const Handle(Geom_Curve)& curve,
                                 const std::vector<gp_Pnt>& pts,
                                 std::string& reason)
    {
        if (curve.IsNull() || pts.size() < 2)
        {
            reason = "points";
            return false;
        }

        const Standard_Real u0 = curve->FirstParameter();
        const Standard_Real u1 = curve->LastParameter();
        if (!(u1 > u0))
        {
            reason = "points";
            return false;
        }

        // Endpoint identity (the first/last committed sample must be kept).
        if (curve->Value(u0).Distance(pts.front()) > 1e-6 ||
            curve->Value(u1).Distance(pts.back()) > 1e-6)
        {
            reason = "endpoint";
            return false;
        }

        // curve -> polyline
        Standard_Real curveToPoly = 0.0;
        for (Standard_Integer i = 0; i <= PieceCurveSamples; ++i)
        {
            const Standard_Real u =
                u0 + (u1 - u0) * (static_cast<Standard_Real>(i) / PieceCurveSamples);
            const gp_Pnt p = curve->Value(u);
            Standard_Real best = std::numeric_limits<Standard_Real>::max();
            for (size_t k = 1; k < pts.size(); ++k)
            {
                const Standard_Real d = PointSegmentDistance(p, pts[k - 1], pts[k]);
                if (d < best) best = d;
            }
            if (best > curveToPoly) curveToPoly = best;
            if (curveToPoly > PieceDeviationTolerance)
            {
                reason = "deviation";
                return false;
            }
        }

        // polyline -> curve, plus progression order.
        Standard_Real polyToCurve = 0.0;
        Standard_Real previousU = -std::numeric_limits<Standard_Real>::max();
        for (size_t i = 0; i < pts.size(); ++i)
        {
            GeomAPI_ProjectPointOnCurve project(pts[i], curve);
            if (project.NbPoints() <= 0)
            {
                reason = "deviation";
                return false;
            }
            const Standard_Real d = project.LowerDistance();
            if (d > polyToCurve) polyToCurve = d;
            if (polyToCurve > PieceDeviationTolerance)
            {
                reason = "deviation";
                return false;
            }
            const Standard_Real u = project.LowerDistanceParameter();
            if (u + 1e-6 < previousU)
            {
                reason = "order";
                return false;
            }
            previousU = u;
        }
        if (previousU < u0 || previousU > u1 + 1e-6)
        {
            reason = "order";
            return false;
        }

        // Length difference.
        GeomAdaptor_Curve adaptor(curve);
        const Standard_Real curveLength = GCPnts_AbscissaPoint::Length(adaptor);
        const Standard_Real polyLength = PolylineLength(pts);
        if (!(curveLength > 1e-9) ||
            std::fabs(curveLength - polyLength) > PieceLengthTolerance)
        {
            reason = "length";
            return false;
        }
        return true;
    }

    // Builds the piece centreline.  Nearly straight pieces become a trimmed
    // line; otherwise a constrained B-spline keeps both endpoints and is
    // verified against every original sample.
    inline bool BuildPieceCurveNative(const std::vector<double>& flat,
                                      Handle(Geom_Curve)& curve,
                                      std::string& reason)
    {
        reason.clear();
        std::vector<gp_Pnt> pts;
        if (!DecodePoints(flat, pts))
        {
            reason = "points";
            return false;
        }

        Standard_Real chord = 0.0;
        if (NearlyStraight(pts, chord))
        {
            const gp_Vec chordVec(pts.front(), pts.back());
            const gp_Dir dir(chordVec);
            const Handle(Geom_Line) line = new Geom_Line(gp_Lin(pts.front(), dir));
            curve = new Geom_TrimmedCurve(line, 0.0, chord);
            if (!VerifyPieceCurve(curve, pts, reason))
            {
                curve.Nullify();
                return false;
            }
            return true;
        }

        const Standard_Integer n = static_cast<Standard_Integer>(pts.size());
        TColgp_Array1OfPnt tab(1, n);
        for (Standard_Integer i = 1; i <= n; ++i)
        {
            tab.SetValue(i, pts[static_cast<size_t>(i - 1)]);
        }
        AppDef_MultiLine line(tab);

        Handle(AppParCurves_HArray1OfConstraintCouple) constraints =
            new AppParCurves_HArray1OfConstraintCouple(1, 2);
        constraints->SetValue(1,
            AppParCurves_ConstraintCouple(1, AppParCurves_PassPoint));
        constraints->SetValue(2,
            AppParCurves_ConstraintCouple(n, AppParCurves_PassPoint));

        AppDef_Variational fit(line, 1, n, constraints,
                               PieceFitDegree, PieceFitSegments, GeomAbs_C1,
                               Standard_False, Standard_True,
                               PieceFitTolerance, PieceFitIterations);
        if (fit.IsOverConstrained())
        {
            reason = "fit";
            return false;
        }
        fit.Approximate();
        if (!fit.IsCreated() || !fit.IsDone())
        {
            reason = "fit";
            return false;
        }

        const AppParCurves_MultiBSpCurve value = fit.Value();
        if (value.NbCurves() != 1)
        {
            reason = "fit-segments";
            return false;
        }
        const Standard_Integer poles = value.NbPoles();
        if (poles < 2 || poles > PiecePoleLimit)
        {
            reason = "poles";
            return false;
        }
        TColgp_Array1OfPnt poleArray(1, poles);
        for (Standard_Integer i = 1; i <= poles; ++i)
        {
            poleArray.SetValue(i, value.Pole(1, i));
        }
        const TColStd_Array1OfReal& knots = value.Knots();
        const TColStd_Array1OfInteger& mults = value.Multiplicities();
        if (knots.Length() != mults.Length() || knots.Length() < 2)
        {
            reason = "fit";
            return false;
        }
        TColStd_Array1OfReal knotArray(1, knots.Length());
        TColStd_Array1OfInteger multArray(1, mults.Length());
        for (Standard_Integer i = 1; i <= knots.Length(); ++i)
        {
            knotArray.SetValue(i, knots.Value(knots.Lower() + i - 1));
            multArray.SetValue(i, mults.Value(mults.Lower() + i - 1));
        }

        curve = new Geom_BSplineCurve(poleArray, knotArray, multArray,
                                      value.Degree(), Standard_False);
        std::string verifyReason;
        if (!VerifyPieceCurve(curve, pts, verifyReason))
        {
            reason = verifyReason.empty() ? "fit" : verifyReason;
            curve.Nullify();
            return false;
        }
        return true;
    }

    inline Standard_Integer CountSubShapes(const TopoDS_Shape& shape,
                                           TopAbs_ShapeEnum type)
    {
        Standard_Integer count = 0;
        if (shape.IsNull()) return 0;
        for (TopExp_Explorer explorer(shape, type); explorer.More(); explorer.Next())
        {
            ++count;
        }
        return count;
    }

    inline Standard_Real SolidVolume(const TopoDS_Shape& shape)
    {
        GProp_GProps props;
        BRepGProp::VolumeProperties(shape, props);
        return props.Mass();
    }

    // Sweeps the circular profile along the piece centreline.  The corrected
    // frame is the normal path; a long, gently curved lane can make that frame
    // numerically fail even though the geometry is smooth (observed: a 642 mm
    // lane whose maximum sample-to-sample turn is 0.086 deg).  Frenet is tried
    // once as a frame-only fallback: the centreline, profile radius and solid
    // acceptance checks are unchanged.
    inline bool MakeSweptSolidNative(const TopoDS_Wire& spine,
                                     const TopoDS_Wire& profile,
                                     const Standard_Boolean frenet,
                                     TopoDS_Shape& wire)
    {
        BRepOffsetAPI_MakePipeShell shell(spine);
        shell.SetMode(frenet);
        shell.SetTolerance(1e-4, 1e-4, 1e-2);
        shell.SetMaxDegree(8);
        shell.SetMaxSegments(32);
        shell.Add(profile, Standard_False, Standard_False);
        shell.Build();
        if (!shell.IsDone())
        {
            return false;
        }
        shell.MakeSolid();
        wire = shell.Shape();
        return !wire.IsNull();
    }

    // Builds the centreline edge and its swept wire solid.  The result must be
    // exactly one valid closed solid; a compound fallback is deliberately not
    // provided, because it would hide a failed sweep.
    inline bool BuildPieceAxisNative(const std::vector<double>& flat,
                                     const Standard_Real radius,
                                     TopoDS_Edge& centreline,
                                     TopoDS_Shape& wire,
                                     std::string& reason)
    {
        reason.clear();
        if (!(radius > 0.0) || !std::isfinite(radius))
        {
            reason = "radius";
            return false;
        }
        std::vector<gp_Pnt> pts;
        if (!DecodePoints(flat, pts))
        {
            reason = "points";
            return false;
        }

        Handle(Geom_Curve) curve;
        std::string curveReason;
        if (!BuildPieceCurveNative(flat, curve, curveReason))
        {
            reason = curveReason.empty() ? "fit" : curveReason;
            return false;
        }

        BRepBuilderAPI_MakeEdge edgeMaker(curve);
        if (!edgeMaker.IsDone())
        {
            reason = "edge";
            return false;
        }
        centreline = edgeMaker.Edge();

        BRepBuilderAPI_MakeWire wireMaker(centreline);
        if (!wireMaker.IsDone())
        {
            reason = "edge";
            return false;
        }
        const TopoDS_Wire spine = wireMaker.Wire();

        // Profile: circle at the start point, perpendicular to the tangent.
        const Standard_Real u0 = curve->FirstParameter();
        gp_Pnt start;
        gp_Vec tangent;
        curve->D1(u0, start, tangent);
        if (tangent.SquareMagnitude() <= 1e-18)
        {
            reason = "edge";
            return false;
        }
        const gp_Dir tangentDir(tangent);
        gp_Circ profileCircle(gp_Ax2(start, tangentDir), radius);
        BRepBuilderAPI_MakeEdge profileEdge(profileCircle);
        if (!profileEdge.IsDone())
        {
            reason = "sweep";
            return false;
        }
        BRepBuilderAPI_MakeWire profileWireMaker(profileEdge.Edge());
        if (!profileWireMaker.IsDone())
        {
            reason = "sweep";
            return false;
        }

        TopoDS_Shape swept;
        if (!MakeSweptSolidNative(spine, profileWireMaker.Wire(), Standard_False, swept) &&
            !MakeSweptSolidNative(spine, profileWireMaker.Wire(), Standard_True, swept))
        {
            reason = "sweep";
            return false;
        }
        wire = swept;

        if (CountSubShapes(wire, TopAbs_SOLID) != 1)
        {
            reason = "solid";
            return false;
        }
        if (!BRepCheck_Analyzer(wire, Standard_True, Standard_False, Standard_False).IsValid())
        {
            reason = "solid";
            return false;
        }
        if (!(SolidVolume(wire) > 0.0))
        {
            reason = "volume";
            return false;
        }
        return true;
    }
}
