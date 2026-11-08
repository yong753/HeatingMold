using HeatingMold.Core.Geometry;

namespace HeatingMold.Export;

/// <summary>작업평면 기준 평면도 좌표 변환/검증.</summary>
public static class PlanViewBuilder
{
    private const double CollapseTolerance = 1e-7;

    /// <summary>
    /// HLR 결과는 월드 원점 기준 평면 2D 좌표이므로, 확정 작업평면의 원점 성분만
    /// 정확히 감산한다. 다시 3D로 되돌려 재투영하지 않는다.
    /// </summary>
    public static Vec2 FromHlr(Vec2 hlrPoint, ProjectionFrame frame) =>
        new(hlrPoint.X - Vec3.Dot(frame.Origin, frame.AxisX),
            hlrPoint.Y - Vec3.Dot(frame.Origin, frame.AxisY));

    /// <summary>
    /// 확정 3D 경로를 작업평면에 투영한다. 연속 중복점만 제거하며, 조각 전체가
    /// 법선 방향으로 붕괴하면 실패한다(부분 삭제로 성공을 가장하지 않는다).
    /// </summary>
    public static bool TryProjectRoute(IReadOnlyList<Vec3> points, ProjectionFrame frame,
        out IReadOnlyList<Vec2> projected, out string failure)
    {
        projected = Array.Empty<Vec2>();
        failure = string.Empty;
        if (frame == null) { failure = "작업평면이 없습니다"; return false; }
        if (points == null || points.Count < 2) { failure = "경로 점이 부족합니다"; return false; }

        double length3d = 0;
        for (int i = 1; i < points.Count; i++)
        {
            Vec3 segment = points[i] - points[i - 1];
            if (!double.IsFinite(segment.X) || !double.IsFinite(segment.Y) || !double.IsFinite(segment.Z))
            { failure = "3D 경로 좌표가 유한하지 않습니다"; return false; }
            length3d += segment.Length;
        }
        if (!double.IsFinite(length3d) || length3d <= CollapseTolerance)
        { failure = "3D 경로 길이가 0입니다"; return false; }

        var result = new List<Vec2>(points.Count);
        foreach (Vec3 point in points)
        {
            Vec2 projectedPoint = frame.Project(point);
            if (!double.IsFinite(projectedPoint.X) || !double.IsFinite(projectedPoint.Y))
            { failure = "투영 좌표가 유한하지 않습니다"; return false; }
            if (result.Count > 0 &&
                (projectedPoint - result[^1]).Length <= CollapseTolerance) continue;
            result.Add(projectedPoint);
        }
        if (result.Count < 2)
        {
            failure = "경로 전체가 작업평면 법선 방향이라 평면도에서 사라집니다";
            return false;
        }
        projected = result;
        return true;
    }
}
