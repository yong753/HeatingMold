using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>화면 길이 라벨의 고정 문자 높이(화면 픽셀).</summary>
    private const double RouteLengthLabelScreenHeight = 16.0;

    /// <summary>
    /// 표시 중인 모든 확정 영역의 길이 라벨을 한 번에 교체한다.
    /// 확정 3D 체인과 확정 3D 길이만 사용하며 경로를 재생성·재매핑하지 않는다.
    /// 라벨은 표시 전용이므로 실패해도 경로 형상은 되돌리지 않는다.
    /// </summary>
    private bool TryReplaceRouteLengthLabels(WorkingSideDefinition side, out string failure)
    {
        failure = null;
        if (side == null || side.Id.Value <= 0)
        {
            failure = "확정된 작업평면이 없습니다.";
            return false;
        }

        var sources = new List<RouteLengthLabelSource>();
        var areaColors = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.Ordinal);

        foreach (WorkingArea area in _areas)
        {
            if (area == null || !area.PatternDisplayed) continue;
            SerpentineResult pattern = area.Pattern;
            if (pattern == null || pattern.RoutePieces.Count == 0) continue;
            if (area.NativePieceIds == null || area.RoutePieceLengths == null ||
                area.NativePieceIds.Length != pattern.RoutePieces.Count ||
                area.RoutePieceLengths.Length != pattern.RoutePieces.Count)
            {
                failure = "영역 " + area.Name + "의 조각/네이티브 ID/길이 대응이 일치하지 않습니다.";
                return false;
            }
            if (areaColors.ContainsKey(area.Name))
            {
                failure = "영역 이름 " + area.Name + "이 중복되어 길이 라벨 대상을 구분할 수 없습니다.";
                return false;
            }
            areaColors[area.Name] = (area.Color.R, area.Color.G, area.Color.B);

            double[][] chains = Bridge.GetCommittedRouteChains(
                side.Id.Value, side.Revision.Value, area.NativePieceIds);
            if (chains == null || chains.Length != area.NativePieceIds.Length)
            {
                failure = "영역 " + area.Name + "의 확정 발열경로를 읽지 못했습니다.";
                return false;
            }

            for (int index = 0; index < pattern.RoutePieces.Count; index++)
            {
                double[] chain = chains[index];
                if (chain == null || chain.Length < 6 || chain.Length % 3 != 0)
                {
                    failure = "영역 " + area.Name + " 조각 " + (index + 1) + "의 확정 좌표가 없습니다.";
                    return false;
                }
                var points3D = new List<Vec3>(chain.Length / 3);
                for (int k = 0; k + 2 < chain.Length; k += 3)
                {
                    var point = new Vec3(chain[k], chain[k + 1], chain[k + 2]);
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
                        || !double.IsFinite(point.Z))
                    {
                        failure = "영역 " + area.Name + " 조각 " + (index + 1)
                            + "의 확정 좌표가 유한하지 않습니다.";
                        return false;
                    }
                    points3D.Add(point);
                }

                // 확정 3D 체인과 1:1로 대응하는 작업평면 투영. 점을 합치거나 다시 매핑하지 않는다.
                var points2D = new List<Vec2>(points3D.Count);
                foreach (Vec3 point in points3D) points2D.Add(side.Frame.Project(point));

                SerpentineRoutePiece piece = pattern.RoutePieces[index];
                sources.Add(new RouteLengthLabelSource(
                    area.Name, index, piece.Kind, piece.LaneIndex,
                    area.RoutePieceLengths[index], points2D, points3D));
            }
        }

        if (sources.Count == 0)
        {
            Bridge.ClearRouteLengthLabels();
            return true;
        }

        var options = new RouteLengthLabelOptions { OffsetMm = 6.0, TextHeightMm = 10.0 };
        if (!RouteLengthLabels.TryBuild(sources, side.Frame, options,
                out List<RouteLengthLabel> labels, out string labelFailure))
        {
            failure = "경로 길이 라벨을 배치하지 못했습니다: " + labelFailure;
            return false;
        }

        var anchors = new double[labels.Count * 3];
        var texts = new string[labels.Count];
        var rgb = new double[labels.Count * 3];
        for (int i = 0; i < labels.Count; i++)
        {
            RouteLengthLabel label = labels[i];
            if (label.Position3D == null)
            {
                failure = "영역 " + label.AreaName + " 조각 " + (label.PieceIndex + 1)
                    + "의 3D 라벨 위치가 없습니다.";
                return false;
            }
            Vec3 anchor = label.Position3D.Value;
            anchors[i * 3] = anchor.X;
            anchors[i * 3 + 1] = anchor.Y;
            anchors[i * 3 + 2] = anchor.Z;
            texts[i] = label.Text;
            (byte R, byte G, byte B) color = BlendLabelColor(areaColors[label.AreaName]);
            rgb[i * 3] = color.R / 255.0;
            rgb[i * 3 + 1] = color.G / 255.0;
            rgb[i * 3 + 2] = color.B / 255.0;
        }

        if (!Bridge.ReplaceRouteLengthLabels(side.Id.Value, side.Revision.Value,
                anchors, texts, rgb, RouteLengthLabelScreenHeight))
        {
            failure = "화면 길이 라벨 갱신에 실패했습니다.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 영역 색은 어두운 뷰포트 배경에서 진한 색이 묻히므로 흰색 쪽으로 밝게 섞어
    /// 라벨을 읽을 수 있게 한다. 표시 색일 뿐 경로 색이나 형상을 바꾸지 않는다.
    /// </summary>
    private static (byte R, byte G, byte B) BlendLabelColor((byte R, byte G, byte B) color) =>
        ((byte)(color.R + (255 - color.R) * 0.55),
         (byte)(color.G + (255 - color.G) * 0.55),
         (byte)(color.B + (255 - color.B) * 0.55));
}
