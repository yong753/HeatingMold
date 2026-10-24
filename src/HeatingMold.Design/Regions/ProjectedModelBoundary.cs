using HeatingMold.Core.Geometry;

namespace HeatingMold.Design.Regions;

/// <summary>삼각 메시 투영으로 얻은 근사 경계 링입니다.</summary>
/// <remarks>정확한 CAD 경계가 아니며 원래 면과의 대응은 전체 모델 BRep 질의로 확인해야 합니다.</remarks>
public sealed record ProjectedModelBoundaryRing(IReadOnlyList<Vec2> Points, int NestingDepth, double MeshDeflection)
{
    /// <summary>중첩 깊이가 홀에 해당하는지 나타냅니다.</summary>
    public bool IsHole => NestingDepth % 2 != 0;
}

/// <summary>삼각 메시 투영 결과에서 외곽과 홀 링을 만드는 레거시 계산기입니다.</summary>
public static class ProjectedModelBoundary
{
    /// <summary>투영된 삼각형을 합쳐 포함 영역과 홀 링을 계산합니다.</summary>
    /// <param name="triangles">작업 평면에 투영된 삼각형 좌표입니다.</param>
    /// <param name="meshDeflection">원본 삼각 메시의 선형 편차(mm)입니다.</param>
    /// <returns>중첩 깊이와 메시 편차를 포함하는 경계 후보 목록입니다.</returns>
    public static IReadOnlyList<ProjectedModelBoundaryRing> FromTriangles(
        IEnumerable<IReadOnlyList<Vec2>> triangles,
        double meshDeflection)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        if (!double.IsFinite(meshDeflection) || meshDeflection <= 0)
            throw new ArgumentOutOfRangeException(nameof(meshDeflection), "메시 편차는 양의 유한값이어야 합니다.");

        List<List<Vec2>> rings = RegionOps.UnionPolygons(
            triangles.Where(triangle => triangle.Count == 3 && Math.Abs(RegionOps.SignedArea(triangle)) > 1e-10),
            precision: 6);

        return rings.Select(ring => new ProjectedModelBoundaryRing(
            ring,
            rings.Count(other => !ReferenceEquals(other, ring) && Contains(other, ring[0])),
            meshDeflection)).ToArray();
    }

    private static bool Contains(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int current = 0, previous = ring.Count - 1; current < ring.Count; previous = current++)
        {
            Vec2 a = ring[current];
            Vec2 b = ring[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
