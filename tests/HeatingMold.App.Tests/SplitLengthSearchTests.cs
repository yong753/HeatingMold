using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// 분할 후보 탐색이 면적 근사가 아니라 실제 선택-인출점 2D 생성 결과의 길이를 쓰는지,
/// 그리고 탐색이 만든 패턴을 최종 확정 경로로 그대로 재사용할 수 있는지 검증한다.
/// 네이티브 CAD 없이 절단선 provenance만 가진 중첩 분할로 구성한다.
/// </summary>
public class SplitLengthSearchTests
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

    private static SplitRegionBoundary2D CreateBoundary(IReadOnlyList<Vec2> parentRing,
        IReadOnlyList<Vec2> region, IReadOnlyList<(Vec2 A, Vec2 B)> cuts,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions)
    {
        bool created = SplitRegionBoundary2D.TryCreate(NoCad, parentRing, region, exclusions, cuts,
            out SplitRegionBoundary2D boundary, out string failure);
        Assert.True(created, failure);
        return boundary;
    }

    private static SplitRegionBoundary2D CreateBoundary(IReadOnlyList<Vec2> parentRing,
        IReadOnlyList<Vec2> region, IReadOnlyList<(Vec2 A, Vec2 B)> cuts) =>
        CreateBoundary(parentRing, region, cuts, NoExclusions);

    private static ExitBoundaryPoint ProjectOnCut(SplitRegionBoundary2D boundary,
        SplitLineBinding binding, double x, double y)
    {
        ExitBoundaryResolution resolution = boundary.ProjectSplitLine(binding, new Vec2(x, y), 500);
        Assert.True(resolution.Succeeded, resolution.Failure?.ToString());
        return resolution.Point!.Value;
    }

    /// <summary>
    /// 세로 3분할의 가운데 영역을 부모로 쓴다. 부모의 좌/우 측면은 이웃 분할의 절단선이므로
    /// 네이티브 CAD 없이도 시작/종료 인출점의 provenance를 만들 수 있다.
    /// </summary>
    private static WorkingArea CreateParent(double startY, double endY)
    {
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameY, 0, 20, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts.Select(VerticalCut).ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts);

        SplitLineBinding left = middle.Segments.OrderBy(segment => segment.A.X).First().Binding;
        SplitLineBinding right = middle.Segments.OrderBy(segment => segment.A.X).Last().Binding;
        var parent = new WorkingArea
        {
            Name = "P",
            Direction = PatternDirection.FrameX,
            RegionCadBoundary = NoCad,
            RegionExitBoundary = middle,
            ExitBoundary = middle,
        };
        parent.Region.Add(middleRing);
        parent.SplitCutLines.AddRange(firstCuts);
        // HasSelectedExits는 영역 인출 경계 체인이 준비된 영역만 인정한다. 실제 분할
        // 자식은 부모의 체인/원본 참조를 물려받으므로 테스트 부모에도 같은 상태를 준다.
        parent.ExitChain.Add(new Vec2(100, 0));
        parent.ExitChain.Add(new Vec2(200, 0));
        parent.ExitEdgeRefs.Add(new ExitEdgeReference());
        parent.SelectedStartExit = ProjectOnCut(middle, left, left.AX, startY);
        parent.SelectedEndExit = ProjectOnCut(middle, right, right.AX, endY);
        parent.StartAnchor = parent.SelectedStartExit.Value.Point;
        parent.EndAnchor = parent.SelectedEndExit.Value.Point;
        return parent;
    }

    /// <summary>
    /// 부모 가운데 영역의 왼쪽에 외곽과 맞닿은 제외영역을 둔다. 제외영역은 구멍을 만들지
    /// 않고 영역을 분리하지도 않으므로 유효하고, 아래쪽 자식의 레인을 둘로 가른다.
    /// </summary>
    private static WorkingArea CreateParentWithExclusion(double startY, double endY)
    {
        AreaSplitResult first = AreaSplitter.Split(ParentRing, NoExclusions,
            PatternDirection.FrameY, 0, 20, 3);
        Assert.True(first.Succeeded, first.Failure);
        List<(Vec2 A, Vec2 B)> firstCuts = first.Cuts.Select(VerticalCut).ToList();
        List<Vec2> middleRing = first.Bands[1].Outer.ToList();
        var exclusions = new[]
        {
            (MinX: 100.0, MinY: 100.0, MaxX: 150.0, MaxY: 140.0)
        };
        SplitRegionBoundary2D middle = CreateBoundary(ParentRing, middleRing, firstCuts, exclusions);

        SplitLineBinding left = middle.Segments.OrderBy(segment => segment.A.X).First().Binding;
        SplitLineBinding right = middle.Segments.OrderBy(segment => segment.A.X).Last().Binding;
        var parent = new WorkingArea
        {
            Name = "PX",
            Direction = PatternDirection.FrameX,
            RegionCadBoundary = NoCad,
            RegionExitBoundary = middle,
            ExitBoundary = middle,
        };
        parent.Region.Add(middleRing);
        parent.Exclusions.AddRange(exclusions);
        parent.SplitCutLines.AddRange(firstCuts);
        parent.ExitChain.Add(new Vec2(100, 0));
        parent.ExitChain.Add(new Vec2(200, 0));
        parent.ExitEdgeRefs.Add(new ExitEdgeReference());
        parent.SelectedStartExit = ProjectOnCut(middle, left, left.AX, startY);
        parent.SelectedEndExit = ProjectOnCut(middle, right, right.AX, endY);
        parent.StartAnchor = parent.SelectedStartExit.Value.Point;
        parent.EndAnchor = parent.SelectedEndExit.Value.Point;
        return parent;
    }

    private static MainWindow UninitializedWindow() =>
        (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));

    [Fact]
    public void Search_ReturnsPatternsWhoseMeasuredLengthsMatchReportedRatio()
    {
        WorkingArea parent = CreateParent(20, 280);
        AreaSplitResult initial = AreaSplitter.Split(parent.Region[0], NoExclusions,
            PatternDirection.FrameX, Margin, Spacing, 2);
        Assert.True(initial.Succeeded, initial.Failure);

        MainWindow window = UninitializedWindow();
        bool assigned = window.TryPrepareBalancedSplit(parent, initial, 2, Margin, Spacing,
            out AreaSplitResult chosen, out List<WorkingArea> children,
            out Dictionary<WorkingArea, SerpentineResult> patterns,
            out double balanceRatio, out string failure);

        Assert.True(assigned, failure);
        Assert.Equal(2, chosen.Bands.Count);
        Assert.Equal(2, children.Count);
        Assert.Equal(2, patterns.Count);
        Assert.True(balanceRatio <= AreaSplitter.HardLengthBalanceLimit + 1e-9,
            $"2D 길이 편차 {balanceRatio:F3}배");

        double[] lengths = children
            .Select(child => patterns[child].RoutePieces.Sum(piece => piece.Length))
            .ToArray();
        Assert.All(lengths, length => Assert.True(length > 0, "모든 자식의 2D 경로 길이가 양수여야 한다."));
        Assert.Equal(lengths.Max() / lengths.Min(), balanceRatio, 9);

        foreach (WorkingArea child in children)
        {
            Assert.True(child.HasSelectedExits, child.Name + ": 자동 인출점이 있어야 한다.");
            Assert.True(patterns[child].Path.Count >= 2, child.Name + ": 2D 경로가 있어야 한다.");
            Assert.True(child.SelectedStartExit!.Value.Point.Y >= 20 - 1e-6);
            Assert.True(child.SelectedEndExit!.Value.Point.Y <= 280 + 1e-6);
            // 자식 경계는 실제로 확정 가능한 SplitRegionBoundary2D여야 한다.
            Assert.IsType<SplitRegionBoundary2D>(child.RegionExitBoundary);
        }

        // 탐색은 부모의 선택점/절단선을 바꾸지 않는다.
        Assert.Equal(20, parent.SelectedStartExit!.Value.Point.Y, 6);
        Assert.Equal(280, parent.SelectedEndExit!.Value.Point.Y, 6);
    }

    [Fact]
    public void Search_ImprovesAsymmetricSweepRangeOverAreaEstimate()
    {
        // 부모 선택 구간이 20..200으로 비대칭이면 면적 균등 절단(가운데)은 두 자식의
        // 실제 레인 수/길이를 크게 벌린다. 탐색은 절단을 아래로 옮겨 실제 2D 길이를 맞춘다.
        WorkingArea parent = CreateParent(20, 200);
        AreaSplitResult initial = AreaSplitter.Split(parent.Region[0], NoExclusions,
            PatternDirection.FrameX, Margin, Spacing, 2);
        Assert.True(initial.Succeeded, initial.Failure);

        MainWindow window = UninitializedWindow();
        Assert.True(window.TryEvaluateSplit2D(parent, initial, Margin, Spacing,
            out _, out _, out _, out double initialRatio, out string initialFailure), initialFailure);

        bool assigned = window.TryPrepareBalancedSplit(parent, initial, 2, Margin, Spacing,
            out _, out List<WorkingArea> children,
            out Dictionary<WorkingArea, SerpentineResult> patterns,
            out double balanceRatio, out string failure);

        Assert.True(assigned, failure);
        Assert.Equal(2, children.Count);
        Assert.True(balanceRatio <= AreaSplitter.HardLengthBalanceLimit + 1e-9,
            $"허용치를 넘었습니다: {balanceRatio:F3}배");
        Assert.True(balanceRatio < initialRatio - 1e-6,
            $"탐색이 면적 균등 절단보다 개선되지 않았습니다: 초기 {initialRatio:F3}배, 결과 {balanceRatio:F3}배");
        double[] measuredLengths = children
            .Select(child => patterns[child].RoutePieces.Sum(piece => piece.Length))
            .ToArray();
        Assert.Equal(measuredLengths.Max() / measuredLengths.Min(), balanceRatio, 9);
    }

    [Fact]
    public void Evaluate_GeneratesRoutesAcrossExclusionSplitLanes()
    {
        // 이 형상은 한 번의 수평 절단으로 1.10배 안에 맞추기 어려운 비대칭 제외영역을 가진다.
        // 여기서는 탐색의 허용 판정이 아니라, 제외영역이 있는 후보에서도 실제 2D 생성이
        // 성공하고 같은 레인의 갈라진 구간이 연결선으로 이어지는지를 검증한다.
        WorkingArea parent = CreateParentWithExclusion(20, 280);
        // 면적 근사 분할은 이 비대칭 제외영역에서 1.10배 한도를 넘어 실패할 수 있다.
        // 후보 평가 자체를 검증하려면 한도를 적용하지 않는 절단 지정 API를 쓴다.
        AreaSplitResult split = AreaSplitter.TryBuildAtCuts(parent.Region[0], parent.Exclusions,
            PatternDirection.FrameX, Margin, Spacing, new[] { 150.0 });
        Assert.True(split.Succeeded, split.Failure);

        MainWindow window = UninitializedWindow();
        bool evaluated = window.TryEvaluateSplit2D(parent, split, Margin, Spacing,
            out List<WorkingArea> children,
            out Dictionary<WorkingArea, SerpentineResult> patterns,
            out double[] lengths, out double ratio, out string failure);

        Assert.True(evaluated, failure);
        Assert.Equal(2, children.Count);
        Assert.Equal(2, lengths.Length);
        Assert.All(lengths, length => Assert.True(length > 0));
        Assert.True(double.IsFinite(ratio) && ratio > 0);
        foreach (WorkingArea child in children)
        {
            Assert.True(patterns[child].Path.Count >= 2, child.Name + ": 2D 경로가 있어야 한다.");
            Assert.Contains(patterns[child].RoutePieces,
                piece => piece.Kind == SerpentineRoutePieceKind.HeatingRun);
        }
        // 제외영역이 있는 아래쪽 자식은 같은 레인이 갈라진 구간을 연결선으로 이어야 한다.
        WorkingArea lower = children.OrderBy(child => child.Region[0].Min(point => point.Y)).First();
        Assert.Contains(patterns[lower].RoutePieces,
            piece => piece.Kind == SerpentineRoutePieceKind.FixedConnector);
    }

    [Fact]
    public void Search_RefusesWhenParentSweepRangeCannotAssignPoints()
    {
        // 시작/종료점의 진행 방향 위치가 같으면 어느 자식에도 유일한 인출점을 배정할 수 없다.
        WorkingArea parent = CreateParent(20, 20);
        AreaSplitResult initial = AreaSplitter.Split(parent.Region[0], NoExclusions,
            PatternDirection.FrameX, Margin, Spacing, 2);
        Assert.True(initial.Succeeded, initial.Failure);

        MainWindow window = UninitializedWindow();
        bool assigned = window.TryPrepareBalancedSplit(parent, initial, 2, Margin, Spacing,
            out _, out List<WorkingArea> children,
            out Dictionary<WorkingArea, SerpentineResult> patterns,
            out double balanceRatio, out string failure);

        Assert.False(assigned);
        Assert.False(string.IsNullOrWhiteSpace(failure));
        Assert.Empty(children);
        Assert.Empty(patterns);
        Assert.True(double.IsPositiveInfinity(balanceRatio));
        // 실패해도 부모의 선택점은 그대로 남는다.
        Assert.Equal(20, parent.SelectedStartExit!.Value.Point.Y, 6);
        Assert.Equal(20, parent.SelectedEndExit!.Value.Point.Y, 6);
    }

    [Fact]
    public void Evaluate_ReportsSameTotalsAsDirectGeneration()
    {
        WorkingArea parent = CreateParent(20, 280);
        AreaSplitResult split = AreaSplitter.Split(parent.Region[0], NoExclusions,
            PatternDirection.FrameX, Margin, Spacing, 2);
        Assert.True(split.Succeeded, split.Failure);

        MainWindow window = UninitializedWindow();
        bool evaluated = window.TryEvaluateSplit2D(parent, split, Margin, Spacing,
            out List<WorkingArea> children, out Dictionary<WorkingArea, SerpentineResult> patterns,
            out double[] lengths, out double ratio, out string failure);

        Assert.True(evaluated, failure);
        Assert.Equal(2, lengths.Length);
        Assert.Equal(2, patterns.Count);
        Assert.Equal(lengths.Max() / lengths.Min(), ratio, 9);
        for (int i = 0; i < children.Count; i++)
        {
            double direct = patterns[children[i]].RoutePieces.Sum(piece => piece.Length);
            Assert.Equal(direct, lengths[i], 9);
        }
    }
}
