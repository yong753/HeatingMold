using System.Collections.Generic;
using HeatingMold.App;

namespace HeatingMold.App.Tests;

/// <summary>
/// 영역별 2D 경로 길이 후보에서 하나씩 골라 최대/최소 비를 최소화하는 조합 선택.
/// 후보 조합을 모두 펼치지 않고도 최적을 찾는지, 동률에서 기본 후보를 우선하는지 검증한다.
/// </summary>
public class SplitLengthCombinationSolverTests
{
    [Fact]
    public void Solve_PicksCombinationWithSmallestRatio()
    {
        var lengths = new List<IReadOnlyList<double>>
        {
            new[] { 100.0, 200.0 },
            new[] { 150.0, 210.0 },
            new[] { 190.0, 205.0 },
        };

        Assert.True(SplitLengthCombinationSolver.TrySolve(lengths,
            out int[] indices, out double ratio, out double min, out double max));
        Assert.Equal(new[] { 1, 1, 1 }, indices);
        Assert.Equal(1.05, ratio, 6);
        Assert.Equal(200.0, min, 6);
        Assert.Equal(210.0, max, 6);
    }

    [Fact]
    public void Solve_PrefersDefaultCandidateOnEqualRatio()
    {
        var lengths = new List<IReadOnlyList<double>>
        {
            new[] { 100.0, 100.0 },
            new[] { 100.0, 100.0 },
        };

        Assert.True(SplitLengthCombinationSolver.TrySolve(lengths,
            out int[] indices, out double ratio, out _, out _));
        Assert.Equal(new[] { 0, 0 }, indices);
        Assert.Equal(1.0, ratio, 9);
    }

    [Fact]
    public void Solve_FailsWhenAChildHasNoCandidate()
    {
        var lengths = new List<IReadOnlyList<double>>
        {
            new[] { 100.0 },
            System.Array.Empty<double>(),
        };

        Assert.False(SplitLengthCombinationSolver.TrySolve(lengths,
            out _, out _, out _, out _));
    }

    [Fact]
    public void Solve_IgnoresNonFiniteOrNonPositiveLengths()
    {
        var lengths = new List<IReadOnlyList<double>>
        {
            new[] { double.NaN, 0.0, 120.0 },
            new[] { double.PositiveInfinity, 130.0 },
        };

        Assert.True(SplitLengthCombinationSolver.TrySolve(lengths,
            out int[] indices, out double ratio, out double min, out double max));
        Assert.Equal(new[] { 2, 1 }, indices);
        Assert.Equal(130.0 / 120.0, ratio, 9);
        Assert.Equal(120.0, min, 9);
        Assert.Equal(130.0, max, 9);
    }
}
