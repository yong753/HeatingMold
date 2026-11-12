// Shared by the bridge and focused native fixtures. No viewer/model globals.
#pragma once
#include <GeomAPI.hxx>
#include <GeomProjLib.hxx>
#include <Geom_Plane.hxx>
#include <Geom_TrimmedCurve.hxx>
#include <GeomAdaptor_Curve.hxx>
#include <Geom2d_Curve.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Line.hxx>
#include <Geom2d_TrimmedCurve.hxx>
#include <Geom2dAPI_ProjectPointOnCurve.hxx>
#include <Geom2dAPI_InterCurveCurve.hxx>
#include <Geom2dInt_GInter.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <IntRes2d_IntersectionPoint.hxx>
#include <IntRes2d_IntersectionSegment.hxx>
#include <gp_Ax3.hxx>
#include <gp_Pln.hxx>
#include <gp_Vec2d.hxx>
#include <gp_Circ2d.hxx>
#include <Precision.hxx>
#include <Standard_Failure.hxx>
#include <algorithm>
#include <cmath>
#include <limits>
#include <vector>

#ifdef _MANAGED
#pragma managed(push, off)
#endif
namespace HeatingMoldExitNative
{
    // Geometry validation never uses the caller's cursor/snap distance.
    constexpr double GeometryEpsilon = 1.0e-7;
    constexpr double AngularEpsilon = 1.0e-10;
    enum class Status { Hit, Miss, Ambiguous, InvalidInput, StaleContext, GeometryFailure,
        TangentOrOverlap, DegenerateProjection, MissingReference };

    inline double ParameterEpsilon(double first, double last)
    {
        return std::max(Precision::PConfusion(), 32.0 * std::numeric_limits<double>::epsilon() *
            std::max(1.0, std::max(std::fabs(first), std::fabs(last))));
    }

    struct ProjectedCurve
    {
        Handle(Geom_Curve) original;
        Handle(Geom_Curve) onPlane;
        Handle(Geom2d_Curve) datum;
        gp_Pnt origin;
        gp_Dir x, y, normal;
        double first = 0.0, last = 0.0;
    };

    inline gp_Pnt2d Datum(const ProjectedCurve& curve, const gp_Pnt& point)
    {
        const gp_Vec relative(curve.origin, point);
        return gp_Pnt2d(relative.Dot(gp_Vec(curve.x)), relative.Dot(gp_Vec(curve.y)));
    }

    inline bool Finite(const gp_Pnt& p)
    { return std::isfinite(p.X()) && std::isfinite(p.Y()) && std::isfinite(p.Z()); }

    inline Status BuildProjectedCurve(const Handle(Geom_Curve)& original, double first, double last,
        const gp_Pnt& origin, const gp_Dir& x, const gp_Dir& y, const gp_Dir& normal, ProjectedCurve& output)
    {
        if (original.IsNull() || !std::isfinite(first) || !std::isfinite(last) || last <= first)
            return Status::GeometryFailure;
        try {
            ProjectedCurve result;
            result.original = original; result.first = first; result.last = last;
            result.origin = origin; result.x = x; result.y = y; result.normal = normal;
            const gp_Pln plane(gp_Ax3(origin, normal, x));
            const Handle(Geom_Plane) surface = new Geom_Plane(plane);
            Handle(Geom_Curve) trimmed = new Geom_TrimmedCurve(original, first, last, Standard_True, Standard_False);
            // In particular an inclined line must NOT become a unit-speed projected line.
            // KeepParametrization is the authoritative OCCT contract: proj(C(t)) = PC(t).
            GeomAdaptor_Curve source(trimmed);
            if (source.GetType() == GeomAbs_Line) {
                const gp_Vec direction(source.Line().Direction());
                if ((direction - gp_Vec(normal) * direction.Dot(gp_Vec(normal))).Magnitude() <= AngularEpsilon)
                    return Status::DegenerateProjection;
            }
            result.onPlane = GeomProjLib::ProjectOnPlane(trimmed, surface, normal, Standard_True);
            if (result.onPlane.IsNull()) return Status::GeometryFailure;
            GeomAdaptor_Curve projected(result.onPlane, first, last);
            const double length = GCPnts_AbscissaPoint::Length(projected, first, last, GeometryEpsilon * 0.1);
            if (!std::isfinite(length)) return Status::GeometryFailure;
            if (length <= GeometryEpsilon) return Status::DegenerateProjection;

            // To2d is a coordinate conversion, NOT a projection. Only call it on the
            // curve already projected onto this exact plane; unwrap trims explicitly.
            Handle(Geom_Curve) basis = result.onPlane;
            for (;;) {
                const Handle(Geom_TrimmedCurve) trim = Handle(Geom_TrimmedCurve)::DownCast(basis);
                if (trim.IsNull()) break;
                basis = trim->BasisCurve();
            }
            result.datum = GeomAPI::To2d(basis, plane);
            if (result.datum.IsNull()) return Status::GeometryFailure;
            // Fail closed for an unsupported conversion/reparameterization. These are
            // verification probes, not a replacement tessellation used for any query.
            for (int i = 0; i <= 16; ++i) {
                const double t = first + (last - first) * (i / 16.0);
                gp_Pnt world, planar; gp_Vec dw, dp;
                gp_Pnt2d p2; gp_Vec2d d2;
                original->D1(t, world, dw);
                result.onPlane->D1(t, planar, dp);
                result.datum->D1(t, p2, d2);
                if (!Finite(world) || !Finite(planar)) return Status::GeometryFailure;
                const gp_Pnt2d expected = Datum(result, world);
                if (p2.Distance(expected) > GeometryEpsilon || Datum(result, planar).Distance(expected) > GeometryEpsilon ||
                    std::fabs(gp_Vec(origin, planar).Dot(gp_Vec(normal))) > GeometryEpsilon)
                    return Status::GeometryFailure;
                const gp_Vec2d expectedDerivative(dw.Dot(gp_Vec(x)), dw.Dot(gp_Vec(y)));
                if ((d2 - expectedDerivative).Magnitude() > GeometryEpsilon * std::max(1.0, expectedDerivative.Magnitude()))
                    return Status::GeometryFailure;
            }
            output = result;
            return Status::Hit;
        } catch (const Standard_Failure&) { return Status::GeometryFailure; }
    }

    inline bool ValidTrim(const ProjectedCurve& curve, double first, double last)
    {
        // No periodic wrap or tolerance-based expansion of a saved trim.
        return std::isfinite(first) && std::isfinite(last) && last > first &&
            first >= curve.first && last <= curve.last;
    }

    inline bool SameSolution(const ProjectedCurve& curve, double a, double b)
    {
        const double pe = ParameterEpsilon(curve.first, curve.last);
        if (std::fabs(a - b) <= pe) return true;
        // Only the actual closed original endpoints are one solution. Coincident
        // datum positions at different depths or interior self-crossings are not.
        const bool endpoints = (std::fabs(a - curve.first) <= pe && std::fabs(b - curve.last) <= pe) ||
            (std::fabs(b - curve.first) <= pe && std::fabs(a - curve.last) <= pe);
        return endpoints && curve.original->Value(a).Distance(curve.original->Value(b)) <= GeometryEpsilon;
    }

    inline Status EvaluateParameter(const ProjectedCurve& curve, double first, double last, double parameter,
        gp_Pnt& point, gp_Vec& tangent, gp_Pnt2d& datum)
    {
        if (!ValidTrim(curve, first, last) || !std::isfinite(parameter)) return Status::InvalidInput;
        if (parameter < first || parameter > last) return Status::Miss;
        try {
            curve.original->D1(parameter, point, tangent);
            if (!Finite(point) || !std::isfinite(tangent.SquareMagnitude()) || tangent.SquareMagnitude() < 1e-24)
                return Status::GeometryFailure;
            const gp_Vec2d planarTangent(tangent.Dot(gp_Vec(curve.x)), tangent.Dot(gp_Vec(curve.y)));
            if (planarTangent.Magnitude() <= AngularEpsilon * tangent.Magnitude()) return Status::DegenerateProjection;
            datum = Datum(curve, point);
            if (datum.Distance(curve.datum->Value(parameter)) > GeometryEpsilon) return Status::GeometryFailure;
            tangent.Normalize();
            return Status::Hit;
        } catch (const Standard_Failure&) { return Status::GeometryFailure; }
    }

    inline Status NearestParameter(const ProjectedCurve& curve, double first, double last,
        const gp_Pnt2d& query, double snapDistance, double& parameter)
    {
        if (!ValidTrim(curve, first, last) || !std::isfinite(query.X()) || !std::isfinite(query.Y()) ||
            !std::isfinite(snapDistance) || snapDistance < 0.0) return Status::InvalidInput;
        try {
            Geom2dAdaptor_Curve adaptor(curve.datum, first, last);
            if (adaptor.GetType() == GeomAbs_Circle && query.Distance(adaptor.Circle().Location()) <= GeometryEpsilon)
                return adaptor.Circle().Radius() > snapDistance ? Status::Miss : Status::Ambiguous;
            Geom2dAPI_ProjectPointOnCurve projector(query, curve.datum, first, last);
            // Endpoints are always candidates, even with interior extrema or a closed curve.
            std::vector<double> candidates{first, last};
            for (int i = 1; i <= projector.NbPoints(); ++i) {
                const double t = projector.Parameter(i);
                if (!std::isfinite(t)) return Status::GeometryFailure;
                if (t >= first && t <= last) candidates.push_back(t);
            }
            if (!projector.Extrema().IsDone()) return Status::GeometryFailure;
            // A closed non-degenerate curve with no isolated stationary solution has
            // an indeterminate nearest solution (e.g. the centre of a circle).
            if (projector.NbPoints() == 0 && SameSolution(curve, first, last)) return Status::Ambiguous;
            double best = std::numeric_limits<double>::infinity();
            for (double t : candidates) best = std::min(best, query.Distance(curve.datum->Value(t)));
            if (!std::isfinite(best)) return Status::GeometryFailure;
            if (best > snapDistance) return Status::Miss;
            bool found = false;
            for (double t : candidates) {
                if (std::fabs(query.Distance(curve.datum->Value(t)) - best) > GeometryEpsilon) continue;
                if (found && !SameSolution(curve, parameter, t)) return Status::Ambiguous;
                if (!found || t < parameter) parameter = t;
                found = true;
            }
            if (!found) return Status::GeometryFailure;
            return Status::Hit;
        } catch (const Standard_Failure&) { return Status::GeometryFailure; }
    }

    inline Status IntersectRay(const ProjectedCurve& curve, double first, double last,
        const gp_Pnt2d& origin, double du, double dv, double tolerance, std::vector<double>& parameters)
    {
        parameters.clear();
        if (!ValidTrim(curve, first, last) || !std::isfinite(origin.X()) || !std::isfinite(origin.Y()) ||
            !std::isfinite(du) || !std::isfinite(dv) || !std::isfinite(tolerance) || tolerance < 0.0)
            return Status::InvalidInput;
        const double scale = std::max(std::fabs(du), std::fabs(dv));
        if (scale == 0.0) return Status::InvalidInput;
        gp_Vec2d direction(du / scale, dv / scale);
        direction.Normalize();
        try {
            const Handle(Geom2d_Curve) trim = new Geom2d_TrimmedCurve(curve.datum, first, last, Standard_True, Standard_False);
            const Handle(Geom2d_Curve) line = new Geom2d_Line(origin, gp_Dir2d(direction));
            // The caller's distance is NOT an intersection/geometry epsilon.
            Geom2dAPI_InterCurveCurve query(trim, line, GeometryEpsilon);
            const Geom2dInt_GInter& intersections = query.Intersector();
            if (!intersections.IsDone()) return Status::GeometryFailure;
            for (int i = 1; i <= intersections.NbSegments(); ++i) {
                const IntRes2d_IntersectionSegment& segment = intersections.Segment(i);
                // Ignore overlaps entirely behind the ray; an unbounded/forward
                // overlap is an explicit failure, never sampled into discrete hits.
                if (!segment.HasFirstPoint() || !segment.HasLastPoint() ||
                    std::max(segment.FirstPoint().ParamOnSecond(), segment.LastPoint().ParamOnSecond()) > GeometryEpsilon)
                    return Status::TangentOrOverlap;
            }
            auto append = [&](double t) -> Status {
                if (t < first || t > last) return Status::Hit;
                gp_Pnt2d point; gp_Vec2d tangent;
                curve.datum->D1(t, point, tangent);
                const gp_Vec2d offset(origin, point);
                const double forward = offset.Dot(direction);
                if (forward <= GeometryEpsilon) return Status::Hit;
                if (std::fabs(offset.Crossed(direction)) > GeometryEpsilon) return Status::GeometryFailure;
                if (!std::isfinite(tangent.SquareMagnitude()) || tangent.Magnitude() <= AngularEpsilon)
                    return Status::DegenerateProjection;
                if (std::fabs(tangent.Crossed(direction)) <= AngularEpsilon * tangent.Magnitude())
                    return Status::TangentOrOverlap;
                for (double previous : parameters) {
                    if (SameSolution(curve, previous, t)) return Status::Hit;
                    if (curve.datum->Value(previous).Distance(point) <= GeometryEpsilon) return Status::Ambiguous;
                }
                parameters.push_back(t);
                return Status::Hit;
            };
            for (int i = 1; i <= intersections.NbPoints(); ++i) {
                const Status status = append(intersections.Point(i).ParamOnFirst());
                if (status != Status::Hit) { parameters.clear(); return status; }
            }
            // OCCT may classify a trim endpoint as outside an intersection domain.
            // Compare both original endpoints independently, not by a chord.
            for (double t : {first, last}) {
                const gp_Vec2d offset(origin, curve.datum->Value(t));
                if (offset.Dot(direction) <= GeometryEpsilon || std::fabs(offset.Crossed(direction)) > GeometryEpsilon) continue;
                const Status status = append(t);
                if (status != Status::Hit) { parameters.clear(); return status; }
            }
            std::sort(parameters.begin(), parameters.end(), [&](double a, double b) {
                return gp_Vec2d(origin, curve.datum->Value(a)).Dot(direction) <
                    gp_Vec2d(origin, curve.datum->Value(b)).Dot(direction);
            });
            return parameters.empty() ? Status::Miss : Status::Hit;
        } catch (const Standard_Failure&) { parameters.clear(); return Status::GeometryFailure; }
    }

    // Full-line intersection: the unbounded line through a and b in both directions.
    // Split-interval construction needs the intersection exactly at a supplied end
    // point, which the ray query intentionally drops. There is no caller tolerance
    // here: only GeometryEpsilon decides two coincident solutions.
    inline Status IntersectLine(const ProjectedCurve& curve, double first, double last,
        const gp_Pnt2d& a, const gp_Pnt2d& b, std::vector<double>& parameters)
    {
        parameters.clear();
        if (!ValidTrim(curve, first, last) || !std::isfinite(a.X()) || !std::isfinite(a.Y()) ||
            !std::isfinite(b.X()) || !std::isfinite(b.Y())) return Status::InvalidInput;
        const gp_Vec2d direction(a, b);
        if (!std::isfinite(direction.Magnitude()) || direction.Magnitude() <= GeometryEpsilon)
            return Status::InvalidInput;
        const gp_Dir2d unit(direction);
        try {
            const Handle(Geom2d_Curve) trim = new Geom2d_TrimmedCurve(curve.datum, first, last, Standard_True, Standard_False);
            const Handle(Geom2d_Curve) line = new Geom2d_Line(a, unit);
            Geom2dAPI_InterCurveCurve query(trim, line, GeometryEpsilon);
            const Geom2dInt_GInter& intersections = query.Intersector();
            if (!intersections.IsDone()) return Status::GeometryFailure;
            // A collinear overlap means this curve runs along the line, so no isolated
            // interval boundary exists. Report it instead of sampling it into hits.
            if (intersections.NbSegments() > 0) return Status::TangentOrOverlap;
            std::vector<double> found;
            auto append = [&](double t) -> Status {
                if (t < first || t > last) return Status::Hit;
                gp_Pnt2d point; gp_Vec2d tangent;
                curve.datum->D1(t, point, tangent);
                if (std::fabs(gp_Vec2d(a, point).Crossed(unit)) > GeometryEpsilon) return Status::GeometryFailure;
                if (!std::isfinite(tangent.SquareMagnitude()) || tangent.Magnitude() <= AngularEpsilon)
                    return Status::DegenerateProjection;
                if (std::fabs(tangent.Crossed(gp_Vec2d(unit))) <= AngularEpsilon * tangent.Magnitude())
                    return Status::TangentOrOverlap;
                for (double previous : found) {
                    if (SameSolution(curve, previous, t)) return Status::Hit;
                    if (curve.datum->Value(previous).Distance(point) <= GeometryEpsilon) return Status::Ambiguous;
                }
                found.push_back(t);
                return Status::Hit;
            };
            for (int i = 1; i <= intersections.NbPoints(); ++i) {
                const Status status = append(intersections.Point(i).ParamOnFirst());
                if (status != Status::Hit) { parameters.clear(); return status; }
            }
            // OCCT may classify a trim endpoint as outside an intersection domain.
            for (double t : {first, last}) {
                if (std::fabs(gp_Vec2d(a, curve.datum->Value(t)).Crossed(unit)) > GeometryEpsilon) continue;
                const Status status = append(t);
                if (status != Status::Hit) { parameters.clear(); return status; }
            }
            std::sort(found.begin(), found.end());
            parameters = found;
            return parameters.empty() ? Status::Miss : Status::Hit;
        } catch (const Standard_Failure&) { parameters.clear(); return Status::GeometryFailure; }
    }
}
#ifdef _MANAGED
#pragma managed(pop)
#endif
