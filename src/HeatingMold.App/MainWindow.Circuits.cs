using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>
    /// 현재 TurnEnabled로 회로 정본을 다시 만든다. 분할 하나라도 끝점을 인출 경계에
    /// 맞출 수 없으면 전체를 실패로 돌리고 기존 회로를 유지한다.
    /// </summary>
    private static bool TryRefreshCircuits(WorkingArea area, out string failure)
    {
        failure = null;
        if (area?.Pattern == null)
        {
            if (area == null) { failure = "작업영역이 없습니다"; return false; }
            area.Circuits = new List<CircuitRoute>();
            area.CircuitPieceIndices = new List<List<int>>();
            return true;
        }

        var pieces = area.Pattern.RoutePieces;
        int turnCount = pieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector);
        if (area.TurnEnabled == null || area.TurnEnabled.Count != turnCount)
        {
            failure = "회로 분할 상태가 턴 개수와 일치하지 않습니다";
            return false;
        }

        var groups = BuildCircuitPieceGroups(pieces, area.TurnEnabled);
        if (area.Pattern.StartExit == null || area.Pattern.EndExit == null)
        {
            failure = "경로 양 끝의 인출점 정보가 없습니다 — 패턴을 다시 생성하세요";
            return false;
        }

        CircuitRouteSetResult split;
        try
        {
            var existingExits = new Dictionary<(int PieceIndex, CircuitExitRole Role), CircuitExitPoint>();
            for (int i = 0; i < area.Circuits.Count && i < area.CircuitPieceIndices.Count; i++)
            {
                List<int> indices = area.CircuitPieceIndices[i];
                if (indices.Count == 0) continue;
                existingExits[(indices[0], CircuitExitRole.Start)] = area.Circuits[i].StartExit;
                existingExits[(indices[^1], CircuitExitRole.End)] = area.Circuits[i].EndExit;
            }
            foreach (CircuitExitOverride moved in area.CircuitExitOverrides)
            {
                if (!moved.Binding.HasValue) continue; // Explicit non-CAD fixture compatibility below.
                existingExits[(moved.PieceIndex, moved.Role)] = new CircuitExitPoint(
                    moved.Role, moved.Point, -1, moved.Binding.Value.Parameter) { Binding = moved.Binding };
            }
            split = CircuitRouteTopology.Split(
                CircuitRoute.FromResult(area.Pattern), area.TurnEnabled, area.ExitChain,
                boundary: area.ExitBoundary, existingExits: existingExits);
        }
        catch (Exception ex)
        {
            failure = "회로 분할 계산 실패: " + ex.Message;
            return false;
        }

        if (!split.Succeeded)
        {
            failure = ExplainCircuitSplitFailure(split.Failure, split.Detail);
            return false;
        }
        List<CircuitRoute> routes = split.Routes.ToList();
        // 분할로 생긴 회로 끝 인출점은 원본 Pattern에 없는 파생 꼭지점이다. 사용자가
        // 드래그한 값이 있으면 같은 원본 조각/역할의 회로 끝에만 다시 적용한다.
        if (area.CircuitExitOverrides.Count > 0)
        {
            for (int i = 0; i < routes.Count && i < groups.Count; i++)
            {
                int firstIndex = groups[i][0];
                int lastIndex = groups[i][^1];
                CircuitExitOverride startOverride = area.CircuitExitOverrides.FirstOrDefault(o =>
                    o.PieceIndex == firstIndex && o.Role == CircuitExitRole.Start);
                CircuitExitOverride endOverride = area.CircuitExitOverrides.FirstOrDefault(o =>
                    o.PieceIndex == lastIndex && o.Role == CircuitExitRole.End);
                if (startOverride == null && endOverride == null) continue;
                if (startOverride != null && !ValidateOverride(area, startOverride))
                {
                    failure = "회로 " + (i + 1) + "의 시작 인출점이 인출 경계 위에 있지 않습니다";
                    return false;
                }
                if (endOverride != null && !ValidateOverride(area, endOverride))
                {
                    failure = "회로 " + (i + 1) + "의 종료 인출점이 인출 경계 위에 있지 않습니다";
                    return false;
                }
                ExitBoundaryPoint start = startOverride == null
                    ? ToBoundary(routes[i].StartExit)
                    : new ExitBoundaryPoint(startOverride.Point,
                        routes[i].StartExit.BoundarySegmentIndex,
                        startOverride.Binding?.Parameter ?? routes[i].StartExit.BoundaryParameter)
                        { Binding = startOverride.Binding };
                ExitBoundaryPoint end = endOverride == null
                    ? ToBoundary(routes[i].EndExit)
                    : new ExitBoundaryPoint(endOverride.Point,
                        routes[i].EndExit.BoundarySegmentIndex,
                        endOverride.Binding?.Parameter ?? routes[i].EndExit.BoundaryParameter)
                        { Binding = endOverride.Binding };
                if (!CircuitRouteBuilder.TryTrimToExit(routes[i].Pieces, start, end,
                        routes[i].AngleDeg, routes[i].LaneCount, routes[i].NominalLaneCount,
                        out CircuitRoute rebuilt, out string trimFailure))
                {
                    failure = "회로 " + (i + 1) + "의 인출점 이동을 적용하지 못했습니다 — " + trimFailure;
                    return false;
                }
                routes[i] = rebuilt with { CandidateId = routes[i].CandidateId };
            }
        }
        if (area.RegionBoundary == null)
        {
            failure = "영역의 정확 CAD 경계가 없습니다 — 영역을 다시 확정하세요";
            return false;
        }
        foreach (CircuitRoute route in routes)
        {
            var segments = new List<(Vec2 A, Vec2 B)>();
            foreach (SerpentineRoutePiece piece in route.Pieces)
            {
                // 제외영역의 TurnConnector/FixedConnector는 도체가 아닌 점선 가이드이므로
                // 영역 검증 대상에서 제외한다. 인출 연장선은 HeatingRun 끝 구간에 포함된다.
                if (piece.Kind != SerpentineRoutePieceKind.HeatingRun) continue;
                for (int i = 1; i < piece.Points.Count; i++)
                    segments.Add((piece.Points[i - 1], piece.Points[i]));
            }
            if (!area.RegionBoundary.ValidateSegments(segments, area.Exclusions, out string regionFailure))
            {
                failure = "회로가 적용영역을 벗어납니다 — " + regionFailure;
                return false;
            }
        }
        if (routes.Count != groups.Count)
        {
            failure = "회로 분할 결과와 경로 조각 대응이 일치하지 않습니다";
            return false;
        }
        for (int i = 0; i < routes.Count; i++)
            if (routes[i].Pieces.Count != groups[i].Count)
            {
                failure = "회로 " + (i + 1) + "의 조각 대응이 일치하지 않습니다";
                return false;
            }

        area.Circuits = routes;
        area.CircuitPieceIndices = groups;
        return true;
    }

    /// <summary>턴 비활성 지점에서 끊어 원본 조각 인덱스 그룹을 만든다.</summary>
    private static List<List<int>> BuildCircuitPieceGroups(
        IReadOnlyList<SerpentineRoutePiece> pieces, IReadOnlyList<bool> turnEnabled)
    {
        var groups = new List<List<int>>();
        var current = new List<int>();
        int turn = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Kind == SerpentineRoutePieceKind.TurnConnector)
            {
                bool enabled = turn < turnEnabled.Count ? turnEnabled[turn] : true;
                turn++;
                if (!enabled)
                {
                    if (current.Count > 0) groups.Add(current);
                    current = new List<int>();
                    continue;
                }
            }
            current.Add(i);
        }
        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private static ExitBoundaryPoint ToBoundary(CircuitExitPoint exit) =>
        new(exit.Point, exit.BoundarySegmentIndex, exit.BoundaryParameter) { Binding = exit.Binding };

    private static bool ValidateOverride(WorkingArea area, CircuitExitOverride moved)
    {
        if (area.ExitBoundary == null)
            return !moved.Binding.HasValue && Serpentine.DistanceToChain(area.ExitChain, moved.Point) <= 0.05;
        if (!moved.Binding.HasValue) return false;
        ExitBoundaryResolution evaluated = area.ExitBoundary.Evaluate(moved.Binding.Value);
        return evaluated.Succeeded && evaluated.Point.Value.Point.DistanceTo(moved.Point) <= ExitBoundaryConstraint.Tolerance;
    }

    /// <summary>분할 회로를 다시 연결하면 그 절단면의 수동 인출점 이동값은 더 이상 유효하지 않다.</summary>
    private static void RemoveOverridesForMerge(WorkingArea area, int turnPieceIndex,
        IReadOnlyList<List<int>> beforeGroups)
    {
        List<int> left = beforeGroups.FirstOrDefault(g => g.Contains(turnPieceIndex - 1));
        List<int> right = beforeGroups.FirstOrDefault(g => g.Contains(turnPieceIndex + 1));
        if (left != null && left.Count > 0)
            area.CircuitExitOverrides.RemoveAll(o =>
                o.PieceIndex == left[^1] && o.Role == CircuitExitRole.End);
        if (right != null && right.Count > 0)
            area.CircuitExitOverrides.RemoveAll(o =>
                o.PieceIndex == right[0] && o.Role == CircuitExitRole.Start);
    }

    private static string ExplainCircuitSplitFailure(
        CircuitRouteTopologyFailureReason? failure, string detail)
    {
        string reason = failure switch
        {
            CircuitRouteTopologyFailureReason.TurnNotFound => "분할할 연결 구간을 찾을 수 없습니다",
            CircuitRouteTopologyFailureReason.AdjacentPieceNotRun => "연결 구간 양쪽이 직선 가열부가 아닙니다",
            CircuitRouteTopologyFailureReason.DegenerateSegment => "분할 후 직선 구간이 퇴화합니다",
            CircuitRouteTopologyFailureReason.ExitBoundaryConflict =>
                "분할된 회로의 양 끝을 인출 경계까지 직선으로 연결할 수 없습니다",
            CircuitRouteTopologyFailureReason.EmptyRoute => "분할할 경로가 없습니다",
            CircuitRouteTopologyFailureReason.InvalidRoute => "분할 결과 경로가 유효하지 않습니다",
            _ => "회로를 분할할 수 없습니다"
        };
        return string.IsNullOrEmpty(detail) ? reason : reason + " — " + detail;
    }

    /// <summary>회로 정본과 원본 조각 인덱스 대응이 모두 온전한지 확인한다.</summary>
    private static bool HasUsableCircuits(WorkingArea area, out string reason)
    {
        reason = null;
        if (area?.Pattern == null) { reason = "발열 경로가 없습니다"; return false; }
        // The caller may be displaying a committed result while its boundary is a draft.
        // Do not reconstruct that result from draft inputs here.
        IExitBoundary2D boundary = area.CommittedMapping?.Boundary ?? area.ExitBoundary;
        if (boundary == null) { reason = "인출면의 원본 CAD 경계가 없습니다"; return false; }
        if (area.Circuits == null || area.Circuits.Count == 0)
        { reason = "회로 경로가 없습니다"; return false; }
        if (area.CircuitPieceIndices == null || area.CircuitPieceIndices.Count != area.Circuits.Count)
        { reason = "회로와 원본 조각 대응이 일치하지 않습니다"; return false; }
        for (int i = 0; i < area.Circuits.Count; i++)
        {
            CircuitRoute route = area.Circuits[i];
            if (route.StartExit == null || route.EndExit == null)
            { reason = "회로 " + (i + 1) + "의 인출점이 없습니다"; return false; }
            foreach (CircuitExitPoint exit in new[] { route.StartExit, route.EndExit })
            {
                if (!exit.Binding.HasValue)
                { reason = "회로 " + (i + 1) + "의 원본 edge/parameter가 없습니다"; return false; }
                ExitBoundaryResolution evaluated = boundary.Evaluate(exit.Binding.Value);
                if (!evaluated.Succeeded || evaluated.Point.Value.Point.DistanceTo(exit.Point) > ExitBoundaryConstraint.Tolerance)
                { reason = "회로 " + (i + 1) + "의 원본 경계가 유효하지 않습니다: " + evaluated.Failure; return false; }
            }
            if (route.Pieces.Count != area.CircuitPieceIndices[i].Count)
            { reason = "회로 " + (i + 1) + "의 조각 수가 일치하지 않습니다"; return false; }
        }
        return true;
    }
}
