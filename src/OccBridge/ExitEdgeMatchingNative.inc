#pragma once
#include <BRep_Tool.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <GeomAPI_ProjectPointOnCurve.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Edge.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include "ProjectedExitCurveNative.inc"

#ifdef _MANAGED
#pragma managed(push, off)
#endif
namespace HeatingMoldExitNative
{
    struct SourceInterval
    {
        TopoDS_Edge edge;
        double first = 0.0, last = 0.0;
        bool reversed = false; // boundary curve's increasing parameter versus original
    };

    inline double CurvePointDistance(const gp_Pnt& point, const Handle(Geom_Curve)& curve, double first, double last)
    {
        // Extrema do not necessarily include endpoints; a line endpoint often has NbPoints==0.
        double distance = std::min(point.Distance(curve->Value(first)), point.Distance(curve->Value(last)));
        GeomAPI_ProjectPointOnCurve query(point, curve, first, last);
        if (!query.Extrema().IsDone()) return std::numeric_limits<double>::infinity();
        for (int i = 1; i <= query.NbPoints(); ++i) distance = std::min(distance, query.Distance(i));
        return distance;
    }

    inline bool Closest3dParameters(const gp_Pnt& point, const Handle(Geom_Curve)& curve,
        double first, double last, double tolerance, std::vector<double>& parameters)
    {
        parameters.clear();
        std::vector<double> candidates{first, last};
        GeomAPI_ProjectPointOnCurve query(point, curve, first, last);
        if (!query.Extrema().IsDone()) return false;
        for (int i = 1; i <= query.NbPoints(); ++i) candidates.push_back(query.Parameter(i));
        double nearest = std::numeric_limits<double>::infinity();
        for (double t : candidates) nearest = std::min(nearest, point.Distance(curve->Value(t)));
        if (nearest > tolerance) return false;
        const double pe = ParameterEpsilon(first, last);
        for (double t : candidates) {
            if (point.Distance(curve->Value(t)) > nearest + GeometryEpsilon) continue;
            bool duplicate = false;
            for (double old : parameters) if (std::fabs(old - t) <= pe) { duplicate = true; break; }
            if (!duplicate) parameters.push_back(t);
        }
        return !parameters.empty();
    }

    // Error-controlled 3D containment, not the old three-probe/whole-length guess.
    // Distance to a fixed set is 1-Lipschitz. Endpoint distances plus half the
    // interval's arc length bound every interior distance. Subdivide until the
    // bound proves containment; exhaustion is a failure, never relaxed tolerance.
    inline bool CurveWithin(const Handle(Geom_Curve)& source, double sf, double sl,
        const Handle(Geom_Curve)& target, double tf, double tl, double tolerance)
    {
        if (source == target && sf >= tf && sl <= tl) return true;
        struct Interval { double first, last, da, db; int depth; };
        const double da = CurvePointDistance(source->Value(sf), target, tf, tl);
        const double db = CurvePointDistance(source->Value(sl), target, tf, tl);
        std::vector<Interval> pending{{sf, sl, da, db, 0}};
        int remaining = 200000;
        while (!pending.empty() && --remaining > 0) {
            const Interval interval = pending.back(); pending.pop_back();
            if (interval.da > tolerance || interval.db > tolerance) return false;
            GeomAdaptor_Curve adaptor(source, interval.first, interval.last);
            const double arc = GCPnts_AbscissaPoint::Length(adaptor, interval.first, interval.last, GeometryEpsilon * 0.1);
            if (!std::isfinite(arc) || arc < 0.0) return false;
            if (std::max(interval.da, interval.db) + arc * 0.5 + GeometryEpsilon <= tolerance) continue;
            if (interval.depth >= 30) return false;
            const double middle = interval.first + (interval.last - interval.first) * 0.5;
            if (middle == interval.first || middle == interval.last) return false;
            const double dm = CurvePointDistance(source->Value(middle), target, tf, tl);
            if (dm > tolerance) return false;
            pending.push_back({middle, interval.last, dm, interval.db, interval.depth + 1});
            pending.push_back({interval.first, middle, interval.da, dm, interval.depth + 1});
        }
        return pending.empty();
    }

    inline bool MatchSourceInterval(const TopoDS_Edge& boundary, const TopoDS_Edge& original,
        double tolerance, SourceInterval& result)
    {
        if (boundary.IsNull() || original.IsNull() || !std::isfinite(tolerance) || tolerance <= GeometryEpsilon) return false;
        try {
            double bf = 0.0, bl = 0.0, of = 0.0, ol = 0.0;
            const Handle(Geom_Curve) bc = BRep_Tool::Curve(boundary, bf, bl);
            const Handle(Geom_Curve) oc = BRep_Tool::Curve(original, of, ol);
            if (bc.IsNull() || oc.IsNull() || !std::isfinite(bf) || !std::isfinite(bl) || bl <= bf ||
                !std::isfinite(of) || !std::isfinite(ol) || ol <= of) return false;
            if (boundary.IsSame(original) || (bc == oc && bf >= of && bl <= ol)) {
                result.edge = original; result.first = bf; result.last = bl; result.reversed = false;
                return true;
            }
            std::vector<double> starts, ends;
            if (!Closest3dParameters(bc->Value(bf), oc, of, ol, tolerance, starts) ||
                !Closest3dParameters(bc->Value(bl), oc, of, ol, tolerance, ends)) return false;
            GeomAdaptor_Curve boundaryAdaptor(bc, bf, bl);
            const double boundaryLength = GCPnts_AbscissaPoint::Length(boundaryAdaptor, bf, bl, GeometryEpsilon * 0.1);
            const double pe = ParameterEpsilon(of, ol);
            bool matched = false;
            for (double start : starts) for (double end : ends) {
                if (std::fabs(start - end) <= pe) continue;
                const double first = std::min(start, end), last = std::max(start, end);
                GeomAdaptor_Curve originalAdaptor(oc, first, last);
                const double originalLength = GCPnts_AbscissaPoint::Length(originalAdaptor, first, last, GeometryEpsilon * 0.1);
                if (!std::isfinite(boundaryLength) || !std::isfinite(originalLength) ||
                    std::fabs(boundaryLength - originalLength) > 2.0 * tolerance) continue;
                if (!CurveWithin(bc, bf, bl, oc, first, last, tolerance) ||
                    !CurveWithin(oc, first, last, bc, bf, bl, tolerance)) continue;
                bool reversed = end < start;
                if (bc->Value(bf).Distance(bc->Value(bl)) <= GeometryEpsilon) {
                    // Closed edges have indistinguishable endpoint coordinates;
                    // use the exact derivatives to retain their traversal sense.
                    gp_Pnt p, q; gp_Vec bd, od;
                    bc->D1(bf, p, bd); oc->D1(first, q, od);
                    reversed = bd.Dot(od) < 0.0;
                }
                if (matched && (std::fabs(result.first - first) > pe || std::fabs(result.last - last) > pe ||
                    result.reversed != reversed)) return false;
                result.edge = original; result.first = first; result.last = last; result.reversed = reversed;
                matched = true;
            }
            return matched;
        } catch (const Standard_Failure&) { return false; }
    }

    inline void AddHistoryEdges(const TopoDS_Shape& modified, int sourceIndex,
        const TopTools_IndexedMapOfShape& boundaries, std::vector<std::vector<int>>& inverse)
    {
        if (modified.IsNull()) return;
        auto add = [&](const TopoDS_Shape& edge) {
            const int index = boundaries.FindIndex(edge);
            if (index <= 0) return;
            std::vector<int>& entries = inverse[index - 1];
            if (std::find(entries.begin(), entries.end(), sourceIndex) == entries.end()) entries.push_back(sourceIndex);
        };
        if (modified.ShapeType() == TopAbs_EDGE) add(modified);
        else for (TopExp_Explorer ex(modified, TopAbs_EDGE); ex.More(); ex.Next()) add(ex.Current());
    }

    inline std::vector<std::vector<int>> SewingSourceInverse(BRepBuilderAPI_Sewing& sewing,
        const std::vector<TopoDS_Edge>& originals, const TopTools_IndexedMapOfShape& boundaries)
    {
        std::vector<std::vector<int>> inverse(static_cast<size_t>(boundaries.Extent()));
        for (int i = 0; i < static_cast<int>(originals.size()); ++i) {
            AddHistoryEdges(originals[i], i, boundaries, inverse);
            if (sewing.IsModified(originals[i])) AddHistoryEdges(sewing.Modified(originals[i]), i, boundaries, inverse);
            if (sewing.IsModifiedSubShape(originals[i])) AddHistoryEdges(sewing.ModifiedSubShape(originals[i]), i, boundaries, inverse);
        }
        // SectionToBoundary is only defined for bound sections, not every free edge.
        for (int i = 1; i <= boundaries.Extent(); ++i) {
            const TopoDS_Edge boundary = TopoDS::Edge(boundaries(i));
            if (!sewing.IsSectionBound(boundary)) continue;
            const TopoDS_Edge source = sewing.SectionToBoundary(boundary);
            for (int k = 0; k < static_cast<int>(originals.size()); ++k) if (source.IsSame(originals[k])) {
                auto& entries = inverse[i - 1];
                if (std::find(entries.begin(), entries.end(), k) == entries.end()) entries.push_back(k);
            }
        }
        return inverse;
    }

    inline bool FindUniqueSourceInterval(const TopoDS_Edge& boundary, const std::vector<TopoDS_Edge>& originals,
        const std::vector<int>& history, double tolerance, SourceInterval& result, bool& ambiguous)
    {
        ambiguous = false;
        bool found = false;
        std::vector<int> candidates = history;
        if (candidates.empty()) for (int i = 0; i < static_cast<int>(originals.size()); ++i) candidates.push_back(i);
        for (int index : candidates) {
            if (index < 0 || index >= static_cast<int>(originals.size())) continue;
            SourceInterval match;
            if (!MatchSourceInterval(boundary, originals[index], tolerance, match)) continue;
            if (found && !result.edge.IsSame(match.edge)) { ambiguous = true; return false; }
            result = match; found = true;
        }
        return found;
    }
}
#ifdef _MANAGED
#pragma managed(pop)
#endif
