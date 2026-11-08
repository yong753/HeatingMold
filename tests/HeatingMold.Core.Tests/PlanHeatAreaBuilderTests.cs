using HeatingMold.Core.Geometry;
using HeatingMold.Export;

namespace HeatingMold.Core.Tests;

/// <summary>
/// DXF 평면도의 발열영역 경계가 마진 전 적용영역 − 제외영역을 따르고,
/// 구멍/분리/프레임 불일치를 명시적으로 거부하는지 검증한다.
/// </summary>
public class PlanHeatAreaBuilderTests
{
    private static readonly ProjectionFrame Frame = new(
        new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0));

    private static readonly (double MinX, double MinY, double MaxX, double MaxY)[]
        NoExclusions = Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>();

    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void BuildsEffectiveRegionWithoutApplyingMargin()
    {
        bool ok = PlanHeatAreaBuilder.TryBuild(
            "A",
            new[] { Rect(0, 0, 100, 60) },
            new[] { (80.0, -10.0, 110.0, 20.0) },
            Frame, Frame,
            out DxfPolyline? ring, out string? failure);

        Assert.True(ok, failure);
        DxfPolyline area = ring!;
        Assert.True(area.Closed);
        Assert.Equal(5600, Math.Abs(RegionOps.SignedArea(area.Points)), 6);
    }

    [Fact]
    public void KeepsTheOriginalRingWhenThereAreNoExclusions()
    {
        bool ok = PlanHeatAreaBuilder.TryBuild(
            "A",
            new[] { Rect(0, 0, 100, 60) },
            NoExclusions,
            Frame, Frame,
            out DxfPolyline? ring, out string? failure);

        Assert.True(ok, failure);
        DxfPolyline area = ring!;
        Assert.True(area.Closed);
        Assert.Equal(4, area.Points.Count);
        Assert.Equal(6000, Math.Abs(RegionOps.SignedArea(area.Points)), 6);
    }

    [Fact]
    public void RemovesDuplicateAndClosingPoints()
    {
        var region = new List<Vec2>
        {
            new(0, 0), new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0)
        };

        bool ok = PlanHeatAreaBuilder.TryBuild(
            "A", new[] { region }, NoExclusions,
            Frame, Frame,
            out DxfPolyline? ring, out string? failure);

        Assert.True(ok, failure);
        DxfPolyline area = ring!;
        Assert.Equal(4, area.Points.Count);
        Assert.True(area.Closed);
    }

    [Fact]
    public void RejectsExclusionThatLeavesAHole()
    {
        bool ok = PlanHeatAreaBuilder.TryBuild(
            "A",
            new[] { Rect(0, 0, 100, 60) },
            new[] { (40.0, 20.0, 60.0, 40.0) },
            Frame, Frame,
            out _, out string? failure);

        Assert.False(ok);
        Assert.Contains("단일 연결", failure!);
    }

    [Fact]
    public void RejectsMultipleInputRings()
    {
        bool ok = PlanHeatAreaBuilder.TryBuild(
            "A",
            new[] { Rect(0, 0, 100, 60), Rect(200, 0, 300, 60) },
            NoExclusions,
            Frame, Frame,
            out _, out string? failure);

        Assert.False(ok);
        Assert.Contains("단일 링", failure!);
    }

    [Fact]
    public void RejectsAreaFromAnotherWorkingPlane()
    {
        var otherFrame = new ProjectionFrame(
            new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0));

        bool ok = PlanHeatAreaBuilder.TryBuild(
            "A",
            new[] { Rect(0, 0, 100, 60) },
            NoExclusions,
            Frame, otherFrame,
            out _, out string? failure);

        Assert.False(ok);
        Assert.Contains("작업평면", failure!);
    }
}
