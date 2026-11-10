namespace HeatingMold.Core.Geometry;

/// <summary>
/// 꼭지점 이동을 확정된 발열 진행 방향으로 제한한다. 진행 방향과 수직인 변위를
/// 제거하므로, 이동해도 발열열 사이 간격(피치)은 생성 당시 값 그대로 유지된다.
/// 간격 값을 다시 격자에 끼워 맞추지 않고 원래 열의 수직 위치를 보존한다.
/// </summary>
public static class RouteVertexMoveConstraint
{
    /// <summary>직교 방향 판정 한계. 이보다 작은 축 성분은 삼각함수 잔차로 보고 무시한다.</summary>
    private const double AxisTolerance = 1e-12;

    /// <summary>
    /// <paramref name="original"/> 위치의 꼭지점을 <paramref name="requested"/>로 옮기려 할 때,
    /// <paramref name="angleDeg"/> 방향(발열 진행축) 성분만 남긴 좌표를 돌려준다.
    /// </summary>
    public static bool TryConstrain(Vec2 original, Vec2 requested, double angleDeg,
        out Vec2 constrained, out string failure)
    {
        constrained = original;
        failure = string.Empty;
        if (!double.IsFinite(original.X) || !double.IsFinite(original.Y))
        { failure = "이동 전 꼭지점 좌표가 유한하지 않습니다"; return false; }
        if (!double.IsFinite(requested.X) || !double.IsFinite(requested.Y))
        { failure = "이동할 꼭지점 좌표가 유한하지 않습니다"; return false; }
        if (!double.IsFinite(angleDeg))
        { failure = "발열 경로의 생성 방향이 유효하지 않습니다"; return false; }

        double radians = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        // 직교 생성은 잔차 없이 원래 성분을 그대로 유지한다.
        if (Math.Abs(sin) <= AxisTolerance)
        {
            constrained = new Vec2(requested.X, original.Y);   // 가로 생성 — 좌우만 이동
            return true;
        }
        if (Math.Abs(cos) <= AxisTolerance)
        {
            constrained = new Vec2(original.X, requested.Y);   // 세로 생성 — 상하만 이동
            return true;
        }

        double along = (requested.X - original.X) * cos + (requested.Y - original.Y) * sin;
        double x = original.X + along * cos;
        double y = original.Y + along * sin;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        { failure = "이동 결과 좌표가 유한하지 않습니다"; return false; }
        constrained = new Vec2(x, y);
        return true;
    }
}
