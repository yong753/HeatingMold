namespace HeatingMold.Core.Geometry;

public enum CircuitRouteTopologyFailureReason
{
    InvalidInput,
    EmptyRoute,
    ExitBoundaryConflict
}

public sealed record CircuitRouteSetResult(
    IReadOnlyList<CircuitRoute> Routes,
    CircuitRouteTopologyFailureReason? Failure,
    string? Detail)
{
    public bool Succeeded => Failure == null;
    public static CircuitRouteSetResult Ok(IReadOnlyList<CircuitRoute> routes) => new(routes, null, null);
    public static CircuitRouteSetResult Fail(CircuitRouteTopologyFailureReason reason, string detail) =>
        new(Array.Empty<CircuitRoute>(), reason, detail);
}

/// <summary>
/// Validates a circuit's saved endpoints against the original exit boundary. One generated
/// area route is one circuit; no turn-based split/merge derivation is performed here.
/// </summary>
public static class CircuitRouteTopology
{
    /// <summary>Validate and retain a circuit's saved endpoints. CAD parameters are evaluated
    /// directly; an existing endpoint is never rebound by a neighbouring-edge search.</summary>
    public static CircuitRouteSetResult Constrain(CircuitRoute route,
        IReadOnlyList<Vec2> exitChain, IExitBoundary2D? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(exitChain);
        if (route.Pieces.Count == 0) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.EmptyRoute, "The circuit route has no pieces.");
        if (boundary == null && exitChain.Count < 2) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.InvalidInput, "The exit boundary needs at least two points.");
        if (!TryValidateExit(route.StartExit, exitChain, boundary, out string startFailure))
            return CircuitRouteSetResult.Fail(CircuitRouteTopologyFailureReason.ExitBoundaryConflict,
                "The saved start exit is invalid: " + startFailure);
        if (!TryValidateExit(route.EndExit, exitChain, boundary, out string endFailure))
            return CircuitRouteSetResult.Fail(CircuitRouteTopologyFailureReason.ExitBoundaryConflict,
                "The saved end exit is invalid: " + endFailure);
        return CircuitRouteSetResult.Ok(new[] { route });
    }

    private static bool TryValidateExit(CircuitExitPoint exit, IReadOnlyList<Vec2> exitChain,
        IExitBoundary2D? boundary, out string failure)
    {
        failure = string.Empty;
        if (boundary != null)
        {
            ExitBoundaryResolution evaluated = ExitBoundaryConstraint.EvaluateSaved(boundary, ToBoundary(exit));
            if (evaluated.Succeeded) return true;
            failure = evaluated.Failure.ToString()!;
            return false;
        }
        if (exit.Binding.HasValue || exit.ArtificialBinding.HasValue)
        {
            failure = ExitBoundaryFailureReason.MissingReference.ToString();
            return false;
        }
        if (!double.IsFinite(exit.Point.X) || !double.IsFinite(exit.Point.Y)
            || Serpentine.DistanceToChain(exitChain, exit.Point) > ExitBoundaryConstraint.Tolerance)
        {
            failure = ExitBoundaryFailureReason.NoIntersection.ToString();
            return false;
        }
        return true;
    }

    private static ExitBoundaryPoint ToBoundary(CircuitExitPoint exit) =>
        new(exit.Point, exit.BoundarySegmentIndex, exit.BoundaryParameter)
        {
            Binding = exit.Binding,
            ArtificialBinding = exit.ArtificialBinding,
        };
}
