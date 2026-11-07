namespace HeatingMold.Core.Geometry;

/// <summary>인출 경계에서 유일한 교차점을 찾지 못한 이유.</summary>
public enum ExitBoundaryFailureReason
{
    InvalidInput,
    NoIntersection,
    MultipleIntersections,
    TangentOrOverlap,
    DegenerateSegment,
    StaleContext,
    MissingReference,
    GeometryFailure,
}

/// <summary>인출 경계 위의 한 점. SegmentIndex/SegmentParameter는 표시용 폴리라인 메타데이터이며,
/// CAD 구속의 정본은 Binding의 원본 에지와 파라미터다.</summary>
public readonly record struct ExitBoundaryPoint(Vec2 Point, int SegmentIndex, double SegmentParameter)
{
    /// <summary>Original CAD provenance, absent only for explicit non-CAD/Core fixtures.</summary>
    public ExitEdgeBinding? Binding { get; init; }
}

public sealed record ExitBoundaryResolution(ExitBoundaryPoint? Point, ExitBoundaryFailureReason? Failure)
{
    public bool Succeeded => Point.HasValue;
    public static ExitBoundaryResolution Ok(ExitBoundaryPoint point) => new(point, null);
    public static ExitBoundaryResolution Fail(ExitBoundaryFailureReason reason) => new(null, reason);
}

/// <summary>
/// 인출점을 사행 경로의 첫/마지막 직선 <b>바깥 연장선</b>과 인출 경계의 교차점으로 한정한다.
/// 경계에 닿지 않거나, 접선/겹침이거나, 유일하지 않으면 실패한다. 다른 경계로 대체하지 않는다.
/// </summary>
public static class ExitBoundaryConstraint
{
    public const double Tolerance = 1e-7;
    private const double ParallelTolerance = 1e-9;

    /// <summary>
    /// <paramref name="endpoint"/>에서 <paramref name="next"/> 방향의 경로를 뒤로 연장해 경계와 만나는
    /// 유일한 점을 찾는다. 연장 방향은 endpoint - next이며, endpoint 바깥(s ≥ 0)만 유효하다.
    /// </summary>
    public static ExitBoundaryResolution TryResolve(
        Vec2 endpoint, Vec2 next, IReadOnlyList<Vec2> chain, double tolerance = Tolerance,
        IExitBoundary2D? boundary = null)
    {
        if (!IsFinite(endpoint) || !IsFinite(next)
            || !double.IsFinite(tolerance) || tolerance <= 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);

        Vec2 direction = endpoint - next;
        double length = direction.Length;
        if (!double.IsFinite(length) || length <= tolerance)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);
        if (boundary != null)
            return RequireBinding(boundary.ResolveOutward(endpoint, next, tolerance));
        if (chain == null || chain.Count < 2)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        Vec2 d = direction * (1.0 / length);

        bool closed = chain.Count >= 3 && chain[0].DistanceTo(chain[^1]) <= tolerance;
        int count = closed ? chain.Count - 1 : chain.Count;
        if (count < 2) return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        int segmentCount = closed ? count : count - 1;

        var hits = new List<Hit>(4);
        for (int i = 0; i < segmentCount; i++)
        {
            Vec2 a = chain[i];
            Vec2 b = chain[(i + 1) % count];
            Vec2 edge = b - a;
            double edgeLength = edge.Length;
            if (!double.IsFinite(edgeLength) || edgeLength <= tolerance) continue;
            Vec2 e = edge * (1.0 / edgeLength);

            Vec2 ap = a - endpoint;
            double denom = Cross(d, e);
            if (Math.Abs(denom) <= ParallelTolerance)
            {
                // 평행. 같은 직선 위에서 연장선과 겹치면 접선/겹침으로 거부한다.
                if (Math.Abs(Cross(d, ap)) <= tolerance)
                {
                    double ta = Dot(ap, d);
                    double tb = Dot(b - endpoint, d);
                    if (Math.Max(ta, tb) >= -tolerance)
                        return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.TangentOrOverlap);
                }
                continue;
            }

            double s = Cross(ap, e) / denom;
            double t = Cross(ap, d) / (denom * edgeLength);
            double tTolerance = tolerance / edgeLength;
            if (s < -tolerance) continue;
            if (t < -tTolerance || t > 1.0 + tTolerance) continue;

            double clampedT = Math.Clamp(t, 0.0, 1.0);
            Vec2 point = a + edge * clampedT;
            double along = Dot(point - endpoint, d);
            if (along < -tolerance) continue;
            hits.Add(new Hit(point, i, clampedT, along, tTolerance));
        }

        if (hits.Count == 0)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);

        var unique = new List<Hit>(hits.Count);
        foreach (Hit hit in hits.OrderBy(h => h.Along).ThenBy(h => h.SegmentIndex))
        {
            int mergedAt = -1;
            for (int i = 0; i < unique.Count; i++)
            {
                if (unique[i].Point.DistanceTo(hit.Point) <= tolerance)
                {
                    mergedAt = i;
                    break;
                }
            }
            if (mergedAt < 0)
            {
                unique.Add(hit);
                continue;
            }

            Hit existing = unique[mergedAt];
            if (!Adjacent(existing.SegmentIndex, hit.SegmentIndex, segmentCount, closed))
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);

            // 인접 구간이 공유하는 꼭짓점에서 만난 경우 하나의 교차로 합친다.
            unique[mergedAt] = existing with { Vertex = true };
        }

        if (unique.Count > 1)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MultipleIntersections);

        Hit best = unique[0];
        if (best.Vertex || best.IsVertex)
        {
            int vertex = best.VertexIndex;
            if (vertex >= 0 && !CrossesAtVertex(chain, count, closed, vertex, endpoint, d, tolerance))
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.TangentOrOverlap);
        }

        return ExitBoundaryResolution.Ok(
            new ExitBoundaryPoint(best.Point, best.SegmentIndex, best.SegmentParameter));
    }

    /// <summary>A CAD query must never be replaced by an unbound sampled result.</summary>
    internal static ExitBoundaryResolution RequireBinding(ExitBoundaryResolution resolution)
    {
        if (resolution.Failure.HasValue)
            return ExitBoundaryResolution.Fail(resolution.Failure.Value);
        if (!resolution.Point.HasValue || !IsFinite(resolution.Point.Value.Point))
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.GeometryFailure);
        if (!resolution.Point.Value.Binding.HasValue)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
        ExitEdgeBinding binding = resolution.Point.Value.Binding.Value;
        if (!double.IsFinite(binding.FirstParameter) || !double.IsFinite(binding.LastParameter)
            || !double.IsFinite(binding.Parameter))
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.GeometryFailure);
        return resolution;
    }

    /// <summary>Validate a saved parameter, without searching for a different edge or parameter.</summary>
    internal static ExitBoundaryResolution EvaluateSaved(
        IExitBoundary2D boundary, ExitEdgeBinding? binding, Vec2 expectedPoint)
    {
        if (!IsFinite(expectedPoint))
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.InvalidInput);
        if (!binding.HasValue)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
        ExitBoundaryResolution result = RequireBinding(boundary.Evaluate(binding.Value));
        if (!result.Succeeded) return result;
        ExitBoundaryPoint evaluated = result.Point!.Value;
        if (evaluated.Binding != binding || evaluated.Point.DistanceTo(expectedPoint) > Tolerance)
            return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.GeometryFailure);
        return result;
    }

    private static bool Adjacent(int a, int b, int segmentCount, bool closed)
    {
        if (a == b) return true;
        if (Math.Abs(a - b) == 1) return true;
        return closed && ((a == 0 && b == segmentCount - 1) || (b == 0 && a == segmentCount - 1));
    }

    /// <summary>체인의 꼭짓점에서 연장선이 실제로 경계를 가로지르는지 확인한다.
    /// 양쪽 이웃이 같은 쪽에 있으면 접선, 한 점이 직선 위면 겹침으로 본다. 열린 끝은 허용한다.</summary>
    private static bool CrossesAtVertex(
        IReadOnlyList<Vec2> chain, int count, bool closed, int vertex,
        Vec2 endpoint, Vec2 direction, double tolerance)
    {
        if (closed) vertex %= count;
        int prev = vertex - 1;
        int next = vertex + 1;
        if (closed)
        {
            prev = (vertex - 1 + count) % count;
            next = (vertex + 1) % count;
        }
        else
        {
            if (prev < 0 || next >= count) return true;
        }

        Vec2 point = chain[vertex];
        double sidePrev = Cross(direction, chain[prev] - point);
        double sideNext = Cross(direction, chain[next] - point);
        if (Math.Abs(sidePrev) <= tolerance || Math.Abs(sideNext) <= tolerance) return false;
        return sidePrev * sideNext < 0;
    }

    private readonly record struct Hit(
        Vec2 Point, int SegmentIndex, double SegmentParameter, double Along, double ParameterTolerance)
    {
        public bool Vertex { get; init; }
        public bool IsVertex => SegmentParameter <= ParameterTolerance
            || SegmentParameter >= 1.0 - ParameterTolerance;
        public int VertexIndex => SegmentParameter <= ParameterTolerance
            ? SegmentIndex
            : SegmentIndex + 1;
    }

    private static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    private static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    private static bool IsFinite(Vec2 v) => double.IsFinite(v.X) && double.IsFinite(v.Y);
}
