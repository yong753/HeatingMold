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

    /// <summary>사각형 제외영역을 amount만큼 바깥으로 팽창시킨 뒤 서로 겹치는 것끼리 병합한다.
    /// 스캔라인 사이 최소 클리어런스를 확보하기 위한 사전 처리. 축퇴 사각형(폭/높이 0)은 무시한다.</summary>
    public static List<List<Vec2>> ExpandAndUnionRectangles(
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> rects,
        double amount)
    {
        ArgumentNullException.ThrowIfNull(rects);
        double grow = Math.Max(0, amount);
        PathsD paths = new();
        foreach (var r in rects)
        {
            if (!(r.MaxX > r.MinX && r.MaxY > r.MinY)) continue;
            paths.Add(ToPath(new[]
            {
                new Vec2(r.MinX - grow, r.MinY - grow),
                new Vec2(r.MaxX + grow, r.MinY - grow),
                new Vec2(r.MaxX + grow, r.MaxY + grow),
                new Vec2(r.MinX - grow, r.MaxY + grow),
            }));
        }
        if (paths.Count == 0) return new();
        return FromPaths(Clipper.Union(paths, new PathsD(), FillRule.NonZero, 2));
    }

    /// <summary>
    /// 발열 경로 생성 전용 영역. 외곽은 margin만큼 안쪽, 제외 사각형은 같은 margin만큼
    /// 바깥쪽으로 확장해 차감한다. 원본 제외영역 값과 기존 <see cref="SubtractRectangles"/>
    /// 계약(정확 차감)은 변경하지 않는다. margin이 0이면 기존 결과와 같다.
    /// 제외영역 확장은 입력 사각형을 복사해서만 적용하며 저장·표시 값에는 영향을 주지 않는다.
    /// </summary>
    public static List<List<Vec2>> BuildGenerationRegion(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        double margin)
    {
        return BuildGenerationRegion(outer, exclusions, margin, out _);
    }

    /// <summary>
    /// <see cref="BuildGenerationRegion"/>와 같은 결과를 만들면서, 차감 전의 마진 적용
    /// 패턴 영역도 함께 돌려준다. 생성 경로가 같은 외곽 오프셋을 다시 계산하지 않도록
    /// 두 결과를 한 번의 오프셋 연산에서 만든다. 반환된 두 목록은 서로 독립적이다.
    /// </summary>
    internal static List<List<Vec2>> BuildGenerationRegion(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        double margin,
        out List<List<Vec2>> patternRegion)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(exclusions);
        patternRegion = BuildPatternRegion(outer, null, margin);
        double grow = Math.Max(0.0, margin);
        var expanded = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        foreach (var r in exclusions)
        {
            if (!(r.MaxX > r.MinX && r.MaxY > r.MinY)) continue;
            expanded.Add((r.MinX - grow, r.MinY - grow, r.MaxX + grow, r.MaxY + grow));
        }
        return SubtractRectangles(patternRegion, expanded);
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
