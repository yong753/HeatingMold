using HeatingMold.Core.Model;

namespace HeatingMold.Core.Geometry;

/// <summary>편집 가능한 꼭지점 하나의 위치 — 조각 인덱스와 그 조각 안의 점 인덱스.</summary>
public readonly record struct RouteVertexOccurrence(int Piece, int Point);

/// <summary>
/// 같은 2D 점을 공유하는 꼭지점 묶음. 이웃 조각의 끝점은 생성 시 값이 복제되어
/// 두 조각에 각각 존재하므로, 화면에는 하나의 핸들로 보이고 함께 움직여야 한다.
/// 서로 다른 조각이 우연히 같은 좌표를 갖는 경우(복귀 경로/교차)는 묶지 않는다.
/// </summary>
public sealed record RouteVertexGroup(IReadOnlyList<RouteVertexOccurrence> Occurrences, Vec2 Datum);

/// <summary>조각 목록에서 드래그 가능한 꼭지점 묶음을 만든다.</summary>
public static class RouteVertexGroups
{
    public static IReadOnlyList<RouteVertexGroup> Build(IReadOnlyList<SerpentineRoutePiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var groups = new List<List<RouteVertexOccurrence>>();
        var datums = new List<Vec2>();
        int previousLastGroup = -1;
        Vec2 previousLastPoint = default;
        bool hasPreviousLast = false;

        for (int pieceIndex = 0; pieceIndex < pieces.Count; pieceIndex++)
        {
            IReadOnlyList<Vec2> points = pieces[pieceIndex].Points;
            if (points == null || points.Count == 0) { hasPreviousLast = false; continue; }

            int lastGroup = -1;
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                // 조각 경계의 공유점만 병합한다. 조각 내부의 중복 좌표는 별도 핸들로 남긴다.
                bool joinsPrevious = pointIndex == 0 && hasPreviousLast && points[0] == previousLastPoint;
                int group;
                if (joinsPrevious)
                {
                    group = previousLastGroup;
                }
                else
                {
                    group = groups.Count;
                    groups.Add(new List<RouteVertexOccurrence>());
                    datums.Add(points[pointIndex]);
                }
                groups[group].Add(new RouteVertexOccurrence(pieceIndex, pointIndex));
                lastGroup = group;
            }

            previousLastGroup = lastGroup;
            previousLastPoint = points[^1];
            hasPreviousLast = true;
        }

        var result = new List<RouteVertexGroup>(groups.Count);
        for (int i = 0; i < groups.Count; i++) result.Add(new RouteVertexGroup(groups[i], datums[i]));
        return result;
    }
}

/// <summary>꼭지점 이동을 적용해 새 불변 패턴을 만든다. 조각 개수/순서/종류는 바뀌지 않는다.</summary>
public static class SerpentineVertexEdit
{
    public static SerpentineResult ApplyVertexEdits(SerpentineResult source,
        IReadOnlyList<(RouteVertexOccurrence At, Vec2 Value)> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (edits == null || edits.Count == 0) return source;
        IReadOnlyList<SerpentineRoutePiece> pieces = source.RoutePieces;
        if (pieces.Count == 0) throw new InvalidOperationException("편집할 경로 조각이 없습니다.");

        var points = new List<Vec2>[pieces.Count];
        for (int i = 0; i < pieces.Count; i++) points[i] = new List<Vec2>(pieces[i].Points);

        foreach ((RouteVertexOccurrence at, Vec2 value) in edits)
        {
            if (at.Piece < 0 || at.Piece >= points.Length)
                throw new ArgumentOutOfRangeException(nameof(edits), "조각 인덱스가 범위를 벗어났습니다.");
            if (at.Point < 0 || at.Point >= points[at.Piece].Count)
                throw new ArgumentOutOfRangeException(nameof(edits), "점 인덱스가 범위를 벗어났습니다.");
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y))
                throw new ArgumentOutOfRangeException(nameof(edits), "꼭지점 좌표가 유한하지 않습니다.");
            points[at.Piece][at.Point] = value;
        }

        // 이웃 조각이 공유하던 점은 함께 움직여야 한다. 한쪽만 바뀌면 경로가 끊긴다.
        for (int i = 1; i < points.Length; i++)
        {
            if (points[i].Count == 0 || points[i - 1].Count == 0) continue;
            if (points[i][0] != points[i - 1][^1])
                throw new ArgumentException("공유 꼭지점이 한쪽만 갱신되었습니다.", nameof(edits));
        }

        var newPieces = new List<SerpentineRoutePiece>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
            newPieces.Add(pieces[i] with { Points = points[i] });

        var path = new List<Vec2>();
        foreach (SerpentineRoutePiece piece in newPieces)
        {
            if (piece.Points.Count == 0) continue;
            if (path.Count == 0) path.AddRange(piece.Points);
            else path.AddRange(piece.Points.Skip(path[^1] == piece.Points[0] ? 1 : 0));
        }

        var segments = new List<SerpentineSegment>();
        foreach (SerpentineRoutePiece piece in newPieces)
        {
            if (piece.Kind != SerpentineRoutePieceKind.HeatingRun || piece.Points.Count < 2) continue;
            segments.Add(new SerpentineSegment(piece.LaneIndex, piece.Points[0], piece.Points[^1]));
        }

        double totalLength = 0;
        for (int i = 1; i < path.Count; i++) totalLength += path[i - 1].DistanceTo(path[i]);

        return new SerpentineResult
        {
            Path = path,
            Segments = segments,
            LaneCount = source.LaneCount,
            NominalLaneCount = source.NominalLaneCount,
            AngleDeg = source.AngleDeg,
            TotalLength = totalLength,
            RoutePieces = newPieces,
        };
    }
}

/// <summary>
/// 드래그 미리보기용 경량 적용. 변경된 조각의 점 목록만 복사하고 Path/Segments/길이는
/// 재구성하지 않는다. 확정 시에는 <see cref="SerpentineVertexEdit.ApplyVertexEdits"/>를 쓴다.
/// </summary>
public static class SerpentineVertexPreview
{
    public static bool TryApplyAffected(SerpentineResult source,
        IReadOnlyList<(RouteVertexOccurrence At, Vec2 Value)> edits,
        out Dictionary<int, IReadOnlyList<Vec2>> affectedPoints,
        out List<(Vec2 A, Vec2 B)> changedSegments,
        out string failure)
    {
        affectedPoints = new Dictionary<int, IReadOnlyList<Vec2>>();
        changedSegments = new List<(Vec2 A, Vec2 B)>();
        failure = string.Empty;
        ArgumentNullException.ThrowIfNull(source);
        if (edits == null || edits.Count == 0)
        {
            failure = "이동할 꼭지점이 없습니다.";
            return false;
        }
        IReadOnlyList<SerpentineRoutePiece> pieces = source.RoutePieces;
        if (pieces.Count == 0)
        {
            failure = "편집할 경로 조각이 없습니다.";
            return false;
        }

        var copies = new Dictionary<int, List<Vec2>>();
        foreach ((RouteVertexOccurrence at, Vec2 value) in edits)
        {
            if (at.Piece < 0 || at.Piece >= pieces.Count)
            {
                failure = "조각 인덱스가 범위를 벗어났습니다.";
                return false;
            }
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y))
            {
                failure = "꼭지점 좌표가 유한하지 않습니다.";
                return false;
            }
            copies.TryGetValue(at.Piece, out List<Vec2>? points);
            if (points == null)
            {
                points = new List<Vec2>(pieces[at.Piece].Points);
                copies[at.Piece] = points;
            }
            if (at.Point < 0 || at.Point >= points.Count)
            {
                failure = "점 인덱스가 범위를 벗어났습니다.";
                return false;
            }
            points[at.Point] = value;
        }

        // 영향을 받은 조각의 인접 경계만 검사한다(공유점이 한쪽만 움직이면 경로가 끊긴다).
        foreach (int pieceIndex in copies.Keys)
        {
            if (pieceIndex > 0 && copies[pieceIndex].Count > 0)
            {
                IReadOnlyList<Vec2> previous = copies.TryGetValue(pieceIndex - 1, out List<Vec2>? before) && before != null
                    ? before
                    : pieces[pieceIndex - 1].Points;
                if (previous.Count > 0 && copies[pieceIndex][0] != previous[^1])
                {
                    failure = "공유 꼭지점이 한쪽만 갱신되었습니다.";
                    return false;
                }
            }
            if (pieceIndex + 1 < pieces.Count && copies[pieceIndex].Count > 0)
            {
                IReadOnlyList<Vec2> next = copies.TryGetValue(pieceIndex + 1, out List<Vec2>? after) && after != null
                    ? after
                    : pieces[pieceIndex + 1].Points;
                if (next.Count > 0 && next[0] != copies[pieceIndex][^1])
                {
                    failure = "공유 꼭지점이 한쪽만 갱신되었습니다.";
                    return false;
                }
            }
        }

        foreach ((RouteVertexOccurrence at, _) in edits)
        {
            IReadOnlyList<Vec2> points = copies[at.Piece];
            if (at.Point > 0) changedSegments.Add((points[at.Point - 1], points[at.Point]));
            if (at.Point + 1 < points.Count) changedSegments.Add((points[at.Point], points[at.Point + 1]));
        }

        foreach (KeyValuePair<int, List<Vec2>> entry in copies)
            affectedPoints[entry.Key] = entry.Value;
        return true;
    }
}

/// <summary>편집된 구간이 적용영역(외곽+홀) 안에 남는지 검사한다.</summary>
public static class RouteVertexValidator
{
    /// <summary>검사 표본 간격(mm). 이보다 좁은 경계 요철은 놓칠 수 있다.</summary>
    public const double SampleSpacing = 0.5;

    public static bool SegmentsStayInRegion(IReadOnlyList<IReadOnlyList<Vec2>> region,
        IReadOnlyList<(Vec2 A, Vec2 B)> changedSegments, out string failure)
    {
        failure = string.Empty;
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(changedSegments);
        if (region.Count == 0 || region[0].Count < 3)
        {
            failure = "적용영역 정보가 없습니다.";
            return false;
        }

        for (int segment = 0; segment < changedSegments.Count; segment++)
        {
            (Vec2 a, Vec2 b) = changedSegments[segment];
            double length = a.DistanceTo(b);
            if (!double.IsFinite(length)) { failure = "구간 좌표가 유한하지 않습니다."; return false; }
            if (length <= 1e-9) continue;

            int steps = Math.Max(1, (int)Math.Ceiling(length / SampleSpacing));
            for (int step = 0; step <= steps; step++)
            {
                Vec2 p = a + (b - a) * (step / (double)steps);
                if (InsideOrOn(region, p)) continue;
                failure = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "수정된 구간이 적용영역을 벗어납니다 — ({0:F3}, {1:F3})", p.X, p.Y);
                return false;
            }
        }
        return true;
    }

    private static bool InsideOrOn(IReadOnlyList<IReadOnlyList<Vec2>> region, Vec2 p)
    {
        bool inside = false;
        foreach (IReadOnlyList<Vec2> ring in region)
        {
            if (ring.Count < 3) continue;
            if (Serpentine.DistanceToRing(ring, p) <= 1e-6) return true;
            if (Serpentine.PointInRing(ring, p)) inside = !inside;
        }
        return inside;
    }
}
