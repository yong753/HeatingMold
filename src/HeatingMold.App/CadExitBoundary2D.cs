using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>Original CAD constraints in the confirmed working frame; display chains are never queried.</summary>
internal sealed class CadExitBoundary2D : IExitBoundary2D
{
    private const double Epsilon = ExitBoundaryConstraint.Tolerance;
    private readonly long _contextId;
    private readonly long _revision;
    private readonly ExitEdgeReference[] _edges;

    public CadExitBoundary2D(long contextId, long revision, IEnumerable<ExitEdgeReference> edges)
    {
        _contextId = contextId;
        _revision = revision;
        _edges = edges.Select(CopyReference).ToArray();
    }

    internal long ContextId => _contextId;
    internal long Revision => _revision;

    internal static ExitEdgeReference CopyReference(ExitEdgeReference edge) => new()
    {
        Revision = edge.Revision, EdgeKey = edge.EdgeKey, FaceId = edge.FaceId,
        FirstParameter = edge.FirstParameter, LastParameter = edge.LastParameter,
        Reversed = edge.Reversed, StartIndex = edge.StartIndex, EndIndex = edge.EndIndex
    };

    public ExitBoundaryResolution Project(Vec2 point, double tolerance)
    {
        if (_edges.Length == 0) return Fail(ExitBoundaryFailureReason.MissingReference);
        ExitEdgeHit best = null;
        ExitEdgeReference selected = null;
        double distance = double.PositiveInfinity;
        bool ambiguous = false;
        foreach (ExitEdgeReference edge in _edges)
        {
            ExitEdgeHit hit = Bridge.ProjectExitEdge(_contextId, _revision, edge, point.X, point.Y, tolerance);
            if (hit == null) return Fail(ExitBoundaryFailureReason.GeometryFailure);
            if (hit.Status == ExitEdgeQueryStatus.Miss) continue;
            if (hit.Status != ExitEdgeQueryStatus.Hit) return Fail(Reason(hit.Status));
            double candidateDistance = Datum(hit).DistanceTo(point);
            if (candidateDistance < distance - Epsilon)
            {
                best = hit; selected = edge; distance = candidateDistance; ambiguous = false;
            }
            else if (Math.Abs(candidateDistance - distance) <= Epsilon &&
                     !SameLocation(best, selected, hit, edge)) ambiguous = true;
        }
        if (best == null) return Fail(ExitBoundaryFailureReason.NoIntersection);
        return ambiguous ? Fail(ExitBoundaryFailureReason.MultipleIntersections) : Convert(best, selected);
    }

    public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance)
    {
        if (_edges.Length == 0) return Fail(ExitBoundaryFailureReason.MissingReference);
        Vec2 direction = endpoint - next;
        if (!double.IsFinite(direction.Length) || direction.Length <= tolerance)
            return Fail(ExitBoundaryFailureReason.DegenerateSegment);
        ExitEdgeHit selectedHit = null;
        ExitEdgeReference selectedEdge = null;
        foreach (ExitEdgeReference edge in _edges)
        {
            ExitEdgeRayResult result = Bridge.IntersectExitEdgeRay(_contextId, _revision, edge,
                endpoint.X, endpoint.Y, direction.X, direction.Y, tolerance);
            if (result == null) return Fail(ExitBoundaryFailureReason.GeometryFailure);
            if (result.Status == ExitEdgeQueryStatus.Miss) continue;
            if (result.Status != ExitEdgeQueryStatus.Hit) return Fail(Reason(result.Status));
            foreach (ExitEdgeHit hit in result.Hits ?? Array.Empty<ExitEdgeHit>())
            {
                if (hit.Status != ExitEdgeQueryStatus.Hit) return Fail(Reason(hit.Status));
                if (selectedHit == null) { selectedHit = hit; selectedEdge = edge; }
                else if (!SameLocation(selectedHit, selectedEdge, hit, edge))
                    return Fail(ExitBoundaryFailureReason.MultipleIntersections);
            }
        }
        return selectedHit == null ? Fail(ExitBoundaryFailureReason.NoIntersection) : Convert(selectedHit, selectedEdge);
    }

    public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance)
    {
        if (!TryReference(binding, out ExitEdgeReference edge, out ExitBoundaryFailureReason reason)) return Fail(reason);
        ExitEdgeHit hit = Bridge.ProjectExitEdge(_contextId, _revision, edge, point.X, point.Y, tolerance);
        return hit?.Status == ExitEdgeQueryStatus.Hit ? Convert(hit, edge) : Fail(Reason(hit?.Status));
    }

    public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding)
    {
        ExitEdgeHit hit = EvaluateNative(binding);
        return hit?.Status == ExitEdgeQueryStatus.Hit
            ? Convert(hit, Reference(binding)) : Fail(Reason(hit?.Status));
    }

    internal ExitEdgeHit EvaluateNative(ExitEdgeBinding binding)
    {
        if (!TryReference(binding, out ExitEdgeReference edge, out ExitBoundaryFailureReason reason))
            return new ExitEdgeHit { Status = reason == ExitBoundaryFailureReason.StaleContext
                ? ExitEdgeQueryStatus.StaleContext : ExitEdgeQueryStatus.MissingReference };
        return Bridge.EvaluateExitEdgeParameter(_contextId, _revision, edge, binding.Parameter);
    }

    private bool TryReference(ExitEdgeBinding binding, out ExitEdgeReference edge,
        out ExitBoundaryFailureReason reason)
    {
        edge = null;
        reason = ExitBoundaryFailureReason.StaleContext;
        if (binding.WorkingContextId != _contextId || binding.WorkingContextRevision != _revision ||
            binding.ModelRevision != _revision) return false;
        reason = ExitBoundaryFailureReason.MissingReference;
        edge = _edges.FirstOrDefault(e => e.EdgeKey == binding.EdgeKey && e.Revision == binding.ModelRevision &&
            e.FirstParameter == binding.FirstParameter && e.LastParameter == binding.LastParameter &&
            e.Reversed == binding.Reversed);
        return edge != null;
    }

    private ExitEdgeReference Reference(ExitEdgeBinding binding) => _edges.First(e =>
        e.EdgeKey == binding.EdgeKey && e.Revision == binding.ModelRevision &&
        e.FirstParameter == binding.FirstParameter && e.LastParameter == binding.LastParameter &&
        e.Reversed == binding.Reversed);

    private ExitBoundaryResolution Convert(ExitEdgeHit hit, ExitEdgeReference edge)
    {
        if (hit.DatumPoint is not { Length: >= 2 } || hit.Point is not { Length: >= 3 } ||
            !double.IsFinite(hit.Parameter)) return Fail(ExitBoundaryFailureReason.GeometryFailure);
        return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(Datum(hit), -1, hit.Parameter)
        {
            Binding = new ExitEdgeBinding(edge.Revision, _contextId, _revision, edge.EdgeKey,
                edge.FirstParameter, edge.LastParameter, hit.Parameter, edge.Reversed)
        });
    }

    private static Vec2 Datum(ExitEdgeHit hit) => new(hit.DatumPoint[0], hit.DatumPoint[1]);

    private static bool SameLocation(ExitEdgeHit a, ExitEdgeReference aEdge, ExitEdgeHit b, ExitEdgeReference bEdge)
    {
        if (a == null || aEdge == null || a.Point is not { Length: >= 3 } || b.Point is not { Length: >= 3 }) return false;
        bool sameSource = aEdge.EdgeKey == bEdge.EdgeKey && Math.Abs(a.Parameter - b.Parameter) <= 1e-9;
        bool sameVertex = a.VertexKey > 0 && a.VertexKey == b.VertexKey;
        if (!sameSource && !sameVertex) return false;
        return Datum(a).DistanceTo(Datum(b)) <= Epsilon &&
            Math.Abs(a.Point[0] - b.Point[0]) <= Epsilon &&
            Math.Abs(a.Point[1] - b.Point[1]) <= Epsilon && Math.Abs(a.Point[2] - b.Point[2]) <= Epsilon;
    }

    private static ExitBoundaryResolution Fail(ExitBoundaryFailureReason reason) => ExitBoundaryResolution.Fail(reason);

    private static ExitBoundaryFailureReason Reason(ExitEdgeQueryStatus? status) => status switch
    {
        ExitEdgeQueryStatus.Miss => ExitBoundaryFailureReason.NoIntersection,
        ExitEdgeQueryStatus.Ambiguous => ExitBoundaryFailureReason.MultipleIntersections,
        ExitEdgeQueryStatus.StaleContext => ExitBoundaryFailureReason.StaleContext,
        ExitEdgeQueryStatus.MissingReference => ExitBoundaryFailureReason.MissingReference,
        ExitEdgeQueryStatus.TangentOrOverlap => ExitBoundaryFailureReason.TangentOrOverlap,
        ExitEdgeQueryStatus.DegenerateProjection => ExitBoundaryFailureReason.DegenerateSegment,
        ExitEdgeQueryStatus.InvalidInput => ExitBoundaryFailureReason.InvalidInput,
        _ => ExitBoundaryFailureReason.GeometryFailure
    };
}
