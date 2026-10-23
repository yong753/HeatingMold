namespace HeatingMold.Core.Geometry;

public readonly record struct ModelRevision(long Value);
public readonly record struct WorkingCoordinateSystemId(long Value);

/// <summary>
/// 작업 좌표계 입력 상태. 미리보기 중에는 기존 확정 좌표계를 유지할 수 있으며,
/// 미리보기를 취소하면 확정 상태 또는 미확정 상태로 돌아간다.
/// </summary>
public enum WorkingCoordinateSystemState { Unconfirmed, Preview, Confirmed }

/// <summary>카메라 상태와 독립적으로 모델에 고정되는 정사영 작업 좌표계.</summary>
public sealed record WorkingCoordinateSystem(WorkingCoordinateSystemId Id, ModelRevision Revision, ProjectionFrame Projection)
{
    public Vec3 RayDirection => Projection.Normal * -1;
}

public sealed class WorkingCoordinateSystemSession
{
    public WorkingCoordinateSystem? Preview { get; private set; }
    public WorkingCoordinateSystem? Confirmed { get; private set; }
    public WorkingCoordinateSystemState State => Preview != null ? WorkingCoordinateSystemState.Preview :
        Confirmed != null ? WorkingCoordinateSystemState.Confirmed : WorkingCoordinateSystemState.Unconfirmed;
    public void SetPreview(WorkingCoordinateSystem coordinateSystem) => Preview = coordinateSystem;
    public void Confirm(ModelRevision revision)
    {
        if (Preview == null || Preview.Revision != revision)
            throw new InvalidOperationException("Working coordinate system preview is missing or stale.");
        Confirmed = Preview;
        Preview = null;
    }
    public void Invalidate() { Preview = null; Confirmed = null; }
    public void CancelPreview() => Preview = null;
}

/// <summary>Mesh projection candidates, NOT exact CAD boundaries. Negative rings are holes.
/// References are obtained separately by nearest full-model BRep queries.</summary>
public sealed record ProjectedModelBoundaryRing(IReadOnlyList<Vec2> Points, int NestingDepth, double MeshDeflection)
{
    public bool IsHole => NestingDepth % 2 != 0;
}

public static class ProjectedModelBoundary
{
    public static IReadOnlyList<ProjectedModelBoundaryRing> FromTriangles(IEnumerable<IReadOnlyList<Vec2>> triangles,
        double meshDeflection)
    {
        if (!double.IsFinite(meshDeflection) || meshDeflection <= 0)
            throw new ArgumentOutOfRangeException(nameof(meshDeflection));
        var rings = RegionOps.UnionPolygons(triangles.Where(t => t.Count == 3 &&
            Math.Abs(RegionOps.SignedArea(t)) > 1e-10), precision: 6);
        return rings.Select(r => new ProjectedModelBoundaryRing(r,
            rings.Count(other => !ReferenceEquals(other, r) && Contains(other, r[0])), meshDeflection)).ToArray();
    }

    private static bool Contains(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
