namespace HeatingMold.Core.Geometry;

public enum CircuitRouteTopologyFailureReason
{
    InvalidInput,
    EmptyRoute,
    TurnNotFound,
    AdjacentPieceNotRun,
    DegenerateSegment,
    ExitBoundaryConflict,
    InvalidRoute
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
/// Derives the circuits that remain when turn connectors are disabled. Each new cut end is
/// extended along the adjacent run's straight direction to the exit boundary; no detour is
/// introduced. The original route is never mutated.
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

    public static CircuitRouteSetResult Split(CircuitRoute route,
        IReadOnlyList<bool> turnEnabled, IReadOnlyList<Vec2> exitChain, IExitBoundary2D? boundary = null,
        IReadOnlyDictionary<(int PieceIndex, CircuitExitRole Role), CircuitExitPoint>? existingExits = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(turnEnabled);
        ArgumentNullException.ThrowIfNull(exitChain);
        if (route.Pieces.Count == 0) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.EmptyRoute, "The circuit route has no pieces.");
        if (boundary == null && exitChain.Count < 2) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.InvalidInput, "The exit boundary needs at least two points.");
        int turnCount = route.Pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector);
        if (turnEnabled.Count != turnCount) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.InvalidInput,
            "One enabled state is required per turn connector.");
        // Retained CAD endpoints must still refer to the same model/context/parameter,
        // even when all connectors remain enabled. A failed query cannot drop provenance.
        if (boundary != null || route.StartExit.Binding.HasValue
            || route.StartExit.ArtificialBinding.HasValue
            || route.EndExit.Binding.HasValue || route.EndExit.ArtificialBinding.HasValue)
        {
            CircuitRouteSetResult constrained = Constrain(route, exitChain, boundary);
            if (!constrained.Succeeded) return constrained;
        }
        if (turnEnabled.All(enabled => enabled))
            return CircuitRouteSetResult.Ok(new[] { route });

        var routes = new List<CircuitRoute>();
        string detail = string.Empty;
        bool failed = false;
        var segment = new Segment(route.Pieces, route.StartExit, route.EndExit,
            turnEnabled.ToArray(), route.AngleDeg, exitChain.ToArray(), boundary, route.CandidateId,
            Enumerable.Range(0, route.Pieces.Count).ToArray(), existingExits?.ToDictionary(e => e.Key, e => e.Value));
        Visit(segment);

        return failed
            ? CircuitRouteSetResult.Fail(CircuitRouteTopologyFailureReason.InvalidRoute, detail)
            : CircuitRouteSetResult.Ok(routes);

        void Visit(Segment current)
        {
            if (failed) return;
            int localTurn = -1;
            for (int i = 0; i < current.Turns.Count; i++)
                if (!current.Turns[i]) { localTurn = i; break; }

            if (localTurn < 0)
            {
                if (!TryBuild(current, out CircuitRoute? built, out string reason))
                {
                    failed = true;
                    detail = reason;
                    return;
                }
                routes.Add(built!);
                return;
            }

            int pieceIndex = FindTurnPiece(current.Pieces, localTurn);
            if (pieceIndex < 0)
            {
                failed = true;
                detail = "The turn connector was not found in the circuit route.";
                return;
            }
            if (!TrySplit(current, pieceIndex, out Segment? left, out Segment? right, out string failure))
            {
                failed = true;
                detail = failure;
                return;
            }
            Visit(left!);
            Visit(right!);
        }
    }

    private static int FindTurnPiece(IReadOnlyList<SerpentineRoutePiece> pieces, int turnOrdinal)
    {
        int turn = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Kind != SerpentineRoutePieceKind.TurnConnector) continue;
            if (turn == turnOrdinal) return i;
            turn++;
        }
        return -1;
    }

    private static bool TrySplit(Segment segment, int turnPieceIndex,
        out Segment? left, out Segment? right, out string failure)
    {
        left = null;
        right = null;
        failure = string.Empty;
        if (turnPieceIndex <= 0 || turnPieceIndex >= segment.Pieces.Count - 1)
        {
            failure = "A split turn must have a heating run on both sides.";
            return false;
        }

        var leftPieces = segment.Pieces.Take(turnPieceIndex).ToList();
        var rightPieces = segment.Pieces.Skip(turnPieceIndex + 1).ToList();
        if (leftPieces.Count == 0 || rightPieces.Count == 0)
        {
            failure = "A split turn produced an empty circuit.";
            return false;
        }

        var leftLast = leftPieces[^1];
        var rightFirst = rightPieces[0];
        if (leftLast.Kind != SerpentineRoutePieceKind.HeatingRun ||
            rightFirst.Kind != SerpentineRoutePieceKind.HeatingRun)
        {
            failure = "A split turn must be adjacent to heating runs.";
            return false;
        }
        if (!TryCutExit(segment, leftLast.Points, fromEnd: true,
                segment.PieceIndices[turnPieceIndex - 1], CircuitExitRole.End,
                out ExitBoundaryPoint leftPoint, out string leftBoundaryFailure))
        {
            failure = "The left cut end cannot reach the exit boundary: " + leftBoundaryFailure;
            return false;
        }
        if (!TryCutExit(segment, rightFirst.Points, fromEnd: false,
                segment.PieceIndices[turnPieceIndex + 1], CircuitExitRole.Start,
                out ExitBoundaryPoint rightPoint, out string rightBoundaryFailure))
        {
            failure = "The right cut end cannot reach the exit boundary: " + rightBoundaryFailure;
            return false;
        }

        var leftExitEnd = ToExit(CircuitExitRole.End, leftPoint);
        var rightExitStart = ToExit(CircuitExitRole.Start, rightPoint);
        var leftTrimmed = ReplaceEndpoint(leftPieces, atStart: false, leftPoint.Point);
        var rightTrimmed = ReplaceEndpoint(rightPieces, atStart: true, rightPoint.Point);

        if (!CircuitRouteBuilder.TryTrimToExit(leftTrimmed, ToBoundary(segment.StartExit),
                ToBoundary(leftExitEnd), segment.AngleDeg, LaneCount(leftTrimmed),
                LaneCount(leftTrimmed), out CircuitRoute? leftRoute, out string leftFailure))
        {
            failure = "The left split circuit is invalid: " + leftFailure;
            return false;
        }
        if (!CircuitRouteBuilder.TryTrimToExit(rightTrimmed, ToBoundary(rightExitStart),
                ToBoundary(segment.EndExit), segment.AngleDeg, LaneCount(rightTrimmed),
                LaneCount(rightTrimmed), out CircuitRoute? rightRoute, out string rightFailure))
        {
            failure = "The right split circuit is invalid: " + rightFailure;
            return false;
        }

        int splitTurn = CountTurnsBefore(segment.Pieces, turnPieceIndex);
        left = new Segment(leftRoute!.Pieces, leftRoute.StartExit, leftRoute.EndExit,
            segment.Turns.Take(splitTurn).ToArray(), segment.AngleDeg, segment.ExitChain,
            segment.Boundary, segment.CandidateId, segment.PieceIndices.Take(turnPieceIndex).ToArray(),
            segment.ExistingExits);
        right = new Segment(rightRoute!.Pieces, rightRoute.StartExit, rightRoute.EndExit,
            segment.Turns.Skip(splitTurn + 1).ToArray(), segment.AngleDeg, segment.ExitChain,
            segment.Boundary, segment.CandidateId, segment.PieceIndices.Skip(turnPieceIndex + 1).ToArray(),
            segment.ExistingExits);
        return true;
    }

    private static bool TryCutExit(Segment segment, IReadOnlyList<Vec2> points, bool fromEnd,
        int originalPieceIndex, CircuitExitRole role, out ExitBoundaryPoint point, out string failure)
    {
        point = default;
        failure = string.Empty;
        if (segment.ExistingExits != null && segment.ExistingExits.TryGetValue((originalPieceIndex, role), out var saved))
        {
            // Piece identity and endpoint role, never projected-coordinate equality, select
            // an existing cut endpoint. A next-bend edit must not recompute its parameter.
            if (saved.Role != role)
            {
                failure = "The saved cut endpoint has the wrong role.";
                return false;
            }
            if (!TryValidateExit(saved, segment.ExitChain, segment.Boundary, out failure)) return false;
            point = ToBoundary(saved);
            return true;
        }
        if (!TryDirection(points, fromEnd, out Vec2 datum, out Vec2 next))
        {
            failure = ExitBoundaryFailureReason.DegenerateSegment.ToString();
            return false;
        }
        ExitBoundaryResolution resolved = ExitBoundaryConstraint.TryResolve(datum, next, segment.ExitChain,
            boundary: segment.Boundary);
        if (!resolved.Succeeded)
        {
            failure = resolved.Failure.ToString()!;
            return false;
        }
        point = resolved.Point!.Value;
        return true;
    }

    private static bool TryBuild(Segment segment, out CircuitRoute? route, out string failure)
    {
        if (!CircuitRouteBuilder.TryTrimToExit(segment.Pieces, ToBoundary(segment.StartExit),
                ToBoundary(segment.EndExit), segment.AngleDeg, LaneCount(segment.Pieces),
                LaneCount(segment.Pieces), out route, out failure))
            return false;
        route = route! with { CandidateId = segment.CandidateId };
        return true;
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

    private static bool TryDirection(IReadOnlyList<Vec2> points, bool fromEnd, out Vec2 datum, out Vec2 next)
    {
        datum = default;
        next = default;
        if (points.Count < 2) return false;
        if (fromEnd)
        {
            datum = points[^1];
            for (int i = points.Count - 2; i >= 0; i--)
            {
                if (points[i].DistanceTo(datum) <= ExitBoundaryConstraint.Tolerance) continue;
                next = points[i];
                return true;
            }
        }
        else
        {
            datum = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                if (points[i].DistanceTo(datum) <= ExitBoundaryConstraint.Tolerance) continue;
                next = points[i];
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<SerpentineRoutePiece> ReplaceEndpoint(
        IReadOnlyList<SerpentineRoutePiece> pieces, bool atStart, Vec2 value)
    {
        var copy = new List<SerpentineRoutePiece>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (atStart && i == 0)
            {
                var points = piece.Points.ToArray();
                points[0] = value;
                copy.Add(piece with { Points = points });
            }
            else if (!atStart && i == pieces.Count - 1)
            {
                var points = piece.Points.ToArray();
                points[^1] = value;
                copy.Add(piece with { Points = points });
            }
            else copy.Add(piece);
        }
        return copy;
    }

    private static CircuitExitPoint ToExit(CircuitExitRole role, ExitBoundaryPoint boundary) =>
        new(role, boundary.Point, boundary.SegmentIndex, boundary.SegmentParameter)
        {
            Binding = boundary.Binding,
            ArtificialBinding = boundary.ArtificialBinding,
        };

    private static ExitBoundaryPoint ToBoundary(CircuitExitPoint exit) =>
        new(exit.Point, exit.BoundarySegmentIndex, exit.BoundaryParameter)
        {
            Binding = exit.Binding,
            ArtificialBinding = exit.ArtificialBinding,
        };

    private static int LaneCount(IReadOnlyList<SerpentineRoutePiece> pieces) =>
        pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun);

    private static int CountTurnsBefore(IReadOnlyList<SerpentineRoutePiece> pieces, int pieceIndex)
    {
        int count = 0;
        for (int i = 0; i < pieceIndex; i++)
            if (pieces[i].Kind == SerpentineRoutePieceKind.TurnConnector) count++;
        return count;
    }

    private sealed record Segment(
        IReadOnlyList<SerpentineRoutePiece> Pieces,
        CircuitExitPoint StartExit,
        CircuitExitPoint EndExit,
        IReadOnlyList<bool> Turns,
        double AngleDeg,
        IReadOnlyList<Vec2> ExitChain,
        IExitBoundary2D? Boundary,
        string? CandidateId,
        IReadOnlyList<int> PieceIndices,
        IReadOnlyDictionary<(int PieceIndex, CircuitExitRole Role), CircuitExitPoint>? ExistingExits);
}
