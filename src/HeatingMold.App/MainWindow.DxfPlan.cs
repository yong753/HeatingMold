using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Export;
using HeatingMold.OccBridge;
using netDxf;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>내보내기용 확정 조각 하나. 좌표는 확정된 실제 3D 체인이다.</summary>
    private sealed record DxfPlanPieceExport(
        string AreaName, int PieceIndex, int NativePieceId, bool Connector,
        IReadOnlyList<Vec3> Points);

    /// <summary>내보내기용 확정 회로 하나. 조각 순서는 회로 정본과 같다.</summary>
    private sealed record DxfPlanCircuitExport(
        string CircuitId, IReadOnlyList<DxfPlanPieceExport> Pieces);

    /// <summary>
    /// 검토 스냅샷과 확정 네이티브 경로로 작업평면 기준 평면도를 만든다.
    /// 경로를 재생성/재매핑하지 않으며, 누락·staged·비활성·중복·퇴화 투영은
    /// 항상 명시적 실패다.
    /// </summary>
    private DxfView BuildPlanExportView(ReviewSnapshot snapshot, out string error)
    {
        error = null;
        WorkingSideDefinition side = _workingSide.Confirmed;
        if (side == null || side.Id.Value <= 0)
        {
            error = "확정된 작업평면이 없습니다. 작업 방향을 먼저 확정하세요.";
            return null;
        }
        if (side.Revision.Value != Bridge.GetModelRevision())
        {
            error = "작업평면이 현재 모델 revision과 일치하지 않습니다. 다시 검토하세요.";
            return null;
        }
        if (snapshot?.Table == null || snapshot.Table.Rows.Count == 0)
        {
            error = "내보낼 전기 사양표가 없습니다.";
            return null;
        }

        var circuits = BuildExportCircuits(snapshot, side, out error);
        if (circuits == null) return null;

        if (!TableMatchesCircuits(snapshot.Table, circuits, out error)) return null;

        var view = new DxfView();
        if (!AddPlanOutline(view, side.Frame, out error)) return null;
        if (!AddHeatAreas(view, snapshot.ReadyAreas, side.Frame, out error)) return null;
        if (!AddHeatRoutes(view, circuits, side.Frame, out error)) return null;
        return view;
    }

    private List<DxfPlanCircuitExport> BuildExportCircuits(
        ReviewSnapshot snapshot, WorkingSideDefinition side, out string error)
    {
        error = null;
        var circuits = new List<DxfPlanCircuitExport>();
        var usedNativeIds = new HashSet<int>();

        foreach (WorkingArea area in snapshot.ReadyAreas)
        {
            if (!TryValidateAreaRouteIndex(area, usedNativeIds, out error)) return null;

            var pieces = area.Pattern.RoutePieces;
            // 하나의 영역은 하나의 연속 회로다. 조각 인덱스는 원본 순서를 정확히 한 번 포함해야 한다.
            if (area.Circuits.Count != 1 || area.CircuitPieceIndices.Count != 1)
            {
                error = "영역 " + area.Name + "의 회로 정본이 단일 회로가 아닙니다.";
                return null;
            }
            List<int> group = area.CircuitPieceIndices[0];
            if (group == null || group.Count != pieces.Count || area.Circuits[0].Pieces.Count != pieces.Count)
            {
                error = "영역 " + area.Name + "의 회로가 모든 경로 조각을 포함하지 않습니다.";
                return null;
            }

            var areaNativeIds = new List<int>(group.Count);
            for (int position = 0; position < group.Count; position++)
            {
                int index = group[position];
                if (index != position)
                {
                    error = "영역 " + area.Name + "의 회로 조각 순서가 원본 경로와 일치하지 않습니다.";
                    return null;
                }
                int nativeId = area.NativePieceIds[index];
                if (nativeId < 0)
                {
                    error = "영역 " + area.Name + " 조각 " + (index + 1) + "의 네이티브 ID가 없습니다.";
                    return null;
                }
                areaNativeIds.Add(nativeId);
            }

            foreach (int nativeId in areaNativeIds)
            {
                if (!usedNativeIds.Add(nativeId))
                {
                    error = "영역 " + area.Name + "의 네이티브 경로 ID " + nativeId + "가 중복되었습니다.";
                    return null;
                }
            }

            double[][] chains = Bridge.GetCommittedRouteChains(
                side.Id.Value, side.Revision.Value, areaNativeIds.ToArray());
            if (chains == null || chains.Length != areaNativeIds.Count)
            {
                error = "영역 " + area.Name + "의 확정 발열경로를 읽지 못했습니다. "
                    + "조각 누락·비활성·작업평면 불일치 여부를 확인하고 다시 생성하세요.";
                return null;
            }

            string prefix = string.IsNullOrEmpty(area.Name) ? "A-" : area.Name + "-";
            int chainCursor = 0;
            var circuitPieces = new List<DxfPlanPieceExport>(group.Count);
            foreach (int index in group)
            {
                double[] chain = chains[chainCursor++];
                if (chain == null || chain.Length < 6 || chain.Length % 3 != 0)
                {
                    error = "영역 " + area.Name + " 회로 1 조각 " + (index + 1)
                        + "의 확정 좌표가 없습니다.";
                    return null;
                }
                var points = new List<Vec3>(chain.Length / 3);
                for (int k = 0; k + 2 < chain.Length; k += 3)
                {
                    var point = new Vec3(chain[k], chain[k + 1], chain[k + 2]);
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                    {
                        error = "영역 " + area.Name + " 회로 1 조각 " + (index + 1)
                            + "의 확정 좌표가 유한하지 않습니다.";
                        return null;
                    }
                    points.Add(point);
                }
                circuitPieces.Add(new DxfPlanPieceExport(
                    area.Name, index, area.NativePieceIds[index],
                    area.Pattern.RoutePieces[index].Kind != SerpentineRoutePieceKind.HeatingRun,
                    points));
            }
            circuits.Add(new DxfPlanCircuitExport(prefix + "1", circuitPieces));
        }

        if (circuits.Count == 0)
        {
            error = "내보낼 확정 회로가 없습니다.";
            return null;
        }
        return circuits;
    }

    private static bool TryValidateAreaRouteIndex(
        WorkingArea area, HashSet<int> usedNativeIds, out string error)
    {
        error = null;
        if (area?.Pattern == null || area.NativePieceIds == null || area.RoutePieceLengths == null
            || area.Circuits == null || area.CircuitPieceIndices == null)
        {
            error = "영역 " + (area?.Name ?? "") + "의 경로 정본이 불완전합니다.";
            return false;
        }
        int count = area.Pattern.RoutePieces.Count;
        if (area.NativePieceIds.Length != count || area.RoutePieceLengths.Length != count)
        {
            error = "영역 " + area.Name + "의 조각/네이티브 ID/길이 대응이 일치하지 않습니다.";
            return false;
        }
        if (area.CircuitPieceIndices.Count != area.Circuits.Count)
        {
            error = "영역 " + area.Name + "의 회로와 조각 대응이 일치하지 않습니다.";
            return false;
        }
        return true;
    }

    private static bool TableMatchesCircuits(
        ElectricalSpecTable table, IReadOnlyList<DxfPlanCircuitExport> circuits, out string error)
    {
        error = null;
        var tableIds = table.Rows.Select(r => r.CircuitId).ToList();
        var circuitIds = circuits.Select(c => c.CircuitId).ToList();
        if (tableIds.Count != circuitIds.Count || !tableIds.SequenceEqual(circuitIds))
        {
            error = "전기 사양표 행과 확정 회로가 일치하지 않습니다. 표를 다시 확인하세요.";
            return false;
        }
        return true;
    }

    private static bool AddPlanOutline(
        DxfView view, ProjectionFrame frame, out string error)
    {
        error = null;
        Vec3 n = frame.Normal, x = frame.AxisX;
        HlrPolyline[] edges = Bridge.HlrEdges(n.X, n.Y, n.Z, x.X, x.Y, x.Z, true);
        if (edges == null)
        {
            error = "금형 평면 투상선을 만들지 못했습니다.";
            return false;
        }
        int visible = 0;
        foreach (HlrPolyline edge in edges)
        {
            if (edge?.Points == null || edge.Points.Length < 4 || edge.Points.Length % 2 != 0)
            {
                error = "금형 평면 투상선 데이터가 불완전합니다. 다시 투상하세요.";
                return false;
            }
            var points = new List<Vec2>(edge.Points.Length / 2);
            for (int k = 0; k + 1 < edge.Points.Length; k += 2)
            {
                double px = edge.Points[k], py = edge.Points[k + 1];
                if (!double.IsFinite(px) || !double.IsFinite(py))
                {
                    error = "금형 평면 투상선 좌표가 유한하지 않습니다.";
                    return false;
                }
                points.Add(PlanViewBuilder.FromHlr(new Vec2(px, py), frame));
            }
            bool distinct = false;
            for (int k = 1; k < points.Count && !distinct; k++)
                distinct = (points[k] - points[0]).Length > 1e-7;
            if (!distinct)
            {
                error = "금형 평면 투상선이 한 점으로 퇴화했습니다.";
                return false;
            }
            var polyline = new DxfPolyline { Points = points };
            if (edge.Visible)
            {
                view.VisibleEdges.Add(polyline);
                visible++;
            }
            else view.HiddenEdges.Add(polyline);
        }
        if (visible == 0)
        {
            error = "금형의 가시 평면 투상선이 없습니다. 작업평면 방향을 확인하세요.";
            return false;
        }
        return true;
    }

    private static bool AddHeatAreas(
        DxfView view, IReadOnlyList<WorkingArea> areas,
        ProjectionFrame frame, out string error)
    {
        error = null;
        if (areas == null || areas.Count == 0)
        {
            error = "평면도에 출력할 발열영역이 없습니다.";
            return false;
        }
        foreach (WorkingArea area in areas)
        {
            if (!PlanHeatAreaBuilder.TryBuild(
                    area.Name, area.Region, area.Exclusions, area.Frame, frame,
                    out DxfPolyline ring, out string failure))
            {
                error = failure;
                return false;
            }
            ring.Color = new AciColor(area.Color.R, area.Color.G, area.Color.B);
            view.HeatAreas.Add(ring);
        }
        return true;
    }

    private static bool AddHeatRoutes(
        DxfView view, IReadOnlyList<DxfPlanCircuitExport> circuits,
        ProjectionFrame frame, out string error)
    {
        error = null;
        foreach (DxfPlanCircuitExport circuit in circuits)
        {
            foreach (DxfPlanPieceExport piece in circuit.Pieces)
            {
                if (!PlanViewBuilder.TryProjectRoute(piece.Points, frame,
                        out IReadOnlyList<Vec2> projected, out string failure))
                {
                    error = "영역 " + piece.AreaName + " 회로 " + circuit.CircuitId
                        + " 조각 " + (piece.PieceIndex + 1) + " (native " + piece.NativePieceId + "): "
                        + failure;
                    return false;
                }
                view.HeatLines.Add(new DxfPolyline { Points = projected.ToList() });
            }
        }
        if (view.HeatLines.Count == 0)
        {
            error = "평면도에 출력할 발열경로가 없습니다.";
            return false;
        }
        return true;
    }
}
