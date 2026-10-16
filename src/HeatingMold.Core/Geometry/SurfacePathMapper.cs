namespace HeatingMold.Core.Geometry;

/// <summary>Maps a complete planar polyline onto one caller-selected surface domain.</summary>
public static class SurfacePathMapper
{
    private const double MaxPlanarSpacing = 0.5;
    private const double MaxChordError = 0.05;
    private const int MaxDepth = 18;
    private const int DefaultMaxProbes = 100_000;

    /// <summary>
    /// Samples endpoints and midpoints, refining until planar spacing and midpoint chord
    /// error are within tolerance (millimetres). The hit delegate must query the entire
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
}
