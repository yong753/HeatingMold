using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>
    /// 생성된 발열경로 전체를 하나의 회로로 확정한다. 저장된 인출점 provenance가
    /// 원본 CAD 경계와 유효하지 않으면 전체를 실패로 돌리고 기존 회로를 유지한다.
    /// </summary>
    private static bool TryRefreshCircuits(WorkingArea area, out string failure)
    {
        failure = null;
        if (area == null) { failure = "작업영역이 없습니다"; return false; }
        if (area.Pattern == null)
        {
            area.Circuits = new List<CircuitRoute>();
            area.CircuitPieceIndices = new List<List<int>>();
            return true;
        }

        if (area.Pattern.StartExit == null || area.Pattern.EndExit == null)
        {
            failure = "경로 양 끝의 인출점 정보가 없습니다 — 패턴을 다시 생성하세요";
            return false;
        }

        IExitBoundary2D boundary = CircuitBoundary(area);
        if (boundary == null)
        {
            failure = "인출 경계가 없습니다 — 영역을 다시 확정하세요";
            return false;
        }

        CircuitRoute route;
        try
        {
            route = CircuitRoute.FromResult(area.Pattern);
        }
        catch (Exception ex)
        {
            failure = "회로 정본 생성 실패: " + ex.Message;
            return false;
        }

        // 저장된 원본 edge/parameter를 그대로 평가한다. 인출점을 이웃 edge로 재탐색하지 않는다.
        CircuitRouteSetResult constrained;
        try
        {
            constrained = CircuitRouteTopology.Constrain(route, area.ExitChain, boundary);
        }
        catch (Exception ex)
        {
            failure = "인출점 검증 실패: " + ex.Message;
            return false;
        }
        if (!constrained.Succeeded)
        {
            failure = "회로 인출점이 유효하지 않습니다 — " + constrained.Detail;
            return false;
        }

        if (area.RegionBoundary == null)
        {
            failure = "영역의 정확 CAD 경계가 없습니다 — 영역을 다시 확정하세요";
            return false;
        }
        var segments = new List<(Vec2 A, Vec2 B)>();
        foreach (SerpentineRoutePiece piece in route.Pieces)
        {
            // 영역 안쪽 발열 여부는 HeatingRun만 검사한다. TurnConnector/FixedConnector는
            // 제외영역을 관통할 수 있는 실제 도체이며, 선택 종료점까지의 리드도 이 조각으로
            // 표현되므로 영역 검증 대상에서 제외한다.
            if (piece.Kind != SerpentineRoutePieceKind.HeatingRun) continue;
            for (int i = 1; i < piece.Points.Count; i++)
                segments.Add((piece.Points[i - 1], piece.Points[i]));
        }
        if (!area.RegionBoundary.ValidateSegments(segments, area.Exclusions, out string regionFailure))
        {
            failure = "회로가 적용영역을 벗어납니다 — " + regionFailure;
            return false;
        }

        area.Circuits = new List<CircuitRoute> { route };
        area.CircuitPieceIndices = new List<List<int>>
        {
            Enumerable.Range(0, route.Pieces.Count).ToList()
        };
        return true;
    }

    /// <summary>
    /// 선택 인출점 방식은 적용영역 전체 경계를, 인출면 방식은 그 면의 경계를 쓴다.
    /// 커밋된 결과를 표시하는 동안에는 draft 경계로 재구성하지 않는다.
    /// </summary>
    private static IExitBoundary2D CircuitBoundary(WorkingArea area) =>
        area.HasSelectedExits
            ? area.CommittedMapping?.RegionBoundary ?? area.RegionExitBoundary
            : area.CommittedMapping?.Boundary ?? area.ExitBoundary;

    private static ExitBoundaryPoint ToBoundary(CircuitExitPoint exit) =>
        new(exit.Point, exit.BoundarySegmentIndex, exit.BoundaryParameter)
        {
            Binding = exit.Binding,
            ArtificialBinding = exit.ArtificialBinding
        };

    /// <summary>회로 정본과 원본 조각 인덱스 대응이 모두 온전한지 확인한다.</summary>
    private static bool HasUsableCircuits(WorkingArea area, out string reason)
    {
        reason = null;
        if (area?.Pattern == null) { reason = "발열 경로가 없습니다"; return false; }
        // The caller may be displaying a committed result while its boundary is a draft.
        // Do not reconstruct that result from draft inputs here.
        IExitBoundary2D boundary = CircuitBoundary(area);
        if (boundary == null) { reason = "인출 경계가 없습니다"; return false; }
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
                if (exit.Binding.HasValue == exit.ArtificialBinding.HasValue)
                { reason = "회로 " + (i + 1) + "의 인출 경계 식별자가 없습니다"; return false; }
                ExitBoundaryResolution evaluated = ExitBoundaryConstraint.EvaluateSaved(boundary, ToBoundary(exit));
                if (!evaluated.Succeeded)
                { reason = "회로 " + (i + 1) + "의 인출 경계가 유효하지 않습니다: " + evaluated.Failure; return false; }
            }
            if (route.Pieces.Count != area.CircuitPieceIndices[i].Count)
            { reason = "회로 " + (i + 1) + "의 조각 수가 일치하지 않습니다"; return false; }
        }
        return true;
    }
}
