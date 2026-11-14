using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

/// <summary>
/// 레인 행 구간 계산기 검증. 제외영역 Boolean 차감 결과의 격자 반올림이 아니라
/// 차감 전 외곽선 좌표를 보존해야 한다.
/// </summary>
public class LaneIntervalScanTests
{
    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void ScanKeepsOriginalOuterEndWhileBooleanDifferenceSnapsIt()
    {
        // 기준선 위 끝은 격자(0.01 mm)에 맞지 않는 값이다. Boolean 차감은 이 값을
        // 68.20으로 반올림하지만, 행 구간 계산은 원래 좌표를 그대로 보존해야 한다.
        const double originalEnd = 68.197;
        var outer = Rect(0, -20, originalEnd, 40);
        var exclusions = new[] { (MinX: 20d, MinY: -10d, MaxX: 40d, MaxY: 10d) };

        LaneRowIntervals2D row = LaneIntervalScan2D.Scan(outer, exclusions, 0, true, 1e-9);

        Assert.Single(row.RawIntervals);
        Assert.Equal(0, row.RawIntervals[0].StartMm, 12);
        Assert.Equal(originalEnd, row.RawIntervals[0].EndMm, 12);

        Assert.Equal(2, row.Fragments.Count);
        Assert.Equal(0, row.Fragments[0].ParentIntervalIndex);
        Assert.Equal(0, row.Fragments[0].StartMm, 12);
        Assert.Equal(20, row.Fragments[0].EndMm, 12);
        Assert.Equal(0, row.Fragments[1].ParentIntervalIndex);
        Assert.Equal(40, row.Fragments[1].StartMm, 12);
        Assert.Equal(originalEnd, row.Fragments[1].EndMm, 12);
        Assert.Equal(originalEnd, row.Fragments[1].ParentEndMm, 12);

        // 같은 입력을 Boolean 차감하면 끝 좌표가 격자로 반올림되어 원래 값과 달라진다.
        List<List<Vec2>> differenced = RegionOps.BuildGenerationRegion(outer, exclusions);
        Assert.NotEmpty(differenced);
        double roundedEnd = differenced.SelectMany(path => path).Max(v => v.X);
        Assert.Equal(68.2, roundedEnd, 9);
        Assert.True(Math.Abs(roundedEnd - originalEnd) > 1e-6,
            "Boolean 차감 결과가 원래 끝 좌표를 보존했습니다: " + roundedEnd);
    }

    [Fact]
    public void ScanSubtractsOnlyRectanglesThatCrossTheRow()
    {
        var outer = Rect(0, 0, 100, 60);
        // 행(y=0)을 가로지르지 않는 제외영역은 구간을 바꾸지 않는다.
        var exclusions = new[] { (MinX: 40d, MinY: 10d, MaxX: 60d, MaxY: 30d) };

        LaneRowIntervals2D row = LaneIntervalScan2D.Scan(outer, exclusions, 0, true, 1e-9);

        Assert.Single(row.Fragments);
        Assert.Equal(0, row.Fragments[0].StartMm, 12);
        Assert.Equal(100, row.Fragments[0].EndMm, 12);
    }

    [Fact]
    public void ScanKeepsParentLineageAcrossOverlappingRectangles()
    {
        var outer = Rect(0, 0, 100, 60);
        var exclusions = new[]
        {
            (MinX: 20d, MinY: -5d, MaxX: 50d, MaxY: 5d),
            (MinX: 40d, MinY: -5d, MaxX: 70d, MaxY: 5d),
        };

        LaneRowIntervals2D row = LaneIntervalScan2D.Scan(outer, exclusions, 0, true, 1e-9);

        Assert.Equal(2, row.Fragments.Count);
        Assert.All(row.Fragments, fragment => Assert.Equal(0, fragment.ParentIntervalIndex));
        Assert.Equal(0, row.Fragments[0].StartMm, 12);
        Assert.Equal(20, row.Fragments[0].EndMm, 12);
        Assert.Equal(70, row.Fragments[1].StartMm, 12);
        Assert.Equal(100, row.Fragments[1].EndMm, 12);
    }

    [Fact]
    public void ScanKeepsSeparateParentIntervalsForConcaveOutlines()
    {
        // 두 갈래(오목) 외곽선: 기준선에서 구간이 두 개다. 제외영역이 첫 구간을 가르면
        // 조각은 늘어나지만 부모 구간 번호로 원래 구간을 구분할 수 있어야 한다.
        var outer = new List<Vec2>
        {
            new(0, 0), new(40, 0), new(40, 20), new(60, 20),
            new(60, 0), new(100, 0), new(100, 80), new(0, 80),
        };
        var exclusions = new[] { (MinX: 10d, MinY: -5d, MaxX: 20d, MaxY: 5d) };

        LaneRowIntervals2D row = LaneIntervalScan2D.Scan(outer, exclusions, 0, true, 1e-9);

        Assert.Equal(2, row.RawIntervals.Count);
        Assert.Equal(3, row.Fragments.Count);
        Assert.Equal(0, row.Fragments[0].ParentIntervalIndex);
        Assert.Equal(0, row.Fragments[1].ParentIntervalIndex);
        Assert.Equal(1, row.Fragments[2].ParentIntervalIndex);
        Assert.NotEqual(row.Fragments[0].ParentStartMm, row.Fragments[2].ParentStartMm);
    }
}
