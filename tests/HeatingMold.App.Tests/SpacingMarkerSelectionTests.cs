using System.Collections.Generic;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using Xunit;

namespace HeatingMold.App.Tests;

public sealed class SpacingMarkerSelectionTests
{
    [Fact]
    public void Collapse_KeepsTheMinimumGapOfEachLanePair()
    {
        var first = new List<Vec2> { new(0, 0), new(100, 0) };
        var second = new List<Vec2> { new(0, 10), new(100, 2) };
        var runs = new List<IReadOnlyList<Vec2>> { first, second };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0, 0.0, 1.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        IReadOnlyList<SpacingPairSample> collapsed = SpacingMarkerSelection.Collapse(result, runs);
        SpacingPairSample marker = Assert.Single(collapsed);
        Assert.Equal(0, marker.RunIndex);
        Assert.Equal(1, marker.OtherRunIndex);
        Assert.Equal(2.0, marker.ActualOuterGapMm, 6);
        Assert.Equal(100.0, marker.At.X, 6);
        Assert.Equal(2.0, marker.Other.Y, 6);
    }

    [Fact]
    public void Collapse_OrdersPairsByRunIndexDeterministically()
    {
        var runs = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(50, 0) },
            new List<Vec2> { new(0, 8), new(50, 8) },
            new List<Vec2> { new(0, 16), new(50, 16) }
        };

        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(runs, 5.0, 0.0, 2.0);

        Assert.Equal(RouteSpacingStatus.Completed, result.Status);
        IReadOnlyList<SpacingPairSample> firstPass = SpacingMarkerSelection.Collapse(result, runs);
        IReadOnlyList<SpacingPairSample> secondPass = SpacingMarkerSelection.Collapse(result, runs);
        Assert.Equal(2, firstPass.Count);
        Assert.Equal((0, 1), (firstPass[0].RunIndex, firstPass[0].OtherRunIndex));
        Assert.Equal((1, 2), (firstPass[1].RunIndex, firstPass[1].OtherRunIndex));
        for (int index = 0; index < firstPass.Count; index++)
        {
            Assert.Equal(firstPass[index].At, secondPass[index].At);
            Assert.Equal(firstPass[index].ActualOuterGapMm, secondPass[index].ActualOuterGapMm, 9);
        }
    }

    [Fact]
    public void Collapse_IgnoresEmptyOrInvalidAnalysis()
    {
        var singleRun = new List<IReadOnlyList<Vec2>> { new List<Vec2> { new(0, 0), new(10, 0) } };
        RouteSpacingResult empty = RouteSpacingAnalyzer.Analyze(singleRun, 5.0, 0.3, 1.0);
        RouteSpacingResult invalid = RouteSpacingAnalyzer.Analyze(singleRun, 0.0, 0.3, 1.0);

        Assert.Empty(SpacingMarkerSelection.Collapse(empty, singleRun));
        Assert.Empty(SpacingMarkerSelection.Collapse(invalid, singleRun));
    }
}
