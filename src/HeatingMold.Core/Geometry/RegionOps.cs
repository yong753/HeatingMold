using Clipper2Lib;

namespace HeatingMold.Core.Geometry;

/// <summary>Clipper2 래퍼 — 마진 오프셋·홀 차집합.</summary>
public static class RegionOps
{
    /// <summary>Union overlapping or adjacent planar face polygons into non-overlapping region rings.</summary>
    public static List<List<Vec2>> UnionPolygons(IEnumerable<IReadOnlyList<Vec2>> polygons, int precision = 2)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        PathsD paths = new();
        foreach (IReadOnlyList<Vec2> polygon in polygons)
        {
            if (polygon.Count < 3) continue;
            paths.Add(ToPath(EnsureOrientation(polygon, ccw: true)));
        }
        return FromPaths(Clipper.Union(paths, new PathsD(), FillRule.NonZero, precision));
    }

    /// <summary>Subtract rectangular exclusion regions from a polygonal region.</summary>
    public static List<List<Vec2>> SubtractRectangles(
        IEnumerable<IReadOnlyList<Vec2>> region,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(exclusions);
        PathsD subject = new();
        foreach (IReadOnlyList<Vec2> ring in region)
            if (ring.Count >= 3) subject.Add(ToPath(ring));
        PathsD clips = new();
        foreach (var r in exclusions)
        {
            if (!(r.MaxX > r.MinX && r.MaxY > r.MinY)) continue;
            clips.Add(ToPath(new[] { new Vec2(r.MinX, r.MinY), new Vec2(r.MaxX, r.MinY),
                new Vec2(r.MaxX, r.MaxY), new Vec2(r.MinX, r.MaxY) }));
        }
        if (subject.Count == 0) return new();
        return clips.Count == 0 ? FromPaths(subject) : FromPaths(Clipper.Difference(subject, clips, FillRule.NonZero));
    }

    /// <summary>링의 부호 있는 면적 (CCW 양수).</summary>
    public static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        double a = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            Vec2 p = ring[i];
            Vec2 q = ring[(i + 1) % ring.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2.0;
    }

    /// <summary>패턴 영역 = outer를 margin만큼 안쪽 오프셋 − holes를 margin만큼 바깥쪽 오프셋.</summary>
    public static List<List<Vec2>> BuildPatternRegion(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        double margin)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));

        PathD outerPath = ToPath(EnsureOrientation(outer, ccw: true));
        PathsD subject = Clipper.InflatePaths(
            new PathsD { outerPath }, -margin, JoinType.Miter, EndType.Polygon);

        if (holes is null || holes.Count == 0)
            return FromPaths(subject);

        var clipPaths = new PathsD();
        foreach (var h in holes)
        {
            if (h.Count < 3) continue;
            clipPaths.Add(ToPath(EnsureOrientation(h, ccw: false)));
        }
        if (clipPaths.Count == 0) return FromPaths(subject);

        PathsD clip = Clipper.InflatePaths(clipPaths, margin, JoinType.Miter, EndType.Polygon);
        PathsD result = Clipper.Difference(subject, clip, FillRule.NonZero);
        return FromPaths(result);
    }

    private static IReadOnlyList<Vec2> EnsureOrientation(IReadOnlyList<Vec2> ring, bool ccw)
    {
        bool isCcw = SignedArea(ring) > 0;
        if (isCcw == ccw) return ring;
        var rev = new List<Vec2>(ring);
        rev.Reverse();
        return rev;
    }

    private static PathD ToPath(IReadOnlyList<Vec2> ring)
    {
        var p = new PathD(ring.Count);
        foreach (Vec2 v in ring) p.Add(new PointD(v.X, v.Y));
        return p;
    }

    private static List<List<Vec2>> FromPaths(PathsD paths)
    {
        var result = new List<List<Vec2>>(paths.Count);
        foreach (PathD path in paths)
        {
            var ring = new List<Vec2>(path.Count);
            foreach (PointD pt in path) ring.Add(new Vec2(pt.x, pt.y));
            if (ring.Count >= 3) result.Add(ring);
        }
        return result;
    }
}
