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

        var circuits = CollectExportCircuits(snapshot.ReadyAreas, side, out error);
        if (circuits == null) return null;

        if (!TableMatchesCircuits(snapshot.Table, circuits, out error)) return null;

        var view = new DxfView();
        if (!AddPlanOutline(view, side.Frame, out error)) return null;
        if (!AddHeatAreas(view, snapshot.ReadyAreas, side.Frame, out error)) return null;
        if (!AddHeatRoutes(view, circuits, side.Frame, out error)) return null;
        return view;
    }

    private static bool TableMatchesCircuits(
        ElectricalSpecTable table, IReadOnlyList<RouteExportCircuit> circuits, out string error)
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
        DxfView view, IReadOnlyList<RouteExportCircuit> circuits,
        ProjectionFrame frame, out string error)
    {
        error = null;
        var sources = new List<RouteLengthLabelSource>();
        var piecesByKey = new Dictionary<(string AreaName, int PieceIndex), RouteExportPiece>();

        foreach (RouteExportCircuit circuit in circuits)
        {
            foreach (RouteExportPiece piece in circuit.Pieces)
            {
                if (!PlanViewBuilder.TryProjectRoute(piece.Points, frame,
                        out IReadOnlyList<Vec2> projected, out string failure))
                {
                    error = "영역 " + piece.AreaName + " 회로 " + circuit.CircuitId
                        + " 조각 " + (piece.PieceIndex + 1) + " (native " + piece.NativePieceId + "): "
                        + failure;
                    return false;
                }
                view.HeatLines.Add(new DxfPolyline
                {
                    Points = projected.ToList(),
                    Color = new AciColor(piece.ColorR, piece.ColorG, piece.ColorB)
                });
                // 인출점 리드(FixedConnector)는 레인도 턴도 아니므로 길이 라벨 대상이 아니다.
                // 다만 배치 충돌 검사에는 그대로 참여한다.
                if (piece.Kind == SerpentineRoutePieceKind.FixedConnector) continue;
                sources.Add(new RouteLengthLabelSource(
                    piece.AreaName, piece.PieceIndex, piece.Kind, piece.LaneIndex,
                    piece.LengthMm, projected));
                piecesByKey[(piece.AreaName, piece.PieceIndex)] = piece;
            }
        }
        if (view.HeatLines.Count == 0)
        {
            error = "평면도에 출력할 발열경로가 없습니다.";
            return false;
        }

        // 라벨 값은 확정된 실제 3D 길이이고, 배치만 확정 작업평면 2D 좌표를 쓴다.
        var options = new RouteLengthLabelOptions
        {
            OffsetMm = 6.0,
            TextHeightMm = DxfExporter.RouteLengthLabelHeight
        };
        if (!RouteLengthLabels.TryBuild(sources, frame, options,
                out List<RouteLengthLabel> labels, out string labelFailure))
        {
            error = "경로 길이 라벨을 배치하지 못했습니다: " + labelFailure;
            return false;
        }
        foreach (RouteLengthLabel label in labels)
        {
            if (!piecesByKey.TryGetValue((label.AreaName, label.PieceIndex), out RouteExportPiece piece))
            {
                error = "경로 길이 라벨의 원본 조각을 찾지 못했습니다: 영역 " + label.AreaName
                    + " 조각 " + (label.PieceIndex + 1) + ".";
                return false;
            }
            var color = new AciColor(piece.ColorR, piece.ColorG, piece.ColorB);
            view.HeatLabels.Add(new DxfRouteLabel
            {
                Text = label.Text,
                Position = label.Position2D,
                RotationDeg = label.RotationDeg,
                Height = DxfExporter.RouteLengthLabelHeight,
                HasLeader = label.HasLeader,
                LeaderStart = label.LeaderStart2D,
                LeaderEnd = label.LeaderEnd2D,
                Color = color
            });
            if (label.HasLeader)
            {
                view.HeatLabelLeaders.Add(new DxfPolyline
                {
                    Points = new[] { label.LeaderStart2D, label.LeaderEnd2D },
                    Color = color
                });
            }
        }
        return true;
    }
}
