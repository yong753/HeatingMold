using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 분할 전 선택 인출점의 진행 방향 측면(레인 좌/우)을 확정한다.
///
/// 사용자 확정 규칙: 새 영역의 인출점은 길이를 맞추기 위해 방향을 바꾸지 않는다. 분할 전
/// 시작점과 같은 측면, 종료점과 같은 측면을 그대로 물려받는다. 측면은 확정 작업평면의
/// 2D 좌표에서 그 점이 놓인 행의 실제 영역 외곽 최좌/최우를 기준으로 판정하며, 자식마다
/// 부모 좌표와의 거리를 다시 비교해 뒤집지 않는다.
/// </summary>
internal static class SplitExitSidePolicy
{
    /// <summary>측면 판정 오차(작업평면 mm). 경계 투영 오차와 같은 값을 쓴다.</summary>
    private const double SideTolerance = 0.02;

    /// <summary>
    /// 부모의 시작/종료 인출점이 레인 진행의 어느 측면인지 구한다.
    /// <paramref name="radians"/>는 확정 작업평면에서 생성 방향을 회전한 각도다.
    /// </summary>
    internal static bool TryResolve(WorkingArea parent, double radians,
        out bool startLow, out bool endLow, out string failure)
    {
        startLow = false;
        endLow = false;
        failure = string.Empty;
        if (parent?.Region is null || parent.Region.Count != 1 || parent.Region[0].Count < 3)
        {
            failure = "분할 전 영역이 단일 폐합 영역이 아니어서 인출 방향을 확정할 수 없습니다.";
            return false;
        }
        if (parent.SelectedStartExit is not ExitBoundaryPoint start
            || parent.SelectedEndExit is not ExitBoundaryPoint end)
        {
            failure = "부모 영역의 시작/종료 인출점이 준비되지 않았습니다.";
            return false;
        }
        if (!TryResolveSide(parent.Region[0], start.Point, radians, "시작", out startLow, out failure))
            return false;
        if (!TryResolveSide(parent.Region[0], end.Point, radians, "종료", out endLow, out failure))
            return false;
        return true;
    }

    private static bool TryResolveSide(IReadOnlyList<Vec2> ring, Vec2 point, double radians,
        string role, out bool low, out string failure)
    {
        low = false;
        failure = string.Empty;
        Vec2 local = point.Rotate(-radians);
        // 진행 방향 끝(영역의 위/아래 변)에 놓인 점은 그 행의 교차가 1개 이하일 수 있다.
        // 그때는 영역 전체의 최좌/최우로 측면을 판정한다 — 좌표를 복사하거나 방향을
        // 임의로 정하는 것이 아니라, 어느 쪽 외곽에 붙어 있는지를 보는 것이다.
        if (!TryRowExtremes(ring, local.Y, radians, out double minX, out double maxX)
            || Math.Abs(maxX - minX) <= SideTolerance)
        {
            var localRing = new List<Vec2>(ring.Count);
            foreach (Vec2 vertex in ring) localRing.Add(vertex.Rotate(-radians));
            minX = localRing.Min(vertex => vertex.X);
            maxX = localRing.Max(vertex => vertex.X);
        }
        if (Math.Abs(maxX - minX) <= SideTolerance)
        {
            failure = "분할 전 " + role + " 인출점의 영역 폭이 없어 인출 방향을 확정할 수 없습니다.";
            return false;
        }
        double toLow = Math.Abs(local.X - minX);
        double toHigh = Math.Abs(local.X - maxX);
        if (Math.Abs(toLow - toHigh) <= SideTolerance)
        {
            failure = "분할 전 " + role
                + " 인출점이 좌우 어느 측면인지 구분되지 않습니다 — 같은 방향을 유지하도록 분할할 수 없습니다.";
            return false;
        }
        low = toLow < toHigh;
        return true;
    }

    /// <summary>주어진 행에서 회전된 영역 외곽의 최좌/최우를 구한다.</summary>
    private static bool TryRowExtremes(IReadOnlyList<Vec2> ring, double rowLocalY, double radians,
        out double minX, out double maxX)
    {
        var xs = new List<double>();
        for (int i = 0; i < ring.Count; i++)
        {
            Vec2 a = ring[i].Rotate(-radians);
            Vec2 b = ring[(i + 1) % ring.Count].Rotate(-radians);
            if ((a.Y <= rowLocalY && b.Y > rowLocalY) || (b.Y <= rowLocalY && a.Y > rowLocalY))
            {
                double t = (rowLocalY - a.Y) / (b.Y - a.Y);
                xs.Add(a.X + t * (b.X - a.X));
            }
        }
        if (xs.Count < 2)
        {
            minX = 0;
            maxX = 0;
            return false;
        }
        xs.Sort();
        minX = xs[0];
        maxX = xs[^1];
        return true;
    }
}
