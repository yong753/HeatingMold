using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>내보내기용 확정 조각 하나. 좌표는 확정된 실제 3D 체인이다.</summary>
    private sealed record RouteExportPiece(
        string AreaName, int PieceIndex, int NativePieceId,
        SerpentineRoutePieceKind Kind, int LaneIndex, double LengthMm,
        IReadOnlyList<Vec3> Points, byte ColorR, byte ColorG, byte ColorB);

    /// <summary>내보내기용 확정 회로 하나. 조각 순서는 회로 정본과 같다.</summary>
    private sealed record RouteExportCircuit(
        string CircuitId, IReadOnlyList<RouteExportPiece> Pieces);

    /// <summary>
    /// 확정 네이티브 경로에서 DXF/STEP 공용 내보내기 회로를 읽는다.
    /// 경로를 재생성/재매핑하지 않으며, 누락·staged·비활성·중복·퇴화 투영은
    /// 항상 명시적 실패다. 전기 사양표·전력 배분은 이 수집의 전제가 아니다.
    /// </summary>
    private List<RouteExportCircuit> CollectExportCircuits(
        IReadOnlyList<WorkingArea> areas, WorkingSideDefinition side, out string error)
    {
        error = null;
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
        if (areas == null || areas.Count == 0)
        {
            error = "내보낼 확정 회로가 없습니다.";
            return null;
        }

        var circuits = new List<RouteExportCircuit>();
        var usedNativeIds = new HashSet<int>();
        var usedAreaNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (WorkingArea area in areas)
        {
            if (string.IsNullOrEmpty(area.Name) || !usedAreaNames.Add(area.Name))
            {
                error = "영역 이름 " + (area.Name ?? "") + "이 비었거나 중복되어 내보낼 수 없습니다.";
                return null;
            }
            if (!TryValidateAreaRouteIndex(area, out error)) return null;

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
            var circuitPieces = new List<RouteExportPiece>(group.Count);
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
                circuitPieces.Add(new RouteExportPiece(
                    area.Name, index, area.NativePieceIds[index],
                    area.Pattern.RoutePieces[index].Kind,
                    area.Pattern.RoutePieces[index].LaneIndex,
                    area.RoutePieceLengths[index],
                    points, area.Color.R, area.Color.G, area.Color.B));
            }
            circuits.Add(new RouteExportCircuit(prefix + "1", circuitPieces));
        }

        if (circuits.Count == 0)
        {
            error = "내보낼 확정 회로가 없습니다.";
            return null;
        }
        return circuits;
    }

    private static bool TryValidateAreaRouteIndex(WorkingArea area, out string error)
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
        // 길이 라벨/검토 값은 확정된 실제 3D 길이를 그대로 쓴다. 표시할 수 없는 값은
        // 조용히 빼지 않고 내보내기 실패로 알린다.
        for (int index = 0; index < count; index++)
        {
            double length = area.RoutePieceLengths[index];
            if (!double.IsFinite(length) || length < 0)
            {
                error = "영역 " + area.Name + " 조각 " + (index + 1) + "의 확정 길이가 유효하지 않습니다.";
                return false;
            }
            if (area.Pattern.RoutePieces[index].Kind != SerpentineRoutePieceKind.FixedConnector
                && length <= 0)
            {
                error = "영역 " + area.Name + " 조각 " + (index + 1) + "의 확정 길이가 0입니다.";
                return false;
            }
        }
        return true;
    }
}
