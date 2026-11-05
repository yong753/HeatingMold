namespace HeatingMold.Core.Geometry;

public readonly record struct SurfaceMappedSample(Vec2 DatumPoint, Vec3 Point, int FaceId);

/// <summary>Maps a complete planar polyline onto one caller-selected surface domain.</summary>
public static class SurfacePathMapper
{
    // Conservative depth-zero route target. Probes are at quarter intervals within
    // accepted leaves (at most 0.025 mm apart); sub-probe-width features are not guaranteed.
    private const double MaxPlanarSpacing = 0.1;
    private const double MaxChordError = 0.05;
    private const int MaxDepth = 18;
    private const int DefaultMaxProbes = 100_000;
    private const string DenseDepthError =
        "Surface mapping could not resolve a discontinuity or sampling tolerance within depth 18.";
    private const string RouteDepthError =
        "Surface mapping could not resolve a discontinuity within depth 18.";

    public static bool AreSamplesContinuous(IReadOnlyList<SurfaceMappedSample> samples,
        Func<int,int,bool> adjacent, double max3DStep = 2.0)
    {
        ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(adjacent);
        if (!double.IsFinite(max3DStep) || max3DStep <= 0) throw new ArgumentOutOfRangeException(nameof(max3DStep));
        for (int i=1;i<samples.Count;i++)
        {
            var a=samples[i-1]; var b=samples[i];
            if ((a.Point-b.Point).Length>max3DStep ||
                (a.FaceId!=b.FaceId && !adjacent(a.FaceId,b.FaceId))) return false;
        }
        return true;
    }

    /// <summary>
    /// Samples endpoints, quarter points and midpoints, refining until planar spacing
    /// (0.1 mm) and chord error (0.05 mm) are within tolerance. The hit delegate must query the entire
    /// selected domain. Any missing/invalid hit or exhausted refinement limit rejects
    /// the entire path; no partial path is returned. Only consecutive duplicates are
    /// removed, preserving intentional returns to earlier vertices.
    /// </summary>
    public static bool TryMap(IReadOnlyList<Vec2> path, Func<Vec2, Vec3?> hit,
        out List<Vec3> mapped, out string failure, int maxProbes = DefaultMaxProbes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(hit);
        return TryMapCore(path, hit, point => point, (a, b) => a == b,
            requirePlanarSpacing: true, gapSpacing: 0, previousSampleFromResult: false,
            DenseDepthError, out mapped, out failure, maxProbes);
    }

    /// <summary>Adaptive map retaining the face identity of every final, path-ordered sample.</summary>
    public static bool TryMapWithMetadata(IReadOnlyList<Vec2> path, Func<Vec2, SurfaceMappedSample?> hit,
        out List<SurfaceMappedSample> mapped, out string failure, int maxProbes = DefaultMaxProbes)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(hit);
        return TryMapCore(path, hit, sample => sample.Point,
            (a, b) => a.DatumPoint == b.DatumPoint,
            requirePlanarSpacing: true, gapSpacing: 0, previousSampleFromResult: true,
            DenseDepthError, out mapped, out failure, maxProbes);
    }

    /// <summary>
    /// Route-grade adaptive map. Acceptance is chord-error driven only (no planar spacing
    /// floor), so a straight lead yields a sparse polyline instead of thousands of points.
    /// Face identity is retained for diagnostics and seam checks.
    /// </summary>
    public static bool TryMapRoute(IReadOnlyList<Vec2> path, Func<Vec2, SurfaceMappedSample?> hit,
        out List<SurfaceMappedSample> mapped, out string failure, int maxProbes = DefaultMaxProbes)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(hit);
        return TryMapRouteCore(path, hit, 0, out mapped, out failure, maxProbes);
    }

    /// <summary>
    /// 편집 검증용 변형: 채택된 리프 구간 내부를 <paramref name="gapSpacing"/>(mm) 간격으로
    /// 추가 표본화해, 끝점/사분점 사이에 숨은 홀·도메인 이탈을 걸러낸다. 이 간격보다 좁은
    /// 결함은 여전히 보장하지 않는다(표본 한계). gapSpacing ≤ 0이면 일반 경로와 동일하다.
    /// </summary>
    public static bool TryMapRouteStrict(IReadOnlyList<Vec2> path, Func<Vec2, SurfaceMappedSample?> hit,
        out List<SurfaceMappedSample> mapped, out string failure,
        double gapSpacing = 0.1, int maxProbes = DefaultMaxProbes)
    {
        if (!double.IsFinite(gapSpacing) || gapSpacing < 0)
            throw new ArgumentOutOfRangeException(nameof(gapSpacing));
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(hit);
        return TryMapRouteCore(path, hit, gapSpacing, out mapped, out failure, maxProbes);
    }

    private static bool TryMapRouteCore(IReadOnlyList<Vec2> path, Func<Vec2, SurfaceMappedSample?> hit,
        double gapSpacing, out List<SurfaceMappedSample> mapped, out string failure, int maxProbes)
    {
        return TryMapCore(path, hit, sample => sample.Point,
            (a, b) => a.DatumPoint == b.DatumPoint,
            requirePlanarSpacing: false, gapSpacing, previousSampleFromResult: true,
            RouteDepthError, out mapped, out failure, maxProbes);
    }

    /// <summary>
    /// Shared adaptive traversal. The three public modes differ only in sample equality,
    /// planar-spacing policy, optional gap probes, and which sample seeds the next segment.
    /// Probe order and every rejection path are intentionally identical across modes.
    /// </summary>
    private static bool TryMapCore<TSample>(IReadOnlyList<Vec2> path, Func<Vec2, TSample?> hit,
        Func<TSample, Vec3> pointOf, Func<TSample, TSample, bool> same,
        bool requirePlanarSpacing, double gapSpacing, bool previousSampleFromResult,
        string depthError, out List<TSample> mapped, out string failure, int maxProbes)
        where TSample : struct
    {
        mapped = new List<TSample>();
        failure = string.Empty;
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
        if (path.Count == 0) { failure = "The planar path is empty."; return false; }

        var result = new List<TSample>();
        string error = string.Empty;
        int probes = 0;

        bool Probe(Vec2 point, out TSample sample)
        {
            sample = default;
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                error = "The planar path contains a nonfinite coordinate.";
                return false;
            }
            if (probes >= maxProbes)
            {
                error = $"Surface mapping exceeded the {maxProbes}-probe limit.";
                return false;
            }
            probes++;
            TSample? value = hit(point);
            if (!value.HasValue)
            {
                error = "The path leaves the selected surface domain.";
                return false;
            }
            sample = value.Value;
            Vec3 mappedPoint = pointOf(sample);
            if (!double.IsFinite(mappedPoint.X) || !double.IsFinite(mappedPoint.Y) || !double.IsFinite(mappedPoint.Z))
            {
                error = "A surface hit contains a nonfinite coordinate.";
                return false;
            }
            return true;
        }

        void Append(TSample sample)
        {
            if (result.Count == 0 || !same(result[^1], sample)) result.Add(sample);
        }

        // 리프 내부의 표본화되지 않은 긴 구간을 gapSpacing 간격으로 확인한다.
        bool ProbeLeafGaps(Vec2 a, Vec2 b)
        {
            if (gapSpacing <= 0) return true;
            double length = a.DistanceTo(b);
            if (length <= gapSpacing) return true;
            int steps = (int)Math.Ceiling(length / gapSpacing);
            for (int step = 1; step < steps; step++)
            {
                Vec2 uv = a + (b - a) * (step / (double)steps);
                if (!Probe(uv, out _)) return false;
            }
            return true;
        }

        bool Refine(Vec2 a, TSample mappedA, Vec2 b, TSample mappedB, int depth)
        {
            Vec2 midpoint = a * 0.5 + b * 0.5;
            if (!Probe(midpoint, out TSample mappedMidpoint)) return false;
            Vec2 quarter = a * 0.75 + b * 0.25;
            Vec2 threeQuarter = a * 0.25 + b * 0.75;
            if (!Probe(quarter, out TSample mappedQuarter) ||
                !Probe(threeQuarter, out TSample mappedThreeQuarter)) return false;

            // Compare at the same parameter, rather than distance to the infinite
            // chord line: a jump along that line must also trigger refinement.
            Vec3 pointA = pointOf(mappedA);
            Vec3 pointB = pointOf(mappedB);
            Vec3 chordMidpoint = pointA * 0.5 + pointB * 0.5;
            Vec3 chordQuarter = pointA * 0.75 + pointB * 0.25;
            Vec3 chordThreeQuarter = pointA * 0.25 + pointB * 0.75;
            bool withinPlanarSpacing = !requirePlanarSpacing || a.DistanceTo(b) <= MaxPlanarSpacing;
            if (withinPlanarSpacing &&
                (pointOf(mappedMidpoint) - chordMidpoint).Length <= MaxChordError &&
                (pointOf(mappedQuarter) - chordQuarter).Length <= MaxChordError &&
                (pointOf(mappedThreeQuarter) - chordThreeQuarter).Length <= MaxChordError)
            {
                if (!ProbeLeafGaps(a, b)) return false;
                // These probes are part of the accepted leaf's ordered geometry: dropping
                // them here can hide a narrow face/domain transition between equal endpoints.
                Append(mappedQuarter);
                Append(mappedMidpoint);
                Append(mappedThreeQuarter);
                Append(mappedB);
                return true;
            }
            if (depth >= MaxDepth || midpoint == a || midpoint == b)
            {
                error = depthError;
                return false;
            }
            return Refine(a, mappedA, midpoint, mappedMidpoint, depth + 1) &&
                Refine(midpoint, mappedMidpoint, b, mappedB, depth + 1);
        }

        if (!Probe(path[0], out TSample previous))
        {
            failure = error;
            return false;
        }
        Append(previous);
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] == path[i - 1]) continue;
            if (!Probe(path[i], out TSample next))
            {
                failure = error;
                return false;
            }
            TSample segmentStart = previousSampleFromResult ? result[^1] : previous;
            if (!Refine(path[i - 1], segmentStart, path[i], next, 0))
            {
                failure = error;
                return false;
            }
            previous = next;
        }
        mapped = result;
        return true;
    }
}
