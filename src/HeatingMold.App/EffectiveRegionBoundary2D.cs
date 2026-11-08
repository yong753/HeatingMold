using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// The raw (margin-free) boundary of the working region after subtracting exclusions.
/// Original CAD edges remain authoritative; portions created by exclusion rectangle
/// sides are exposed through <see cref="IArtificialExitBoundary2D"/> and never pretend
/// to be CAD edges. This type is only valid for a single connected region without holes.
/// </summary>
internal sealed class EffectiveRegionBoundary2D : IExitBoundary2D, IArtificialExitBoundary2D
{
    private const double ParameterEpsilon = 1e-9;
    private const double PointEpsilon = 1e-6;
    private const double InsideTolerance = 1e-7;

    private readonly CadExitBoundary2D _cad;
    private readonly IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> _exclusions;
    private readonly IReadOnlyList<Vec2> _outline;
    private readonly IReadOnlyList<ArtificialSegment> _segments;

    private EffectiveRegionBoundary2D(CadExitBoundary2D cad,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        IReadOnlyList<Vec2> outline, IReadOnlyList<ArtificialSegment> segments)
    {
        _cad = cad;
        _exclusions = exclusions;
        _outline = outline;
        _segments = segments;
    }

    internal readonly record struct ArtificialSegment(
        ArtificialEdgeBinding Binding, Vec2 A, Vec2 B);

    internal CadExitBoundary2D CadBoundary => _cad;
    internal IReadOnlyList<Vec2> Outline => _outline;
    internal IReadOnlyList<ArtificialSegment> Segments => _segments;

    internal static bool TryCreate(CadExitBoundary2D cad, IReadOnlyList<Vec2> region,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        out EffectiveRegionBoundary2D boundary, out string failure)
    {
        boundary = null!;
        failure = string.Empty;
        if (cad == null) { failure = "원본 CAD 경계가 없습니다."; return false; }
        if (region == null || region.Count < 3) { failure = "적용영역 폴리곤이 올바르지 않습니다."; return false; }
        if (exclusions == null) { failure = "제외영역 목록이 없습니다."; return false; }

        List<List<Vec2>> effective = RegionOps.SubtractRectangles(new[] { region }, exclusions);
        if (effective.Count == 0)
        {
            failure = "제외영역이 적용영역 전체를 제거합니다.";
            return false;
        }

        if (effective.Count > 1)
        {
            bool hasHole = effective.Any(ring => RegionOps.SignedArea(ring) < 0);
            failure = hasHole
                ? "제외영역이 작업영역 내부에 구멍을 만듭니다 — 내부 제외영역은 허용되지 않습니다."
                : "제외영역이 작업영역을 둘 이상으로 분리합니다 — 분리 제외영역은 허용되지 않습니다.";
            return false;
        }

        List<Vec2> ring = NormalizeRing(effective[0]);
        if (ring.Count < 3 || RegionOps.SignedArea(ring) <= 1e-9)
        {
            failure = "제외 후 적용영역의 외곽선이 퇴화했습니다.";
            return false;
        }

        double originalArea = Math.Abs(RegionOps.SignedArea(region));
        double effectiveArea = RegionOps.SignedArea(ring);
        if (originalArea - effectiveArea <= 1e-6)
        {
            failure = "제외영역이 적용영역을 실제로 자르지 않습니다.";
            return false;
        }

        if (HasPinch(ring))
        {
            failure = "제외 후 적용영역의 외곽선이 한 점에서 접힙니다 — 허용되지 않는 제외영역입니다.";
            return false;
        }

        var segments = new List<ArtificialSegment>();
        for (int i = 0; i < exclusions.Count; i++)
        {
            var rect = exclusions[i];
            if (!(rect.MaxX > rect.MinX && rect.MaxY > rect.MinY)) continue;
            for (int side = 0; side < 4; side++)
            {
                if (!TryGetSide(rect, side, out Vec2 a, out Vec2 b)) continue;
                var intervals = new List<(double First, double Last)> { (0.0, 1.0) };
                for (int j = 0; j < exclusions.Count && intervals.Count > 0; j++)
                {
                    if (j == i) continue;
                    if (!TryInsideInterval(a, b, exclusions[j], out double first, out double last)) continue;
                    intervals = SubtractInterval(intervals, first, last);
                }
                intervals = KeepInsideRegion(intervals, a, b, region);
                foreach ((double first, double last) in intervals)
                {
                    if (last - first <= ParameterEpsilon) continue;
                    Vec2 start = Lerp(a, b, first);
                    Vec2 end = Lerp(a, b, last);
                    if (start.DistanceTo(end) <= PointEpsilon) continue;
                    var binding = new ArtificialEdgeBinding(cad.ContextId, cad.Revision,
                        rect.MinX, rect.MinY, rect.MaxX, rect.MaxY, side, first, last, first);
                    segments.Add(new ArtificialSegment(binding, start, end));
                }
            }
        }

        // 제외 사각형끼리 변을 공유하면 사각형 변 일부가 합집합 내부에 남는다.
        // 선택/생성 경계는 실제 유효 외곽선과 일치해야 하므로 외곽선 위의 구간만 남긴다.
        segments.RemoveAll(segment => !IsOnOutline(segment, ring));

        boundary = new EffectiveRegionBoundary2D(cad, exclusions, ring, segments);
        return true;
    }

    public ExitBoundaryResolution Project(Vec2 point, double tolerance)
    {
        if (!IsFinite(point) || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);

        ExitBoundaryResolution cad = _cad.Project(point, tolerance);
        ExitBoundaryPoint? cadPoint = null;
        if (cad.Succeeded && !IsRemovedCadPoint(cad.Point!.Value.Point)) cadPoint = cad.Point;

        bool hasArtificial = TryNearestArtificial(point, tolerance, out ExitBoundaryPoint artificial,
            out bool artificialAmbiguous);
        if (artificialAmbiguous)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
        if (!cad.Succeeded && cad.Failure != ExitBoundaryFailureReason.NoIntersection) return cad;
        if (cadPoint.HasValue && hasArtificial)
        {
            double cadDistance = cadPoint.Value.Point.DistanceTo(point);
            double artificialDistance = artificial.Point.DistanceTo(point);
            if (cadPoint.Value.Point.DistanceTo(artificial.Point) > PointEpsilon
                && Math.Abs(cadDistance - artificialDistance) <= ExitBoundaryConstraint.Tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
            return artificialDistance < cadDistance ? ExitBoundaryResolution.Ok(artificial) : cad;
        }
        if (cadPoint.HasValue) return cad;
        if (hasArtificial) return ExitBoundaryResolution.Ok(artificial);
        return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
    }

    public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance)
    {
        if (!IsFinite(endpoint) || !IsFinite(next) || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        Vec2 direction = endpoint - next;
        if (!double.IsFinite(direction.Length) || direction.Length <= tolerance)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);

        ExitBoundaryResolution cad = _cad.ResolveOutward(endpoint, next, tolerance);
        ExitBoundaryPoint? cadPoint = null;
        if (cad.Succeeded && !IsRemovedCadPoint(cad.Point!.Value.Point)) cadPoint = cad.Point;

        var artificial = new List<ExitBoundaryPoint>();
        foreach (ArtificialSegment segment in _segments)
        {
            if (!TryRaySegment(endpoint, direction, segment, tolerance, out Vec2 hit, out ExitBoundaryFailureReason? reason))
            {
                if (reason == ExitBoundaryFailureReason.TangentOrOverlap) return ExitBoundaryResolution.Fail(reason.Value);
                continue;
            }
            if (artificial.All(p => p.Point.DistanceTo(hit) > tolerance))
            {
                double first = segment.Binding.FirstParameter;
                double last = segment.Binding.LastParameter;
                double t = ParameterOf(segment, hit);
                if (t < first - ParameterEpsilon || t > last + ParameterEpsilon) continue;
                artificial.Add(new ExitBoundaryPoint(hit, -1, Math.Clamp(t, first, last))
                {
                    ArtificialBinding = segment.Binding with
                    {
                        FirstParameter = first,
                        LastParameter = last,
                        Parameter = Math.Clamp(t, first, last),
                    },
                });
            }
        }

        if (artificial.Count > 1)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
        if (!cad.Succeeded && cad.Failure != ExitBoundaryFailureReason.NoIntersection) return cad;

        if (cadPoint.HasValue)
        {
            if (artificial.Count == 1
                && cadPoint.Value.Point.DistanceTo(artificial[0].Point) > PointEpsilon)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
            return cad;
        }

        if (artificial.Count == 1) return ExitBoundaryResolution.Ok(artificial[0]);
        return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
    }

    public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance)
    {
        ExitBoundaryResolution result = _cad.ProjectBound(binding, point, tolerance);
        if (!result.Succeeded) return result;
        return IsRemovedCadPoint(result.Point!.Value.Point)
            ? ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference)
            : result;
    }

    public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding)
    {
        ExitBoundaryResolution result = _cad.Evaluate(binding);
        if (!result.Succeeded) return result;
        return IsRemovedCadPoint(result.Point!.Value.Point)
            ? ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference)
            : result;
    }

    public ExitBoundaryResolution ProjectArtificial(ArtificialEdgeBinding binding, Vec2 point, double tolerance)
    {
        if (!IsFinite(point) || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        if (!TryFindSegment(binding, out ArtificialSegment segment, out ExitBoundaryFailureReason reason))
            return ExitBoundaryResolution.Fail(reason);

        Vec2 side = segment.B - segment.A;
        double lengthSquared = side.X * side.X + side.Y * side.Y;
        if (!(lengthSquared > 1e-18))
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);
        double u = ((point.X - segment.A.X) * side.X + (point.Y - segment.A.Y) * side.Y) / lengthSquared;
        u = Math.Clamp(u, 0.0, 1.0);
        double first = segment.Binding.FirstParameter;
        double last = segment.Binding.LastParameter;
        double parameter = Math.Clamp(first + u * (last - first), first, last);
        Vec2 projected = Lerp(segment.A, segment.B, u);
        if (projected.DistanceTo(point) > tolerance)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);

        ArtificialEdgeBinding saved = binding with
        {
            FirstParameter = first,
            LastParameter = last,
            Parameter = parameter,
        };
        return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(projected, -1, parameter)
        {
            ArtificialBinding = saved,
        });
    }

    public ExitBoundaryResolution EvaluateArtificial(ArtificialEdgeBinding binding)
    {
        if (!TryFindSegment(binding, out ArtificialSegment segment, out ExitBoundaryFailureReason reason))
            return ExitBoundaryResolution.Fail(reason);
        double first = segment.Binding.FirstParameter;
        double last = segment.Binding.LastParameter;
        if (!double.IsFinite(binding.Parameter)
            || binding.Parameter < first - ParameterEpsilon
            || binding.Parameter > last + ParameterEpsilon)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);

        double u = (binding.Parameter - first) / (last - first);
        Vec2 point = Lerp(segment.A, segment.B, u);
        ArtificialEdgeBinding saved = binding with
        {
            FirstParameter = first,
            LastParameter = last,
            Parameter = Math.Clamp(binding.Parameter, first, last),
        };
        return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(point, -1, saved.Parameter)
        {
            ArtificialBinding = saved,
        });
    }

    private bool TryNearestArtificial(Vec2 point, double tolerance, out ExitBoundaryPoint result,
        out bool ambiguous)
    {
        result = default;
        ambiguous = false;
        bool found = false;
        double bestDistance = double.PositiveInfinity;
        foreach (ArtificialSegment segment in _segments)
        {
            Vec2 side = segment.B - segment.A;
            double lengthSquared = Dot(side, side);
            if (!(lengthSquared > 1e-18)) continue;
            double u = Math.Clamp(Dot(point - segment.A, side) / lengthSquared, 0.0, 1.0);
            Vec2 candidate = Lerp(segment.A, segment.B, u);
            double distance = candidate.DistanceTo(point);
            if (distance > tolerance) continue;
            double first = segment.Binding.FirstParameter;
            double last = segment.Binding.LastParameter;
            double parameter = first + u * (last - first);
            var pointResult = new ExitBoundaryPoint(candidate, -1, parameter)
            {
                ArtificialBinding = segment.Binding with
                {
                    FirstParameter = first,
                    LastParameter = last,
                    Parameter = parameter,
                },
            };
            if (!found || distance < bestDistance - ExitBoundaryConstraint.Tolerance)
            {
                result = pointResult;
                bestDistance = distance;
                found = true;
            }
            else if (Math.Abs(distance - bestDistance) <= ExitBoundaryConstraint.Tolerance
                     && candidate.DistanceTo(result.Point) > PointEpsilon)
            {
                ambiguous = true;
                return false;
            }
        }
        return found;
    }

    private bool TryFindSegment(ArtificialEdgeBinding binding, out ArtificialSegment segment,
        out ExitBoundaryFailureReason reason)
    {
        segment = default;
        reason = ExitBoundaryFailureReason.StaleContext;
        if (binding.WorkingContextId != _cad.ContextId || binding.WorkingContextRevision != _cad.Revision)
            return false;
        reason = ExitBoundaryFailureReason.MissingReference;
        foreach (ArtificialSegment candidate in _segments)
        {
            ArtificialEdgeBinding saved = candidate.Binding;
            if (saved.MinX != binding.MinX || saved.MinY != binding.MinY
                || saved.MaxX != binding.MaxX || saved.MaxY != binding.MaxY
                || saved.Side != binding.Side) continue;
            if (Math.Abs(saved.FirstParameter - binding.FirstParameter) > ParameterEpsilon
                || Math.Abs(saved.LastParameter - binding.LastParameter) > ParameterEpsilon) continue;
            segment = candidate;
            return true;
        }
        return false;
    }

    private bool IsRemovedCadPoint(Vec2 point)
    {
        foreach (var r in _exclusions)
        {
            if (point.X > r.MinX + InsideTolerance && point.X < r.MaxX - InsideTolerance
                && point.Y > r.MinY + InsideTolerance && point.Y < r.MaxY - InsideTolerance)
                return true;
        }
        return false;
    }

    private static List<(double First, double Last)> KeepInsideRegion(
        List<(double First, double Last)> intervals, Vec2 a, Vec2 b, IReadOnlyList<Vec2> region)
    {
        var result = new List<(double First, double Last)>();
        foreach ((double first, double last) in intervals)
        {
            var cuts = new List<double> { first, last };
            for (int i = 0; i < region.Count; i++)
            {
                Vec2 p = region[i];
                Vec2 q = region[(i + 1) % region.Count];
                if (TrySegmentParameters(a, b, p, q, out double t0, out double t1))
                {
                    if (t0 >= first - ParameterEpsilon && t0 <= last + ParameterEpsilon) cuts.Add(t0);
                    if (t1 >= first - ParameterEpsilon && t1 <= last + ParameterEpsilon) cuts.Add(t1);
                }
            }
            cuts.Sort();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double t0 = Math.Max(first, cuts[i]);
                double t1 = Math.Min(last, cuts[i + 1]);
                if (t1 - t0 <= ParameterEpsilon) continue;
                Vec2 mid = Lerp(a, b, (t0 + t1) * 0.5);
                if (ClassifyPoint(mid, region) == PointRelation.Inside)
                    result.Add((t0, t1));
            }
        }
        return MergeIntervals(result);
    }

    private static List<(double First, double Last)> MergeIntervals(
        List<(double First, double Last)> intervals)
    {
        if (intervals.Count == 0) return intervals;
        intervals.Sort((x, y) => x.First.CompareTo(y.First));
        var merged = new List<(double First, double Last)> { intervals[0] };
        for (int i = 1; i < intervals.Count; i++)
        {
            var last = merged[^1];
            if (intervals[i].First <= last.Last + 1e-9)
                merged[^1] = (last.First, Math.Max(last.Last, intervals[i].Last));
            else merged.Add(intervals[i]);
        }
        return merged;
    }

    private static List<(double First, double Last)> SubtractInterval(
        List<(double First, double Last)> intervals, double first, double last)
    {
        var result = new List<(double First, double Last)>();
        foreach ((double a, double b) in intervals)
        {
            if (last <= a + ParameterEpsilon || first >= b - ParameterEpsilon)
            {
                result.Add((a, b));
                continue;
            }
            if (first > a + ParameterEpsilon) result.Add((a, Math.Min(b, first)));
            if (last < b - ParameterEpsilon) result.Add((Math.Max(a, last), b));
        }
        return result;
    }

    private static bool TryInsideInterval(Vec2 a, Vec2 b, (double MinX, double MinY, double MaxX, double MaxY) rect,
        out double first, out double last)
    {
        first = 0;
        last = 0;
        if (Math.Abs(a.Y - b.Y) <= ParameterEpsilon)
        {
            double y = a.Y;
            if (y <= rect.MinY + InsideTolerance || y >= rect.MaxY - InsideTolerance) return false;
            double dx = b.X - a.X;
            if (Math.Abs(dx) <= ParameterEpsilon) return false;
            first = (rect.MinX - a.X) / dx;
            last = (rect.MaxX - a.X) / dx;
        }
        else
        {
            double x = a.X;
            if (x <= rect.MinX + InsideTolerance || x >= rect.MaxX - InsideTolerance) return false;
            double dy = b.Y - a.Y;
            if (Math.Abs(dy) <= ParameterEpsilon) return false;
            first = (rect.MinY - a.Y) / dy;
            last = (rect.MaxY - a.Y) / dy;
        }
        if (first > last) (first, last) = (last, first);
        first = Math.Max(0, first);
        last = Math.Min(1, last);
        return last - first > ParameterEpsilon;
    }

    private static bool TryGetSide((double MinX, double MinY, double MaxX, double MaxY) r, int side,
        out Vec2 a, out Vec2 b)
    {
        a = default;
        b = default;
        switch (side)
        {
            case 0: a = new Vec2(r.MinX, r.MinY); b = new Vec2(r.MaxX, r.MinY); return true;
            case 1: a = new Vec2(r.MaxX, r.MinY); b = new Vec2(r.MaxX, r.MaxY); return true;
            case 2: a = new Vec2(r.MinX, r.MaxY); b = new Vec2(r.MaxX, r.MaxY); return true;
            case 3: a = new Vec2(r.MinX, r.MinY); b = new Vec2(r.MinX, r.MaxY); return true;
            default: return false;
        }
    }

    private static bool TrySegmentParameters(Vec2 a, Vec2 b, Vec2 c, Vec2 d,
        out double t0, out double t1)
    {
        t0 = 0;
        t1 = 0;
        Vec2 r = b - a;
        Vec2 s = d - c;
        double cross = Cross(r, s);
        double denominator = cross;
        if (Math.Abs(denominator) <= ParameterEpsilon)
        {
            if (Math.Abs(Cross(c - a, r)) > ParameterEpsilon) return false;
            double rr = Dot(r, r);
            if (rr <= 1e-18) return false;
            double u0 = Dot(c - a, r) / rr;
            double u1 = Dot(d - a, r) / rr;
            t0 = Math.Min(u0, u1);
            t1 = Math.Max(u0, u1);
            return true;
        }
        double t = Cross(c - a, s) / denominator;
        double u = Cross(c - a, r) / denominator;
        t0 = t1 = t;
        return u >= -ParameterEpsilon && u <= 1 + ParameterEpsilon;
    }

    private static bool TryRaySegment(Vec2 origin, Vec2 direction, ArtificialSegment segment,
        double tolerance, out Vec2 hit, out ExitBoundaryFailureReason? reason)
    {
        hit = default;
        reason = null;
        Vec2 e = segment.B - segment.A;
        double cross = Cross(direction, e);
        double directionLength = direction.Length;
        double edgeLength = e.Length;
        if (Math.Abs(cross) <= ExitBoundaryConstraint.Tolerance * directionLength * edgeLength)
        {
            if (Math.Abs(Cross(segment.A - origin, direction)) <= tolerance * directionLength)
                reason = ExitBoundaryFailureReason.TangentOrOverlap;
            return false;
        }
        double s = Cross(segment.A - origin, e) / cross;
        double t = Cross(segment.A - origin, direction) / cross;
        if (s < -tolerance) return false;
        if (t < -ParameterEpsilon || t > 1 + ParameterEpsilon) return false;
        hit = origin + direction * s;
        return true;
    }

    private static double ParameterOf(ArtificialSegment segment, Vec2 point)
    {
        Vec2 side = segment.B - segment.A;
        double lengthSquared = Dot(side, side);
        if (lengthSquared <= 1e-18) return segment.Binding.FirstParameter;
        double u = Dot(point - segment.A, side) / lengthSquared;
        return segment.Binding.FirstParameter
            + Math.Clamp(u, 0, 1) * (segment.Binding.LastParameter - segment.Binding.FirstParameter);
    }

    private static PointRelation ClassifyPoint(Vec2 point, IReadOnlyList<Vec2> ring)
    {
        bool inside = false;
        for (int i = 0; i < ring.Count; i++)
        {
            Vec2 a = ring[i];
            Vec2 b = ring[(i + 1) % ring.Count];
            if (DistanceToSegment(a, b, point) <= PointEpsilon) return PointRelation.OnBoundary;
            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                double x = a.X + (point.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                if (x > point.X) inside = !inside;
            }
        }
        return inside ? PointRelation.Inside : PointRelation.Outside;
    }

    private static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 point)
    {
        Vec2 side = b - a;
        double lengthSquared = Dot(side, side);
        if (lengthSquared <= 1e-18) return point.DistanceTo(a);
        double u = Math.Clamp(Dot(point - a, side) / lengthSquared, 0, 1);
        return Lerp(a, b, u).DistanceTo(point);
    }

    private static bool IsOnOutline(ArtificialSegment segment, IReadOnlyList<Vec2> ring)
    {
        const double tolerance = 0.02;
        Vec2 mid = Lerp(segment.A, segment.B, 0.5);
        return IsOnRing(segment.A, ring, tolerance)
            && IsOnRing(mid, ring, tolerance)
            && IsOnRing(segment.B, ring, tolerance);
    }

    private static bool IsOnRing(Vec2 point, IReadOnlyList<Vec2> ring, double tolerance)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            if (DistanceToSegment(ring[i], ring[(i + 1) % ring.Count], point) <= tolerance)
                return true;
        }
        return false;
    }

    private static bool HasPinch(IReadOnlyList<Vec2> ring)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            for (int j = i + 1; j < ring.Count; j++)
            {
                if (j == i || j == (i + 1) % ring.Count || i == (j + 1) % ring.Count) continue;
                if (ring[i].DistanceTo(ring[j]) <= PointEpsilon) return true;
            }
        }
        return false;
    }

    private static List<Vec2> NormalizeRing(IReadOnlyList<Vec2> ring)
    {
        var result = new List<Vec2>(ring.Count);
        foreach (Vec2 point in ring)
        {
            if (result.Count == 0 || result[^1].DistanceTo(point) > PointEpsilon)
                result.Add(point);
        }
        if (result.Count > 1 && result[0].DistanceTo(result[^1]) <= PointEpsilon)
            result.RemoveAt(result.Count - 1);
        if (result.Count >= 3 && RegionOps.SignedArea(result) < 0) result.Reverse();
        return result;
    }

    private static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    private static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    private static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    private static bool IsFinite(Vec2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private enum PointRelation { Outside, OnBoundary, Inside }
}
