namespace HeatingMold.Core.Geometry;

/// <summary>
/// Immutable provenance for a point on an original CAD edge. Parameter bounds and
/// Parameter are native curve parameters, never tessellation indices or ratios.
/// </summary>
public readonly record struct ExitEdgeBinding(
    long ModelRevision,
    long WorkingContextId,
    long WorkingContextRevision,
    long EdgeKey,
    double FirstParameter,
    double LastParameter,
    double Parameter,
    bool Reversed);

/// <summary>
/// Plane-only boundary queries. Implementations may use CAD geometry without
/// exposing CAD types to Core. Sampled guide chains are not authoritative tests.
/// Every successful CAD result must carry Binding. Tolerance is geometric, not a
/// cursor snapping distance. Saved bindings must fail explicitly when stale/missing.
/// </summary>
public interface IExitBoundary2D
{
    /// <summary>Nearest point over the selected boundary; ambiguity is a failure.</summary>
    ExitBoundaryResolution Project(Vec2 point, double tolerance);

    /// <summary>Unique intersection outside endpoint in direction endpoint-next.</summary>
    ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance);

    /// <summary>Move only on the saved original edge and its saved valid interval.</summary>
    ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance);

    /// <summary>Evaluate a previously resolved parameter without a new search.</summary>
    ExitBoundaryResolution Evaluate(ExitEdgeBinding binding);
}
