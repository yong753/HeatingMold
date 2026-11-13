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
    private const double Margin = 0;
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
        double spacing, double parentStartY, double parentEndY)
    {
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameY, 20, 3);
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
            PatternDirection.FrameX, spacing, 2);
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
            _, _) = CreateNestedSplit(Spacing, 20, 280);

        Assert.True(SplitExitPointPlanner.HasAssignablePair(parent),
            "부모가 시작/종료점 쌍을 가지고 있어야 자동 지정 대상이다.");
        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Spacing,
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

        // 시작/종료 행은 부모 선택 구간 안에 있어야 하고, 마지막 행이 영역 끝에 정확히 닿지 않을 수 있다.
        Assert.InRange(lower.SelectedStartExit!.Value.Point.Y, 20, 280);
        Assert.InRange(lower.SelectedEndExit!.Value.Point.Y, 20, 280);
        Assert.True(lower.SelectedEndExit.Value.Point.Y > lower.SelectedStartExit.Value.Point.Y);
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
        Assert.InRange(upper.SelectedStartExit!.Value.Point.Y, 20, 280);
        Assert.InRange(upper.SelectedEndExit!.Value.Point.Y, 20, 280);

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
    public void ConcaveLaneTargetsTheActualScanlineIntervalEnd()
    {
        // 위쪽이 열린 홈이 있는 U자 영역. 150/250 행에는 [0,100]과 [200,300] 두 구간이 있다.
        // 자식에 제외영역이 있으므로 실제 생성은 제외 경로를 쓴다. 그 경로는 다중 구간 행에서
        // 오른쪽 진입이면 '가장 오른쪽 구간의 오른쪽 끝', 왼쪽 진입이면 '가장 왼쪽 구간의 왼쪽 끝'만
        // 허용한다. 따라서 시작은 x=300, 종료는 x=0이어야 하며, 내부 구간 끝(100/200)을 고르면
        // 실제 생성이 AnchorNotAtLaneEnd로 거부된다.
        var childRing = new List<Vec2>
        {
            new(0, 0), new(300, 0), new(300, 300), new(200, 300),
            new(200, 100), new(100, 100), new(100, 300), new(0, 300),
        };
        var exclusion = (MinX: 100.0, MinY: 100.0, MaxX: 200.0, MaxY: 300.0);
        // 좌우 변은 부모의 세로 절단선, 아래 변은 가로 절단선이다. 노치 변은 제외영역에서 온다.
        bool created = SplitRegionBoundary2D.TryCreate(NoCad, ParentRing, childRing,
            new[] { exclusion }, new[] { HorizontalCut(0), VerticalCut(0), VerticalCut(300) },
            out SplitRegionBoundary2D boundary, out string boundaryFailure);
        Assert.True(created, boundaryFailure);

        var parent = new WorkingArea { Name = "P", Direction = PatternDirection.FrameX };
        parent.Region.Add(ParentRing.ToList());
        var dummy = new ArtificialEdgeBinding(1, 1, 0, 0, 400, 400, 0, 0, 1, 0);
        parent.SelectedStartExit = new ExitBoundaryPoint(new Vec2(300, 150), -1, 0)
        {
            ArtificialBinding = dummy,
        };
        parent.SelectedEndExit = new ExitBoundaryPoint(new Vec2(0, 250), -1, 0)
        {
            ArtificialBinding = dummy,
        };

        var child = CreateArea("C", childRing, boundary);
        child.Exclusions.Add(exclusion);
        Assert.True(SplitExitPointPlanner.TryAssign(parent, new[] { child }, Spacing,
            out string failure), failure);

        Assert.Equal(150, child.SelectedStartExit!.Value.Point.Y, 6);
        Assert.Equal(300, child.SelectedStartExit.Value.Point.X, 6);
        Assert.Equal(250, child.SelectedEndExit!.Value.Point.Y, 6);
        Assert.Equal(0, child.SelectedEndExit.Value.Point.X, 6);

        // 자동 배정 결과가 실제 제품 생성 경로에서 성립해야 한다. 좌표만 맞고 생성이 실패하면
        // 분할 전체가 거부되므로, 배정과 생성을 함께 검증한다.
        var parameters = new PatternParams
        {
            Spacing = Spacing,
            WireDiameter = 0,
            Direction = PatternDirection.FrameX,
        };
        AnchoredResult generated = Serpentine.GenerateComponentsWithSelectedExits(
            childRing, new[] { exclusion }, parameters, PatternDirection.FrameX,
            child.SelectedStartExit.Value, child.SelectedEndExit.Value, boundary);
        Assert.Null(generated.Failure);
        Assert.NotNull(generated.Pattern);
    }

    [Fact]
    public void TrimmedParentSide_UsesTheEffectiveRegionNotTheOuterRing()
    {
        // 외곽 제외로 왼쪽이 60까지 깎인 영역. 원본 외곽(0~100) 기준으로는 x=60이 오른쪽에
        // 가깝지만, 실제 발열 구간(60~100)에서는 왼쪽 끝이다. 인출 측면은 실제 발열영역
        // (적용영역 − 제외영역)으로만 판정해야 하므로 시작·종료 모두 왼쪽(low)이어야 한다.
        var ring = new List<Vec2> { new(0, 0), new(100, 0), new(100, 100), new(0, 100) };
        var parent = new WorkingArea { Name = "P", Direction = PatternDirection.FrameX };
        parent.Region.Add(ring);
        parent.Exclusions.Add((MinX: 0.0, MinY: 0.0, MaxX: 60.0, MaxY: 100.0));
        parent.SelectedStartExit = new ExitBoundaryPoint(new Vec2(60, 30), -1, 0);
        parent.SelectedEndExit = new ExitBoundaryPoint(new Vec2(60, 70), -1, 0);

        Assert.True(SplitExitSidePolicy.TryResolve(parent, 0, out bool startLow, out bool endLow,
            out string failure), failure);
        Assert.True(startLow);
        Assert.True(endLow);
    }

    [Fact]
    public void ConcaveParentSide_UsesThePointOwnIntervalNotTheRowExtremes()
    {
        // 제외영역이 없는 오목 부모. 150행 구간은 [0,100], [200,300]이고 시작점은 첫 구간의
        // 오른쪽 끝(x=100)이다. 행 전체 최좌/최우(0/300)로 비교하면 x=100을 왼쪽으로
        // 오판하지만, 생성기는 레인을 그 구간의 오른쪽 끝에서 시작하므로 오른쪽(high)이다.
        // 250행 종료점 x=200은 둘째 구간의 왼쪽 끝이므로 왼쪽(low)이다.
        var ring = new List<Vec2>
        {
            new(0, 0), new(300, 0), new(300, 300), new(200, 300),
            new(200, 100), new(100, 100), new(100, 300), new(0, 300),
        };
        var parent = new WorkingArea { Name = "P", Direction = PatternDirection.FrameX };
        parent.Region.Add(ring);
        parent.SelectedStartExit = new ExitBoundaryPoint(new Vec2(100, 150), -1, 0);
        parent.SelectedEndExit = new ExitBoundaryPoint(new Vec2(200, 250), -1, 0);

        Assert.True(SplitExitSidePolicy.TryResolve(parent, 0, out bool startLow, out bool endLow,
            out string failure), failure);
        Assert.False(startLow);
        Assert.True(endLow);
    }

    [Fact]
    public void ParentRangeClampsChildRows()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, double lowerMinY, double lowerMaxY) =
            CreateNestedSplit(Spacing, 40, 60);

        Assert.True(SplitExitPointPlanner.TryAssign(parent, new[] { children[0] }, Spacing,
            out string failure), failure);

        // 부모 구간(40..60)이 마진 안쪽 범위(5..145)보다 좁으므로 부모 구간이 이긴다.
        Assert.True(lowerMinY + Margin < 40);
        Assert.True(lowerMaxY - Margin > 60);
        Assert.Equal(40, children[0].SelectedStartExit!.Value.Point.Y, 9);
        Assert.Equal(60, children[0].SelectedEndExit!.Value.Point.Y, 9);
    }

    [Fact]
    public void VerticalLayout_UsesXAxisRange()
    {
        // 가로 3분할의 가운데 영역을 다시 세로 2분할한다(진행 방향 = X).
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameX, 20, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts.Select(HorizontalCut).ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts);

        SplitLineBinding bottom = middle.Segments.OrderBy(segment => segment.A.Y).First().Binding;
        SplitLineBinding top = middle.Segments.OrderBy(segment => segment.A.Y).Last().Binding;
        WorkingArea parent = CreateArea("V", middleRing, middle);
        parent.Direction = PatternDirection.FrameY;
        parent.SelectedStartExit = ProjectOnCut(middle, bottom, 25, bottom.AY);
        parent.SelectedEndExit = ProjectOnCut(middle, top, 275, top.AY);

        AreaSplitResult second = AreaSplitter.Split(middleRing, NoExclusions,
            PatternDirection.FrameY, Spacing, 2);
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

        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Spacing,
            out string failure), failure);

        foreach (WorkingArea child in children)
        {
            double minY = child.Region[0].Min(point => point.Y);
            double maxY = child.Region[0].Max(point => point.Y);
            Assert.InRange(child.SelectedStartExit!.Value.Point.X, 20, 280);
            Assert.InRange(child.SelectedEndExit!.Value.Point.X, 20, 280);
            Assert.True(child.SelectedEndExit.Value.Point.X > child.SelectedStartExit.Value.Point.X);
            // 세로 배치에서 인출 측면은 영역의 위/아래 절단선이다.
            Assert.InRange(child.SelectedStartExit.Value.Point.Y, minY - 1e-6, maxY + 1e-6);
            Assert.InRange(child.SelectedEndExit.Value.Point.Y, minY - 1e-6, maxY + 1e-6);
        }
    }

    [Fact]
    public void IntersectLine_ReportsExactRowAndExclusionIdentity()
    {
        var exclusions = new[] { (100.0, 0.0, 200.0, 50.0) };
        var ring = new List<Vec2>
        {
            new(0, 0), new(100, 0), new(100, 50), new(200, 50), new(200, 0),
            new(300, 0), new(300, 150), new(0, 150),
        };
        var parent = new List<Vec2> { new(0, 0), new(300, 0), new(300, 150), new(0, 150) };
        var cuts = new[] { HorizontalCut(150) };
        bool created = SplitRegionBoundary2D.TryCreate(NoCad, parent, ring, exclusions, cuts,
            out SplitRegionBoundary2D boundary, out string failure);
        Assert.True(created, failure);
        Assert.Equal(3, boundary.ArtificialSegmentCount);
        var hits = new List<ExitBoundaryPoint>();
        ExitBoundaryFailureReason reason = boundary.IntersectLine(
            new Vec2(-1, 25), new Vec2(301, 25), hits);
        Assert.Equal(ExitBoundaryFailureReason.NoIntersection, reason);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit =>
        {
            Assert.NotNull(hit.ArtificialBinding);
            Assert.Equal(25, hit.Point.Y, 9);
            Assert.Equal(0.0, hit.ArtificialBinding!.Value.MinY, 9);
            Assert.Equal(50.0, hit.ArtificialBinding.Value.MaxY, 9);
        });
        Assert.Contains(hits, hit => Math.Abs(hit.Point.X - 100) <= 1e-6);
        Assert.Contains(hits, hit => Math.Abs(hit.Point.X - 200) <= 1e-6);
    }

    [Fact]
    public void IntersectLine_RowCoincidentWithCutLine_IsTangent()
    {
        var ring = new List<Vec2> { new(0, 0), new(300, 0), new(300, 150), new(0, 150) };
        var cuts = new[] { HorizontalCut(150) };
        bool created = SplitRegionBoundary2D.TryCreate(NoCad, ring, ring, NoExclusions, cuts,
            out SplitRegionBoundary2D boundary, out string failure);
        Assert.True(created, failure);

        var hits = new List<ExitBoundaryPoint>();
        ExitBoundaryFailureReason reason = boundary.IntersectLine(
            new Vec2(-1, 150), new Vec2(301, 150), hits);
        Assert.Equal(ExitBoundaryFailureReason.TangentOrOverlap, reason);
        Assert.Empty(hits);
    }

    [Fact]
    public void IntersectLine_OutsideRegion_ReturnsNoIntersection()
    {
        var ring = new List<Vec2> { new(0, 0), new(300, 0), new(300, 150), new(0, 150) };
        var cuts = new[] { HorizontalCut(150) };
        bool created = SplitRegionBoundary2D.TryCreate(NoCad, ring, ring, NoExclusions, cuts,
            out SplitRegionBoundary2D boundary, out string failure);
        Assert.True(created, failure);

        var hits = new List<ExitBoundaryPoint>();
        ExitBoundaryFailureReason reason = boundary.IntersectLine(
            new Vec2(-1, 400), new Vec2(301, 400), hits);
        Assert.Equal(ExitBoundaryFailureReason.NoIntersection, reason);
        Assert.Empty(hits);
    }

    [Fact]
    public void SameSweepRow_FailsWithoutAssigningAnyChild()
    {
        (WorkingArea parent, List<WorkingArea> children, SplitLineBinding left, SplitLineBinding right,
            _, _) = CreateNestedSplit(Spacing, 20, 280);
        // 두 점을 같은 진행 방향 위치에 두면 사행 방향을 정할 수 없다.
        var boundary = (SplitRegionBoundary2D)parent.RegionExitBoundary!;
        parent.SelectedStartExit = ProjectOnCut(boundary, left, left.AX, 50);
        parent.SelectedEndExit = ProjectOnCut(boundary, right, right.AX, 50);

        Assert.True(SplitExitPointPlanner.HasAssignablePair(parent));
        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, Spacing,
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
            CreateNestedSplit(Spacing, 20, 280);
        // 두 번째 영역의 경계를 분할 경계가 아닌 원본 CAD 경계로 바꾸면 측면을 옮길 수 없다.
        children[1].RegionExitBoundary = NoCad;
        children[1].ExitBoundary = NoCad;

        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, Spacing,
            out string failure));
        Assert.Contains(children[1].Name, failure);
        Assert.Null(children[0].SelectedStartExit);
        Assert.Null(children[0].SelectedEndExit);
    }

    [Fact]
    public void ZeroSpacing_FailsExplicitly()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, _, _) =
            CreateNestedSplit(Spacing, 20, 280);
        Assert.False(SplitExitPointPlanner.TryAssign(parent, children, 0,
            out string failure));
        Assert.Contains("간격", failure);
        Assert.Null(children[0].SelectedStartExit);
    }

    [Fact]
    public void HasAssignablePair_RequiresBothSeparatedPoints()
    {
        (WorkingArea parent, _, SplitLineBinding left, SplitLineBinding right, _, _) =
            CreateNestedSplit(Spacing, 20, 280);
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

    // 분할 전 종료점이 오른쪽에 있는데 실제 사행이 왼쪽에서 끝나는(레인 수 짝수) 경우에도
    // 종료 인출점은 분할 전과 같은 오른쪽을 유지해야 한다 — 반대로 바꾸면 방향이 뒤집힌다.
    [Fact]
    public void EndSideFollowsParentDirection_EvenWhenNaturalSerpentineEndsOnTheOtherSide()
    {
        (WorkingArea parent, List<WorkingArea> children, SplitLineBinding left, SplitLineBinding right,
            _, _) = CreateNestedSplit(Spacing, 10, 300);

        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Spacing,
            out string failure), failure);

        foreach (WorkingArea child in children)
        {
            Assert.Equal(left.AX, child.SelectedStartExit!.Value.Point.X, 6);
            Assert.Equal(right.AX, child.SelectedEndExit!.Value.Point.X, 6);
        }
    }

    // 인출점 배치는 길이 균등화 후보를 만들지 않는다. 자식마다 방향이 고정된 후보 하나만 나온다.
    [Fact]
    public void FixedSides_AreReturnedAsOneCandidatePerChild()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, _, _) =
            CreateNestedSplit(Spacing, 20, 280);

        Assert.True(SplitExitPointPlanner.TryBuildCandidates(parent, children, Spacing,
                out List<SplitExitPointPlanner.SplitExitCandidate> candidates, out string failure), failure);
        Assert.Equal(children.Count, candidates.Count);
        Assert.All(candidates, candidate => Assert.True(candidate.StartLow));
        Assert.All(candidates, candidate => Assert.False(candidate.EndLow));

        for (int i = 0; i < children.Count; i++)
        {
            var boundary = (SplitRegionBoundary2D)children[i].RegionExitBoundary!;
            Assert.True(boundary.Project(candidates[i].Start.Point, 1e-3).Succeeded);
            Assert.True(boundary.Project(candidates[i].End.Point, 1e-3).Succeeded);
        }
    }

    // 마진 적용 영역의 경계 행은 생성기의 반개구간 스캔라인에서 빈 행이 된다.
    // 자동 배정은 그 행을 그대로 쓰지 않고 안쪽 행에서 시작해야 실제 생성이 성공한다.
    [Fact]
    public void MarginBandBoundaryStartRow_IsMovedInside()
    {
        (WorkingArea parent, List<WorkingArea> children, _, _, _, _) =
            CreateNestedSplit(Spacing, 0, 300);

        Assert.True(SplitExitPointPlanner.TryAssign(parent, children, Spacing,
            out string failure), failure);

        foreach (WorkingArea child in children)
        {
            List<List<Vec2>> inset = RegionOps.BuildPatternRegion(child.Region[0], null);
            Assert.NotEmpty(inset);
            double low = inset[0].Min(point => point.Y);
            double high = inset[0].Max(point => point.Y);
            double row = child.SelectedStartExit!.Value.Point.Y;
            Assert.True(row > low + 1e-6, $"{child.Name} 시작 행 {row}이 마진 영역 아래 끝 {low}에 붙어 있습니다.");
            Assert.True(row < high, $"{child.Name} 시작 행 {row}이 마진 영역 위 끝 {high}를 벗어났습니다.");
        }
    }
}
