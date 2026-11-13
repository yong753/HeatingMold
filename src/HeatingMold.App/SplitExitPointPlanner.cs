using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 분할로 새로 생긴 독립 영역의 시작/종료 인출점을 배정한다.
///
/// 사용자 확정 규칙:
/// 1) 시작/종료점은 분할 전 사용자가 지정한 두 점의 진행 방향 구간(가로 배치 = Y, 세로 배치 = X)
///    안에 있어야 한다.
/// 2) 두 점은 각 영역의 진행 방향 양 끝에서 margin 이상 떨어져야 한다. 가운데 영역도 같다.
/// 3) 측면(레인 진행의 좌/우)은 분할 전 시작점·종료점이 있던 방향을 그대로 물려받는다.
///    길이를 맞추기 위해 시작/종료 측면이나 시작 레인 위상을 바꾸지 않는다.
///
/// 배정은 좌표 복사가 아니라 각 영역의 실제 경계에서 provenance를 다시 구한다. 후보 행은
/// 마진이 적용된 생성 영역의 실제 레인 행에서만 고르므로, 시작점이 발열구간 안쪽에 놓이거나
/// 마진 밖으로 나가지 않는다. 어느 영역이든 유일하게 결정되지 않으면 아무 영역에도 기록하지
/// 않고 실패한다 — 임의 측면으로 대체하지 않는다. 고정 측면으로 실제 경로를 만들 수 없는
/// 절단은 호출자의 절단 탐색에서 제외된다.
/// </summary>
internal static class SplitExitPointPlanner
{
    /// <summary>경계 다각형과 원본 CAD 곡선 사이의 측면 판정 오차(작업평면 mm).</summary>
    private const double SideTolerance = 0.02;

    /// <summary>Serpentine.AnchorMatchTolerance와 같은 값. 시작/종료점이 겹치면 방향을 정할 수 없다.</summary>
    private const double MinimumPointSeparation = 1e-4;

    /// <summary>레인 행 비교 여유. 마진 영역 경계에 정확히 놓인 행을 포함한다.</summary>
    private const double RowEpsilon = 1e-6;
    // 마진 영역 경계 행은 생성기의 반개구간 스캔에서 비어 있거나 모서리 조각만 남는다.
    // 경계에서 이만큼 안쪽으로 들어간 행을 시작 행으로 쓴다(Clipper 오프셋 오차보다 크다).
    private const double RowInsetGuard = 0.01;

    /// <summary>
    /// 경계 다각형의 chord를 원본 CAD 곡선으로 투영할 때 허용하는 거리(작업평면 mm).
    /// MainWindow.ExitFace의 인출점 픽 허용치와 같은 값이다.
    /// </summary>
    private const double BoundaryProjectionTolerance = 5.0;

    /// <summary>
    /// 한 영역의 자동 인출점 배치. 시작/종료점은 모두 실제 경계 provenance를 가진다.
    /// <see cref="AnchorOffset"/>은 시작 레인 행이 기본 앵커에서 진행 방향으로 들어온 거리다.
    /// </summary>
    internal sealed record SplitExitCandidate(
        ExitBoundaryPoint Start,
        ExitBoundaryPoint End,
        bool StartLow,
        bool EndLow,
        double AnchorOffset);

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
    /// 시작점은 분할 전 시작점과 같은 측면, 종료점은 분할 전 종료점과 같은 측면을 쓴다.
    /// </summary>
    internal static bool TryAssign(WorkingArea parent, IReadOnlyList<WorkingArea> children,
        double spacing, out string failure)
    {
        if (!TryBuildCandidates(parent, children, spacing,
                out List<SplitExitCandidate> candidates, out failure))
            return false;

        for (int index = 0; index < children.Count; index++)
        {
            WorkingArea child = children[index];
            SplitExitCandidate candidate = candidates[index];
            child.SelectedStartExit = candidate.Start;
            child.SelectedEndExit = candidate.End;
            // 기존 수동 선택 경로와 같은 호환 좌표를 함께 맞춘다.
            child.StartAnchor = candidate.Start.Point;
            child.EndAnchor = candidate.End.Point;
        }
        return true;
    }

    /// <summary>
    /// 자식마다 배정할 인출점을 하나 만든다. 시작/종료 측면은 부모의 선택점에서 확정한
    /// 방향을 그대로 쓰고, 시작 행은 마진이 적용된 생성 영역의 첫 레인 행이다.
    /// 후보는 기록하지 않는다(호출자가 게시한다).
    /// </summary>
    internal static bool TryBuildCandidates(WorkingArea parent, IReadOnlyList<WorkingArea> children,
        double spacing,
        out List<SplitExitCandidate> candidates, out string failure)
    {
        candidates = new List<SplitExitCandidate>();
        failure = string.Empty;
        if (!HasAssignablePair(parent))
        {
            failure = "부모 영역의 시작/종료 인출점이 준비되지 않았습니다.";
            return false;
        }
        if (parent.Region.Count != 1 || parent.Region[0].Count < 3)
        {
            failure = "분할 전 영역이 단일 폐합 영역이 아니어서 인출 방향을 확정할 수 없습니다.";
            return false;
        }
        if (!double.IsFinite(spacing) || spacing <= 0)
        {
            failure = "분할 영역의 레인 간격이 올바르지 않습니다.";
            return false;
        }
        if (children == null)
        {
            failure = "분할 영역 목록이 없습니다.";
            return false;
        }

        ExitBoundaryPoint parentStart = parent.SelectedStartExit!.Value;
        ExitBoundaryPoint parentEnd = parent.SelectedEndExit!.Value;

        // 인출 방향은 모든 자식에서 같은 생성 방향·작업평면을 쓰므로 한 번만 확정한다.
        double radians = ResolveRadians(parent);
        if (!SplitExitSidePolicy.TryResolve(parent, radians,
                out bool startLow, out bool endLow, out failure))
            return false;

        // 모든 자식의 레이아웃(방향·유효 범위·생성 영역)을 먼저 확정한다. 하나라도 성립하지
        // 않으면 후보를 만들지 않고 전체를 실패시킨다.
        var layouts = new List<ChildLayout>(children.Count);
        foreach (WorkingArea child in children)
        {
            if (!TryPrepareLayout(child, parentStart, parentEnd, radians, spacing,
                    out ChildLayout layout, out failure))
                return false;
            layouts.Add(layout);
        }

        foreach (ChildLayout layout in layouts)
        {
            if (!TryBuildChildCandidate(layout, startLow, endLow, spacing,
                    out SplitExitCandidate candidate, out string childFailure))
            {
                failure = childFailure;
                return false;
            }
            candidates.Add(candidate);
        }
        return true;
    }

    private static double ResolveRadians(WorkingArea area)
    {
        // 생성 단계가 쓰는 방향과 같은 방식으로 해석한다(UI의 EffectiveDirection과 동일:
        // Auto는 가로/Frame X). 여기서 다른 해석을 쓰면 인출 측면과 실제 레인 방향이 어긋난다.
        PatternDirection effectiveDirection = area.Direction == PatternDirection.FrameY
            ? PatternDirection.FrameY
            : PatternDirection.FrameX;
        double angleDeg = Serpentine.ResolveAngleDeg(area.Region[0], effectiveDirection);
        return angleDeg * Math.PI / 180.0;
    }

    /// <summary>자식 하나의 고정 조건: 레인 좌표계, 허용 진행 구간, 실제 생성 영역.</summary>
    private sealed class ChildLayout
    {
        public WorkingArea Child { get; init; } = null!;
        public SplitRegionBoundary2D Boundary { get; init; } = null!;
        public double Radians { get; init; }
        public int StepSign { get; init; }
        public double ParentLow { get; init; }
        public double ParentHigh { get; init; }
        public double Spacing { get; init; }
        public List<Vec2> LocalRing { get; init; } = null!;
        public List<List<Vec2>> GenerationRegion { get; init; } = null!;
        public double OffsetMinY { get; init; }
        public double OffsetMaxY { get; init; }
        public double RowLow { get; init; }
        public double RowHigh { get; init; }
    }

    private static bool TryPrepareLayout(WorkingArea child, ExitBoundaryPoint parentStart,
        ExitBoundaryPoint parentEnd, double radians, double spacing,
        out ChildLayout layout, out string failure)
    {
        layout = null!;
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

        Vec2 parentStartLocal = parentStart.Point.Rotate(-radians);
        Vec2 parentEndLocal = parentEnd.Point.Rotate(-radians);
        double sweep = parentEndLocal.Y - parentStartLocal.Y;
        if (Math.Abs(sweep) <= 1e-9)
        {
            failure = child.Name + ": 시작점과 종료점의 진행 방향 위치가 같아 측면을 정할 수 없습니다.";
            return false;
        }
        int stepSign = sweep > 0 ? 1 : -1;
        double parentLow = Math.Min(parentStartLocal.Y, parentEndLocal.Y);
        double parentHigh = Math.Max(parentStartLocal.Y, parentEndLocal.Y);

        List<Vec2> localRing = child.Region[0].Select(point => point.Rotate(-radians)).ToList();

        // 생성 영역. 제외영역은 Y 범위를 넓히지 않으므로 범위 산출에는 쓰지 않는다.
        List<List<Vec2>> patternRegion = RegionOps.BuildPatternRegion(localRing, null);
        if (patternRegion.Count == 0)
        {
            failure = child.Name + ": 발열 영역이 남지 않습니다.";
            return false;
        }
        double offsetMinY = patternRegion.Min(ring => ring.Min(point => point.Y));
        double offsetMaxY = patternRegion.Max(ring => ring.Max(point => point.Y));
        if (!(offsetMaxY - offsetMinY > RowEpsilon))
        {
            failure = child.Name + ": 영역의 폭이 없어 레인을 만들 수 없습니다.";
            return false;
        }

        // 사용자가 지정한 두 점의 진행 방향 구간과 영역의 교집합.
        // 이 범위 밖으로는 시작/종료점을 배정하지 않는다.
        double rowLow = Math.Max(parentLow, offsetMinY);
        double rowHigh = Math.Min(parentHigh, offsetMaxY);
        if (!(rowHigh - rowLow > RowEpsilon))
        {
            failure = child.Name + ": 분할 전 시작/종료점 범위가 영역과 겹치지 않습니다.";
            return false;
        }

        // 실제 생성기가 쓰는 영역과 같은 규칙: 원시 제외 사각형을 그대로 차감한다.
        List<List<Vec2>> generationRegion = RegionOps.BuildGenerationRegion(
            localRing, RotateExclusions(child.Exclusions, radians));
        if (generationRegion.Count == 0)
        {
            failure = child.Name + ": 제외영역을 적용하면 발열 영역이 남지 않습니다.";
            return false;
        }

        layout = new ChildLayout
        {
            Child = child,
            Boundary = boundary,
            Radians = radians,
            StepSign = stepSign,
            ParentLow = parentLow,
            ParentHigh = parentHigh,
            Spacing = spacing,
            LocalRing = localRing,
            GenerationRegion = generationRegion,
            OffsetMinY = offsetMinY,
            OffsetMaxY = offsetMaxY,
            RowLow = rowLow,
            RowHigh = rowHigh,
        };
        return true;
    }

    private static bool TryBuildChildCandidate(ChildLayout layout, bool startLow, bool endLow,
        double spacing, out SplitExitCandidate candidate, out string failure)
    {
        candidate = null!;
        failure = string.Empty;
        if (!TryScanLanes(layout, spacing,
                out List<(double Y, List<(double X0, double X1)> Intervals)> lanes,
                out failure))
            return false;

        (double startRow, List<(double X0, double X1)> startIntervals) = lanes[0];
        (double endRow, List<(double X0, double X1)> endIntervals) = lanes[^1];
        if (startIntervals.Count == 0 || endIntervals.Count == 0)
        {
            failure = layout.Child.Name + ": 레인 행의 발열 구간을 찾지 못했습니다.";
            return false;
        }

        // 목표는 영역 외곽의 극값이 아니라 그 행의 실제 발열 구간 끝이다. 제외영역으로
        // 레인이 안쪽에서 끊기면 외곽 극값은 레인 끝이 아니므로 다른 구간을 고를 수 있다.
        // 시작은 실제 생성기가 허용하는 구간, 종료는 레인이 실제로 끝나는 쪽 구간을 쓴다.
        //
        // 두 생성 경로의 시작 규칙이 다르므로 하나로 합치지 않는다.
        //  - 제외영역 있음: 다중 구간 행에서 오른쪽 진입은 '가장 오른쪽 구간의 오른쪽 끝'만
        //    허용한다(Serpentine.TryBuildExcludedSequentialPieces).
        //  - 제외영역 없음: 첫 레인의 시작 구간이 항상 intervals[0]이어야 하므로(같은 파일의
        //    anchored 경로) 오른쪽 진입도 첫 구간의 오른쪽 끝을 쓴다.
        bool exclusionPath = layout.Child.Exclusions is { Count: > 0 };
        (double startX0, double startX1) = startIntervals[0];
        double startTargetX = startLow
            ? startX0
            : exclusionPath ? startIntervals[^1].X1 : startX1;
        // 종료는 두 경로 모두 레인 방출이 끝나는 쪽 구간 끝과 맞춘다.
        double endTargetX = endLow ? endIntervals[0].X0 : endIntervals[^1].X1;

        if (!TryResolveCandidate(layout, startRow, startLow, startTargetX,
                endRow, endLow, endTargetX,
                out candidate, out failure))
            return false;
        return true;
    }

    private static bool TryScanLanes(ChildLayout layout, double spacing,
        out List<(double Y, List<(double X0, double X1)> Intervals)> lanes, out string failure)
    {
        lanes = new List<(double Y, List<(double X0, double X1)> Intervals)>();
        failure = string.Empty;

        double anchor = layout.StepSign > 0 ? layout.RowLow : layout.RowHigh;

        // 마진 영역의 경계 행은 그 행에서 시작하는 변만 세는 반개구간 판정 때문에 모서리 조각만
        // 남거나 완전히 비어 있다. 그런 행을 시작 행으로 넘기면 생성기의 첫 스캔라인이 비어
        // 사행 레인 없음으로 실패하므로, 경계에서 안쪽으로 아주 조금 들어간 행부터 센다.
        if (anchor >= layout.OffsetMaxY - RowInsetGuard) anchor = layout.OffsetMaxY - RowInsetGuard;
        else if (anchor <= layout.OffsetMinY + RowInsetGuard) anchor = layout.OffsetMinY + RowInsetGuard;

        // 시작 행에서 진행 방향으로 spacing 간격으로 훑어 실제로 발열구간이 있는 행만 레인으로 센다.
        // 생성기의 스캔라인 규칙과 같은 반개구간 판정을 쓴다.
        double y = anchor;
        while (layout.StepSign > 0
                   ? y <= layout.RowHigh + RowEpsilon
                   : y >= layout.RowLow - RowEpsilon)
        {
            List<(double X0, double X1)> intervals = ScanlineIntervals(layout.GenerationRegion, y);
            if (intervals.Count > 0) lanes.Add((y, intervals));
            double next = y + layout.StepSign * spacing;
            if (next == y) break;
            y = next;
        }
        if (lanes.Count == 0)
        {
            failure = layout.Child.Name + ": 마진 적용 영역에서 발열 레인을 만들 수 없습니다.";
            return false;
        }
        return true;
    }

    private static bool TryResolveCandidate(ChildLayout layout,
        double startRow, bool startLow, double startTargetX,
        double endRow, bool endLow, double endTargetX,
        out SplitExitCandidate candidate, out string failure)
    {
        candidate = null!;
        failure = string.Empty;
        double rowMatchTolerance = Math.Min(1.0, layout.Spacing * 0.5);

        if (!TryResolveBoundaryPoint(layout.Boundary, layout.Radians, startRow, startLow,
                startTargetX, rowMatchTolerance, out ExitBoundaryPoint start, out string startError))
        {
            failure = layout.Child.Name + " 시작점: " + startError;
            return false;
        }
        if (!TryResolveBoundaryPoint(layout.Boundary, layout.Radians, endRow, endLow,
                endTargetX, rowMatchTolerance, out ExitBoundaryPoint end, out string endError))
        {
            failure = layout.Child.Name + " 종료점: " + endError;
            return false;
        }
        if (start.Point.DistanceTo(end.Point) <= MinimumPointSeparation)
        {
            failure = layout.Child.Name + ": 자동 인출점 두 개가 같은 위치가 되어 진행 방향을 정할 수 없습니다.";
            return false;
        }

        // 투영된 실제 점이 부모 선택 구간과 마진 적용 영역을 벗어나지 않는지 다시 확인한다.
        // 경계 투영 오차(최대 1 mm)는 행 판정 허용치와 같은 값을 쓴다.
        double tolerance = Math.Max(SideTolerance, rowMatchTolerance);
        if (!IsSweepCoordinateAllowed(layout, start, tolerance, out failure)) return false;
        if (!IsSweepCoordinateAllowed(layout, end, tolerance, out failure)) return false;

        double anchorOffset = layout.StepSign > 0
            ? startRow - layout.RowLow
            : layout.RowHigh - startRow;
        candidate = new SplitExitCandidate(start, end, startLow, endLow, Math.Max(0, anchorOffset));
        return true;
    }

    private static bool IsSweepCoordinateAllowed(ChildLayout layout, ExitBoundaryPoint point,
        double tolerance, out string failure)
    {
        failure = string.Empty;
        double y = point.Point.Rotate(-layout.Radians).Y;
        if (y < layout.ParentLow - tolerance || y > layout.ParentHigh + tolerance)
        {
            failure = layout.Child.Name + ": 배정한 인출점이 분할 전 시작/종료 구간을 벗어났습니다.";
            return false;
        }
        if (y < layout.OffsetMinY - tolerance || y > layout.OffsetMaxY + tolerance)
        {
            failure = layout.Child.Name + ": 배정한 인출점이 마진 적용 영역을 벗어났습니다.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 주어진 행/측면의 경계점을 <b>정확한 행 직선 교차</b>로 구한다. 최근접 투영이 아니므로
    /// 점은 배정한 격자 행 위에 그대로 놓인다. 원본 CAD는 원본 parameter identity를,
    /// 제외변·절단선은 각자의 provenance를 유지한다. 후보가 없거나 접선·겹침·모호성이면 실패한다.
    /// </summary>
    private static bool TryResolveBoundaryPoint(SplitRegionBoundary2D boundary, double radians,
        double rowLocalY, bool lowSide, double targetLocalX, double rowMatchTolerance,
        out ExitBoundaryPoint point, out string failure)
    {
        point = default;
        failure = string.Empty;

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        foreach (Vec2 ringPoint in boundary.Ring)
        {
            Vec2 local = ringPoint.Rotate(-radians);
            if (local.X < minX) minX = local.X;
            if (local.X > maxX) maxX = local.X;
        }
        if (!(maxX - minX > RowEpsilon))
        {
            failure = "배정한 레인 행에서 영역 폭을 찾지 못했습니다.";
            return false;
        }

        // 레인 방향으로 자식 외곽보다 1 mm 바깥까지 연장한 무한 직선을 쓴다.
        Vec2 a = new Vec2(minX - 1.0, rowLocalY).Rotate(radians);
        Vec2 b = new Vec2(maxX + 1.0, rowLocalY).Rotate(radians);
        var hits = new List<ExitBoundaryPoint>(8);
        ExitBoundaryFailureReason reason = boundary.IntersectLine(a, b, hits);
        if (reason != ExitBoundaryFailureReason.NoIntersection)
        {
            failure = "영역 경계와 배정한 레인 행의 교차를 계산하지 못했습니다 — " + reason;
            return false;
        }
        if (hits.Count == 0)
        {
            failure = "영역 경계와 배정한 레인 행이 만나지 않습니다.";
            return false;
        }

        var candidates = new List<(ExitBoundaryPoint Point, double X)>(hits.Count);
        foreach (ExitBoundaryPoint hit in hits)
        {
            Vec2 local = hit.Point.Rotate(-radians);
            if (Math.Abs(local.Y - rowLocalY) > RowEpsilon) continue;
            candidates.Add((hit, local.X));
        }
        if (candidates.Count == 0)
        {
            failure = "영역 경계 교차점이 배정한 레인 행 위에 있지 않습니다.";
            return false;
        }

        // 요구 측면에 있는 후보만 남긴 뒤 목표 구간 끝에 가장 가까운 후보를 고른다.
        // 측면 밖 후보로 대체하지 않으며, 같은 거리의 서로 다른 provenance는 모호성 실패다.
        double sideTolerance = Math.Max(SideTolerance, rowMatchTolerance);
        var eligible = new List<(ExitBoundaryPoint Point, double X)>(candidates.Count);
        foreach ((ExitBoundaryPoint hit, double x) in candidates)
        {
            bool onRequestedSide = lowSide
                ? x <= targetLocalX + sideTolerance
                : x >= targetLocalX - sideTolerance;
            if (onRequestedSide) eligible.Add((hit, x));
        }
        if (eligible.Count == 0)
        {
            failure = "영역 경계가 요구 측면에 있지 않습니다 — 다른 측면으로 대체하지 않습니다.";
            return false;
        }

        double bestDistance = eligible.Min(candidate => Math.Abs(candidate.X - targetLocalX));
        var tied = eligible
            .Where(candidate => Math.Abs(candidate.X - targetLocalX) - bestDistance <= RowEpsilon)
            .ToList();
        for (int i = 1; i < tied.Count; i++)
        {
            if (ExitBoundaryIdentity.Same(tied[0].Point, tied[i].Point)) continue;
            failure = "같은 위치에 서로 다른 경계 후보가 있어 인출점을 확정할 수 없습니다.";
            return false;
        }

        point = tied[0].Point;
        return true;
    }

    /// <summary>
    /// 이미 확정된 레인 행 하나에 대해, 요구 측면의 정확 경계점을 구한다. 자동 배정처럼 행을
    /// 새로 스캔하지 않고, 호출자가 확정 경로에서 읽은 행·레인 끝 좌표를 그대로 넘긴다.
    /// </summary>
    internal static bool TryResolveLaneRowPoint(SplitRegionBoundary2D boundary, double radians,
        double rowLocalY, bool lowSide, double targetLocalX, double rowMatchTolerance,
        out ExitBoundaryPoint point, out string failure)
    {
        if (boundary == null)
        {
            point = default;
            failure = "분할 영역 경계가 준비되지 않았습니다.";
            return false;
        }
        return TryResolveBoundaryPoint(boundary, radians, rowLocalY, lowSide, targetLocalX,
            rowMatchTolerance, out point, out failure);
    }

    /// <summary>
    /// Serpentine.ScanlineIntervals와 같은 반개구간([y0,y1)) 판정으로 수평선 교차를 짝지어
    /// 발열구간 목록을 만든다. 생성기와 같은 행 판정을 쓰기 위해 규칙을 그대로 유지한다.
    /// </summary>
    private static List<(double X0, double X1)> ScanlineIntervals(
        IReadOnlyList<IReadOnlyList<Vec2>> region, double y)
    {
        var xs = new List<double>();
        foreach (IReadOnlyList<Vec2> ring in region)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vec2 a = ring[i];
                Vec2 b = ring[(i + 1) % ring.Count];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                {
                    double t = (y - a.Y) / (b.Y - a.Y);
                    xs.Add(a.X + t * (b.X - a.X));
                }
            }
        }
        xs.Sort();
        var result = new List<(double X0, double X1)>(xs.Count / 2);
        for (int i = 0; i + 1 < xs.Count; i += 2)
        {
            if (xs[i + 1] - xs[i] > 1e-9) result.Add((xs[i], xs[i + 1]));
        }
        return result;
    }

    /// <summary>
    /// 제외 사각형을 레인 좌표계로 옮긴다. 생성 방향은 축 정렬(0°/90°)이므로 사각형도
    /// 축 정렬로 남는다. 네 꼭짓점을 회전해 경계 상자를 쓰는 것은 그 두 각도에서 정확하다.
    /// </summary>
    private static List<(double MinX, double MinY, double MaxX, double MaxY)> RotateExclusions(
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions, double radians)
    {
        var result = new List<(double MinX, double MinY, double MaxX, double MaxY)>(exclusions.Count);
        foreach ((double minX, double minY, double maxX, double maxY) in exclusions)
        {
            if (!(maxX > minX && maxY > minY)) continue;
            double lowX = double.PositiveInfinity, lowY = double.PositiveInfinity;
            double highX = double.NegativeInfinity, highY = double.NegativeInfinity;
            foreach (Vec2 corner in new[]
                     {
                         new Vec2(minX, minY), new Vec2(maxX, minY),
                         new Vec2(maxX, maxY), new Vec2(minX, maxY),
                     })
            {
                Vec2 local = corner.Rotate(-radians);
                if (local.X < lowX) lowX = local.X;
                if (local.X > highX) highX = local.X;
                if (local.Y < lowY) lowY = local.Y;
                if (local.Y > highY) highY = local.Y;
            }
            result.Add((lowX, lowY, highX, highY));
        }
        return result;
    }
}
