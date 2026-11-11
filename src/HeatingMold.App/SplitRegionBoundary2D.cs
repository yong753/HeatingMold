using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// Boundary of one independent area produced by splitting a working area. Original CAD edges
/// keep their edge identity, exclusion-created sides keep their exclusion provenance, and the
/// new cut line is exposed only through <see cref="IArtificialSplitBoundary2D"/>. CAD and
/// exclusion results that fall outside this child's outline are rejected instead of being
/// re-bound to a neighbouring area's boundary.
/// </summary>
internal sealed class SplitRegionBoundary2D :
    IExitBoundary2D, IArtificialExitBoundary2D, IArtificialSplitBoundary2D
{
    private const double ParameterEpsilon = 1e-9;
    private const double PointEpsilon = 1e-6;
    private const double OnRingTolerance = 0.02;

    // 분할 밴드 폴리곤은 0.01 mm 격자에서 만들어지므로, 절단선 위에 놓인 변이
    // 반 격자(0.005 mm)만큼 바깥으로 어긋날 수 있다. 이는 선이 영역을 가로지르는
    // 것이 아니라 같은 경계를 공유하는 것이므로 유지측 판정에서 허용한다.
    private const double RingGridTolerance = 0.01;

    private readonly EffectiveRegionBoundary2D _inner;
    private readonly IReadOnlyList<Vec2> _ring;
    private readonly IReadOnlyList<SplitSegment> _segments;

    private SplitRegionBoundary2D(EffectiveRegionBoundary2D inner, IReadOnlyList<Vec2> ring,
        IReadOnlyList<SplitSegment> segments)
    {
        _inner = inner;
        _ring = ring;
        _segments = segments;
    }

    internal readonly record struct SplitSegment(SplitLineBinding Binding, Vec2 A, Vec2 B);

    internal CadExitBoundary2D CadBoundary => _inner.CadBoundary;
    internal IReadOnlyList<Vec2> Ring => _ring;

    /// <summary>이 영역 외곽에 실제로 남은 절단선 구간.</summary>
    internal IReadOnlyList<SplitSegment> Segments => _segments;

    /// <summary>
    /// 이 영역이 실제로 유지되는 쪽을 나타내는 절단선 반평면(작업평면 축 정렬 기준).
    /// 전체 절단선 목록이 아니라 <b>이 영역 외곽에 실제로 남은 절단선</b>만 사용한다.
    /// 이웃 영역만 가르는 먼 절단선까지 적용하면 이 영역을 잘못 잘라낸다.
    /// </summary>
    internal readonly record struct HalfPlane(int Axis, double Limit, bool KeepLess);

    /// <summary>
    /// 이 영역의 표시용 클리핑 마스크. 제품의 모든 분할 절단선은 가로/세로 축 정렬이므로
    /// 축·한계·유지측만으로 완전히 표현된다. 축 정렬이 아니거나 영역이 선을 가로지르면
    /// 표시용으로도 표현할 수 없으므로 명시적으로 실패한다.
    /// </summary>
    internal bool TryGetRetainedHalfPlanes(out List<HalfPlane> planes, out string failure)
    {
        planes = new List<HalfPlane>();
        failure = string.Empty;
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (Vec2 point in _ring)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        var seen = new List<(double AX, double AY, double BX, double BY)>();
        foreach (SplitSegment segment in _segments)
        {
            // 링 변과 정확히 겹치는 절단선은 클리핑 결과가 길이 0인 조각으로 남는다.
            // 그 조각은 영역을 가르는 선이 아니므로 축 판정 대상에서 제외한다.
            if (segment.A.DistanceTo(segment.B) <= PointEpsilon) continue;
            SplitLineBinding binding = segment.Binding;
            bool duplicate = false;
            foreach (var line in seen)
            {
                if (Math.Abs(line.AX - binding.AX) <= PointEpsilon &&
                    Math.Abs(line.AY - binding.AY) <= PointEpsilon &&
                    Math.Abs(line.BX - binding.BX) <= PointEpsilon &&
                    Math.Abs(line.BY - binding.BY) <= PointEpsilon)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate) continue;
            seen.Add((binding.AX, binding.AY, binding.BX, binding.BY));

            if (Math.Abs(binding.AX - binding.BX) <= PointEpsilon)
            {
                double limit = binding.AX;
                double beyondLow = limit - minX;
                double beyondHigh = maxX - limit;
                if (beyondLow <= RingGridTolerance)
                    planes.Add(new HalfPlane(0, limit, KeepLess: false));
                else if (beyondHigh <= RingGridTolerance)
                    planes.Add(new HalfPlane(0, limit, KeepLess: true));
                else
                {
                    failure = "분할 영역 외곽을 절단선이 가로지릅니다.";
                    planes.Clear();
                    return false;
                }
            }
            else if (Math.Abs(binding.AY - binding.BY) <= PointEpsilon)
            {
                double limit = binding.AY;
                double beyondLow = limit - minY;
                double beyondHigh = maxY - limit;
                if (beyondLow <= RingGridTolerance)
                    planes.Add(new HalfPlane(1, limit, KeepLess: false));
                else if (beyondHigh <= RingGridTolerance)
                    planes.Add(new HalfPlane(1, limit, KeepLess: true));
                else
                {
                    failure = "분할 영역 외곽을 절단선이 가로지릅니다.";
                    planes.Clear();
                    return false;
                }
            }
            else
            {
                failure = "축 정렬이 아닌 분할 절단선은 영역 하이라이트로 표시할 수 없습니다.";
                planes.Clear();
                return false;
            }
        }

        // 절단선이 외곽에 하나도 남지 않으면 분할 영역을 자를 근거가 없다.
        // 제외 사각형만으로 채우는 기존 경로로 조용히 되돌리지 않고 명시적으로 거부한다.
        if (planes.Count == 0)
        {
            failure = "분할 영역의 절단선을 외곽에서 찾지 못했습니다.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 이 영역의 정확 포함 판정. 경계가 원본 CAD 구간과 artificial 절단선의 혼합이므로
    /// 확정 작업평면 외곽 폴리곤을 기준으로 하되, CAD 구간 identity는 그대로 유지한다.
    /// </summary>
    internal IExactRegionBoundary CreateContainment() => new SplitRegionContainmentBoundary(_ring);

    /// <summary>
    /// <paramref name="parentRegion"/> is the unsplit applied region, <paramref name="region"/>
    /// is this child's outline (exclusions already subtracted by the split), and
    /// <paramref name="cutLines"/> are the full cut lines in working-plane coordinates.
    /// </summary>
    internal static bool TryCreate(CadExitBoundary2D cad,
        IReadOnlyList<Vec2> parentRegion,
        IReadOnlyList<Vec2> region,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        IReadOnlyList<(Vec2 A, Vec2 B)> cutLines,
        out SplitRegionBoundary2D boundary, out string failure)
    {
        boundary = null!;
        if (!EffectiveRegionBoundary2D.TryCreatePreSubtracted(cad, parentRegion, region,
                exclusions, out EffectiveRegionBoundary2D inner, out failure))
            return false;

        IReadOnlyList<Vec2> ring = inner.Outline;
        var segments = new List<SplitSegment>();
        foreach ((Vec2 a, Vec2 b) in cutLines ?? Array.Empty<(Vec2, Vec2)>())
        {
            foreach ((double first, double last) in ClipToRing(a, b, ring))
            {
                var binding = new SplitLineBinding(cad.ContextId, cad.Revision,
                    a.X, a.Y, b.X, b.Y, first, last, first);
                segments.Add(new SplitSegment(binding, Lerp(a, b, first), Lerp(a, b, last)));
            }
        }

        boundary = new SplitRegionBoundary2D(inner, ring, segments);
        failure = string.Empty;
        return true;
    }

    public ExitBoundaryResolution Project(Vec2 point, double tolerance)
    {
        if (!IsFinite(point) || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);

        ExitBoundaryResolution inner = _inner.Project(point, tolerance);
        ExitBoundaryPoint? innerPoint = null;
        if (inner.Succeeded && IsOnRing(inner.Point!.Value.Point)) innerPoint = inner.Point;

        bool hasSplit = TryNearestSplit(point, tolerance, out ExitBoundaryPoint split, out bool ambiguous);
        if (ambiguous) return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
        if (!inner.Succeeded && inner.Failure != ExitBoundaryFailureReason.NoIntersection && !hasSplit)
            return inner;
        if (innerPoint.HasValue && hasSplit)
        {
            double innerDistance = innerPoint.Value.Point.DistanceTo(point);
            double splitDistance = split.Point.DistanceTo(point);
            if (innerPoint.Value.Point.DistanceTo(split.Point) > PointEpsilon
                && Math.Abs(innerDistance - splitDistance) <= ExitBoundaryConstraint.Tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
            return splitDistance < innerDistance
                ? ExitBoundaryResolution.Ok(split)
                : ExitBoundaryResolution.Ok(innerPoint.Value);
        }
        if (innerPoint.HasValue) return ExitBoundaryResolution.Ok(innerPoint.Value);
        if (hasSplit) return ExitBoundaryResolution.Ok(split);
        return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
    }

    public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance)
    {
        if (!IsFinite(endpoint) || !IsFinite(next) || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        Vec2 direction = endpoint - next;
        if (!double.IsFinite(direction.Length) || direction.Length <= tolerance)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);

        ExitBoundaryResolution inner = _inner.ResolveOutward(endpoint, next, tolerance);
        ExitBoundaryPoint? innerPoint = null;
        if (inner.Succeeded && IsOnRing(inner.Point!.Value.Point)) innerPoint = inner.Point;

        var hits = new List<ExitBoundaryPoint>();
        foreach (SplitSegment segment in _segments)
        {
            if (!TryRaySegment(endpoint, direction, segment, tolerance, out Vec2 hit, out bool tangent))
            {
                if (tangent) return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.TangentOrOverlap);
                continue;
            }
            if (hits.All(p => p.Point.DistanceTo(hit) > tolerance))
                hits.Add(MakeSplitPoint(segment, hit));
        }
        if (hits.Count > 1)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
        if (!inner.Succeeded && inner.Failure != ExitBoundaryFailureReason.NoIntersection && hits.Count == 0)
            return inner;

        if (innerPoint.HasValue)
        {
            if (hits.Count == 1 && innerPoint.Value.Point.DistanceTo(hits[0].Point) > PointEpsilon)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);
            return ExitBoundaryResolution.Ok(innerPoint.Value);
        }
        if (hits.Count == 1) return ExitBoundaryResolution.Ok(hits[0]);
        return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
    }

    public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance)
    {
        ExitBoundaryResolution result = _inner.ProjectBound(binding, point, tolerance);
        if (!result.Succeeded) return result;
        return IsOnRing(result.Point!.Value.Point)
            ? result
            : ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
    }

    public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding)
    {
        ExitBoundaryResolution result = _inner.Evaluate(binding);
        if (!result.Succeeded) return result;
        return IsOnRing(result.Point!.Value.Point)
            ? result
            : ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
    }

    public ExitBoundaryResolution ProjectArtificial(ArtificialEdgeBinding binding, Vec2 point, double tolerance)
    {
        ExitBoundaryResolution result = _inner.ProjectArtificial(binding, point, tolerance);
        if (!result.Succeeded) return result;
        return IsOnRing(result.Point!.Value.Point)
            ? result
            : ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
    }

    public ExitBoundaryResolution EvaluateArtificial(ArtificialEdgeBinding binding)
    {
        ExitBoundaryResolution result = _inner.EvaluateArtificial(binding);
        if (!result.Succeeded) return result;
        return IsOnRing(result.Point!.Value.Point)
            ? result
            : ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
    }

    public ExitBoundaryResolution ProjectSplitLine(SplitLineBinding binding, Vec2 point, double tolerance)
    {
        if (!IsFinite(point) || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        if (!TryFindSegment(binding, out SplitSegment segment, out ExitBoundaryFailureReason reason))
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

        return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(projected, -1, parameter)
        {
            SplitBinding = binding with
            {
                AX = segment.Binding.AX,
                AY = segment.Binding.AY,
                BX = segment.Binding.BX,
                BY = segment.Binding.BY,
                FirstParameter = first,
                LastParameter = last,
                Parameter = parameter,
            },
        });
    }

    public ExitBoundaryResolution EvaluateSplitLine(SplitLineBinding binding)
    {
        if (!TryFindSegment(binding, out SplitSegment segment, out ExitBoundaryFailureReason reason))
            return ExitBoundaryResolution.Fail(reason);
        double first = segment.Binding.FirstParameter;
        double last = segment.Binding.LastParameter;
        if (!double.IsFinite(binding.Parameter)
            || binding.Parameter < first - ParameterEpsilon
            || binding.Parameter > last + ParameterEpsilon)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);

        double span = last - first;
        if (!(span > ParameterEpsilon))
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);
        double u = (binding.Parameter - first) / span;
        Vec2 point = Lerp(segment.A, segment.B, u);
        return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(point, -1, Math.Clamp(binding.Parameter, first, last))
        {
            SplitBinding = binding with
            {
                AX = segment.Binding.AX,
                AY = segment.Binding.AY,
                BX = segment.Binding.BX,
                BY = segment.Binding.BY,
                FirstParameter = first,
                LastParameter = last,
                Parameter = Math.Clamp(binding.Parameter, first, last),
            },
        });
    }

    private bool TryFindSegment(SplitLineBinding binding, out SplitSegment segment,
        out ExitBoundaryFailureReason reason)
    {
        segment = default;
        reason = ExitBoundaryFailureReason.StaleContext;
        if (binding.WorkingContextId != _inner.CadBoundary.ContextId
            || binding.WorkingContextRevision != _inner.CadBoundary.Revision)
            return false;
        reason = ExitBoundaryFailureReason.MissingReference;
        foreach (SplitSegment candidate in _segments)
        {
            SplitLineBinding saved = candidate.Binding;
            if (saved.AX != binding.AX || saved.AY != binding.AY
                || saved.BX != binding.BX || saved.BY != binding.BY) continue;
            if (Math.Abs(saved.FirstParameter - binding.FirstParameter) > ParameterEpsilon
                || Math.Abs(saved.LastParameter - binding.LastParameter) > ParameterEpsilon) continue;
            segment = candidate;
            return true;
        }
        return false;
    }

    private bool TryNearestSplit(Vec2 point, double tolerance, out ExitBoundaryPoint result,
        out bool ambiguous)
    {
        result = default;
        ambiguous = false;
        bool found = false;
        double bestDistance = double.PositiveInfinity;
        foreach (SplitSegment segment in _segments)
        {
            Vec2 side = segment.B - segment.A;
            double lengthSquared = Dot(side, side);
            if (!(lengthSquared > 1e-18)) continue;
            double u = Math.Clamp(Dot(point - segment.A, side) / lengthSquared, 0.0, 1.0);
            Vec2 candidate = Lerp(segment.A, segment.B, u);
            double distance = candidate.DistanceTo(point);
            if (distance > tolerance) continue;
            ExitBoundaryPoint pointResult = MakeSplitPoint(segment, candidate);
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

    private static ExitBoundaryPoint MakeSplitPoint(SplitSegment segment, Vec2 point)
    {
        double first = segment.Binding.FirstParameter;
        double last = segment.Binding.LastParameter;
        double lengthSquared = Dot(segment.B - segment.A, segment.B - segment.A);
        double u = lengthSquared <= 1e-18 ? 0.0
            : Math.Clamp(Dot(point - segment.A, segment.B - segment.A) / lengthSquared, 0.0, 1.0);
        double parameter = Math.Clamp(first + u * (last - first), first, last);
        return new ExitBoundaryPoint(point, -1, parameter)
        {
            SplitBinding = segment.Binding with { Parameter = parameter },
        };
    }

    private bool IsOnRing(Vec2 point) => DistanceToRing(point) <= OnRingTolerance;

    private double DistanceToRing(Vec2 point)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < _ring.Count; i++)
        {
            double distance = DistanceToSegment(_ring[i], _ring[(i + 1) % _ring.Count], point);
            if (distance < best) best = distance;
        }
        return best;
    }

    /// <summary>Cut-line parameter intervals that actually lie on this child's outline.</summary>
    private static List<(double First, double Last)> ClipToRing(Vec2 a, Vec2 b, IReadOnlyList<Vec2> ring)
    {
        var result = new List<(double First, double Last)>();
        Vec2 side = b - a;
        double length = side.Length;
        if (!double.IsFinite(length) || length <= PointEpsilon) return result;

        var cuts = new List<double> { 0.0, 1.0 };
        for (int i = 0; i < ring.Count; i++)
        {
            Vec2 p = ring[i];
            Vec2 q = ring[(i + 1) % ring.Count];
            Vec2 edge = q - p;
            double denominator = Cross(side, edge);
            if (Math.Abs(denominator) <= ParameterEpsilon) continue;
            double t = Cross(p - a, edge) / denominator;
            double u = Cross(p - a, side) / denominator;
            if (u < -ParameterEpsilon || u > 1 + ParameterEpsilon) continue;
            if (t < -ParameterEpsilon || t > 1 + ParameterEpsilon) continue;
            cuts.Add(Math.Clamp(t, 0.0, 1.0));
        }
        cuts.Sort();

        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            double first = cuts[i];
            double last = cuts[i + 1];
            if (last - first <= ParameterEpsilon) continue;
            Vec2 mid = Lerp(a, b, (first + last) * 0.5);
            if (!IsOnRingStatic(mid, ring)) continue;
            if (result.Count > 0 && Math.Abs(result[^1].Last - first) <= ParameterEpsilon)
                result[^1] = (result[^1].First, last);
            else
                result.Add((first, last));
        }
        return result;
    }

    private static bool IsOnRingStatic(Vec2 point, IReadOnlyList<Vec2> ring)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            if (DistanceToSegment(ring[i], ring[(i + 1) % ring.Count], point) <= OnRingTolerance)
                return true;
        }
        return false;
    }

    private static bool TryRaySegment(Vec2 origin, Vec2 direction, SplitSegment segment,
        double tolerance, out Vec2 hit, out bool tangent)
    {
        hit = default;
        tangent = false;
        Vec2 e = segment.B - segment.A;
        double cross = Cross(direction, e);
        double directionLength = direction.Length;
        double edgeLength = e.Length;
        if (Math.Abs(cross) <= ExitBoundaryConstraint.Tolerance * directionLength * edgeLength)
        {
            if (Math.Abs(Cross(segment.A - origin, direction)) <= tolerance * directionLength)
                tangent = true;
            return false;
        }
        double s = Cross(segment.A - origin, e) / cross;
        double t = Cross(segment.A - origin, direction) / cross;
        if (s < -tolerance) return false;
        if (t < -ParameterEpsilon || t > 1 + ParameterEpsilon) return false;
        hit = origin + direction * s;
        return true;
    }

    private static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 point)
    {
        Vec2 side = b - a;
        double lengthSquared = Dot(side, side);
        if (lengthSquared <= 1e-18) return point.DistanceTo(a);
        double u = Math.Clamp(Dot(point - a, side) / lengthSquared, 0, 1);
        return Lerp(a, b, u).DistanceTo(point);
    }

    private static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    private static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    private static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    private static bool IsFinite(Vec2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
