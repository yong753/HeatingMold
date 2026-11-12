using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class AreaSplitterTests
{
    private static readonly (double, double, double, double)[] NoExclusions =
        Array.Empty<(double, double, double, double)>();

    private static List<Vec2> Rect(double minX, double minY, double maxX, double maxY) => new()
    {
        new Vec2(minX, minY), new Vec2(maxX, minY), new Vec2(maxX, maxY), new Vec2(minX, maxY)
    };

    private static double Area(List<Vec2> ring) => Math.Abs(RegionOps.SignedArea(ring));

    [Fact]
    public void HorizontalSplit_HalvesEqualLengthBands()
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: 2);

        Assert.True(result.Succeeded, result.Failure);
        Assert.True(result.SplitAxisIsY);
        Assert.Equal(2, result.Bands.Count);
        Assert.Single(result.Cuts);
        // 위/아래 절단: 각 밴드의 면적 합이 원래 영역과 같다.
        Assert.Equal(400.0 * 200.0, result.Bands.Sum(b => Area(b.Outer)), 1);
        Assert.True(result.MaxToMinLengthRatio <= 1.02, $"ratio={result.MaxToMinLengthRatio}");
    }

    [Fact]
    public void VerticalSplit_ForFrameY_UsesVerticalCuts()
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameY, margin: 5, spacing: 50, count: 2);

        Assert.True(result.Succeeded, result.Failure);
        Assert.False(result.SplitAxisIsY);
        Assert.Equal(2, result.Bands.Count);
        double leftMaxX = result.Bands[0].Outer.Max(p => p.X);
        double rightMinX = result.Bands[1].Outer.Min(p => p.X);
        Assert.Equal(leftMaxX, rightMinX, 1);
        Assert.Equal(200.0, leftMaxX, 1);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void MultiSplit_NotPowerOfTwo_BalancesEstimatedLength(int count)
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 500, 320), NoExclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: count);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(count, result.Bands.Count);
        Assert.Equal(count - 1, result.Cuts.Count);
        Assert.True(result.MaxToMinLengthRatio <= 1.02, $"ratio={result.MaxToMinLengthRatio}");
        Assert.Equal(500.0 * 320.0, result.Bands.Sum(b => b.AreaMm2), 0);
        // 절단 좌표는 엄격히 증가한다.
        for (int i = 1; i < result.Cuts.Count; i++)
            Assert.True(result.Cuts[i] > result.Cuts[i - 1]);
    }

    [Fact]
    public void Bands_DoNotOverlap_AndCoverParent()
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 300, 400), NoExclusions, PatternDirection.FrameY, margin: 0, spacing: 40, count: 4);

        Assert.True(result.Succeeded, result.Failure);
        double totalArea = result.Bands.Sum(b => b.AreaMm2);
        Assert.Equal(300.0 * 400.0, totalArea, 0);
        for (int i = 0; i + 1 < result.Bands.Count; i++)
        {
            double right = result.Bands[i].Outer.Max(p => p.X);
            double left = result.Bands[i + 1].Outer.Min(p => p.X);
            Assert.Equal(right, left, 1);
        }
    }

    [Fact]
    public void ExclusionTouchingBoundary_IsKeptOutOfBands()
    {
        // 아래쪽 절반에 외곽과 맞닿은 제외영역을 둔다. 유효영역은 여전히 단일 연결이고 구멍도 없다.
        var exclusions = new[] { (0.0, 10.0, 60.0, 60.0) };

        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), exclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: 2);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(2, result.Bands.Count);
        // 제외면적 60x50 이 빠진다.
        Assert.Equal(400.0 * 200.0 - 3000.0, result.Bands.Sum(b => b.AreaMm2), 0);
    }

    [Fact]
    public void ExclusionCrossingWholeArea_IsExplicitFailure()
    {
        // Y 방향으로 완전히 가로지르는 제외영역은 영역을 둘로 분리한다.
        var exclusions = new[] { (150.0, -10.0, 250.0, 210.0) };

        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), exclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: 2);

        Assert.False(result.Succeeded);
        Assert.Contains("분리", result.Failure);
    }

    [Fact]
    public void InnerExclusion_CreatingHole_IsExplicitFailure()
    {
        var exclusions = new[] { (150.0, 70.0, 250.0, 130.0) };

        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), exclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: 2);

        Assert.False(result.Succeeded);
        Assert.Contains("구멍", result.Failure);
    }

    [Fact]
    public void CountOne_ReturnsSingleBandWithoutCuts()
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: 1);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Single(result.Bands);
        Assert.Empty(result.Cuts);
        Assert.Equal(1.0, result.MaxToMinLengthRatio, 9);
        Assert.Equal(80_000.0, result.Bands[0].AreaMm2, 0);
    }

    [Fact]
    public void NarrowBand_WithoutMarginRoom_IsExplicitFailure()
    {
        // 200 mm 높이를 40개로 나누면 각 밴드 높이가 5 mm라 마진 5 mm에서 발열영역이 남지 않는다.
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: 40);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Failure));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCount_IsRejected(int count)
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX, margin: 5, spacing: 50, count: count);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ZeroSpacing_IsRejected()
    {
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX, margin: 5, spacing: 0, count: 2);

        Assert.False(result.Succeeded);
        Assert.Contains("간격", result.Failure);
    }

    [Fact]
    public void TryBuildAtCuts_UsesRequestedCutsWithoutBalanceLimit()
    {
        // 30 mm / 170 mm로 일부러 크게 치우친 절단. 예상 길이 비는 허용치를 넘지만,
        // 후보 평가용 API는 실제 2D 길이를 재는 호출자가 판단하도록 그대로 구성해야 한다.
        AreaSplitResult result = AreaSplitter.TryBuildAtCuts(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX,
            margin: 5, spacing: 50, cuts: new[] { 30.0 });

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(2, result.Bands.Count);
        Assert.Single(result.Cuts);
        Assert.Equal(30.0, result.Cuts[0], 6);
        Assert.Equal(80_000.0, result.Bands.Sum(b => b.AreaMm2), 0);
        Assert.True(result.MaxToMinLengthRatio > AreaSplitter.HardLengthBalanceLimit);
    }

    [Fact]
    public void TryBuildAtCuts_RejectsUnorderedOrOutOfRangeCuts()
    {
        Assert.False(AreaSplitter.TryBuildAtCuts(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX,
            margin: 5, spacing: 50, cuts: new[] { 150.0, 100.0 }).Succeeded);
        Assert.False(AreaSplitter.TryBuildAtCuts(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX,
            margin: 5, spacing: 50, cuts: new[] { 0.0 }).Succeeded);
        Assert.False(AreaSplitter.TryBuildAtCuts(
            Rect(0, 0, 400, 200), NoExclusions, PatternDirection.FrameX,
            margin: 5, spacing: 50, cuts: new[] { 200.0 }).Succeeded);
    }

    [Fact]
    public void AutoDirection_BehavesAsFrameX()
    {
        // 세로로 긴 200x400 영역에서 기존 AutoAngleDeg는 세로 절단(X=상수)을 골랐다.
        // UI/생성기와 같은 해석을 위해 분할의 Auto는 항상 가로(Y=상수)여야 한다.
        AreaSplitResult result = AreaSplitter.Split(
            Rect(0, 0, 200, 400), NoExclusions, PatternDirection.Auto,
            margin: 5, spacing: 50, count: 2);

        Assert.True(result.Succeeded, result.Failure);
        Assert.True(result.SplitAxisIsY);
        Assert.Single(result.Cuts);
        Assert.InRange(result.Cuts[0], 150.0, 250.0);
    }
}
