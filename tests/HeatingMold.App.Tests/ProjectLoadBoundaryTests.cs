using HeatingMold.App;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// 프로젝트 로드가 저장된 제외영역에서 마진 적용 전 유효 외곽선을 다시 만드는 경로.
/// 계산 함수는 선택점/앵커/회로를 건드리지 않으며, 허용되지 않는 제외는
/// 원본 외곽선으로 대체하지 않고 명시적으로 실패해야 한다.
/// </summary>
public class ProjectLoadBoundaryTests
{
    private static readonly CadExitBoundary2D NoCadEdges = new(1, 1, Array.Empty<ExitEdgeReference>());

    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void NoExclusionsKeepsTheOriginalCadBoundary()
    {
        var cad = new CadExitBoundary2D(7, 9, Array.Empty<ExitEdgeReference>());

        Assert.True(MainWindow.TryBuildRegionExitBoundary(
            cad, Rect(0, 0, 100, 60),
            Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(),
            out IExitBoundary2D boundary, out string failure), failure);

        Assert.Same(cad, boundary);
    }

    [Fact]
    public void ExclusionCutRebuildsTheEffectiveOutline()
    {
        List<Vec2> region = Rect(0, 0, 100, 60);

        Assert.True(MainWindow.TryBuildRegionExitBoundary(NoCadEdges, region,
            new[] { (80.0, -10.0, 110.0, 20.0) },
            out IExitBoundary2D boundary, out string failure), failure);

        var effective = Assert.IsType<EffectiveRegionBoundary2D>(boundary);
        Assert.Equal(2, effective.Segments.Count);
        // 제거된 모서리 (100,0) 쪽은 더 이상 외곽선에 남지 않는다.
        Assert.DoesNotContain(effective.Outline,
            point => point.X > 80 + 1e-6 && point.Y < 20 - 1e-6);
        Assert.Contains(effective.Outline,
            point => point.DistanceTo(new Vec2(80, 20)) < 1e-6);
    }

    [Fact]
    public void RejectedExclusionFailsInsteadOfFallingBackToTheOriginalBoundary()
    {
        Assert.False(MainWindow.TryBuildRegionExitBoundary(NoCadEdges,
            Rect(0, 0, 100, 60), new[] { (40.0, 20.0, 60.0, 40.0) },
            out IExitBoundary2D boundary, out string failure));

        Assert.Null(boundary);
        Assert.Contains("구멍", failure);
    }

    [Fact]
    public void MissingOriginalCadBoundaryIsAnExplicitFailure()
    {
        Assert.False(MainWindow.TryBuildRegionExitBoundary(null,
            Rect(0, 0, 100, 60), Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(),
            out IExitBoundary2D boundary, out string failure));

        Assert.Null(boundary);
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void MultipleRegionLoopsAreRejectedBeforeBuildingTheOutline()
    {
        Assert.False(MainWindow.TryBuildRegionExitBoundary(NoCadEdges, null,
            new[] { (80.0, -10.0, 110.0, 20.0) },
            out IExitBoundary2D boundary, out string failure));

        Assert.Null(boundary);
        Assert.Contains("단일 적용영역", failure);
    }

    [Fact]
    public void SavedCommittedContextSelectsItsOwnBoundaryKind()
    {
        var exitBoundary = new CadExitBoundary2D(1, 1, Array.Empty<ExitEdgeReference>());
        var regionBoundary = new CadExitBoundary2D(2, 2, Array.Empty<ExitEdgeReference>());
        var area = new WorkingArea
        {
            ExitBoundary = exitBoundary,
            RegionExitBoundary = regionBoundary
        };

        // 선택 인출점으로 확정한 경로는 초안 선택점이 나중에 지워져도 영역 경계를 쓴다.
        Assert.Same(regionBoundary,
            MainWindow.ResolveRestoredRouteBoundary(area, hasCommittedContext: true,
                committedRegionMode: true));
        // 인출면/앵커 방식으로 확정한 경로는 초안 선택점이 추가되어도 인출 경계를 쓴다.
        Assert.Same(exitBoundary,
            MainWindow.ResolveRestoredRouteBoundary(area, hasCommittedContext: true,
                committedRegionMode: false));
    }

    [Fact]
    public void MissingCommittedContextFallsBackToTheDraftBoundaryRule()
    {
        var exitBoundary = new CadExitBoundary2D(1, 1, Array.Empty<ExitEdgeReference>());
        var regionBoundary = new CadExitBoundary2D(2, 2, Array.Empty<ExitEdgeReference>());
        var area = new WorkingArea
        {
            ExitBoundary = exitBoundary,
            RegionExitBoundary = regionBoundary
        };

        // 선택 인출점이 없는 초안 상태는 기존처럼 인출 경계를 쓴다.
        Assert.Same(exitBoundary,
            MainWindow.ResolveRestoredRouteBoundary(area, hasCommittedContext: false,
                committedRegionMode: true));
    }

    [Fact]
    public void SavedRegionModeWinsOverDraftSelectedExits()
    {
        // 이전 로드가 초안 선택점을 확정 매핑에 다시 저장했더라도, 저장된 모드가
        // 인출면 방식이면 영역 경계로 오판하지 않는다.
        var anchored = new ProjectCommittedContextDocument
        {
            RegionMode = false,
            SelectedStartExit = new ProjectExitBoundaryPoint(),
            SelectedEndExit = new ProjectExitBoundaryPoint()
        };
        Assert.False(MainWindow.ResolveCommittedRegionMode(anchored));

        var region = new ProjectCommittedContextDocument { RegionMode = true };
        Assert.True(MainWindow.ResolveCommittedRegionMode(region));
    }

    [Fact]
    public void LegacyCommittedContextInfersRegionModeFromSavedSelectedExits()
    {
        var legacyRegion = new ProjectCommittedContextDocument
        {
            SelectedStartExit = new ProjectExitBoundaryPoint(),
            SelectedEndExit = new ProjectExitBoundaryPoint()
        };
        Assert.True(MainWindow.ResolveCommittedRegionMode(legacyRegion));

        Assert.False(MainWindow.ResolveCommittedRegionMode(
            new ProjectCommittedContextDocument()));
        Assert.False(MainWindow.ResolveCommittedRegionMode(null));
    }
}
