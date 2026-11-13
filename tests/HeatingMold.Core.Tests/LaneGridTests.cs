using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class LaneGridTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void Build_ProducesOneRowPerPitchStep()
    {
        Assert.True(LaneGridSpec.TryBuild(
            Rect(0, 0, 100, 60), null, PatternDirection.FrameX, 0, 10, 1, 5,
            out LaneGridSpec grid, out string failure), failure);

        Assert.Equal(5, grid.Rows.Count);
        Assert.Equal(10.0, grid.Rows[0].SweepCoordinateMm, 9);
        Assert.Equal(50.0, grid.Rows[^1].SweepCoordinateMm, 9);
        Assert.All(grid.Rows, row =>
        {
            Assert.True(row.HasIntervals);
            Assert.Equal(100.0, row.CoveredLengthMm, 9);
        });
    }

    [Fact]
    public void Build_SubtractsExclusionInsideTheRow()
    {
        var exclusions = new[] { (MinX: 40.0, MinY: 0.0, MaxX: 60.0, MaxY: 30.0) };
        Assert.True(LaneGridSpec.TryBuild(
            Rect(0, 0, 100, 60), exclusions, PatternDirection.FrameX, 0, 10, 1, 5,
            out LaneGridSpec grid, out string failure), failure);

        LaneGridRow row = grid.Rows[0];   // y = 10, 제외영역 내부
        Assert.Equal(2, row.Intervals.Count);
        Assert.Equal(40.0, row.Intervals[0].EndMm, 9);
        Assert.Equal(60.0, row.Intervals[1].StartMm, 9);

        LaneGridRow outside = grid.Rows[^1];   // y = 50, 제외영역 밖
        Assert.Single(outside.Intervals);
        Assert.Equal(100.0, outside.CoveredLengthMm, 9);
    }

    [Fact]
    public void Build_FrameY_SwapsSweepAxis()
    {
        Assert.True(LaneGridSpec.TryBuild(
            Rect(0, 0, 60, 100), null, PatternDirection.FrameY, 0, 10, 1, 5,
            out LaneGridSpec grid, out string failure), failure);

        Assert.False(grid.IsSweepAlongY);
        Assert.Equal(10.0, grid.Rows[0].SweepCoordinateMm, 9);
        Assert.Equal(100.0, grid.Rows[0].CoveredLengthMm, 9);
    }

    [Fact]
    public void TryBuild_RejectsNonPositivePitch()
    {
        Assert.False(LaneGridSpec.TryBuild(
            Rect(0, 0, 100, 60), null, PatternDirection.FrameX, 0, 0, 1, 5,
            out _, out string failure));
        Assert.Contains("간격", failure);
    }

    [Fact]
    public void Partition_CoversEveryRowOnce()
    {
        Assert.True(LaneGridSpec.TryBuild(
            Rect(0, 0, 100, 60), null, PatternDirection.FrameX, 0, 10, 1, 9,
            out LaneGridSpec grid, out string failure), failure);

        Assert.True(LaneGridPartition.TrySplit(grid.Rows, 3,
            out List<LaneRowRange> ranges, out string splitFailure), splitFailure);

        Assert.Equal(3, ranges.Count);
        Assert.Equal(grid.Rows[0].Index, ranges[0].FirstIndex);
        Assert.Equal(grid.Rows[^1].Index, ranges[^1].LastIndex);
        for (int i = 1; i < ranges.Count; ++i)
            Assert.Equal(ranges[i - 1].LastIndex + 1, ranges[i].FirstIndex);
    }

    [Fact]
    public void Partition_KeepsEveryGroupMeasurable()
    {
        // 마지막 행(y=50)을 완전히 덮는 제외영역 → 그 행은 발열 구간이 없다.
        var exclusions = new[] { (MinX: -10.0, MinY: 45.0, MaxX: 110.0, MaxY: 55.0) };
        Assert.True(LaneGridSpec.TryBuild(
            Rect(0, 0, 100, 60), exclusions, PatternDirection.FrameX, 0, 10, 1, 5,
            out LaneGridSpec grid, out string failure), failure);
        Assert.False(grid.Rows[^1].HasIntervals);

        Assert.True(LaneGridPartition.TrySplit(grid.Rows, 2,
            out List<LaneRowRange> ranges, out string splitFailure), splitFailure);

        foreach (LaneRowRange range in ranges)
        {
            bool usable = false;
            foreach (LaneGridRow row in grid.Rows)
                if (row.Index >= range.FirstIndex && row.Index <= range.LastIndex && row.HasIntervals)
                    usable = true;
            Assert.True(usable);
        }
    }

    [Fact]
    public void Partition_RejectsTooManyParts()
    {
        Assert.True(LaneGridSpec.TryBuild(
            Rect(0, 0, 100, 60), null, PatternDirection.FrameX, 0, 10, 1, 3,
            out LaneGridSpec grid, out string failure), failure);

        Assert.False(LaneGridPartition.TrySplit(grid.Rows, 5, out _, out string splitFailure));
        Assert.Contains("분할 영역 수", splitFailure);
    }
}
