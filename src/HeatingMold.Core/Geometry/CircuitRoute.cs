namespace HeatingMold.Core.Geometry;

/// <summary>회로 경로의 시작/종료 인출점 역할.</summary>
public enum CircuitExitRole { Start, End }

/// <summary>회로 정본에 붙는 인출점. CAD 경계는 원본 에지 Binding을 보관하고,
/// 폴리라인 구간 정보는 비-CAD 호환 경로와 표시 용도로만 사용한다.</summary>
public sealed record CircuitExitPoint(
    CircuitExitRole Role,
    Vec2 Point,
    int BoundarySegmentIndex,
    double BoundaryParameter)
{
    public ExitEdgeBinding? Binding { get; init; }
    public ArtificialEdgeBinding? ArtificialBinding { get; init; }
}

/// <summary>
/// 한 회로의 완결 경로 정본. 첫 조각의 첫 점과 마지막 조각의 마지막 점은 각각 시작/종료
/// 인출점이다. 마지막 발열선은 축과 평행하게 유지하고, 그 끝에서 종료 인출점까지의 리드는
/// 별도 연결선 조각으로 둘 수 있다.
/// </summary>
public sealed record CircuitRoute(
    IReadOnlyList<SerpentineRoutePiece> Pieces,
    CircuitExitPoint StartExit,
    CircuitExitPoint EndExit,
    double AngleDeg,
    int LaneCount,
    int NominalLaneCount)
{
    public string? CandidateId { get; init; }
    public IReadOnlyList<Vec2> Path => CircuitRouteGeometry.PathFromPieces(Pieces);
    public IReadOnlyList<SerpentineSegment> Segments => CircuitRouteGeometry.SegmentsFromPieces(Pieces);
    public double TotalLength => CircuitRouteGeometry.TotalLength(Pieces);

    public static CircuitRoute FromResult(SerpentineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.StartExit is null || result.EndExit is null)
            throw new InvalidOperationException("The pattern has no exit points.");
        return new CircuitRoute(
            result.RoutePieces,
            result.StartExit,
            result.EndExit,
            result.AngleDeg,
            result.LaneCount,
            result.NominalLaneCount) { CandidateId = result.CandidateId };
    }

    public SerpentineResult ToResult() => new()
    {
        Path = Path,
        Segments = Segments,
        LaneCount = LaneCount,
        NominalLaneCount = NominalLaneCount,
        AngleDeg = AngleDeg,
        TotalLength = TotalLength,
        RoutePieces = Pieces,
        StartExit = StartExit,
        EndExit = EndExit,
        CandidateId = CandidateId,
    };
}

public static class CircuitRouteGeometry
{
    public static List<Vec2> PathFromPieces(IReadOnlyList<SerpentineRoutePiece> pieces)
    {
        var path = new List<Vec2>();
        foreach (SerpentineRoutePiece piece in pieces)
        {
            int start = path.Count > 0 && piece.Points.Count > 0
                && path[^1].DistanceTo(piece.Points[0]) <= 1e-9 ? 1 : 0;
            for (int i = start; i < piece.Points.Count; i++) path.Add(piece.Points[i]);
        }
        return path;
    }

    public static List<SerpentineSegment> SegmentsFromPieces(IReadOnlyList<SerpentineRoutePiece> pieces)
    {
        var segments = new List<SerpentineSegment>();
        foreach (SerpentineRoutePiece piece in pieces)
        {
            if (piece.Kind != SerpentineRoutePieceKind.HeatingRun || piece.Points.Count < 2) continue;
            segments.Add(new SerpentineSegment(piece.LaneIndex, piece.Points[0], piece.Points[^1]));
        }
        return segments;
    }

    public static double TotalLength(IReadOnlyList<SerpentineRoutePiece> pieces)
    {
        double total = 0;
        foreach (SerpentineRoutePiece piece in pieces) total += piece.Length;
        return total;
    }
}

/// <summary>경계 교차점을 실제 회로 조각의 양 끝에 적용하는 공통 절차.</summary>
public static class CircuitRouteBuilder
{
    public static bool TryTrimToExit(
        IReadOnlyList<SerpentineRoutePiece> pieces,
        ExitBoundaryPoint start,
        ExitBoundaryPoint end,
        double angleDeg,
        int laneCount,
        int nominalLaneCount,
        out CircuitRoute? route,
        out string failure)
    {
        route = null;
        failure = string.Empty;
        if (pieces == null || pieces.Count == 0)
        {
            failure = "The route has no pieces.";
            return false;
        }
        if (!IsFinite(start.Point) || !IsFinite(end.Point))
        {
            failure = "The route endpoint is not finite.";
            return false;
        }
        if (start.Point.DistanceTo(end.Point) <= ExitBoundaryConstraint.Tolerance)
        {
            failure = "The route start and end points collapse to one point.";
            return false;
        }

        var pointLists = new List<List<Vec2>>(pieces.Count);
        var trimmed = new List<SerpentineRoutePiece>(pieces.Count);
        foreach (SerpentineRoutePiece piece in pieces)
        {
            if (piece.Points.Count < 2)
            {
                failure = "A route piece has fewer than two points.";
                return false;
            }
            var points = piece.Points.ToList();
            pointLists.Add(points);
            trimmed.Add(piece with { Points = points });
        }

        pointLists[0][0] = start.Point;
        pointLists[^1][^1] = end.Point;

        for (int i = 1; i < trimmed.Count; i++)
        {
            if (pointLists[i - 1][^1].DistanceTo(pointLists[i][0]) > 1e-6)
            {
                failure = "The route pieces are not continuous after trimming.";
                return false;
            }
        }

        if (pointLists[0][0].DistanceTo(pointLists[0][1]) <= ExitBoundaryConstraint.Tolerance)
        {
            failure = "The start exit segment is degenerate.";
            return false;
        }
        List<Vec2> last = pointLists[^1];
        if (last[^2].DistanceTo(last[^1]) <= ExitBoundaryConstraint.Tolerance)
        {
            failure = "The end exit segment is degenerate.";
            return false;
        }

        route = new CircuitRoute(
            trimmed,
            new CircuitExitPoint(CircuitExitRole.Start, start.Point,
                start.SegmentIndex, start.SegmentParameter)
            {
                Binding = start.Binding,
                ArtificialBinding = start.ArtificialBinding,
            },
            new CircuitExitPoint(CircuitExitRole.End, end.Point,
                end.SegmentIndex, end.SegmentParameter)
            {
                Binding = end.Binding,
                ArtificialBinding = end.ArtificialBinding,
            },
            angleDeg,
            laneCount,
            nominalLaneCount);
        return true;
    }

    private static bool IsFinite(Vec2 v) => double.IsFinite(v.X) && double.IsFinite(v.Y);
}
