using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 확정 회로를 조각 단위 STEP 입력으로 옮긴다. 조각 경계는 유지하며, 각 조각은
/// 네이티브에서 하나의 중심선과 하나의 발열사 솔리드로 만들어진다.
/// </summary>
internal static class StepExportPayload
{
    internal static StepExportArea[] Build(
        IReadOnlyList<MainWindow.RouteExportCircuit> circuits,
        IReadOnlyList<WorkingArea> areaList)
    {
        if (circuits == null || circuits.Count == 0)
        {
            throw new IOException("확정된 발열경로가 없습니다.");
        }
        if (areaList == null || areaList.Count == 0)
        {
            throw new IOException("내보낼 작업 영역이 없습니다.");
        }

        var byName = new Dictionary<string, WorkingArea>(StringComparer.Ordinal);
        foreach (WorkingArea candidate in areaList)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Name)) continue;
            if (!byName.ContainsKey(candidate.Name)) byName.Add(candidate.Name, candidate);
        }

        var areas = new StepExportArea[circuits.Count];
        for (int i = 0; i < circuits.Count; i++)
        {
            MainWindow.RouteExportCircuit circuit = circuits[i];
            if (circuit?.Pieces == null || circuit.Pieces.Count == 0)
            {
                throw new IOException("영역 회로 " + (i + 1) + "에 확정 조각이 없습니다.");
            }

            MainWindow.RouteExportPiece first = circuit.Pieces[0];
            string areaName = string.IsNullOrWhiteSpace(first.AreaName) ? "영역" : first.AreaName;
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var pieces = new StepExportPiece[circuit.Pieces.Count];
            int lane = 0;
            int turn = 0;
            int connector = 0;

            for (int p = 0; p < circuit.Pieces.Count; p++)
            {
                MainWindow.RouteExportPiece piece = circuit.Pieces[p];
                if (p > 0)
                {
                    ValidateJoin(circuit, p);
                }
                double[] points = Flatten(piece);
                if (points.Length < 6)
                {
                    throw new IOException("영역 " + areaName + " 조각 "
                        + (piece.PieceIndex + 1) + "의 확정 경로 좌표가 부족합니다. "
                        + "경로를 다시 생성한 뒤 내보내세요.");
                }
                string name = NextPieceName(areaName, piece.Kind, ref lane, ref turn,
                    ref connector, usedNames);
                pieces[p] = new StepExportPiece(name, points);
            }

            if (!byName.TryGetValue(areaName, out WorkingArea source) || source == null)
            {
                throw new IOException("영역 " + areaName
                    + "의 작업 정보를 찾지 못했습니다. 경로를 다시 생성한 뒤 내보내세요.");
            }
            BuildRegion(source, out int[] faceIds, out double[] frame, out double[] ring);

            areas[i] = new StepExportArea(areaName, areaName,
                first.ColorR, first.ColorG, first.ColorB, pieces, faceIds, frame, ring);
        }
        return areas;
    }

    /// <summary>
    /// 영역 색을 칠할 원본 CAD 면과 작업평면, 유효 발열영역 외곽(적용영역 − 제외영역,
    /// 마진 적용 전)을 모은다. 표시용 mesh나 마진 적용 영역을 설계 경계로 쓰지 않는다.
    /// </summary>
    private static void BuildRegion(WorkingArea area,
        out int[] faceIds, out double[] frame, out double[] ring)
    {
        string name = area.Name;
        if (area.FaceIdSet == null || area.FaceIdSet.Count == 0)
        {
            throw new IOException("영역 " + name + "에 선택된 CAD 면이 없어 발열영역 색을 표시할 수 없습니다.");
        }
        ProjectionFrame projection = area.Frame;
        if (projection == null)
        {
            throw new IOException("영역 " + name + "의 작업평면 좌표계가 없어 발열영역 색을 표시할 수 없습니다.");
        }
        frame = new[]
        {
            projection.Origin.X, projection.Origin.Y, projection.Origin.Z,
            projection.AxisX.X, projection.AxisX.Y, projection.AxisX.Z,
            projection.AxisY.X, projection.AxisY.Y, projection.AxisY.Z
        };
        for (int i = 0; i < frame.Length; i++)
        {
            if (!double.IsFinite(frame[i]))
            {
                throw new IOException("영역 " + name + "의 작업평면 좌표계가 유한하지 않습니다.");
            }
        }

        List<List<Vec2>> effective = area.EffectiveRegion();
        if (effective == null || effective.Count == 0)
        {
            throw new IOException("영역 " + name + "의 유효 발열영역이 비어 있어 색을 표시할 수 없습니다.");
        }
        if (effective.Count > 1)
        {
            throw new IOException("영역 " + name + "의 유효 발열영역이 " + effective.Count
                + "개 외곽으로 나뉘어 있어 STEP 영역 색을 표시할 수 없습니다.");
        }
        List<Vec2> outline = effective[0];
        if (outline == null || outline.Count < 3)
        {
            throw new IOException("영역 " + name + "의 유효 발열영역 외곽 점이 부족합니다.");
        }
        ring = new double[outline.Count * 2];
        for (int i = 0; i < outline.Count; i++)
        {
            Vec2 point = outline[i];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new IOException("영역 " + name + "의 유효 발열영역 외곽 좌표가 유한하지 않습니다.");
            }
            ring[i * 2] = point.X;
            ring[i * 2 + 1] = point.Y;
        }
        faceIds = area.FaceIdSet.OrderBy(id => id).ToArray();
        if (faceIds.Length == 0 || faceIds.Any(id => id < 0))
        {
            throw new IOException("영역 " + name + "의 CAD 면 ID가 유효하지 않습니다.");
        }
    }

    private static void ValidateJoin(MainWindow.RouteExportCircuit circuit, int index)
    {
        MainWindow.RouteExportPiece previous = circuit.Pieces[index - 1];
        MainWindow.RouteExportPiece current = circuit.Pieces[index];
        if (previous.Points == null || previous.Points.Count == 0
            || current.Points == null || current.Points.Count == 0)
        {
            throw new IOException("영역 " + previous.AreaName + "의 확정 경로 좌표가 없습니다.");
        }

        Vec3 previousEnd = previous.Points[previous.Points.Count - 1];
        Vec3 currentStart = current.Points[0];
        double gx = previousEnd.X - currentStart.X;
        double gy = previousEnd.Y - currentStart.Y;
        double gz = previousEnd.Z - currentStart.Z;
        double gap = Math.Sqrt(gx * gx + gy * gy + gz * gz);
        if (gap > JoinTolerance)
        {
            throw new IOException("영역 " + previous.AreaName + " 회로 1 조각 "
                + (previous.PieceIndex + 1) + "과 " + (current.PieceIndex + 1)
                + "의 확정 경로가 " + gap.ToString("0.###", CultureInfo.InvariantCulture)
                + " mm 벌어져 있어 하나의 발열사로 내보낼 수 없습니다. 경로를 다시 생성하세요.");
        }
    }

    /// <summary>확정 조각 하나의 3D 점을 flat xyz로 옮기고 연속 완전 중복점만 생략한다.</summary>
    private static double[] Flatten(MainWindow.RouteExportPiece piece)
    {
        if (piece.Points == null || piece.Points.Count == 0)
        {
            throw new IOException("영역 " + piece.AreaName + " 조각 "
                + (piece.PieceIndex + 1) + "의 확정 경로 좌표가 없습니다.");
        }
        var points = new List<double>(piece.Points.Count * 3);
        for (int k = 0; k < piece.Points.Count; k++)
        {
            Vec3 point = piece.Points[k];
            if (points.Count >= 3
                && points[points.Count - 3] == point.X
                && points[points.Count - 2] == point.Y
                && points[points.Count - 1] == point.Z)
            {
                continue;
            }
            points.Add(point.X);
            points.Add(point.Y);
            points.Add(point.Z);
        }
        return points.ToArray();
    }

    private static string NextPieceName(string areaName, SerpentineRoutePieceKind kind,
        ref int lane, ref int turn, ref int connector, HashSet<string> used)
    {
        string baseName = kind switch
        {
            SerpentineRoutePieceKind.HeatingRun => areaName + " 레인 " + (++lane),
            SerpentineRoutePieceKind.TurnConnector => areaName + " 턴 " + (++turn),
            _ => areaName + " 연결 " + (++connector),
        };
        string name = baseName;
        int suffix = 1;
        while (!used.Add(name))
        {
            ++suffix;
            name = baseName + " (" + suffix + ")";
        }
        return name;
    }

    /// <summary>확정 커밋이 허용한 조각 간 최대 간격(mm). 더 벌어진 경로는 거부한다.</summary>
    internal const double JoinTolerance = 0.1;
}
