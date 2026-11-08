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
/// Immutable provenance for a point on a straight boundary created by an exclusion
/// rectangle. This is not a CAD edge: the rectangle, side and normalized parameter
/// are the complete identity. Side: 0=bottom, 1=right, 2=top, 3=left.
/// </summary>
public readonly record struct ArtificialEdgeBinding(
    long WorkingContextId,
    long WorkingContextRevision,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    int Side,
    double FirstParameter,
    double LastParameter,
    double Parameter);

/// <summary>
/// Plane-only queries for the non-CAD portion of an effective region boundary.
/// Implementations must reject stale/missing rectangles, sides or intervals.
/// </summary>
public interface IArtificialExitBoundary2D
{
    /// <summary>Move only on the saved exclusion side and its saved valid interval.</summary>
    ExitBoundaryResolution ProjectArtificial(
        ArtificialEdgeBinding binding, Vec2 point, double tolerance);

    /// <summary>Evaluate a previously resolved artificial parameter without a new search.</summary>
    ExitBoundaryResolution EvaluateArtificial(ArtificialEdgeBinding binding);
}

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
