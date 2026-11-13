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
        // 제외영역이 외곽을 깎으면 원본 Region의 좌우 극값은 실제 발열 구간의 끝이 아니다.
        // 예를 들어 원래 0~100인 행이 제외로 60~100만 남으면 x=60은 원본 극값 기준으로는
        // 오른쪽(최우에 가까움)이지만 실제로는 남은 구간의 왼쪽 끝이다. 인출 측면은 실제
        // 발열영역(적용영역 − 제외영역)의 같은 행 극값으로만 판정한다.
        List<List<Vec2>> effective = parent.EffectiveRegion();
        if (effective.Count != 1 || effective[0].Count < 3)
        {
            failure = "분할 전 영역이 단일 폐합 영역이 아니어서 인출 방향을 확정할 수 없습니다.";
            return false;
        }
        if (!TryResolveSide(effective[0], start.Point, radians, "시작", out startLow, out failure))
            return false;
        if (!TryResolveSide(effective[0], end.Point, radians, "종료", out endLow, out failure))
            return false;
        return true;
    }

    private static bool TryResolveSide(IReadOnlyList<Vec2> ring, Vec2 point, double radians,
        string role, out bool low, out string failure)
    {
        low = false;
        failure = string.Empty;
        Vec2 local = point.Rotate(-radians);
        // 생성기는 레인을 그 행에서 '점이 속한 구간'의 끝에서 시작·종료한다. 행 전체 최좌/최우만
        // 비교하면 다중 구간 행의 내부 구간 끝(예: [0,100]의 x=100)을 오른쪽이 아니라 왼쪽으로
        // 오판하고, 그 결과 자식이 부모와 반대 측면에서 시작한다. 먼저 점이 놓인 구간의 두 끝과
        // 비교하고, 어느 끝도 아니면(진행 방향 끝에 놓인 점 등) 아래의 극값 비교로 돌아간다.
        if (TryResolveWithinInterval(ring, local, radians, out bool intervalLow))
        {
            low = intervalLow;
            return true;
        }
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
        CollectRowCrossings(ring, rowLocalY, radians, xs);
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

    /// <summary>
    /// 점이 놓인 행 구간의 양 끝과 비교해 측면을 정한다. 구간 끝이 아니면 false를 돌려
    /// 호출자가 기존 극값 비교로 처리하게 한다.
    /// </summary>
    private static bool TryResolveWithinInterval(IReadOnlyList<Vec2> ring, Vec2 local, double radians,
        out bool low)
    {
        low = false;
        var xs = new List<double>();
        CollectRowCrossings(ring, local.Y, radians, xs);
        if (xs.Count < 2) return false;
        xs.Sort();
        for (int i = 0; i + 1 < xs.Count; i += 2)
        {
            double x0 = xs[i];
            double x1 = xs[i + 1];
            if (local.X < x0 - SideTolerance || local.X > x1 + SideTolerance) continue;
            if (Math.Abs(local.X - x0) <= SideTolerance) { low = true; return true; }
            if (Math.Abs(local.X - x1) <= SideTolerance) { low = false; return true; }
            return false;
        }
        return false;
    }

    /// <summary>주어진 행과 회전된 영역 외곽 변의 교차 X를 모두 모은다.</summary>
    private static void CollectRowCrossings(IReadOnlyList<Vec2> ring, double rowLocalY, double radians,
        List<double> xs)
    {
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
    }
}
