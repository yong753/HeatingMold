using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// 분할 자동 인출점: 시작/종료점은 분할 전 부모의 두 점이 정한 진행 방향 구간 안에서,
/// 각 영역의 마진이 적용된 생성 영역의 실제 레인 행에 놓인다. 좌표를 복사하지 않고
/// 그 영역 자신의 경계 provenance(절단선/원본 edge)로 다시 만든다.
/// CAD edge 참조는 네이티브 모델이 필요하므로, 이웃 분할의 절단선이 측면을 이루는
/// 중첩 분할로 검사한다.
/// </summary>
public class SplitExitPointPlannerTests
{
    private const double Margin = 5;
    private const double Spacing = 10;

    private static readonly Vec2[] ParentRing =
    {
        new(0, 0), new(300, 0), new(300, 300), new(0, 300)
    };

    private static readonly (double MinX, double MinY, double MaxX, double MaxY)[] NoExclusions =
        Array.Empty<(double, double, double, double)>();

    private static readonly CadExitBoundary2D NoCad =
        new(1, 1, Array.Empty<ExitEdgeReference>());

    private static (Vec2 A, Vec2 B) VerticalCut(double x) => (new Vec2(x, -20), new Vec2(x, 320));

    private static (Vec2 A, Vec2 B) HorizontalCut(double y) => (new Vec2(-20, y), new Vec2(320, y));

    private static SplitRegionBoundary2D CreateBoundary(IReadOnlyList<Vec2> parentRing,
        IReadOnlyList<Vec2> region, IReadOnlyList<(Vec2 A, Vec2 B)> cuts)
    {
        bool created = SplitRegionBoundary2D.TryCreate(NoCad, parentRing, region, NoExclusions, cuts,
            out SplitRegionBoundary2D boundary, out string failure);
        Assert.True(created, failure);
        return boundary;
    }

    private static WorkingArea CreateArea(string name, IReadOnlyList<Vec2> ring,
        SplitRegionBoundary2D boundary)
    {
        var area = new WorkingArea
        {
            Name = name,
            Direction = PatternDirection.FrameX,
            RegionExitBoundary = boundary,
            ExitBoundary = boundary,
        };
        area.Region.Add(ring.ToList());
        // 실제 분할 영역처럼 부모의 인출 체인/원본 참조를 물려받은 상태로 둔다.
        area.ExitChain.Add(new Vec2(0, 0));
        area.ExitChain.Add(new Vec2(300, 0));
        area.ExitEdgeRefs.Add(new ExitEdgeReference());
        return area;
    }

    private static ExitBoundaryPoint ProjectOnCut(SplitRegionBoundary2D boundary,
        SplitLineBinding binding, double x, double y)
    {
        ExitBoundaryResolution resolution = boundary.ProjectSplitLine(binding, new Vec2(x, y), 500);
        Assert.True(resolution.Succeeded, resolution.Failure?.ToString());
        return resolution.Point!.Value;
    }

    /// <summary>
    /// 세로 3분할의 가운데 영역을 다시 가로 2분할한다. 이웃 분할의 세로 절단선이 자식의
    /// 좌/우 측면이 되므로, 네이티브 CAD 없이도 레인 양 끝의 provenance를 검사할 수 있다.
    /// </summary>
    private static (WorkingArea Parent, List<WorkingArea> Children, SplitLineBinding Left,
        SplitLineBinding Right, double LowerMinY, double LowerMaxY) CreateNestedSplit(
        double margin, double spacing, double parentStartY, double parentEndY)
    {
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameY, 0, 20, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts.Select(VerticalCut).ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts);
        Assert.Equal(2, middle.Segments.Count);

        SplitLineBinding left = middle.Segments.OrderBy(segment => segment.A.X).First().Binding;
        SplitLineBinding right = middle.Segments.OrderBy(segment => segment.A.X).Last().Binding;
        WorkingArea parent = CreateArea("P", middleRing, middle);
        parent.SelectedStartExit = ProjectOnCut(middle, left, left.AX, parentStartY);
        parent.SelectedEndExit = ProjectOnCut(middle, right, right.AX, parentEndY);
        parent.StartAnchor = parent.SelectedStartExit.Value.Point;
        parent.EndAnchor = parent.SelectedEndExit.Value.Point;
        parent.SplitCutLines.AddRange(firstCuts);

        AreaSplitResult second = AreaSplitter.Split(middleRing, NoExclusions,
            PatternDirection.FrameX, margin, spacing, 2);
        Assert.True(second.Succeeded, second.Failure);
        List<(Vec2 A, Vec2 B)> allCuts = firstCuts
            .Concat(new[] { HorizontalCut(second.Cuts.Single()) })
            .ToList();

        var children = new List<WorkingArea>();
        foreach (AreaSplitBand band in second.Bands)
            children.Add(CreateArea("P-" + band.Index, band.Outer,
                CreateBoundary(middleRing, band.Outer, allCuts)));

        double lowerMinY = children[0].Region[0].Min(point => point.Y);
        double lowerMaxY = children[0].Region[0].Max(point => point.Y);
        return (parent, children, left, right, lowerMinY, lowerMaxY);
    }

    /// <summary>
    /// 부모 구간과 마진 안쪽 범위의 교집합에서 spacing 간격으로 놓이는 첫/마지막 레인 행.
    /// (가로 배치 기준. 세로 배치는 같은 계산이 X축에 적용된다.)
    /// </summary>
    private static (double Start, double End) ExpectedRows(double min, double max,
        double parentLow, double parentHigh, double margin, double spacing)
    {
        double low = Math.Max(min + margin, parentLow);
        double high = Math.Min(max - margin, parentHigh);
        Assert.True(high - low > 1e-6, "부모 구간과 마진 영역이 겹쳐야 한다.");
        int lanes = (int)Math.Floor((high - low) / spacing + 1e-9) + 1;
        return (low, low + (lanes - 1) * spacing);
    }

    [Fact]
    public void NestedSplit_KeepsMarginAndParentRangeOnEveryChild()
    {
        (WorkingArea parent, List<WorkingArea> children, SplitLineBinding left, SplitLineBinding right,
            _, _) = CreateNestedSplit(Margin, Spacing, 20, 280);

        Assert.True(SplitExitPointPlanner.HasAssignablePair(parent),
            "부모가 시작/종료점 쌍을 가지고 있어야 자동 지정 대상이다.");
        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Margin, Spacing,
            out string failure), failure);

        // 두 점의 진행 방향 구간: 부모가 지정한 20..280 안에서, 각 영역의 마진 안쪽 행만 쓴다.
        // 레인은 시작 행에서 spacing 간격으로만 놓이므로 마지막 행은 마진 끝에 정확히 닿지 않을 수 있다.
        WorkingArea lower = children[0];
        double lowerMin = children[0].Region[0].Min(point => point.Y);
        double lowerMax = children[0].Region[0].Max(point => point.Y);
        (double lowerStart, double lowerEnd) =
            ExpectedRows(lowerMin, lowerMax, 20, 280, Margin, Spacing);
        Assert.True(lowerStart >= lowerMin + Margin - 1e-6);
        Assert.True(lowerEnd <= lowerMax - Margin + 1e-6);

        // 시작 행은 마진 밴드 경계에 정확히 닿으면 안쪽으로 0.01 mm 들어간다.
        Assert.True(Math.Abs(lowerStart - lower.SelectedStartExit!.Value.Point.Y) <= 0.02);
        Assert.True(Math.Abs(lowerEnd - lower.SelectedEndExit!.Value.Point.Y) <= 0.02);
        // 시작은 부모 시작점과 같은 쪽(왼쪽)에서, 종료는 마지막 레인이 끝나는 쪽에서 얻는다.
        Assert.Equal(left.AX, lower.SelectedStartExit.Value.Point.X, 6);
        Assert.Equal(left.AX, lower.SelectedStartExit.Value.SplitBinding!.Value.AX, 6);
        Assert.Equal(right.AX, lower.SelectedEndExit.Value.Point.X, 6);
        Assert.Equal(right.AX, lower.SelectedEndExit.Value.SplitBinding!.Value.AX, 6);

        // 위 영역도 자기 마진 안쪽 행에서 시작/종료한다.
        WorkingArea upper = children[1];
        double upperMin = upper.Region[0].Min(point => point.Y);
        double upperMax = upper.Region[0].Max(point => point.Y);
        (double upperStart, double upperEnd) =
            ExpectedRows(upperMin, upperMax, 20, 280, Margin, Spacing);
        Assert.True(Math.Abs(upperStart - upper.SelectedStartExit!.Value.Point.Y) <= 0.02);
        Assert.True(Math.Abs(upperEnd - upper.SelectedEndExit!.Value.Point.Y) <= 0.02);

        foreach (WorkingArea child in children)
        {
            Assert.True(child.StartAnchor.HasValue && child.EndAnchor.HasValue);
            Assert.Equal(child.SelectedStartExit!.Value.Point, child.StartAnchor!.Value);
            Assert.Equal(child.SelectedEndExit!.Value.Point, child.EndAnchor!.Value);
            Assert.True(child.HasSelectedExits, child.Name + ": 자동 지정점이 생성 준비 상태여야 한다.");
            // 배정점은 그 영역의 실제 경계 위에 있어야 한다.
            var boundary = (SplitRegionBoundary2D)child.RegionExitBoundary!;
            Assert.True(boundary.Project(child.SelectedStartExit.Value.Point, 1e-3).Succeeded);
            Assert.True(boundary.Project(child.SelectedEndExit.Value.Point, 1e-3).Succeeded);
        }
    }

    [Fact]
    public void ParentRangeClampsChildRows()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, double lowerMinY, double lowerMaxY) =
            CreateNestedSplit(Margin, Spacing, 40, 60);

        Assert.True(SplitExitPointPlanner.TryAssign(parent, new[] { children[0] }, Margin, Spacing,
            out string failure), failure);

        // 부모 구간(40..60)이 마진 안쪽 범위(5..145)보다 좁으므로 부모 구간이 이긴다.
        Assert.True(lowerMinY + Margin < 40);
        Assert.True(lowerMaxY - Margin > 60);
        Assert.Equal(40, children[0].SelectedStartExit!.Value.Point.Y, 6);
        Assert.Equal(60, children[0].SelectedEndExit!.Value.Point.Y, 6);
    }

    [Fact]
    public void VerticalLayout_UsesXAxisRange()
    {
        // 가로 3분할의 가운데 영역을 다시 세로 2분할한다(진행 방향 = X).
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameX, 0, 20, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts.Select(HorizontalCut).ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts);

        SplitLineBinding bottom = middle.Segments.OrderBy(segment => segment.A.Y).First().Binding;
        SplitLineBinding top = middle.Segments.OrderBy(segment => segment.A.Y).Last().Binding;
        WorkingArea parent = CreateArea("V", middleRing, middle);
        parent.Direction = PatternDirection.FrameY;
        parent.SelectedStartExit = ProjectOnCut(middle, bottom, 20, bottom.AY);
        parent.SelectedEndExit = ProjectOnCut(middle, top, 280, top.AY);

        AreaSplitResult second = AreaSplitter.Split(middleRing, NoExclusions,
            PatternDirection.FrameY, Margin, Spacing, 2);
        Assert.True(second.Succeeded, second.Failure);
        List<(Vec2 A, Vec2 B)> allCuts = firstCuts
            .Concat(new[] { VerticalCut(second.Cuts.Single()) })
            .ToList();

        var children = new List<WorkingArea>();
        foreach (AreaSplitBand band in second.Bands)
        {
            WorkingArea child = CreateArea("V-" + band.Index, band.Outer,
                CreateBoundary(middleRing, band.Outer, allCuts));
            child.Direction = PatternDirection.FrameY;
            children.Add(child);
        }

        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Margin, Spacing,
            out string failure), failure);

        foreach (WorkingArea child in children)
        {
            double minX = child.Region[0].Min(point => point.X);
            double maxX = child.Region[0].Max(point => point.X);
            double minY = child.Region[0].Min(point => point.Y);
            double maxY = child.Region[0].Max(point => point.Y);
            (double start, double end) = ExpectedRows(minX, maxX, 20, 280, Margin, Spacing);
            Assert.True(Math.Abs(start - child.SelectedStartExit!.Value.Point.X) <= 0.02);
            Assert.True(Math.Abs(end - child.SelectedEndExit!.Value.Point.X) <= 0.02);
            // 세로 배치에서 인출 측면은 영역의 위/아래 절단선이다.
            Assert.InRange(child.SelectedStartExit.Value.Point.Y, minY - 1e-6, maxY + 1e-6);
            Assert.InRange(child.SelectedEndExit.Value.Point.Y, minY - 1e-6, maxY + 1e-6);
        }
    }

    [Fact]
    public void SameSweepRow_FailsWithoutAssigningAnyChild()
    {
        (WorkingArea parent, List<WorkingArea> children, SplitLineBinding left, SplitLineBinding right,
            _, _) = CreateNestedSplit(Margin, Spacing, 20, 280);
        // 두 점을 같은 진행 방향 위치에 두면 사행 방향을 정할 수 없다.
        var boundary = (SplitRegionBoundary2D)parent.RegionExitBoundary!;
        parent.SelectedStartExit = ProjectOnCut(boundary, left, left.AX, 50);
        parent.SelectedEndExit = ProjectOnCut(boundary, right, right.AX, 50);

        Assert.True(SplitExitPointPlanner.HasAssignablePair(parent));
        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, Margin, Spacing,
            out string failure));
        Assert.Contains("진행 방향", failure);
        foreach (WorkingArea child in children)
        {
            Assert.Null(child.SelectedStartExit);
            Assert.Null(child.SelectedEndExit);
            Assert.Null(child.StartAnchor);
        }
    }

    [Fact]
    public void ChildThatCannotBeResolved_LeavesEveryChildUnassigned()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, _, _) =
            CreateNestedSplit(Margin, Spacing, 20, 280);
        // 두 번째 영역의 경계를 분할 경계가 아닌 원본 CAD 경계로 바꾸면 측면을 옮길 수 없다.
        children[1].RegionExitBoundary = NoCad;
        children[1].ExitBoundary = NoCad;

        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, Margin, Spacing,
            out string failure));
        Assert.Contains(children[1].Name, failure);
        Assert.Null(children[0].SelectedStartExit);
        Assert.Null(children[0].SelectedEndExit);
    }

    [Fact]
    public void MarginThatConsumesTheChild_FailsExplicitly()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, _, _) =
            CreateNestedSplit(Margin, Spacing, 20, 280);
        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, 500, Spacing,
            out string failure));
        Assert.Contains("마진", failure);
        Assert.Null(children[0].SelectedStartExit);
    }

    [Fact]
    public void HasAssignablePair_RequiresBothSeparatedPoints()
    {
        (WorkingArea parent, _, SplitLineBinding left, SplitLineBinding right, _, _) =
            CreateNestedSplit(Margin, Spacing, 20, 280);
        var copy = new WorkingArea { Name = "copy", Direction = PatternDirection.FrameX };
        Assert.False(SplitExitPointPlanner.HasAssignablePair(copy));

        copy.SelectedStartExit = parent.SelectedStartExit;
        Assert.False(SplitExitPointPlanner.HasAssignablePair(copy));

        var boundary = (SplitRegionBoundary2D)parent.RegionExitBoundary!;
        copy.SelectedEndExit = ProjectOnCut(boundary, right, right.AX, 280);
        Assert.True(SplitExitPointPlanner.HasAssignablePair(copy));

        // 같은 위치의 두 점은 진행 방향을 정할 수 없으므로 대상이 아니다.
        copy.SelectedEndExit = parent.SelectedStartExit;
        Assert.False(SplitExitPointPlanner.HasAssignablePair(copy));
    }

    [Fact]
    public void SavedFullCutLine_RestoresCutLinePointIdentity()
    {
        var cuts = new[] { HorizontalCut(50) };
        var lowerRing = new List<Vec2> { new(0, 0), new(300, 0), new(300, 50), new(0, 50) };
        SplitRegionBoundary2D original = CreateBoundary(ParentRing, lowerRing, cuts);
        ExitBoundaryPoint point = ProjectOnCut(original, original.Segments[0].Binding, 30, 50);

        // 저장된 전체 절단선으로 다시 만든 경계에서는 저장된 점이 같은 선으로 복원된다.
        SplitRegionBoundary2D reloaded = CreateBoundary(ParentRing, lowerRing, cuts);
        Assert.True(ExitBoundaryConstraint.EvaluateSaved(reloaded, point).Succeeded,
            "전체 절단선을 저장하면 재열기에서도 절단선 인출점 identity가 유지되어야 한다.");

        // 잘라낸 구간만 저장하면 절단선 식별자가 달라져 같은 점을 복원할 수 없다(과거 저장 방식).
        SplitRegionBoundary2D clipped = CreateBoundary(ParentRing, lowerRing,
            new[] { (original.Segments[0].A, original.Segments[0].B) });
        Assert.False(ExitBoundaryConstraint.EvaluateSaved(clipped, point).Succeeded);
    }

    [Fact]
    public void HasSelectedExits_AcceptsSplitLineProvenance()
    {
        var cuts = new[] { HorizontalCut(50) };
        var ring = new List<Vec2> { new(0, 0), new(300, 0), new(300, 50), new(0, 50) };
        SplitRegionBoundary2D boundary = CreateBoundary(ParentRing, ring, cuts);
        Assert.Single(boundary.Segments);

        WorkingArea area = CreateArea("cut", ring, boundary);

        area.SelectedStartExit = ProjectOnCut(boundary, boundary.Segments[0].Binding, 20, 50);
        Assert.False(area.HasSelectedExits, "한 점만 있으면 생성 준비가 아니다.");
        area.SelectedEndExit = ProjectOnCut(boundary, boundary.Segments[0].Binding, 280, 50);
        Assert.True(area.HasSelectedExits, "절단선 provenance 점도 선택 완료로 인정해야 한다.");
    }

    // 마진 적용 영역의 경계 행은 생성기의 반개구간 스캔라인에서 빈 행이 된다.
    // 자동 배정은 그 행을 그대로 쓰지 않고 안쪽 행에서 시작해야 실제 생성이 성공한다.
    [Fact]
    public void MarginBandBoundaryStartRow_IsMovedInside()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, _, _) =
            CreateNestedSplit(Margin, Spacing, 0, 300);

        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Margin, Spacing,
            out string failure), failure);

        foreach (WorkingArea child in children)
        {
            List<List<Vec2>> inset = RegionOps.BuildPatternRegion(child.Region[0], null, Margin);
            Assert.NotEmpty(inset);
            double low = inset[0].Min(point => point.Y);
            double high = inset[0].Max(point => point.Y);
            double row = child.SelectedStartExit!.Value.Point.Y;
            Assert.True(row > low + 1e-6, $"{child.Name} 시작 행 {row}이 마진 영역 아래 끝 {low}에 붙어 있습니다.");
            Assert.True(row < high, $"{child.Name} 시작 행 {row}이 마진 영역 위 끝 {high}를 벗어났습니다.");
        }
    }
}
