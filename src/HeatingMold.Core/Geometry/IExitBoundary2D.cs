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
/// Immutable provenance for a point on a cut line created by splitting one working area
/// into independent areas. A cut line is not a CAD edge and not an exclusion rectangle
/// side: the working context plus this line's own endpoints and parameter are the whole
/// identity. Parameter runs from (AX,AY)=0 to (BX,BY)=1.
/// </summary>
public readonly record struct SplitLineBinding(
    long WorkingContextId,
    long WorkingContextRevision,
    double AX,
    double AY,
    double BX,
    double BY,
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
/// Plane-only queries for split-created straight boundaries. Implementations must reject
/// stale contexts, unknown lines and out-of-interval parameters.
/// </summary>
public interface IArtificialSplitBoundary2D
{
    /// <summary>Move only on the saved cut line and its saved valid interval.</summary>
    ExitBoundaryResolution ProjectSplitLine(
        SplitLineBinding binding, Vec2 point, double tolerance);

    /// <summary>Evaluate a previously resolved cut-line parameter without a new search.</summary>
    ExitBoundaryResolution EvaluateSplitLine(SplitLineBinding binding);
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
