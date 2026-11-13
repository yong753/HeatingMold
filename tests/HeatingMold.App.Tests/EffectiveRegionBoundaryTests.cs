using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// 제외영역이 외곽을 깎을 때의 유효 경계 구성과 artificial 구간 평가를 검증한다.
/// 내부 구멍/영역 분리는 사용자 확정 전제에 따라 거부되어야 한다.
/// </summary>
public class EffectiveRegionBoundaryTests
{
    private static readonly CadExitBoundary2D NoCadEdges = new(1, 1, Array.Empty<ExitEdgeReference>());

    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    private static EffectiveRegionBoundary2D Create(params (double, double, double, double)[] exclusions)
    {
        Assert.True(EffectiveRegionBoundary2D.TryCreate(
            NoCadEdges, Rect(0, 0, 100, 60), exclusions, out var boundary, out string failure),
            failure);
        return boundary;
    }

    [Fact]
    public void BoundaryCutExposesOnlyTheNewCutSides()
    {
        var boundary = Create((80, -10, 110, 20));

        Assert.Equal(2, boundary.Segments.Count);
        Assert.Contains(boundary.Segments, s => s.Binding.Side == 3);
        Assert.Contains(boundary.Segments, s => s.Binding.Side == 2);
        Assert.DoesNotContain(boundary.Segments, s => s.Binding.Side == 0);
        Assert.DoesNotContain(boundary.Segments, s => s.Binding.Side == 1);
        Assert.All(boundary.Segments, s => Assert.True(s.A.Y <= 20 + 1e-9));
    }

    [Fact]
    public void InteriorExclusionIsRejected()
    {
        Assert.False(EffectiveRegionBoundary2D.TryCreate(
            NoCadEdges, Rect(0, 0, 100, 60), new[] { (40.0, 20.0, 60.0, 40.0) },
            out _, out string failure));
        Assert.Contains("구멍", failure);
    }

    [Fact]
    public void SplittingExclusionIsRejected()
    {
        Assert.False(EffectiveRegionBoundary2D.TryCreate(
            NoCadEdges, Rect(0, 0, 100, 60), new[] { (40.0, -10.0, 60.0, 70.0) },
            out _, out string failure));
        Assert.Contains("분리", failure);
    }

    [Fact]
    public void ExclusionOutsideTheRegionIsRejected()
    {
        Assert.False(EffectiveRegionBoundary2D.TryCreate(
            NoCadEdges, Rect(0, 0, 100, 60), new[] { (200.0, 200.0, 210.0, 210.0) },
            out _, out string failure));
        Assert.Contains("자르지 않습니다", failure);
    }

    [Fact]
    public void SharedExclusionSideDoesNotBecomeAnArtificialBoundary()
    {
        // 두 제외영역이 y=20 변을 공유한다. 합집합 내부가 된 첫 제외영역의 윗변은
        // artificial 경계로 남으면 안 된다.
        var boundary = Create((80, -10, 110, 20), (80, 20, 110, 40));

        Assert.DoesNotContain(boundary.Segments, s =>
            s.Binding.Side == 2 && Math.Abs(s.Binding.MinY - (-10)) < 1e-9);
        Assert.Contains(boundary.Segments, s =>
            s.Binding.Side == 2 && Math.Abs(s.Binding.MinY - 20) < 1e-9);
        Assert.Equal(3, boundary.Segments.Count);
    }

    [Fact]
    public void EvaluateArtificialReturnsTheExactSavedParameter()
    {
        var boundary = Create((80, -10, 110, 20));
        EffectiveRegionBoundary2D.ArtificialSegment segment = boundary.Segments[0];
        double parameter = (segment.Binding.FirstParameter + segment.Binding.LastParameter) * 0.5;
        var binding = segment.Binding with { Parameter = parameter };

        ExitBoundaryResolution result = boundary.EvaluateArtificial(binding);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        ExitBoundaryPoint point = result.Point!.Value;
        Assert.Equal(parameter, point.ArtificialBinding!.Value.Parameter, 9);
        Assert.True(segment.A.DistanceTo(point.Point) > 1e-9);
        Assert.True(segment.B.DistanceTo(point.Point) > 1e-9);
    }

    [Fact]
    public void ProjectArtificialClampsToTheSavedSegment()
    {
        var boundary = Create((80, -10, 110, 20));
        EffectiveRegionBoundary2D.ArtificialSegment segment = boundary.Segments[0];
        Vec2 side = segment.B - segment.A;
        Vec2 normal = new(-side.Y, side.X);
        normal = new Vec2(normal.X / normal.Length, normal.Y / normal.Length);
        Vec2 probe = segment.A + side * 0.5 + normal * 0.5;

        ExitBoundaryResolution result = boundary.ProjectArtificial(
            segment.Binding, probe, 1.0);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        ExitBoundaryPoint point = result.Point!.Value;
        Assert.True(point.Point.DistanceTo(probe) <= 1.0);
        Assert.InRange(point.ArtificialBinding!.Value.Parameter,
            segment.Binding.FirstParameter, segment.Binding.LastParameter);
    }

    [Fact]
    public void EvaluateSavedAcceptsArtificialProvenance()
    {
        var boundary = Create((80, -10, 110, 20));
        EffectiveRegionBoundary2D.ArtificialSegment segment = boundary.Segments[0];
        double parameter = (segment.Binding.FirstParameter + segment.Binding.LastParameter) * 0.5;
        var binding = segment.Binding with { Parameter = parameter };
        ExitBoundaryResolution evaluated = boundary.EvaluateArtificial(binding);

        ExitBoundaryPoint saved = evaluated.Point!.Value;
        ExitBoundaryResolution result = ExitBoundaryConstraint.EvaluateSaved(boundary, saved);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.True(result.Point!.Value.Point.DistanceTo(saved.Point) <= 1e-9);
    }

    [Fact]
    public void SelectedExitGenerationAcceptsArtificialBoundaryPoints()
    {
        var boundary = Create((80, -10, 110, 20));
        var start = ArtificialPoint(boundary, side: 3, t: 0.5);
        var end = ArtificialPoint(boundary, side: 2, t: 0.5);
        var parameters = new PatternParams
        {
            Spacing = 10,
            Direction = PatternDirection.FrameX,
        };

        AnchoredResult result = Serpentine.GenerateComponentsWithSelectedExits(
            Rect(0, 0, 100, 60), new[] { (80.0, -10.0, 110.0, 20.0) },
            parameters, PatternDirection.FrameX, start, end, boundary);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        SerpentineResult pattern = result.Pattern!;
        Assert.True(pattern.RoutePieces[0].Points[0].DistanceTo(start.Point) <= 1e-9);
        Assert.True(pattern.RoutePieces[^1].Points[^1].DistanceTo(end.Point) <= 1e-9);
        Assert.Contains(pattern.RoutePieces, piece =>
            piece.Kind is SerpentineRoutePieceKind.FixedConnector or SerpentineRoutePieceKind.TurnConnector);
    }

    [Fact]
    public void SplitPreservesArtificialEndpointProvenance()
    {
        var boundary = Create((80, -10, 110, 20));
        var start = ArtificialPoint(boundary, side: 3, t: 0.5);
        var end = ArtificialPoint(boundary, side: 2, t: 0.5);
        var parameters = new PatternParams
        {
            Spacing = 10,
            Direction = PatternDirection.FrameX,
        };
        AnchoredResult result = Serpentine.GenerateComponentsWithSelectedExits(
            Rect(0, 0, 100, 60), new[] { (80.0, -10.0, 110.0, 20.0) },
            parameters, PatternDirection.FrameX, start, end, boundary);
        Assert.True(result.Succeeded, result.Failure?.ToString());
        CircuitRoute route = CircuitRoute.FromResult(result.Pattern!);

        CircuitRouteSetResult constrained = CircuitRouteTopology.Constrain(
            route, Array.Empty<Vec2>(), boundary);

        Assert.True(constrained.Succeeded, constrained.Detail);
        CircuitRoute single = Assert.Single(constrained.Routes);
        Assert.Equal(start.ArtificialBinding, single.StartExit.ArtificialBinding);
        Assert.Equal(end.ArtificialBinding, single.EndExit.ArtificialBinding);
    }

    private static ExitBoundaryPoint ArtificialPoint(
        EffectiveRegionBoundary2D boundary, int side, double t)
    {
        EffectiveRegionBoundary2D.ArtificialSegment segment =
            boundary.Segments.Single(s => s.Binding.Side == side);
        double parameter = segment.Binding.FirstParameter
            + t * (segment.Binding.LastParameter - segment.Binding.FirstParameter);
        var binding = segment.Binding with { Parameter = parameter };
        ExitBoundaryResolution result = boundary.EvaluateArtificial(binding);
        Assert.True(result.Succeeded, result.Failure?.ToString());
        return result.Point!.Value;
    }

    [Fact]
    public void ProjectPropagatesCadReferenceFailureInsteadOfFallingBackToArtificial()
    {
        var boundary = Create((80, -10, 110, 20));

        ExitBoundaryResolution result = boundary.Project(new Vec2(80, 10), 5.0);

        Assert.False(result.Succeeded);
        Assert.Equal(ExitBoundaryFailureReason.MissingReference, result.Failure);
    }
}
