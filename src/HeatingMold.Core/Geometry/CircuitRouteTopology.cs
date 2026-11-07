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
    public static CircuitRouteSetResult Split(CircuitRoute route,
        IReadOnlyList<bool> turnEnabled, IReadOnlyList<Vec2> exitChain)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(turnEnabled);
        ArgumentNullException.ThrowIfNull(exitChain);
        if (route.Pieces.Count == 0) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.EmptyRoute, "The circuit route has no pieces.");
        if (exitChain.Count < 2) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.InvalidInput, "The exit boundary needs at least two points.");
        int turnCount = route.Pieces.Count(piece => piece.Kind == SerpentineRoutePieceKind.TurnConnector);
        if (turnEnabled.Count != turnCount) return CircuitRouteSetResult.Fail(
            CircuitRouteTopologyFailureReason.InvalidInput,
            "One enabled state is required per turn connector.");
        if (turnEnabled.All(enabled => enabled))
            return CircuitRouteSetResult.Ok(new[] { route });

        var routes = new List<CircuitRoute>();
        string detail = string.Empty;
        bool failed = false;
        var segment = new Segment(route.Pieces, route.StartExit, route.EndExit,
            turnEnabled.ToArray(), route.AngleDeg, exitChain.ToArray());
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
        if (!TryDirection(leftLast.Points, fromEnd: true, out Vec2 leftEnd, out Vec2 leftPrevious) ||
            !TryDirection(rightFirst.Points, fromEnd: false, out Vec2 rightStart, out Vec2 rightNext))
        {
            failure = "A run adjacent to the split turn is degenerate.";
            return false;
        }

        var leftBoundary = ExitBoundaryConstraint.TryResolve(leftEnd, leftPrevious, segment.ExitChain);
        if (!leftBoundary.Succeeded)
        {
            failure = "The left cut end cannot reach the exit boundary: " + leftBoundary.Failure;
            return false;
        }
        var rightBoundary = ExitBoundaryConstraint.TryResolve(rightStart, rightNext, segment.ExitChain);
        if (!rightBoundary.Succeeded)
        {
            failure = "The right cut end cannot reach the exit boundary: " + rightBoundary.Failure;
            return false;
        }

        ExitBoundaryPoint leftPoint = leftBoundary.Point!.Value;
        ExitBoundaryPoint rightPoint = rightBoundary.Point!.Value;
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
            segment.Turns.Take(splitTurn).ToArray(), segment.AngleDeg, segment.ExitChain);
        right = new Segment(rightRoute!.Pieces, rightRoute.StartExit, rightRoute.EndExit,
            segment.Turns.Skip(splitTurn + 1).ToArray(), segment.AngleDeg, segment.ExitChain);
        return true;
    }

    private static bool TryBuild(Segment segment, out CircuitRoute? route, out string failure)
    {
        if (!CircuitRouteBuilder.TryTrimToExit(segment.Pieces, ToBoundary(segment.StartExit),
                ToBoundary(segment.EndExit), segment.AngleDeg, LaneCount(segment.Pieces),
                LaneCount(segment.Pieces), out route, out failure))
            return false;
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
        new(role, boundary.Point, boundary.SegmentIndex, boundary.SegmentParameter);

    private static ExitBoundaryPoint ToBoundary(CircuitExitPoint exit) =>
        new(exit.Point, exit.BoundarySegmentIndex, exit.BoundaryParameter);

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
        IReadOnlyList<Vec2> ExitChain);
}
