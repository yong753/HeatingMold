using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Export;

/// <summary>
/// DXF 평면도에 그릴 발열영역 경계를 만든다. 범위는 마진을 적용하지 않은
/// 적용영역 − 제외영역이며, 앱에서 영역 외곽선으로 보이는 경계와 같다.
/// 내부 구멍/영역 분리는 이미 영역 확정 단계에서 거부되므로 여기서도
/// 단일 연결 링이 아니면 명시적으로 실패한다.
/// </summary>
public static class PlanHeatAreaBuilder
{
    private const double DuplicateTolerance = 1e-7;
    private const double FrameTolerance = 1e-6;
    private const double MinimumArea = 1e-9;

    public static bool TryBuild(
        string areaName,
        IReadOnlyList<IReadOnlyList<Vec2>> region,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)>? exclusions,
        ProjectionFrame areaFrame,
        ProjectionFrame frame,
        [NotNullWhen(true)] out DxfPolyline? ring,
        [NotNullWhen(false)] out string? error)
    {
        ring = null;
        error = null;

        string label = string.IsNullOrEmpty(areaName) ? "이름 없는 영역" : areaName;
        if (areaFrame == null || frame == null || !SameFrame(areaFrame, frame))
        {
            error = "영역 " + label + "의 작업평면이 평면도와 일치하지 않습니다.";
            return false;
        }
        if (region == null || region.Count != 1 || region[0] == null)
        {
            error = "영역 " + label + "의 적용영역 외곽이 단일 링이 아닙니다.";
            return false;
        }
        if (!AllFinite(region[0]))
        {
            error = "영역 " + label + "의 적용영역 좌표가 유한하지 않습니다.";
            return false;
        }

        List<Vec2> source;
        if (exclusions == null || exclusions.Count == 0)
        {
            source = NormalizeRing(region[0]);
        }
        else
        {
            List<List<Vec2>> effective;
            try
            {
                effective = RegionOps.SubtractRectangles(region, exclusions);
            }
            catch (Exception ex)
            {
                error = "영역 " + label + "의 발열영역 계산에 실패했습니다: " + ex.Message;
                return false;
            }
            if (effective == null || effective.Count != 1 || effective[0] == null)
            {
                error = "영역 " + label + "의 발열영역이 단일 연결 영역이 아닙니다"
                    + " (제외영역으로 구멍이나 분리가 생겼습니다).";
                return false;
            }
            if (!AllFinite(effective[0]))
            {
                error = "영역 " + label + "의 발열영역 좌표가 유한하지 않습니다.";
                return false;
            }
            source = NormalizeRing(effective[0]);
        }

        if (source.Count < 3)
        {
            error = "영역 " + label + "의 발열영역 경계가 퇴화했습니다.";
            return false;
        }
        double areaValue = RegionOps.SignedArea(source);
        if (!double.IsFinite(areaValue) || Math.Abs(areaValue) <= MinimumArea)
        {
            error = "영역 " + label + "의 발열영역 면적이 0입니다.";
            return false;
        }

        ring = new DxfPolyline { Points = source, Closed = true };
        return true;
    }

    private static List<Vec2> NormalizeRing(IReadOnlyList<Vec2> source)
    {
        var points = new List<Vec2>(source.Count);
        foreach (Vec2 point in source)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            if (points.Count > 0 && points[^1].DistanceTo(point) <= DuplicateTolerance) continue;
            points.Add(point);
        }
        if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= DuplicateTolerance)
            points.RemoveAt(points.Count - 1);
        return points;
    }

    private static bool AllFinite(IReadOnlyList<Vec2> points)
    {
        foreach (Vec2 point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        }
        return true;
    }

    private static bool SameFrame(ProjectionFrame a, ProjectionFrame b)
    {
        if (ReferenceEquals(a, b)) return true;
        return (a.Origin - b.Origin).Length <= FrameTolerance
            && (a.AxisX - b.AxisX).Length <= FrameTolerance
            && (a.AxisY - b.AxisY).Length <= FrameTolerance;
    }
}
