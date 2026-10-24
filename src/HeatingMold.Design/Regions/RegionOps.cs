using Clipper2Lib;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Design.Regions;

/// <summary>
/// 2차원 발열 영역의 합집합·차집합·경계 오프셋을 수행하는 Clipper2 어댑터입니다.
/// 3차원 표면 매핑과 실제 CAD 면 검증은 담당하지 않습니다.
/// </summary>
public static class RegionOps
{
    /// <summary>겹치거나 맞닿은 평면 다각형을 겹침 없는 경계 링으로 합칩니다.</summary>
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

    /// <summary>outer·hole 방향을 유지한 영역에서 다른 다각형 합집합을 뺍니다.</summary>
    public static List<List<Vec2>> DifferencePolygons(
        IEnumerable<IReadOnlyList<Vec2>> subjectRings,
        IEnumerable<IReadOnlyList<Vec2>> clipPolygons,
        int precision = 6)
    {
        PathsD subject = ToPaths(subjectRings, nameof(subjectRings));
        PathsD clips = ToPaths(clipPolygons, nameof(clipPolygons), normalizeAsOuter: true);
        if (subject.Count == 0)
            return new List<List<Vec2>>();
        if (clips.Count == 0)
            return FromPaths(subject);

        return FromPaths(Clipper.Difference(subject, clips, FillRule.NonZero, precision));
    }

    /// <summary>선택된 실제 표면 영역을 포함 다각형 합집합과 교차합니다.</summary>
    public static List<List<Vec2>> IntersectPolygons(
        IEnumerable<IReadOnlyList<Vec2>> subjectRings,
        IEnumerable<IReadOnlyList<Vec2>> clipPolygons,
        int precision = 6)
    {
        PathsD subject = ToPaths(subjectRings, nameof(subjectRings));
        PathsD clips = ToPaths(clipPolygons, nameof(clipPolygons), normalizeAsOuter: true);
        if (subject.Count == 0 || clips.Count == 0)
            return new List<List<Vec2>>();

        return FromPaths(Clipper.Intersect(subject, clips, FillRule.NonZero, precision));
    }

    /// <summary>다각형 영역에서 직사각형 제외 영역을 뺍니다.</summary>
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

    /// <summary>배치 영역 = outer를 경계 여유만큼 안쪽 오프셋 − holes를 여유만큼 바깥쪽 오프셋.</summary>
    public static List<List<Vec2>> BuildPatternRegion(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        double boundaryMarginMm)
    {
        if (outer.Count < 3) throw new ArgumentException("외곽 링은 점 3개 이상 필요", nameof(outer));

        PathD outerPath = ToPath(EnsureOrientation(outer, ccw: true));
        PathsD subject = Clipper.InflatePaths(
            new PathsD { outerPath }, -boundaryMarginMm, JoinType.Miter, EndType.Polygon);

        if (holes is null || holes.Count == 0)
            return FromPaths(subject);

        var clipPaths = new PathsD();
        foreach (var h in holes)
        {
            if (h.Count < 3) continue;
            clipPaths.Add(ToPath(EnsureOrientation(h, ccw: false)));
        }
        if (clipPaths.Count == 0) return FromPaths(subject);

        PathsD clip = Clipper.InflatePaths(clipPaths, boundaryMarginMm, JoinType.Miter, EndType.Polygon);
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

    private static PathsD ToPaths(
        IEnumerable<IReadOnlyList<Vec2>> rings,
        string parameterName,
        bool normalizeAsOuter = false)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var paths = new PathsD();
        foreach (IReadOnlyList<Vec2> ring in rings)
        {
            if (ring is null)
                throw new ArgumentException("다각형 목록은 null 링을 포함할 수 없습니다.", parameterName);
            if (ring.Count < 3)
                continue;

            IReadOnlyList<Vec2> oriented = normalizeAsOuter
                ? EnsureOrientation(ring, ccw: true)
                : ring;
            paths.Add(ToPath(oriented));
        }

        return paths;
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
