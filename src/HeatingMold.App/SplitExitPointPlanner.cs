using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 분할로 새로 생긴 독립 영역에 부모 인출점의 <b>측면</b>을 물려준다. 부모의 시작/종료점이
/// 있던 쪽(사행 진행의 시작 측면과 종료 측면)을 각 영역의 같은 측면으로 옮기고, 위치(레인 방향)는
/// 부모 점의 상대 위치를 그대로 가져간다. 좌표를 복사하지 않고 각 영역의 원본 경계에서 정확한
/// 점을 다시 구하므로, 저장되는 provenance는 항상 그 영역 자신의 edge/제외 변/절단선이다.
///
/// 우선순위:
/// 1) 부모 점이 이 영역의 같은 측면 위에 그대로 있으면 원본 parameter를 유지한다.
/// 2) 그 측면이 이 영역의 새 절단선이면 절단선 provenance로 점을 만든다.
/// 3) 같은 원본 edge/제외 변 위에서 측면을 따라 위치만 옮긴다(다른 edge로 갈아타지 않는다).
/// 4) 어느 경우도 유일하게 결정되지 않으면 명시적으로 실패한다 — 임의 측면으로 대체하지 않는다.
///
/// 모든 영역의 점이 준비된 뒤에만 자식 영역에 기록한다. 하나라도 실패하면 자식 영역은
/// 선택점이 없는 상태로 남고, 호출자는 분할 자체를 확정하지 않는다.
/// </summary>
internal static class SplitExitPointPlanner
{
    /// <summary>경계 다각형과 원본 CAD 곡선 사이의 측면 판정 오차(작업평면 mm).</summary>
    private const double SideTolerance = 0.02;

    /// <summary>같은 측면에서 위치를 옮길 때 쓰는 최소 투영 허용치(작업평면 mm).</summary>
    private const double MinProjectionAllowance = 1.0;

    /// <summary>측면을 따라 이동해도 되는 최소 거리(작업평면 mm).</summary>
    private const double SideAllowanceFloor = 0.5;

    /// <summary>측면 이동 허용 거리 = 영역 폭 × 이 비율. 반대편으로 넘어가는 이동은 거부된다.</summary>
    private const double SideAllowanceFraction = 0.25;

    /// <summary>Serpentine.AnchorMatchTolerance와 같은 값. 시작/종료점이 겹치면 방향을 정할 수 없다.</summary>
    private const double MinimumPointSeparation = 1e-4;

    /// <summary>부모가 측면을 물려줄 수 있는 시작/종료점 쌍을 가지고 있는지.</summary>
    internal static bool HasAssignablePair(WorkingArea area)
    {
        if (area?.SelectedStartExit is not ExitBoundaryPoint start
            || area.SelectedEndExit is not ExitBoundaryPoint end) return false;
        return HasSingleProvenance(start) && HasSingleProvenance(end)
            && start.Point.DistanceTo(end.Point) > MinimumPointSeparation;
    }

    private static bool HasSingleProvenance(ExitBoundaryPoint point)
    {
        int kinds = (point.Binding.HasValue ? 1 : 0)
            + (point.ArtificialBinding.HasValue ? 1 : 0)
            + (point.SplitBinding.HasValue ? 1 : 0);
        return kinds == 1;
    }

    /// <summary>
    /// 모든 자식 영역의 시작/종료점을 먼저 계산한 뒤 한 번에 기록한다.
    /// </summary>
    internal static bool TryAssign(WorkingArea parent, IReadOnlyList<WorkingArea> children,
        out string failure)
    {
        failure = string.Empty;
        if (!HasAssignablePair(parent))
        {
            failure = "부모 영역의 시작/종료 인출점이 준비되지 않았습니다.";
            return false;
        }

        ExitBoundaryPoint parentStart = parent.SelectedStartExit!.Value;
        ExitBoundaryPoint parentEnd = parent.SelectedEndExit!.Value;

        var assignments = new List<(WorkingArea Child, ExitBoundaryPoint Start, ExitBoundaryPoint End)>();
        foreach (WorkingArea child in children)
        {
            if (!TryResolveChild(child, parentStart, parentEnd,
                    out ExitBoundaryPoint start, out ExitBoundaryPoint end, out failure))
                return false;
            assignments.Add((child, start, end));
        }

        foreach ((WorkingArea child, ExitBoundaryPoint start, ExitBoundaryPoint end) in assignments)
        {
            child.SelectedStartExit = start;
            child.SelectedEndExit = end;
            // 기존 수동 선택 경로와 같은 호환 좌표를 함께 맞춘다.
            child.StartAnchor = start.Point;
            child.EndAnchor = end.Point;
        }
        return true;
    }

    private static bool TryResolveChild(WorkingArea child, ExitBoundaryPoint parentStart,
        ExitBoundaryPoint parentEnd, out ExitBoundaryPoint start, out ExitBoundaryPoint end,
        out string failure)
    {
        start = default;
        end = default;
        failure = string.Empty;

        if (child == null)
        {
            failure = "분할 영역이 없습니다.";
            return false;
        }
        if (child.Region.Count != 1 || child.Region[0].Count < 3)
        {
            failure = child.Name + ": 적용영역이 단일 폐합 영역이 아닙니다.";
            return false;
        }
        if (child.RegionExitBoundary is not SplitRegionBoundary2D boundary)
        {
            failure = child.Name + ": 분할 영역 경계가 준비되지 않았습니다.";
            return false;
        }

        // 생성 단계가 쓰는 방향과 같은 방식으로 해석한다(UI의 EffectiveDirection과 동일:
        // Auto는 가로/Frame X). 여기서 다른 해석을 쓰면 인출 측면과 실제 레인 방향이 어긋난다.
        PatternDirection effectiveDirection = child.Direction == PatternDirection.FrameY
            ? PatternDirection.FrameY
            : PatternDirection.FrameX;
        double angleDeg = Serpentine.ResolveAngleDeg(child.Region[0], effectiveDirection);
        double radians = angleDeg * Math.PI / 180.0;
        Vec2 startLocal = parentStart.Point.Rotate(-radians);
        Vec2 endLocal = parentEnd.Point.Rotate(-radians);
        double sweep = endLocal.Y - startLocal.Y;
        if (Math.Abs(sweep) <= 1e-9)
        {
            failure = child.Name + ": 시작점과 종료점의 진행 방향 위치가 같아 측면을 정할 수 없습니다.";
            return false;
        }
        int stepSign = sweep > 0 ? 1 : -1;

        List<Vec2> localRing = child.Region[0].Select(point => point.Rotate(-radians)).ToList();
        double minY = localRing.Min(point => point.Y);
        double maxY = localRing.Max(point => point.Y);
        double span = maxY - minY;
        if (!(span > SideTolerance))
        {
            failure = child.Name + ": 분할 영역의 폭이 없어 인출 측면을 정할 수 없습니다.";
            return false;
        }
        double sideAllowance = Math.Max(SideAllowanceFloor, span * SideAllowanceFraction);
        double startSide = stepSign > 0 ? minY : maxY;
        double endSide = stepSign > 0 ? maxY : minY;
        if (Math.Abs(endSide - startSide) <= SideTolerance)
        {
            failure = child.Name + ": 시작 측면과 종료 측면이 같아 사행을 만들 수 없습니다.";
            return false;
        }

        if (!TryResolvePoint(boundary, radians, new Vec2(startLocal.X, startSide), startSide,
                sideAllowance, parentStart, out start, out string startError))
        {
            failure = child.Name + " 시작점: " + startError;
            return false;
        }
        if (!TryResolvePoint(boundary, radians, new Vec2(endLocal.X, endSide), endSide,
                sideAllowance, parentEnd, out end, out string endError))
        {
            failure = child.Name + " 종료점: " + endError;
            return false;
        }
        if (start.Point.DistanceTo(end.Point) <= MinimumPointSeparation)
        {
            failure = child.Name + ": 자동 인출점 두 개가 같은 위치가 되어 진행 방향을 정할 수 없습니다.";
            return false;
        }
        return true;
    }

    private static bool TryResolvePoint(SplitRegionBoundary2D boundary, double radians,
        Vec2 targetLocal, double sideLocalY, double sideAllowance, ExitBoundaryPoint parentPoint,
        out ExitBoundaryPoint point, out string failure)
    {
        point = default;
        failure = string.Empty;
        Vec2 target = targetLocal.Rotate(radians);

        // 1) 부모 점이 이 영역의 같은 측면 위에 그대로 남아 있으면 원본 parameter를 유지한다.
        ExitBoundaryResolution reused = ExitBoundaryConstraint.EvaluateSaved(boundary, parentPoint);
        if (reused.Succeeded && reused.Point is { } reusedPoint
            && Math.Abs(reusedPoint.Point.Rotate(-radians).Y - sideLocalY) <= SideTolerance)
        {
            point = reusedPoint;
            return true;
        }

        double projectionAllowance = Math.Max(MinProjectionAllowance, Diagonal(boundary.Ring));

        // 2) 그 측면이 이 영역의 절단선이면 절단선 자신의 구간으로 점을 만든다.
        foreach (SplitRegionBoundary2D.SplitSegment segment in boundary.Segments)
        {
            if (!IsOnSide(segment.A, segment.B, radians, sideLocalY)) continue;
            ExitBoundaryResolution cut = boundary.ProjectSplitLine(segment.Binding, target, projectionAllowance);
            if (!cut.Succeeded || cut.Point is not { } cutPoint)
            {
                failure = "절단선 위 인출점을 정할 수 없습니다 — "
                    + (cut.Failure?.ToString() ?? nameof(ExitBoundaryFailureReason.GeometryFailure));
                return false;
            }
            point = cutPoint;
            return true;
        }

        // 3) 같은 원본 edge/제외 변 위에서 측면을 따라 위치만 옮긴다.
        ExitBoundaryResolution projected = parentPoint.Binding is ExitEdgeBinding cadBinding
            ? boundary.ProjectBound(cadBinding, target, projectionAllowance)
            : parentPoint.ArtificialBinding is ArtificialEdgeBinding artificialBinding
                ? boundary.ProjectArtificial(artificialBinding, target, projectionAllowance)
                : parentPoint.SplitBinding is SplitLineBinding splitBinding
                    ? boundary.ProjectSplitLine(splitBinding, target, projectionAllowance)
                    : ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
        if (!projected.Succeeded || projected.Point is not { } candidate)
        {
            failure = "같은 측면의 원본 경계에서 인출점을 찾지 못했습니다 — "
                + (projected.Failure?.ToString() ?? nameof(ExitBoundaryFailureReason.MissingReference));
            return false;
        }

        double moved = Math.Abs(candidate.Point.Rotate(-radians).Y - sideLocalY);
        if (moved > sideAllowance)
        {
            failure = string.Format(CultureInfo.CurrentCulture,
                "같은 측면으로 옮길 수 없습니다 — 측면에서 {0:F1} mm 벗어났습니다.", moved);
            return false;
        }
        point = candidate;
        return true;
    }

    private static bool IsOnSide(Vec2 a, Vec2 b, double radians, double sideLocalY)
    {
        double aY = a.Rotate(-radians).Y;
        double bY = b.Rotate(-radians).Y;
        return Math.Abs(aY - sideLocalY) <= SideTolerance
            && Math.Abs(bY - sideLocalY) <= SideTolerance;
    }

    private static double Diagonal(IReadOnlyList<Vec2> ring)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (Vec2 point in ring)
        {
            if (point.X < minX) minX = point.X;
            if (point.X > maxX) maxX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.Y > maxY) maxY = point.Y;
        }
        double dx = maxX - minX, dy = maxY - minY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
