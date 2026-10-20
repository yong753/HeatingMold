namespace HeatingMold.Core.Geometry;

public readonly record struct ModelRevision(long Value);
public readonly record struct WorkingSideId(long Value);
public enum WorkingSideState { Unconfirmed, Preview, Confirmed }

/// <summary>Immutable model-owned orthographic datum. Camera state is deliberately absent.</summary>
public sealed record WorkingSideDefinition(WorkingSideId Id, ModelRevision Revision, ProjectionFrame Frame)
{
    public Vec3 RayDirection => Frame.Normal * -1;
}

public sealed class WorkingSideSession
{
    public WorkingSideDefinition? Preview { get; private set; }
    public WorkingSideDefinition? Confirmed { get; private set; }
    public WorkingSideState State => Preview != null ? WorkingSideState.Preview :
        Confirmed != null ? WorkingSideState.Confirmed : WorkingSideState.Unconfirmed;
    public void SetPreview(WorkingSideDefinition side) => Preview = side;
    public void Confirm(ModelRevision revision)
    {
        if (Preview == null || Preview.Revision != revision)
            throw new InvalidOperationException("Working-side preview is missing or stale.");
        Confirmed = Preview;
        Preview = null;
    }
    public void Invalidate() { Preview = null; Confirmed = null; }
    public void CancelPreview() => Preview = null;
}

/// <summary>Mesh projection candidates, NOT exact CAD boundaries. Negative rings are holes.
/// References are obtained separately by nearest full-model BRep queries.</summary>
public sealed record WorkingBoundaryRing(IReadOnlyList<Vec2> Points, int NestingDepth, double MeshDeflection)
{
    public bool IsHole => NestingDepth % 2 != 0;
}

public static class WorkingBoundary
{
    public static IReadOnlyList<WorkingBoundaryRing> FromTriangles(IEnumerable<IReadOnlyList<Vec2>> triangles,
        double meshDeflection)
    {
        if (!double.IsFinite(meshDeflection) || meshDeflection <= 0)
            throw new ArgumentOutOfRangeException(nameof(meshDeflection));
        var rings = RegionOps.UnionPolygons(triangles.Where(t => t.Count == 3 &&
            Math.Abs(RegionOps.SignedArea(t)) > 1e-10), precision: 6);
        return rings.Select(r => new WorkingBoundaryRing(r,
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
