using System;
using System.Collections.Generic;

namespace HeatingMold.Core.Geometry;

/// <summary>
/// 확정된 발열경로에서 추출한 레인 하나. 같은 sweep 행에 놓인 발열선 묶음과 그 사이 연결선을
/// 포함한다. 분할은 이 레인들을 <b>연속 묶음</b>으로 나누는 방식으로만 수행하며, 레인 좌표를
/// 다시 계산하거나 새 레인을 만들지 않는다.
/// </summary>
public sealed record CommittedLane(
    int LaneIndex,
    double SweepCoordinate,
    int FirstPieceIndex,
    int LastPieceIndex,
    Vec2 StartPoint,
    Vec2 EndPoint,
    double StartCrossCoordinate,
    double EndCrossCoordinate,
    bool StartsLow,
    bool EndsLow,
    double LengthMm);

/// <summary>확정 경로의 레인 목록. 좌표는 확정 작업평면에서 생성 방향으로 회전한 지역 좌표다.</summary>
public sealed record CommittedRouteLayout(
    IReadOnlyList<CommittedLane> Lanes,
    double AngleDeg,
    double TotalPieceLengthMm);

/// <summary>
/// 확정된 <see cref="SerpentineResult"/>의 조각 순서에서 실제 발열 레인을 추출한다.
///
/// 신뢰하는 정본은 확정 패턴의 조각 배열 하나뿐이다. 근사 레인 격자나 재생성 결과를 원본으로
/// 삼지 않는다. 레인 사이 <see cref="SerpentineRoutePieceKind.TurnConnector"/>는 두 레인을 잇는
/// 연결선이므로 어느 레인에도 속하지 않는다 — 분할할 때 제거되고, 각 자식에는 그 자식의 실제
/// 첫/마지막 발열선에서 인출점까지의 연결선이 새로 붙는다.
/// </summary>
public static class CommittedRouteLayoutAnalyzer
{
    /// <summary>조각 연결 판정 오차(작업평면 mm). 좌표를 다시 계산하지 않고 이어진 조각만 인정한다.</summary>
    public const double ContinuityTolerance = 1e-6;

    public static bool TryAnalyze(SerpentineResult pattern, double radians,
        out CommittedRouteLayout layout, out string failure)
    {
        layout = null!;
        failure = string.Empty;
        if (pattern == null || pattern.RoutePieces == null || pattern.RoutePieces.Count == 0)
        {
            failure = "확정된 발열경로 조각이 없습니다.";
            return false;
        }
        if (!double.IsFinite(radians))
        {
            failure = "생성 방향 각도가 올바르지 않습니다.";
            return false;
        }

        IReadOnlyList<SerpentineRoutePiece> pieces = pattern.RoutePieces;
        int count = pieces.Count;
        for (int i = 0; i < count; i++)
        {
            SerpentineRoutePiece piece = pieces[i];
            if (piece?.Points == null || piece.Points.Count < 2)
            {
                failure = "발열경로 조각 " + (i + 1) + "의 점이 부족합니다.";
                return false;
            }
            if (!IsFinite(piece.Points[0]) || !IsFinite(piece.Points[piece.Points.Count - 1]))
            {
                failure = "발열경로 조각 " + (i + 1) + "의 좌표가 올바르지 않습니다.";
                return false;
            }
            if (i + 1 < count)
            {
                Vec2 last = piece.Points[piece.Points.Count - 1];
                if (last.DistanceTo(pieces[i + 1].Points[0]) > ContinuityTolerance)
                {
                    failure = "발열경로 조각 " + (i + 1) + "과 " + (i + 2) + "이(가) 이어지지 않습니다.";
                    return false;
                }
            }
        }

        // 레인 사이 연결선(TurnConnector)을 경계로 조각 범위를 나눈다. 연결선 자체는 어느
        // 범위에도 넣지 않는다. 발열선이 하나도 없는 범위는 다음 범위와 합친다 — 경로 시작
        // 연결선처럼 레인 앞에 붙은 조각을 리드로 유지하기 위해서다.
        var ranges = new List<(int First, int Last)>();
        int cursor = 0;
        while (cursor < count)
        {
            int end = cursor;
            while (end + 1 < count && pieces[end + 1].Kind != SerpentineRoutePieceKind.TurnConnector)
                end++;
            ranges.Add((cursor, end));
            if (end + 1 >= count) break;
            cursor = end + 2;
        }

        for (int i = ranges.Count - 1; i > 0; i--)
        {
            if (HasHeatingRun(pieces, ranges[i].First, ranges[i].Last)) continue;
            ranges[i - 1] = (ranges[i - 1].First, ranges[i].Last);
            ranges.RemoveAt(i);
        }
        if (ranges.Count == 0 || !HasHeatingRun(pieces, ranges[0].First, ranges[0].Last))
        {
            failure = "확정된 발열경로에서 발열 레인을 찾지 못했습니다.";
            return false;
        }

        var lanes = new List<CommittedLane>(ranges.Count);
        double total = 0;
        double? previousSweep = null;
        int sweepSign = 0;
        for (int r = 0; r < ranges.Count; r++)
        {
            (int first, int last) = ranges[r];
            int firstRun = -1, lastRun = -1, laneIndex = -1;
            double sweep = 0;
            double length = 0;
            for (int i = first; i <= last; i++)
            {
                SerpentineRoutePiece piece = pieces[i];
                length += piece.Length;
                if (piece.Kind != SerpentineRoutePieceKind.HeatingRun) continue;
                Vec2 a = piece.Points[0].Rotate(-radians);
                Vec2 b = piece.Points[piece.Points.Count - 1].Rotate(-radians);
                if (Math.Abs(a.Y - b.Y) > ContinuityTolerance)
                {
                    failure = "발열선 조각 " + (i + 1) + "이(가) 생성 방향과 평행하지 않습니다.";
                    return false;
                }
                if (firstRun < 0)
                {
                    firstRun = i;
                    laneIndex = piece.LaneIndex;
                    sweep = a.Y;
                }
                else if (piece.LaneIndex != laneIndex)
                {
                    failure = "한 레인 범위에 서로 다른 레인 번호가 섞여 있습니다.";
                    return false;
                }
                if (Math.Abs(a.Y - sweep) > ContinuityTolerance)
                {
                    failure = "한 레인 범위의 발열선이 서로 다른 행에 놓여 있습니다.";
                    return false;
                }
                lastRun = i;
            }
            if (firstRun < 0)
            {
                failure = "발열 레인 범위 " + (r + 1) + "에 발열선이 없습니다.";
                return false;
            }

            if (previousSweep.HasValue)
            {
                double delta = sweep - previousSweep.Value;
                if (Math.Abs(delta) <= ContinuityTolerance)
                {
                    failure = "연속한 두 레인이 같은 행에 놓여 있습니다.";
                    return false;
                }
                int sign = delta > 0 ? 1 : -1;
                if (sweepSign == 0) sweepSign = sign;
                else if (sign != sweepSign)
                {
                    failure = "레인 순서가 한 진행 방향으로 이어지지 않습니다.";
                    return false;
                }
            }
            previousSweep = sweep;

            Vec2 runStart = pieces[firstRun].Points[0].Rotate(-radians);
            Vec2 runEnd = pieces[firstRun].Points[pieces[firstRun].Points.Count - 1].Rotate(-radians);
            Vec2 lastStart = pieces[lastRun].Points[0].Rotate(-radians);
            Vec2 lastEnd = pieces[lastRun].Points[pieces[lastRun].Points.Count - 1].Rotate(-radians);
            bool startsLow = runStart.X < runEnd.X;
            bool endsLow = lastEnd.X < lastStart.X;

            lanes.Add(new CommittedLane(
                laneIndex,
                sweep,
                first,
                last,
                pieces[first].Points[0],
                pieces[last].Points[pieces[last].Points.Count - 1],
                pieces[first].Points[0].Rotate(-radians).X,
                pieces[last].Points[pieces[last].Points.Count - 1].Rotate(-radians).X,
                startsLow,
                endsLow,
                length));
            total += length;
        }

        layout = new CommittedRouteLayout(lanes, pattern.AngleDeg, total);
        return true;
    }

    private static bool HasHeatingRun(IReadOnlyList<SerpentineRoutePiece> pieces, int first, int last)
    {
        for (int i = first; i <= last; i++)
            if (pieces[i].Kind == SerpentineRoutePieceKind.HeatingRun) return true;
        return false;
    }

    private static bool IsFinite(Vec2 v) => double.IsFinite(v.X) && double.IsFinite(v.Y);
}

/// <summary>
/// 확정 레인 묶음에서 자식 영역의 경로를 <b>복사</b>로 구성한다.
///
/// - 기존 발열선 좌표·순서·레인 번호를 그대로 유지한다. 레인을 뒤집거나 새로 만들지 않는다.
/// - 자식 사이를 잇던 TurnConnector는 이미 범위에서 빠져 있다.
/// - 자식의 실제 첫/마지막 발열선 끝과 인출점이 다르면 그 차이만큼의 연결선을 붙인다.
///   차이가 <see cref="EndpointSnapTolerance"/> 이내면 경계 다각형 근사 오차를 정확 경계점으로
///   바로잡는 것으로 보고 발열선 끝점을 그 점에 맞춘다(발열선을 인출점까지 늘리지 않는다).
/// </summary>
public static class CommittedLanePartition
{
    /// <summary>발열선 끝과 인출점을 같은 점으로 보는 최대 차이(작업평면 mm).
    /// 경계 다각형 근사(deflection 0.1 mm)와 같은 수준이며, 이보다 크면 연결선을 붙인다.</summary>
    public const double EndpointSnapTolerance = 0.1;

    public static bool TryBuildChildRoute(
        SerpentineResult parent,
        CommittedRouteLayout layout,
        int firstLane,
        int lastLane,
        ExitBoundaryPoint start,
        ExitBoundaryPoint end,
        out SerpentineResult child,
        out string failure)
    {
        child = null!;
        failure = string.Empty;
        if (parent?.RoutePieces == null || layout?.Lanes == null || layout.Lanes.Count == 0)
        {
            failure = "분할할 확정 발열경로가 준비되지 않았습니다.";
            return false;
        }
        if (firstLane < 0 || lastLane >= layout.Lanes.Count || firstLane > lastLane)
        {
            failure = "분할 레인 범위가 올바르지 않습니다.";
            return false;
        }
        if (!HasSingleProvenance(start) || !HasSingleProvenance(end))
        {
            failure = "자식 영역의 시작/종료 인출점에 경계 근거가 정확히 하나 있어야 합니다.";
            return false;
        }

        int firstPiece = layout.Lanes[firstLane].FirstPieceIndex;
        int lastPiece = layout.Lanes[lastLane].LastPieceIndex;
        if (firstPiece < 0 || lastPiece >= parent.RoutePieces.Count || firstPiece > lastPiece)
        {
            failure = "확정 경로에서 분할 레인 범위를 찾지 못했습니다.";
            return false;
        }

        var pieces = new List<SerpentineRoutePiece>(lastPiece - firstPiece + 1);
        for (int i = firstPiece; i <= lastPiece; i++)
        {
            SerpentineRoutePiece piece = parent.RoutePieces[i];
            pieces.Add(piece with { Points = new List<Vec2>(piece.Points) });
        }

        int laneCount = lastLane - firstLane + 1;
        Vec2 startPoint = pieces[0].Points[0];
        Vec2 endPoint = pieces[^1].Points[^1];
        double startGap = startPoint.DistanceTo(start.Point);
        double endGap = endPoint.DistanceTo(end.Point);
        if (!double.IsFinite(startGap) || !double.IsFinite(endGap))
        {
            failure = "자식 영역 인출점 좌표가 올바르지 않습니다.";
            return false;
        }

        if (startGap > EndpointSnapTolerance)
        {
            pieces.Insert(0, new SerpentineRoutePiece(
                SerpentineRoutePieceKind.FixedConnector,
                layout.Lanes[firstLane].LaneIndex,
                new List<Vec2> { start.Point, startPoint }));
        }
        if (endGap > EndpointSnapTolerance)
        {
            pieces.Add(new SerpentineRoutePiece(
                SerpentineRoutePieceKind.FixedConnector,
                layout.Lanes[lastLane].LaneIndex,
                new List<Vec2> { endPoint, end.Point }));
        }

        if (!CircuitRouteBuilder.TryTrimToExit(pieces, start, end, parent.AngleDeg, laneCount, 0,
                out CircuitRoute? route, out string trimFailure))
        {
            failure = "분할 영역의 경로를 구성하지 못했습니다 — " + trimFailure;
            return false;
        }

        child = route!.ToResult();
        return true;
    }

    private static bool HasSingleProvenance(ExitBoundaryPoint point)
    {
        int kinds = (point.Binding.HasValue ? 1 : 0)
            + (point.ArtificialBinding.HasValue ? 1 : 0)
            + (point.SplitBinding.HasValue ? 1 : 0);
        return kinds == 1 && double.IsFinite(point.Point.X) && double.IsFinite(point.Point.Y);
    }
}
