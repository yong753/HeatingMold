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
/// 자동 분할은 확정된 발열레인을 그대로 나눈다. 자식 경로를 다시 생성하지 않고,
/// 부모가 확정한 발열선 좌표·순서·개수를 보존하며 인출 측면만 유지하는지 검증한다.
/// </summary>
public class SplitLengthSearchTests
{
    private const double Spacing = 10;

    private static readonly Vec2[] ParentRing =
    {
        new(0, 0), new(300, 0), new(300, 300), new(0, 300)
    };

    private static readonly List<(double MinX, double MinY, double MaxX, double MaxY)> NoExclusions = new();

    private static readonly CadExitBoundary2D NoCad = new(1, 1, Array.Empty<ExitEdgeReference>());

    private static readonly LanePitchSpec Pitch = new(Spacing, 0);

    private static MainWindow UninitializedWindow() =>
        (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));

    private static SplitRegionBoundary2D CreateBoundary(
        IReadOnlyList<Vec2> parentRing,
        IReadOnlyList<Vec2> region,
        IReadOnlyList<(Vec2 A, Vec2 B)> cuts,
        List<(double MinX, double MinY, double MaxX, double MaxY)>? exclusions = null)
    {
        bool created = SplitRegionBoundary2D.TryCreate(
            NoCad, parentRing, region, exclusions ?? NoExclusions, cuts, out var boundary, out string failure);
        Assert.True(created, failure);
        return boundary!;
    }

    private static ExitBoundaryPoint ProjectOnCut(
        SplitRegionBoundary2D boundary, SplitLineBinding binding, double x, double y) =>
        boundary.ProjectSplitLine(binding, new Vec2(x, y), 500).Point!.Value;
    /// <summary>
    /// 폭 100의 가운데 밴드를 부모로 만들고, 실제 생성기로 확정 경로까지 만든다.
    /// 시작/종료점은 모두 왼쪽 절단선 위에 두어 시작·종료 측면이 같아지게 한다.
    /// </summary>
    private static WorkingArea CreateParent(double startY, double endY, bool withExclusion)
    {
        var areaSplit = AreaSplitter.Split(ParentRing, NoExclusions, PatternDirection.FrameY, 20, 3);
        Assert.True(areaSplit.Succeeded, areaSplit.Failure);
        var middleRing = areaSplit.Bands[1].Outer;
        var cuts = areaSplit.Cuts
            .Select(c => (new Vec2(c, -20), new Vec2(c, 320)))
            .ToList();
        var boundary = CreateBoundary(ParentRing, middleRing, cuts);

        var parent = new WorkingArea
        {
            Name = withExclusion ? "PX" : "P",
            Direction = PatternDirection.FrameX,
            RegionCadBoundary = NoCad,
            RegionExitBoundary = boundary,
            ExitBoundary = boundary
        };
        parent.Region.Add(middleRing);
        parent.ExitChain.Add(new Vec2(100, 0));
        parent.ExitChain.Add(new Vec2(200, 0));
        parent.ExitEdgeRefs.Add(new ExitEdgeReference());
        parent.SplitCutLines.AddRange(cuts);
        if (withExclusion)
        {
            parent.Exclusions.Add((120, 22, 200, 38));
        }

        var left = SegmentBindingAt(boundary, 100);
        var start = ProjectOnCut(boundary, left, 100, startY);
        var end = ProjectOnCut(boundary, left, 100, endY);
        parent.SelectedStartExit = start;
        parent.SelectedEndExit = end;
        parent.StartAnchor = start.Point;
        parent.EndAnchor = end.Point;

        var parameters = new PatternParams
        {
            Spacing = Spacing,
            WireDiameter = 0,
            Direction = PatternDirection.FrameX
        };
        var generated = parent.Exclusions.Count == 0
            ? Serpentine.GenerateWithSelectedExits(
                parent.Region[0], null, parameters, PatternDirection.FrameX,
                parent.SelectedStartExit.Value, parent.SelectedEndExit.Value, parent.RegionExitBoundary)
            : Serpentine.GenerateComponentsWithSelectedExits(
                parent.Region[0], parent.Exclusions, parameters, PatternDirection.FrameX,
                parent.SelectedStartExit.Value, parent.SelectedEndExit.Value, parent.RegionExitBoundary);
        Assert.True(generated.Succeeded, generated.Failure?.ToString());
        parent.Pattern = generated.Pattern;
        parent.PatternDisplayed = true;
        return parent;
    }

    private static SplitLineBinding SegmentBindingAt(SplitRegionBoundary2D boundary, double x) =>
        boundary.Segments
            .Select(segment => segment.Binding)
            .First(binding => Math.Abs(binding.AX - x) < 1e-6 && Math.Abs(binding.BX - x) < 1e-6);

    private static AreaSplitResult InitialSplit(WorkingArea parent, int parts) =>
        AreaSplitter.Split(
            parent.Region[0], parent.Exclusions, parent.Direction, Pitch.CenterPitchMm, parts);

    private static List<SerpentineRoutePiece> HeatingRuns(SerpentineResult pattern) =>
        pattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.HeatingRun).ToList();

    private static List<Vec2> FlatPoints(IEnumerable<SerpentineRoutePiece> pieces) =>
        pieces.SelectMany(p => p.Points).ToList();

    [Fact]
    public void Search_PreservesEveryCommittedHeatingRunExactlyOnce()
    {
        var parent = CreateParent(20, 50, withExclusion: false);
        var parentRuns = HeatingRuns(parent.Pattern!);
        Assert.True(parentRuns.Count >= 4, "테스트 부모 레인이 너무 적습니다.");

        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 2, Pitch,
            out _, out var children, out var patterns, out double ratio, out string failure);
        Assert.True(ok, failure);
        Assert.Equal(2, children.Count);

        var childRuns = children
            .SelectMany(child => HeatingRuns(patterns[child]))
            .ToList();

        Assert.Equal(parentRuns.Count, childRuns.Count);
        var expected = FlatPoints(parentRuns);
        var actual = FlatPoints(childRuns);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 9);
            Assert.Equal(expected[i].Y, actual[i].Y, 9);
        }
    }

    [Fact]
    public void Search_ReportsTheMeasuredRatioOfTheAssignedRuns()
    {
        var parent = CreateParent(20, 50, withExclusion: false);
        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 2, Pitch,
            out _, out var children, out var patterns, out double ratio, out string failure);
        Assert.True(ok, failure);

        double[] lengths = children
            .Select(child => patterns[child].RoutePieces.Sum(p => p.Length))
            .ToArray();
        Assert.Equal(lengths.Max() / lengths.Min(), ratio, 9);
    }

    [Fact]
    public void Search_KeepsStartAndEndOnTheParentExitSides()
    {
        var parent = CreateParent(20, 50, withExclusion: false);
        bool parentStartLow = parent.SelectedStartExit!.Value.Point.X < 150;
        bool parentEndLow = parent.SelectedEndExit!.Value.Point.X < 150;

        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 2, Pitch,
            out _, out var children, out var patterns, out _, out string failure);
        Assert.True(ok, failure);

        foreach (var child in children)
        {
            var runs = HeatingRuns(patterns[child]);
            Assert.NotEmpty(runs);
            var first = runs[0].Points;
            var last = runs[^1].Points;
            Assert.Equal(parentStartLow, first[0].X < first[^1].X);
            Assert.Equal(parentEndLow, last[^1].X < last[0].X);
        }
    }

    [Fact]
    public void Search_RefusesWhenTheParentHasNoCommittedRoute()
    {
        var parent = CreateParent(20, 50, withExclusion: false);
        parent.Pattern = null;

        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 2, Pitch,
            out _, out _, out _, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("확정된 발열경로", failure);
    }

    [Fact]
    public void Search_RejectsPartCountAboveTheSupportedMaximum()
    {
        var parent = CreateParent(20, 50, withExclusion: false);
        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 5, Pitch,
            out _, out _, out _, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("최대 4개", failure);
    }

    [Fact]
    public void Search_RefusesWhenTheParentHasFewerLanesThanParts()
    {
        var parent = CreateParent(20, 30, withExclusion: false);
        int laneCount = HeatingRuns(parent.Pattern!).Count;
        Assert.True(laneCount < 3, $"레인 수 {laneCount}");

        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 3, Pitch,
            out _, out _, out _, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("레인", failure);
    }

    [Fact]
    public void Search_SplitsAcrossAnExclusionWithoutRenewingLanes()
    {
        var parent = CreateParent(20, 50, withExclusion: true);
        var parentRuns = HeatingRuns(parent.Pattern!);
        var window = UninitializedWindow();
        bool ok = window.TryPrepareBalancedSplit(
            parent, InitialSplit(parent, 2), 2, Pitch,
            out _, out var children, out var patterns, out _, out string failure);
        Assert.True(ok, failure);

        var childRuns = children.SelectMany(child => HeatingRuns(patterns[child])).ToList();
        Assert.Equal(parentRuns.Count, childRuns.Count);
        var expected = FlatPoints(parentRuns);
        var actual = FlatPoints(childRuns);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 9);
            Assert.Equal(expected[i].Y, actual[i].Y, 9);
        }
    }
}
