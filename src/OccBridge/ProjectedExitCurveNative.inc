// Shared by the bridge and focused native fixtures. No viewer/model globals.
//
// The working-plane boundary curve is the ORIGINAL 3D curve evaluated at the
// same parameter and projected by dot products with the confirmed X/Y axes.
// There is no second (approximated) 2D representation to disagree with, so an
// oblique/edge-on conic cannot fail a "projection residual" check any more.
// A curve whose whole trim collapses to one 2D point is reported by the query
// that actually needs it (Never by the shared cache build).
#pragma once
#include <Adaptor2d_Curve2d.hxx>
#include <Bnd_Box.hxx>
#include <BndLib_Add3dCurve.hxx>
#include <Extrema_ExtPC2d.hxx>
#include <Extrema_POnCurv2d.hxx>
#include <GeomAdaptor_Curve.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2d_Line.hxx>
#include <Geom2dInt_GInter.hxx>
#include <Geom_Circle.hxx>
#include <IntRes2d_Domain.hxx>
#include <IntRes2d_IntersectionPoint.hxx>
#include <IntRes2d_IntersectionSegment.hxx>
#include <gp_Circ2d.hxx>
#include <gp_Dir2d.hxx>
#include <gp_Pnt2d.hxx>
#include <gp_Vec2d.hxx>
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

    // Direct projection of the original curve. Every D0..DN evaluates the
    // original curve at the identical parameter and dots it with the confirmed
    // working-plane axes. Continuity/interval metadata is delegated to the
    // original curve so intersection algorithms see the real knot structure.
    class ProjectedCurveAdaptor : public Adaptor2d_Curve2d
    {
    public:
        ProjectedCurveAdaptor(const Handle(Geom_Curve)& original, double first, double last,
            const gp_Pnt& origin, const gp_Dir& x, const gp_Dir& y)
            : myOriginal(original), myFirst(first), myLast(last), myOrigin(origin), myX(x), myY(y) {}

        Handle(Adaptor2d_Curve2d) ShallowCopy() const override
        { return new ProjectedCurveAdaptor(myOriginal, myFirst, myLast, myOrigin, myX, myY); }

        Standard_Real FirstParameter() const override { return myFirst; }
        Standard_Real LastParameter() const override { return myLast; }

        GeomAbs_Shape Continuity() const override { return CurveAdaptor().Continuity(); }
        Standard_Integer NbIntervals(const GeomAbs_Shape s) const override
        { return CurveAdaptor().NbIntervals(s); }
        void Intervals(TColStd_Array1OfReal& t, const GeomAbs_Shape s) const override
        { CurveAdaptor().Intervals(t, s); }

        Handle(Adaptor2d_Curve2d) Trim(Standard_Real first, Standard_Real last, Standard_Real) const override
        {
            if (last < first) std::swap(first, last);
            double lo = std::max(myFirst, first);
            double hi = std::min(myLast, last);
            if (hi < lo) { lo = myFirst; hi = myFirst; }
            return new ProjectedCurveAdaptor(myOriginal, lo, hi, myOrigin, myX, myY);
        }

        Standard_Boolean IsClosed() const override
        {
            // Closure is a property of the original boundary edge, not of a
            // projected coincidence: a folded 3D curve may share one 2D point
            // at different depths and must stay an open curve.
            if (!myOriginal->IsClosed()) return Standard_False;
            const double pe = ParameterEpsilon(myFirst, myLast);
            return std::fabs(myFirst - myOriginal->FirstParameter()) <= pe &&
                std::fabs(myLast - myOriginal->LastParameter()) <= pe;
        }
        Standard_Boolean IsPeriodic() const override { return Standard_False; }
        Standard_Real Period() const override
        {
            try { return myOriginal->IsPeriodic() ? myOriginal->Period() : 0.0; }
            catch (const Standard_Failure&) { return 0.0; }
        }

        gp_Pnt2d Value(const Standard_Real u) const override
        { gp_Pnt p; myOriginal->D0(u, p); return Project(p); }
        void D0(const Standard_Real u, gp_Pnt2d& p) const override
        { gp_Pnt world; myOriginal->D0(u, world); p = Project(world); }
        void D1(const Standard_Real u, gp_Pnt2d& p, gp_Vec2d& v) const override
        { gp_Pnt world; gp_Vec dw; myOriginal->D1(u, world, dw); p = Project(world); v = Project(dw); }
        void D2(const Standard_Real u, gp_Pnt2d& p, gp_Vec2d& v1, gp_Vec2d& v2) const override
        { gp_Pnt world; gp_Vec a, b; myOriginal->D2(u, world, a, b); p = Project(world); v1 = Project(a); v2 = Project(b); }
        void D3(const Standard_Real u, gp_Pnt2d& p, gp_Vec2d& v1, gp_Vec2d& v2, gp_Vec2d& v3) const override
        { gp_Pnt world; gp_Vec a, b, c; myOriginal->D3(u, world, a, b, c); p = Project(world); v1 = Project(a); v2 = Project(b); v3 = Project(c); }
        gp_Vec2d DN(const Standard_Real u, const Standard_Integer n) const override
        { return Project(myOriginal->DN(u, n)); }

        Standard_Real Resolution(const Standard_Real r3d) const override
        { return CurveAdaptor().Resolution(r3d); }
        GeomAbs_CurveType GetType() const override { return GeomAbs_OtherCurve; }
        Standard_Integer Degree() const override { return 0; }
        Standard_Boolean IsRational() const override { return Standard_False; }
        Standard_Integer NbPoles() const override { return 0; }
        Standard_Integer NbKnots() const override { return 0; }
        Standard_Integer NbSamples() const override { return 100; }

    private:
        GeomAdaptor_Curve CurveAdaptor() const
        { return GeomAdaptor_Curve(myOriginal, myFirst, myLast); }
        gp_Pnt2d Project(const gp_Pnt& p) const
        {
            const gp_Vec relative(myOrigin, p);
            return gp_Pnt2d(relative.Dot(gp_Vec(myX)), relative.Dot(gp_Vec(myY)));
        }
        gp_Vec2d Project(const gp_Vec& v) const
        { return gp_Vec2d(v.Dot(gp_Vec(myX)), v.Dot(gp_Vec(myY))); }

        Handle(Geom_Curve) myOriginal;
        double myFirst = 0.0, myLast = 0.0;
        gp_Pnt myOrigin;
        gp_Dir myX, myY;
    };

    struct ProjectedCurve
    {
        Handle(Geom_Curve) original;
        Handle(Adaptor2d_Curve2d) datum;
        gp_Pnt origin;
        gp_Dir x, y, normal;
        double first = 0.0, last = 0.0;
        // Conservative 2D bounds from the original 3D bounding box. They are a
        // broad phase only: an unknown bound is never used to skip an edge.
        bool hasBounds = false;
        bool collapsed = false;
        double minU = 0.0, minV = 0.0, maxU = 0.0, maxV = 0.0;
        // All trim points of a working-plane-parallel circle are equidistant
        // from the projected centre; that stays an explicit ambiguity.
        bool circleCentre = false;
        gp_Pnt2d centre;
        double radius = 0.0;
    };

    inline gp_Pnt2d Datum(const ProjectedCurve& curve, const gp_Pnt& point)
    {
        const gp_Vec relative(curve.origin, point);
        return gp_Pnt2d(relative.Dot(gp_Vec(curve.x)), relative.Dot(gp_Vec(curve.y)));
    }

    inline bool Finite(const gp_Pnt& p)
    { return std::isfinite(p.X()) && std::isfinite(p.Y()) && std::isfinite(p.Z()); }

    inline bool ComputeBounds(const ProjectedCurve& curve, double first, double last,
        double& minU, double& minV, double& maxU, double& maxV)
    {
        try {
            GeomAdaptor_Curve adaptor(curve.original, first, last);
            Bnd_Box box;
            BndLib_Add3dCurve::Add(adaptor, 0.0, box);
            if (box.IsVoid()) return false;
            double xa, ya, za, xb, yb, zb;
            box.Get(xa, ya, za, xb, yb, zb);
            minU = minV = std::numeric_limits<double>::infinity();
            maxU = maxV = -std::numeric_limits<double>::infinity();
            for (int i = 0; i < 8; ++i) {
                const gp_Pnt corner((i & 1) ? xb : xa, (i & 2) ? yb : ya, (i & 4) ? zb : za);
                const gp_Vec relative(curve.origin, corner);
                const double u = relative.Dot(gp_Vec(curve.x));
                const double v = relative.Dot(gp_Vec(curve.y));
                minU = std::min(minU, u); maxU = std::max(maxU, u);
                minV = std::min(minV, v); maxV = std::max(maxV, v);
            }
            return std::isfinite(minU) && std::isfinite(minV) &&
                std::isfinite(maxU) && std::isfinite(maxV);
        } catch (const Standard_Failure&) { return false; }
    }

    inline Status BuildProjectedCurve(const Handle(Geom_Curve)& original, double first, double last,
        const gp_Pnt& origin, const gp_Dir& x, const gp_Dir& y, const gp_Dir& normal, ProjectedCurve& output)
    {
        if (original.IsNull() || !std::isfinite(first) || !std::isfinite(last) || last <= first)
            return Status::GeometryFailure;
        try {
            ProjectedCurve result;
            result.original = original; result.first = first; result.last = last;
            result.origin = origin; result.x = x; result.y = y; result.normal = normal;
            result.hasBounds = ComputeBounds(result, first, last,
                result.minU, result.minV, result.maxU, result.maxV);
            result.collapsed = result.hasBounds &&
                (result.maxU - result.minU <= GeometryEpsilon) &&
                (result.maxV - result.minV <= GeometryEpsilon);
            result.datum = new ProjectedCurveAdaptor(original, first, last, origin, x, y);
            const Handle(Geom_Circle) circle = Handle(Geom_Circle)::DownCast(original);
            if (!circle.IsNull() &&
                gp_Vec(circle->Axis().Direction()).Crossed(gp_Vec(normal)).Magnitude() <= AngularEpsilon) {
                result.circleCentre = true;
                result.centre = Datum(result, circle->Location());
                result.radius = circle->Radius();
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

    inline Handle(Adaptor2d_Curve2d) TrimmedDatum(const ProjectedCurve& curve, double first, double last)
    { return curve.datum->Trim(first, last, Precision::Confusion()); }

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

    // An interior extremum that lies on the original edge endpoint (a numeric
    // double solution of the same physical point) is reported as that endpoint.
    // The parameter window is local to the endpoint; no global epsilon change.
    inline double SnapToEndpoint(const ProjectedCurve& curve, double t)
    {
        const double window = std::max(ParameterEpsilon(curve.first, curve.last),
            1.0e-6 * std::fabs(curve.last - curve.first));
        const double pe = ParameterEpsilon(curve.first, curve.last);
        if (std::fabs(t - curve.first) > pe && std::fabs(t - curve.first) <= window &&
            curve.original->Value(t).Distance(curve.original->Value(curve.first)) <= GeometryEpsilon)
            return curve.first;
        if (std::fabs(t - curve.last) > pe && std::fabs(t - curve.last) <= window &&
            curve.original->Value(t).Distance(curve.original->Value(curve.last)) <= GeometryEpsilon)
            return curve.last;
        return t;
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
            if (curve.circleCentre && query.Distance(curve.centre) <= GeometryEpsilon)
                return curve.radius > snapDistance ? Status::Miss : Status::Ambiguous;
            if (curve.collapsed) return Status::DegenerateProjection;
            const Handle(Adaptor2d_Curve2d) datum = TrimmedDatum(curve, first, last);
            Extrema_ExtPC2d projector(query, *datum, first, last);
            if (!projector.IsDone()) return Status::GeometryFailure;
            // Endpoints are always candidates, even with interior extrema or a closed curve.
            std::vector<double> candidates{first, last};
            for (int i = 1; i <= projector.NbExt(); ++i) {
                const double t = projector.Point(i).Parameter();
                if (!std::isfinite(t)) return Status::GeometryFailure;
                if (t >= first && t <= last) candidates.push_back(SnapToEndpoint(curve, t));
            }
            // A closed non-degenerate curve with no isolated stationary solution has
            // an indeterminate nearest solution (e.g. the centre of a circle).
            if (projector.NbExt() == 0 && SameSolution(curve, first, last)) return Status::Ambiguous;
            double best = std::numeric_limits<double>::infinity();
            for (double t : candidates) best = std::min(best, query.Distance(datum->Value(t)));
            if (!std::isfinite(best)) return Status::GeometryFailure;
            if (best > snapDistance) return Status::Miss;
            bool found = false;
            for (double t : candidates) {
                if (std::fabs(query.Distance(datum->Value(t)) - best) > GeometryEpsilon) continue;
                if (found && !SameSolution(curve, parameter, t)) return Status::Ambiguous;
                if (!found || t < parameter) parameter = t;
                found = true;
            }
            if (!found) return Status::GeometryFailure;
            return Status::Hit;
        } catch (const Standard_Failure&) { return Status::GeometryFailure; }
    }

    // A segment long enough to cross every conservative bound of this curve.
    inline double QuerySpan(const ProjectedCurve& curve, const gp_Pnt2d& origin)
    {
        double reach = std::max({ std::fabs(origin.X()), std::fabs(origin.Y()), 1.0 });
        if (curve.hasBounds) reach = std::max({ reach, std::fabs(curve.minU), std::fabs(curve.maxU),
            std::fabs(curve.minV), std::fabs(curve.maxV) });
        return reach * 4.0 + 1.0e3;
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
            if (curve.collapsed) return Status::DegenerateProjection;
            const Handle(Adaptor2d_Curve2d) datum = TrimmedDatum(curve, first, last);
            const gp_Pnt2d p0 = datum->Value(first);
            const gp_Pnt2d p1 = datum->Value(last);
            IntRes2d_Domain domain1(p0, first, GeometryEpsilon, p1, last, GeometryEpsilon);
            if (datum->IsClosed()) domain1.SetEquivalentParameters(first, last);
            const double span = QuerySpan(curve, origin);
            Geom2dAdaptor_Curve line(new Geom2d_Line(origin, gp_Dir2d(direction)), -span, span);
            const gp_Pnt2d a(origin.X() - direction.X() * span, origin.Y() - direction.Y() * span);
            const gp_Pnt2d b(origin.X() + direction.X() * span, origin.Y() + direction.Y() * span);
            const IntRes2d_Domain domain2(a, -span, GeometryEpsilon, b, span, GeometryEpsilon);
            // The caller's distance is NOT an intersection/geometry epsilon.
            Geom2dInt_GInter intersections(*datum, domain1, line, domain2, GeometryEpsilon, GeometryEpsilon);
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
                const gp_Pnt2d point = datum->Value(t);
                const gp_Vec2d offset(origin, point);
                const double forward = offset.Dot(direction);
                if (forward <= GeometryEpsilon) return Status::Hit;
                if (std::fabs(offset.Crossed(direction)) > GeometryEpsilon) return Status::GeometryFailure;
                gp_Vec2d tangent;
                { gp_Pnt2d p; datum->D1(t, p, tangent); }
                if (!std::isfinite(tangent.SquareMagnitude()) || tangent.Magnitude() <= AngularEpsilon)
                    return Status::DegenerateProjection;
                if (std::fabs(tangent.Crossed(direction)) <= AngularEpsilon * tangent.Magnitude())
                    return Status::TangentOrOverlap;
                for (double previous : parameters) {
                    if (SameSolution(curve, previous, t)) return Status::Hit;
                    if (datum->Value(previous).Distance(point) <= GeometryEpsilon) return Status::Ambiguous;
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
                const gp_Vec2d offset(origin, datum->Value(t));
                if (offset.Dot(direction) <= GeometryEpsilon || std::fabs(offset.Crossed(direction)) > GeometryEpsilon) continue;
                const Status status = append(t);
                if (status != Status::Hit) { parameters.clear(); return status; }
            }
            std::sort(parameters.begin(), parameters.end(), [&](double a, double b) {
                return gp_Vec2d(origin, datum->Value(a)).Dot(direction) <
                    gp_Vec2d(origin, datum->Value(b)).Dot(direction);
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
        try {
            if (curve.collapsed) return Status::DegenerateProjection;
            const gp_Dir2d unit(direction);
            const Handle(Adaptor2d_Curve2d) datum = TrimmedDatum(curve, first, last);
            const gp_Pnt2d p0 = datum->Value(first);
            const gp_Pnt2d p1 = datum->Value(last);
            IntRes2d_Domain domain1(p0, first, GeometryEpsilon, p1, last, GeometryEpsilon);
            if (datum->IsClosed()) domain1.SetEquivalentParameters(first, last);
            const double span = QuerySpan(curve, a);
            Geom2dAdaptor_Curve line(new Geom2d_Line(a, unit), -span, span);
            const gp_Pnt2d start(a.X() - unit.X() * span, a.Y() - unit.Y() * span);
            const gp_Pnt2d end(a.X() + unit.X() * span, a.Y() + unit.Y() * span);
            const IntRes2d_Domain domain2(start, -span, GeometryEpsilon, end, span, GeometryEpsilon);
            Geom2dInt_GInter intersections(*datum, domain1, line, domain2, GeometryEpsilon, GeometryEpsilon);
            if (!intersections.IsDone()) return Status::GeometryFailure;
            // A collinear overlap means this curve runs along the line, so no isolated
            // interval boundary exists. Report it instead of sampling it into hits.
            if (intersections.NbSegments() > 0) return Status::TangentOrOverlap;
            std::vector<double> found;
            auto append = [&](double t) -> Status {
                if (t < first || t > last) return Status::Hit;
                const gp_Pnt2d point = datum->Value(t);
                if (std::fabs(gp_Vec2d(a, point).Crossed(gp_Vec2d(unit))) > GeometryEpsilon)
                    return Status::GeometryFailure;
                gp_Vec2d tangent;
                { gp_Pnt2d p; datum->D1(t, p, tangent); }
                if (!std::isfinite(tangent.SquareMagnitude()) || tangent.Magnitude() <= AngularEpsilon)
                    return Status::DegenerateProjection;
                if (std::fabs(tangent.Crossed(gp_Vec2d(unit))) <= AngularEpsilon * tangent.Magnitude())
                    return Status::TangentOrOverlap;
                for (double previous : found) {
                    if (SameSolution(curve, previous, t)) return Status::Hit;
                    if (datum->Value(previous).Distance(point) <= GeometryEpsilon) return Status::Ambiguous;
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
                if (std::fabs(gp_Vec2d(a, datum->Value(t)).Crossed(gp_Vec2d(unit))) > GeometryEpsilon) continue;
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
