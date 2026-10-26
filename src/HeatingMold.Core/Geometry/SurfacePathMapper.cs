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
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
        mapped = new List<Vec3>();
        failure = string.Empty;
        if (path.Count == 0)
        {
            failure = "The planar path is empty.";
            return false;
        }

        var result = new List<Vec3>();
        string error = string.Empty;
        int probes = 0;

        bool TryProbe(Vec2 point, out Vec3 value)
        {
            value = default;
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
            Vec3? sample = hit(point);
            if (!sample.HasValue)
            {
                error = "The path leaves the selected surface domain.";
                return false;
            }
            value = sample.Value;
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z))
            {
                error = "A surface hit contains a nonfinite coordinate.";
                return false;
            }
            return true;
        }

        void Append(Vec3 point)
        {
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        }

        bool Refine(Vec2 a, Vec3 mappedA, Vec2 b, Vec3 mappedB, int depth)
        {
            Vec2 midpoint = a * 0.5 + b * 0.5;
            if (!TryProbe(midpoint, out Vec3 mappedMidpoint)) return false;
            Vec2 quarter = a * 0.75 + b * 0.25;
            Vec2 threeQuarter = a * 0.25 + b * 0.75;
            if (!TryProbe(quarter, out Vec3 mappedQuarter) ||
                !TryProbe(threeQuarter, out Vec3 mappedThreeQuarter)) return false;

            // Compare at the same parameter, rather than distance to the infinite
            // chord line: a jump along that line must also trigger refinement.
            Vec3 chordMidpoint = mappedA * 0.5 + mappedB * 0.5;
            Vec3 chordQuarter = mappedA * 0.75 + mappedB * 0.25;
            Vec3 chordThreeQuarter = mappedA * 0.25 + mappedB * 0.75;
            if (a.DistanceTo(b) <= MaxPlanarSpacing &&
                (mappedMidpoint - chordMidpoint).Length <= MaxChordError &&
                (mappedQuarter - chordQuarter).Length <= MaxChordError &&
                (mappedThreeQuarter - chordThreeQuarter).Length <= MaxChordError)
            {
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
                error = "Surface mapping could not resolve a discontinuity or sampling tolerance within depth 18.";
                return false;
            }
            return Refine(a, mappedA, midpoint, mappedMidpoint, depth + 1) &&
                Refine(midpoint, mappedMidpoint, b, mappedB, depth + 1);
        }

        if (!TryProbe(path[0], out Vec3 previous))
        {
            failure = error;
            return false;
        }
        Append(previous);
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] == path[i - 1]) continue;
            if (!TryProbe(path[i], out Vec3 next) ||
                !Refine(path[i - 1], previous, path[i], next, 0))
            {
                failure = error;
                return false;
            }
            previous = next;
        }
        mapped = result;
        return true;
    }

    /// <summary>Adaptive map retaining the face identity of every final, path-ordered sample.</summary>
    public static bool TryMapWithMetadata(IReadOnlyList<Vec2> path, Func<Vec2, SurfaceMappedSample?> hit,
        out List<SurfaceMappedSample> mapped, out string failure, int maxProbes = DefaultMaxProbes)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(hit);
        mapped = new(); failure = string.Empty;
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
        if (path.Count == 0) { failure = "The planar path is empty."; return false; }
        var result = new List<SurfaceMappedSample>(); string error = string.Empty; int probes = 0;
        bool Probe(Vec2 uv, out SurfaceMappedSample sample)
        {
            sample = default;
            if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) { error = "The planar path contains a nonfinite coordinate."; return false; }
            if (probes++ >= maxProbes) { error = $"Surface mapping exceeded the {maxProbes}-probe limit."; return false; }
            var value = hit(uv);
            if (!value.HasValue) { error = "The path leaves the selected surface domain."; return false; }
            sample = value.Value;
            if (!double.IsFinite(sample.Point.X) || !double.IsFinite(sample.Point.Y) || !double.IsFinite(sample.Point.Z))
            { error = "A surface hit contains a nonfinite coordinate."; return false; }
            return true;
        }
        void Append(SurfaceMappedSample sample)
        { if (result.Count == 0 || result[^1].DatumPoint != sample.DatumPoint) result.Add(sample); }
        bool Refine(Vec2 a, SurfaceMappedSample ma, Vec2 b, SurfaceMappedSample mb, int depth)
        {
            Vec2 mid=a*0.5+b*0.5, q1=a*0.75+b*0.25, q3=a*0.25+b*0.75;
            if (!Probe(mid,out var mm) || !Probe(q1,out var m1) || !Probe(q3,out var m3)) return false;
            Vec3 chordMid=ma.Point*0.5+mb.Point*0.5, chord1=ma.Point*0.75+mb.Point*0.25, chord3=ma.Point*0.25+mb.Point*0.75;
            if (a.DistanceTo(b)<=MaxPlanarSpacing && (mm.Point-chordMid).Length<=MaxChordError &&
                (m1.Point-chord1).Length<=MaxChordError && (m3.Point-chord3).Length<=MaxChordError)
            { Append(m1); Append(mm); Append(m3); Append(mb); return true; }
            if (depth>=MaxDepth || mid==a || mid==b) { error="Surface mapping could not resolve a discontinuity or sampling tolerance within depth 18."; return false; }
            return Refine(a,ma,mid,mm,depth+1) && Refine(mid,mm,b,mb,depth+1);
        }
        if (!Probe(path[0],out var first)) { failure=error; return false; }
        Append(first);
        for (int i=1;i<path.Count;i++)
        {
            if (path[i]==path[i-1]) continue;
            if (!Probe(path[i],out var next) || !Refine(path[i-1],result[^1],path[i],next,0)) { failure=error; return false; }
        }
        mapped=result; return true;
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
        mapped = new(); failure = string.Empty;
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
        if (path.Count == 0) { failure = "The planar path is empty."; return false; }
        var result = new List<SurfaceMappedSample>(); string error = string.Empty; int probes = 0;
        bool Probe(Vec2 uv, out SurfaceMappedSample sample)
        {
            sample = default;
            if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) { error = "The planar path contains a nonfinite coordinate."; return false; }
            if (probes++ >= maxProbes) { error = $"Surface mapping exceeded the {maxProbes}-probe limit."; return false; }
            var value = hit(uv);
            if (!value.HasValue) { error = "The path leaves the selected surface domain."; return false; }
            sample = value.Value;
            if (!double.IsFinite(sample.Point.X) || !double.IsFinite(sample.Point.Y) || !double.IsFinite(sample.Point.Z))
            { error = "A surface hit contains a nonfinite coordinate."; return false; }
            return true;
        }
        void Append(SurfaceMappedSample sample)
        { if (result.Count == 0 || result[^1].DatumPoint != sample.DatumPoint) result.Add(sample); }
        bool Refine(Vec2 a, SurfaceMappedSample ma, Vec2 b, SurfaceMappedSample mb, int depth)
        {
            Vec2 mid=a*0.5+b*0.5, q1=a*0.75+b*0.25, q3=a*0.25+b*0.75;
            if (!Probe(mid,out var mm) || !Probe(q1,out var m1) || !Probe(q3,out var m3)) return false;
            Vec3 chordMid=ma.Point*0.5+mb.Point*0.5, chord1=ma.Point*0.75+mb.Point*0.25, chord3=ma.Point*0.25+mb.Point*0.75;
            if ((mm.Point-chordMid).Length<=MaxChordError &&
                (m1.Point-chord1).Length<=MaxChordError && (m3.Point-chord3).Length<=MaxChordError)
            { Append(m1); Append(mm); Append(m3); Append(mb); return true; }
            if (depth>=MaxDepth || mid==a || mid==b) { error="Surface mapping could not resolve a discontinuity within depth 18."; return false; }
            return Refine(a,ma,mid,mm,depth+1) && Refine(mid,mm,b,mb,depth+1);
        }
        if (!Probe(path[0],out var first)) { failure=error; return false; }
        Append(first);
        for (int i=1;i<path.Count;i++)
        {
            if (path[i]==path[i-1]) continue;
            if (!Probe(path[i],out var next) || !Refine(path[i-1],result[^1],path[i],next,0)) { failure=error; return false; }
        }
        mapped=result; return true;
    }
}
