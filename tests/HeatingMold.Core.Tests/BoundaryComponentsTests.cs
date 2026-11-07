using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

/// <summary>
/// 제외영역+인출면 후보 계산이 원본 경계 질의를 만나면 정상 후보를 잃는지 검사한다.
/// 경계는 우측 수직선(u=-10, v∈[-20,20])인 단순 mock이다. 같은 기하에서
/// 폴리라인(boundary null)과 mock 경계의 결과가 일치해야 한다.
/// </summary>
public sealed class BoundaryComponentsTests
{
    private static List<Vec2> RegionRing() => new()
    {
        new(-70, -20), new(-10, -20), new(-10, 20), new(-70, 20),
    };

    private sealed class VerticalLineBoundary : IExitBoundary2D
    {
        public ExitBoundaryResolution Project(Vec2 point, double tolerance)
        {
            var projected = new Vec2(-10, Math.Clamp(point.Y, -20.0, 20.0));
            double distance = point.DistanceTo(projected);
            if (distance > tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
            return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(projected, 0, 0)
            {
                Binding = BindingAt(projected),
            });
        }

        public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance)
        {
            Vec2 direction = endpoint - next;
            if (direction.Length <= tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);
            if (Math.Abs(direction.X) <= tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
            double t = (-10 - endpoint.X) / direction.X;
            if (t < -tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
            var hit = endpoint + direction * t;
            if (Math.Abs(hit.Y) > 20 + tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
            return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(hit, 0, 0)
            {
                Binding = BindingAt(hit),
            });
        }

        public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance) =>
            Project(point, tolerance);

        public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding) =>
            ExitBoundaryResolution.Ok(new ExitBoundaryPoint(new Vec2(-10, Math.Clamp(binding.Parameter - 5.0, -20.0, 20.0)), 0, 0)
            {
                Binding = binding,
            });

        private static ExitEdgeBinding BindingAt(Vec2 point) =>
            new(1, 1, 1, 1, 0.0, 40.0, point.Y + 20.0, false);
    }

    [Fact]
    public void EvenLaneRegionWithSameSideExitFindsTheSameCandidatesWithAndWithoutBoundary()
    {
        var chain = new List<Vec2> { new(-10, -20), new(-10, 20) };
        var exclusions = new[] { (10.0, 10.0, 16.0, 14.0) }; // 영역 바깥(불활성) 제외영역
        var p = new PatternParams { Margin = 2.0, Spacing = 6.0, Direction = PatternDirection.FrameX };

        var without = Serpentine.ComputeCandidatesComponents(
            RegionRing(), exclusions, p, chain, PatternDirection.FrameX, null);
        var withBoundary = Serpentine.ComputeCandidatesComponents(
            RegionRing(), exclusions, p, chain, PatternDirection.FrameX, new VerticalLineBoundary());

        Assert.NotNull(without);
        Assert.NotNull(withBoundary);
        Assert.Equal(without.Candidates.Count, withBoundary.Candidates.Count);
        Assert.True(withBoundary.Candidates.Count > 0);
        Assert.Null(withBoundary.BoundaryFailure);
        foreach (ExcludedAnchorCandidate candidate in withBoundary.Candidates)
        {
            Assert.NotNull(candidate.StartBinding);
            Assert.NotNull(candidate.EndBinding);
        }
    }

    [Fact]
    public void OddLaneRegionWithSameSideExitStillFindsConsistentCandidates()
    {
        // spacing 5 → 36/5 = 7 레인(홀수). 홀수 레인이면 최소 비용 사행 경로의 양 끝이
        // 반대쪽으로 교대하지만, DP는 노드별 진입 끝을 독립 선택하므로 긴 대각 커넥터를
        // 가진 완전 해가 존재한다. 경계 유무에 따른 후보 수가 일치하고, 경계가 있으면
        // 모든 후보가 원본 binding을 가져야 한다.
        var chain = new List<Vec2> { new(-10, -20), new(-10, 20) };
        var p = new PatternParams { Margin = 2.0, Spacing = 5.0, Direction = PatternDirection.FrameX };

        var without = Serpentine.ComputeCandidatesComponents(
            RegionRing(), Array.Empty<(double, double, double, double)>(), p, chain,
            PatternDirection.FrameX, null);
        var withBoundary = Serpentine.ComputeCandidatesComponents(
            RegionRing(), Array.Empty<(double, double, double, double)>(), p, chain,
            PatternDirection.FrameX, new VerticalLineBoundary());

        Assert.NotNull(without);
        Assert.NotNull(withBoundary);
        Assert.Equal(without.Candidates.Count, withBoundary.Candidates.Count);
        Assert.True(withBoundary.Candidates.Count > 0);
        Assert.Null(withBoundary.BoundaryFailure);
        foreach (ExcludedAnchorCandidate candidate in withBoundary.Candidates)
        {
            Assert.NotNull(candidate.StartBinding);
            Assert.NotNull(candidate.EndBinding);
        }
    }
}
