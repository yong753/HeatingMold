using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using Xunit;

namespace HeatingMold.Core.Tests;

public class RouteSpacingAnalyzerTests
{
    [Fact]
    public void ParallelRuns_ReportConfiguredOuterGap()
    {
        var runs = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(100, 0) },
            new List<Vec2> { new(0, 5.30), new(100, 5.30) }
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0, 0.30, 5.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        Assert.InRange(result.MinOuterGapMm, 4.999, 5.001);
        Assert.InRange(result.MaxOuterGapMm, 4.999, 5.001);
    }

    [Fact]
    public void ShortenedRun_ReportsWiderGap()
    {
        var runs = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(100, 0) },
            new List<Vec2> { new(0, 10.30), new(40, 10.30) }
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0, 0.30, 5.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        Assert.InRange(result.MinOuterGapMm, 9.999, 10.001);
        Assert.True(result.MaxOuterGapMm > 40.0, "짧아진 레인 끝에서는 이웃이 멀어져야 한다.");
    }

    [Fact]
    public void SingleRun_IsReportedAsUnmeasurable()
    {
        var runs = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(100, 0) }
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0, 0.30);

        Assert.Equal(RouteSpacingStatus.Empty, result.Status);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void InvalidOuterGap_IsRejectedExplicitly()
    {
        var runs = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(100, 0) },
            new List<Vec2> { new(0, 5), new(100, 5) }
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 0, 0.30);

        Assert.Equal(RouteSpacingStatus.InvalidInput, result.Status);
        Assert.Contains("외곽 간격", result.Message);
    }

    [Fact]
    public void AnalyzerDoesNotChangeInputs()
    {
        var first = new List<Vec2> { new(0, 0), new(100, 0) };
        var second = new List<Vec2> { new(0, 5.30), new(100, 5.30) };
        var runs = new List<IReadOnlyList<Vec2>> { first, second };

        RouteSpacingAnalyzer.Analyze(runs, 5.0, 0.30, 5.0);

        Assert.Equal(2, first.Count);
        Assert.Equal(new Vec2(100, 0), first[1]);
        Assert.Equal(new Vec2(0, 5.30), second[0]);
    }

    [Fact]
    public void DifferentConfiguredGaps_AreComparedAcrossAreas()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 14.7, 0.30, 0.0),
            new("B", 0, new List<Vec2> { new(0, 15), new(100, 15) }, 24.7, 0.30, 0.0)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        Assert.InRange(result.MinOuterGapMm, 14.699, 14.701);
        RouteSpacingDirectionGroup group = Assert.Single(result.DirectionGroups);
        RouteSpacingPairResult pair = Assert.Single(group.Pairs);
        Assert.Equal(0, pair.RunIndex);
        Assert.Equal(1, pair.OtherRunIndex);
        Assert.Equal(0, pair.AtRunIndex);
        Assert.InRange(pair.MinOuterGapMm, 14.699, 14.701);
        Assert.Equal(14.7, pair.ConfiguredOuterGapMm, 9);
        Assert.Equal(24.7, pair.OtherConfiguredOuterGapMm, 9);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("A", result.Runs[0].AreaId);
        Assert.Equal("B", result.Runs[1].AreaId);
        Assert.Equal(14.7, result.Runs[0].ConfiguredOuterGapMm, 9);
        Assert.Equal(24.7, result.Runs[1].ConfiguredOuterGapMm, 9);
    }

    [Fact]
    public void DifferentDiameters_UseHalfOfBothRadii()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 5.0, 0.30, 0.0),
            new("B", 0, new List<Vec2> { new(0, 10), new(100, 10) }, 5.0, 3.00, 0.0)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        Assert.InRange(result.MinOuterGapMm, 8.349, 8.351);
        Assert.InRange(result.MaxOuterGapMm, 8.349, 8.351);
    }

    [Fact]
    public void PerpendicularRuns_AreSeparatedByDirection()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 15.0, 0.30, 0.0),
            new("B", 0, new List<Vec2> { new(50, -50), new(50, 50) }, 15.0, 0.30, 90.0)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.DirectionSeparated, result.Status);
        Assert.Empty(result.Samples);
        Assert.Equal(2, result.DirectionGroups.Count);
        Assert.All(result.DirectionGroups, group => Assert.Empty(group.Pairs));
        Assert.Contains("방향", result.Message);
    }

    [Fact]
    public void InvalidCoordinates_AreRejectedExplicitly()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(double.NaN, 0) }, 5.0, 0.30, 0.0)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.InvalidInput, result.Status);
        Assert.Contains("좌표", result.Message);
    }

    [Fact]
    public void InvalidDirection_IsRejectedExplicitly()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 5.0, 0.30, double.NaN)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.InvalidInput, result.Status);
        Assert.Contains("방향", result.Message);
    }

    [Fact]
    public void TwoDirections_AreAnalyzedWithinTheirOwnGroups()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 14.7, 0.30, 0.0),
            new("A", 1, new List<Vec2> { new(0, 15), new(100, 15) }, 14.7, 0.30, 0.0),
            new("B", 0, new List<Vec2> { new(200, -50), new(200, 50) }, 14.7, 0.30, 90.0),
            new("B", 1, new List<Vec2> { new(215, -50), new(215, 50) }, 14.7, 0.30, 90.0)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        Assert.Equal(2, result.DirectionGroups.Count);
        Assert.Equal(0, result.UncomparedRunCount);
        var pairs = new List<(int, int)>();
        foreach (RouteSpacingDirectionGroup group in result.DirectionGroups)
            foreach (RouteSpacingPairResult pair in group.Pairs)
                pairs.Add((pair.RunIndex, pair.OtherRunIndex));
        Assert.Equal(new[] { (0, 1), (2, 3) }, pairs);
    }

    [Fact]
    public void DirectionGroups_HoldOnlyMutuallyParallelRuns()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 5.0, 0.30, 0.0),
            new("B", 1, new List<Vec2> { new(0, 10), new(100, 10) }, 5.0, 0.30, 0.09),
            new("C", 2, new List<Vec2> { new(0, 20), new(100, 20) }, 5.0, 0.30, -0.09)
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        Assert.Equal(2, result.DirectionGroups.Count);
        Assert.Equal(new[] { 0, 1 }, result.DirectionGroups[0].RunIndices);
        Assert.Equal(new[] { 2 }, result.DirectionGroups[1].RunIndices);
        Assert.Single(result.DirectionGroups[0].Pairs);
        Assert.Empty(result.DirectionGroups[1].Pairs);
        Assert.Equal(1, result.UncomparedRunCount);
        Assert.Equal(
            (0, 1),
            (result.DirectionGroups[0].Pairs[0].RunIndex, result.DirectionGroups[0].Pairs[0].OtherRunIndex));
    }

    [Fact]
    public void CancelledAnalysis_IsRejectedExplicitly()
    {
        var runs = new List<RouteSpacingRunInput>
        {
            new("A", 0, new List<Vec2> { new(0, 0), new(100, 0) }, 5.0, 0.30, 0.0),
            new("B", 1, new List<Vec2> { new(0, 10), new(100, 10) }, 5.0, 0.30, 0.0)
        };

        using var cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<System.OperationCanceledException>(
            () => RouteSpacingAnalyzer.Analyze(runs, 1.0, cancellation.Token));
    }
}
