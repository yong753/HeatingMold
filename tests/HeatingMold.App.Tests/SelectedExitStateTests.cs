using System.Reflection;
using System.Runtime.CompilerServices;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>직접 인출점 선택 상태가 기존 인출면/후보 상태와 독립적으로 유지되는지 검증한다.</summary>
public class SelectedExitStateTests
{
    private static readonly ExitEdgeBinding StartBinding = new(7, 11, 7, 21, 2, 12, 4, false);
    private static readonly ExitEdgeBinding EndBinding = new(7, 11, 7, 22, 3, 10, 0, true);

    private static WorkingArea Area()
    {
        var boundary = new RecordingBoundary();
        var area = new WorkingArea
        {
            ExitChain = new() { new(20, 2), new(20, 12) },
            ExitChain3D = new() { new(20, 2, 0), new(20, 12, 0) },
            ExitBoundary = boundary,
            RegionExitBoundary = boundary,
            SelectedStartExit = new ExitBoundaryPoint(new Vec2(20, 4), 0, 4) { Binding = StartBinding },
            SelectedEndExit = new ExitBoundaryPoint(new Vec2(20, 10), 1, 10) { Binding = EndBinding }
        };
        area.ExitEdgeRefs.Add(new ExitEdgeReference
        {
            Revision = 7, EdgeKey = 21, FaceId = 3,
            FirstParameter = 2, LastParameter = 12, StartIndex = 0, EndIndex = 1
        });
        return area;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectedExitsRequireBothBindings(bool hasStart, bool hasEnd)
    {
        var area = Area();
        area.SelectedStartExit = hasStart ? area.SelectedStartExit : null;
        area.SelectedEndExit = hasEnd ? area.SelectedEndExit : null;

        Assert.Equal(hasStart && hasEnd, area.HasSelectedExits);
    }

    [Fact]
    public void MissingRegionBoundaryKeepsDisplayChainButIsNotSelectable()
    {
        var area = Area();
        area.RegionExitBoundary = null;

        Assert.True(area.HasExitBoundary);
        Assert.False(area.HasSelectedExits);
    }

    [Fact]
    public void SameCoordinatesOnDifferentOriginalEdgesRetainDistinctContextKeys()
    {
        var area = Area();
        area.SelectedStartExit = new ExitBoundaryPoint(new Vec2(20, 4), 0, 4) { Binding = StartBinding };
        string first = RouteContextKey(area);

        area.SelectedStartExit = new ExitBoundaryPoint(new Vec2(20, 4), 0, 4)
        {
            Binding = StartBinding with { EdgeKey = 99 }
        };
        string second = RouteContextKey(area);

        Assert.NotEqual(first, second);
        Assert.True(area.HasSelectedExits);
    }

    [Fact]
    public void CommittedContextCapturesSelectedPointsAndRegionBoundary()
    {
        var area = Area();
        var context = CommittedRouteContext.Capture(area);

        Assert.Same(area.RegionExitBoundary, context.RegionBoundary);
        Assert.Equal(StartBinding, context.SelectedStartExit?.Binding);
        Assert.Equal(EndBinding, context.SelectedEndExit?.Binding);
        Assert.Equal(area.SelectedStartExit?.Point, context.SelectedStartExit?.Point);
        Assert.Equal(area.SelectedEndExit?.Point, context.SelectedEndExit?.Point);
    }

    [Fact]
    public void ForkPreservesSelectedPointsAndRegionBoundary()
    {
        var area = Area();
        var method = typeof(MainWindow).GetMethod("ForkAreaRoute",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var fork = (WorkingArea)method.Invoke(null, new object[] { area })!;

        Assert.NotSame(area, fork);
        Assert.Same(area.RegionExitBoundary, fork.RegionExitBoundary);
        Assert.Equal(area.SelectedStartExit, fork.SelectedStartExit);
        Assert.Equal(area.SelectedEndExit, fork.SelectedEndExit);
        Assert.Equal(area.SelectedStartExit?.Binding, fork.SelectedStartExit?.Binding);
        Assert.Equal(area.SelectedEndExit?.Binding, fork.SelectedEndExit?.Binding);
        Assert.Equal(area.ExitChain, fork.ExitChain);
    }

    private static string RouteContextKey(WorkingArea area)
    {
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var method = typeof(MainWindow).GetMethod("BuildRouteContextKey",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)method.Invoke(window, new object[] { area })!;
    }

    private sealed class RecordingBoundary : IExitBoundary2D
    {
        public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding) =>
            ExitBoundaryResolution.Ok(new ExitBoundaryPoint(new Vec2(20, binding.Parameter), -1, binding.Parameter)
            {
                Binding = binding
            });

        public ExitBoundaryResolution Project(Vec2 point, double tolerance) =>
            ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);

        public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance) =>
            ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);

        public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance) =>
            ExitBoundaryResolution.Ok(new ExitBoundaryPoint(point, -1, binding.Parameter) { Binding = binding });
    }
}
