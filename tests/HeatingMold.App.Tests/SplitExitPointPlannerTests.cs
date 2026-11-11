using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// 분할 자동 인출점: 부모 인출점의 측면을 각 새 영역의 같은 측면으로 옮기고,
/// 좌표를 복사하지 않고 그 영역 자신의 경계 provenance(절단선/원본 edge)로 다시 만든다.
/// CAD edge 참조는 네이티브 모델이 필요하므로 절단선만으로 결정되는 중첩 분할을 검사한다.
/// </summary>
public class SplitExitPointPlannerTests
{
    private static readonly Vec2[] ParentRing =
    {
        new(0, 0), new(200, 0), new(200, 100), new(0, 100)
    };

    private static readonly (double MinX, double MinY, double MaxX, double MaxY)[] NoExclusions =
        Array.Empty<(double, double, double, double)>();

    private static readonly CadExitBoundary2D NoCad =
        new(1, 1, Array.Empty<ExitEdgeReference>());

    private static (Vec2 A, Vec2 B) FullCut(double y) => (new Vec2(-20, y), new Vec2(220, y));

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
        area.ExitChain.Add(new Vec2(200, 0));
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

    /// <summary>3분할의 가운데 영역(위/아래가 모두 절단선)을 다시 2분할하는 경우.</summary>
    private static (WorkingArea Parent, List<WorkingArea> Children, SplitLineBinding Bottom,
        SplitLineBinding Top, double NewCutY) CreateNestedSplit()
    {
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameX, 0, 50, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts.Select(FullCut).ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts);
        Assert.Equal(2, middle.Segments.Count);

        SplitLineBinding bottom = middle.Segments.OrderBy(segment => segment.A.Y).First().Binding;
        SplitLineBinding top = middle.Segments.OrderBy(segment => segment.A.Y).Last().Binding;
        WorkingArea parent = CreateArea("P", middleRing, middle);
        parent.SelectedStartExit = ProjectOnCut(middle, bottom, 30, 50);
        parent.SelectedEndExit = ProjectOnCut(middle, top, 170, 100);
        parent.StartAnchor = parent.SelectedStartExit.Value.Point;
        parent.EndAnchor = parent.SelectedEndExit.Value.Point;
        parent.SplitCutLines.AddRange(firstCuts);

        AreaSplitResult second = AreaSplitter.Split(middleRing, NoExclusions,
            PatternDirection.FrameX, 0, 50, 2);
        Assert.True(second.Succeeded, second.Failure);
        double newCutY = second.Cuts.Single();
        List<(Vec2 A, Vec2 B)> allCuts = firstCuts.Concat(new[] { FullCut(newCutY) }).ToList();

        var children = new List<WorkingArea>();
        foreach (AreaSplitBand band in second.Bands)
            children.Add(CreateArea("P-" + band.Index, band.Outer,
                CreateBoundary(middleRing, band.Outer, allCuts)));
        return (parent, children, bottom, top, newCutY);
    }

    [Fact]
    public void NestedSplit_CarriesParentSidesToInheritedAndNewCutLines()
    {
        (WorkingArea parent, List<WorkingArea> children, SplitLineBinding bottom, SplitLineBinding top,
            double newCutY) = CreateNestedSplit();
        Assert.Equal(2, children.Count);
        // 영역 다각형은 폴리곤 연산 정밀도로 반올림되고 절단선 값은 그대로이므로,
        // 물려받은 점은 자기 절단선 값(원본 parameter)을 유지한다(측면 판정 오차 0.02 mm 이내).
        double bottomY = bottom.AY;
        double topY = top.AY;

        Assert.True(SplitExitPointPlanner.HasAssignablePair(parent),
            "부모가 시작/종료점 쌍을 가지고 있어야 자동 지정 대상이다.");
        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, out string failure), failure);

        WorkingArea lower = children[0];
        WorkingArea upper = children[1];

        // 아래 영역: 시작은 물려받은 아래 절단선 위 그대로(원본 parameter 유지),
        // 종료는 새로 생긴 절단선 위로 옮겨진다.
        Assert.Equal(30.0, lower.SelectedStartExit!.Value.Point.X, 6);
        Assert.Equal(bottomY, lower.SelectedStartExit.Value.Point.Y, 6);
        Assert.Equal(parent.SelectedStartExit!.Value.SplitBinding,
            lower.SelectedStartExit.Value.SplitBinding);
        Assert.Equal(170.0, lower.SelectedEndExit!.Value.Point.X, 6);
        Assert.Equal(newCutY, lower.SelectedEndExit.Value.Point.Y, 6);
        Assert.Equal(newCutY, lower.SelectedEndExit.Value.SplitBinding!.Value.AY, 6);

        // 위 영역: 시작이 새 절단선, 종료가 물려받은 위 절단선이다.
        Assert.Equal(30.0, upper.SelectedStartExit!.Value.Point.X, 6);
        Assert.Equal(newCutY, upper.SelectedStartExit.Value.Point.Y, 6);
        Assert.Equal(170.0, upper.SelectedEndExit!.Value.Point.X, 6);
        Assert.Equal(topY, upper.SelectedEndExit.Value.Point.Y, 6);
        Assert.Equal(parent.SelectedEndExit!.Value.SplitBinding,
            upper.SelectedEndExit.Value.SplitBinding);

        // 자식이 물려받은 절단선 식별자는 부모 자신의 절단선과 같다(좌표 복사가 아니라 같은 선).
        // Parameter만 점의 위치를 나타내므로 식별자 비교에서는 제외한다.
        Assert.Equal(bottom with { Parameter = 0 },
            lower.SelectedStartExit.Value.SplitBinding!.Value with { Parameter = 0 });
        Assert.Equal(top with { Parameter = 0 },
            upper.SelectedEndExit.Value.SplitBinding!.Value with { Parameter = 0 });

        // 기존 수동 선택 경로와 같은 호환 좌표도 함께 맞춘다.
        foreach (WorkingArea child in children)
        {
            Assert.True(child.StartAnchor.HasValue && child.EndAnchor.HasValue);
            Assert.Equal(child.SelectedStartExit!.Value.Point, child.StartAnchor!.Value);
            Assert.Equal(child.SelectedEndExit!.Value.Point, child.EndAnchor!.Value);
            Assert.True(child.HasSelectedExits, child.Name + ": 자동 지정점이 생성 준비 상태여야 한다.");
        }
    }

    [Fact]
    public void Assignment_IsNotOrderDependentForVerticalLayout()
    {
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameY, 0, 50, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts
            .Select(cut => (new Vec2(cut, -20), new Vec2(cut, 120)))
            .ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts);
        Assert.Equal(2, middle.Segments.Count);

        SplitLineBinding left = middle.Segments.OrderBy(segment => segment.A.X).First().Binding;
        SplitLineBinding right = middle.Segments.OrderBy(segment => segment.A.X).Last().Binding;
        WorkingArea parent = CreateArea("V", middleRing, middle);
        parent.Direction = PatternDirection.FrameY;
        // 세로 배치(레인 진행 = 로컬 X)에서 시작=왼쪽, 종료=오른쪽.
        parent.SelectedStartExit = ProjectOnCut(middle, left, 40, 25);
        parent.SelectedEndExit = ProjectOnCut(middle, right, 60, 75);

        AreaSplitResult second = AreaSplitter.Split(middleRing, NoExclusions,
            PatternDirection.FrameY, 0, 50, 2);
        Assert.True(second.Succeeded, second.Failure);
        double newCutX = second.Cuts.Single();
        List<(Vec2 A, Vec2 B)> allCuts = firstCuts
            .Concat(new[] { (new Vec2(newCutX, -20), new Vec2(newCutX, 120)) })
            .ToList();

        var children = new List<WorkingArea>();
        foreach (AreaSplitBand band in second.Bands)
        {
            WorkingArea child = CreateArea("V-" + band.Index, band.Outer,
                CreateBoundary(middleRing, band.Outer, allCuts));
            child.Direction = PatternDirection.FrameY;
            children.Add(child);
        }

        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, out string failure), failure);

        Assert.Equal(left.AX, children[0].SelectedStartExit!.Value.Point.X, 6);
        Assert.Equal(newCutX, children[0].SelectedEndExit!.Value.Point.X, 6);
        Assert.Equal(newCutX, children[1].SelectedStartExit!.Value.Point.X, 6);
        Assert.Equal(right.AX, children[1].SelectedEndExit!.Value.Point.X, 6);
    }

    [Fact]
    public void SameSweepSide_FailsWithoutAssigningAnyChild()
    {
        (WorkingArea parent, List<WorkingArea> children, SplitLineBinding bottom, _, _) =
            CreateNestedSplit();
        // 두 점을 같은 절단선 위에 두면 사행 진행 방향을 정할 수 없다.
        parent.SelectedStartExit = ProjectOnCut((SplitRegionBoundary2D)parent.RegionExitBoundary!,
            bottom, 30, 50);
        parent.SelectedEndExit = ProjectOnCut((SplitRegionBoundary2D)parent.RegionExitBoundary!,
            bottom, 170, 50);

        Assert.True(SplitExitPointPlanner.HasAssignablePair(parent));
        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, out string failure));
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
        (WorkingArea parent, List<WorkingArea> children, _, _, _) = CreateNestedSplit();
        // 두 번째 영역의 경계를 분할 경계가 아닌 원본 CAD 경계로 바꾸면 측면을 옮길 수 없다.
        children[1].RegionExitBoundary = NoCad;
        children[1].ExitBoundary = NoCad;

        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, out string failure));
        Assert.Contains(children[1].Name, failure);
        Assert.Null(children[0].SelectedStartExit);
        Assert.Null(children[0].SelectedEndExit);
    }

    [Fact]
    public void HasAssignablePair_RequiresBothSeparatedPoints()
    {
        (WorkingArea parent, _, SplitLineBinding bottom, SplitLineBinding top, _) = CreateNestedSplit();
        var copy = new WorkingArea { Name = "copy", Direction = PatternDirection.FrameX };
        Assert.False(SplitExitPointPlanner.HasAssignablePair(copy));

        copy.SelectedStartExit = parent.SelectedStartExit;
        Assert.False(SplitExitPointPlanner.HasAssignablePair(copy));

        var boundary = (SplitRegionBoundary2D)parent.RegionExitBoundary!;
        copy.SelectedEndExit = ProjectOnCut(boundary, top, 170, 100);
        Assert.True(SplitExitPointPlanner.HasAssignablePair(copy));

        // 같은 위치의 두 점은 진행 방향을 정할 수 없으므로 대상이 아니다.
        copy.SelectedEndExit = ProjectOnCut(boundary, bottom, 30, 50);
        Assert.False(SplitExitPointPlanner.HasAssignablePair(copy));
    }

    [Fact]
    public void SavedFullCutLine_RestoresCutLinePointIdentity()
    {
        var cuts = new[] { FullCut(50) };
        var lowerRing = new List<Vec2> { new(0, 0), new(200, 0), new(200, 50), new(0, 50) };
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
        var cuts = new[] { FullCut(50) };
        var ring = new List<Vec2> { new(0, 0), new(200, 0), new(200, 50), new(0, 50) };
        SplitRegionBoundary2D boundary = CreateBoundary(ParentRing, ring, cuts);
        Assert.Single(boundary.Segments);

        WorkingArea area = CreateArea("cut", ring, boundary);

        area.SelectedStartExit = ProjectOnCut(boundary, boundary.Segments[0].Binding, 20, 50);
        Assert.False(area.HasSelectedExits, "한 점만 있으면 생성 준비가 아니다.");
        area.SelectedEndExit = ProjectOnCut(boundary, boundary.Segments[0].Binding, 180, 50);
        Assert.True(area.HasSelectedExits, "절단선 provenance 점도 선택 완료로 인정해야 한다.");
    }
}
