using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

/// <summary>Analytic, plane-only boundary fixtures. These tests do not exercise OCCT or a user CAD model.</summary>
public sealed class BoundSerpentineTests
{
    private static Vec2[] Outer(double height = 70) =>
        new[] { new Vec2(0, 0), new Vec2(100, 0), new Vec2(100, height), new Vec2(0, height) };

    private static PatternParams Parameters => new() { Margin = 5, Spacing = 10 };
    private static Vec2[] MisleadingGuide => new[] { new Vec2(40, 0), new Vec2(40, 70) };
    private static Vec2[] ChordGuide => new[] { new Vec2(100, 0), new Vec2(100, 70) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedBoundaryOverridesDistanceAndAnchorTestsAgainstGuide(bool emptyGuide)
    {
        var boundary = new CircleBoundary();
        Vec2[] guide = emptyGuide ? Array.Empty<Vec2>() : MisleadingGuide;
        var candidate = Assert.Single(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, guide, PatternDirection.FrameX, boundary));

        var generated = Serpentine.GenerateAnchored(Outer(), null, Parameters, guide,
            candidate.Start, candidate.End, PatternDirection.FrameX, boundary: boundary, candidate: candidate);

        Assert.True(generated.Succeeded, generated.BoundaryFailure.ToString());
        var pattern = generated.Pattern!;
        Assert.Equal(candidate.CandidateId, pattern.CandidateId);
        Assert.NotEqual(string.Empty, candidate.CandidateId);
        Assert.Equal(candidate.StartBinding, pattern.StartExit!.Binding);
        Assert.Equal(candidate.EndBinding, pattern.EndExit!.Binding);
        Assert.True(pattern.Path[0].DistanceTo(candidate.Start) <= ExitBoundaryConstraint.Tolerance);
        Assert.True(pattern.Path[^1].DistanceTo(candidate.End) <= ExitBoundaryConstraint.Tolerance);
        Assert.True(pattern.Path[0].X < 100); // True curve, not its endpoint chord.
        Assert.Equal(2, pattern.RoutePieces[0].Points.Count);
        Assert.Equal(2, pattern.RoutePieces[^1].Points.Count);
        Assert.Equal(pattern.Path[0].Y, pattern.RoutePieces[0].Points[1].Y, 7);
        Assert.Equal(pattern.Path[^1].Y, pattern.RoutePieces[^1].Points[^2].Y, 7);
        Assert.True(boundary.ProjectCount > 0);
        Assert.True(boundary.OutwardCount >= 4);
        Assert.Equal(2, boundary.EvaluateCount);
        Assert.Equal(0, boundary.ProjectBoundCount);

        CircuitRoute route = CircuitRoute.FromResult(pattern);
        Assert.Equal(candidate.CandidateId, route.CandidateId);
        Assert.Equal(candidate.StartBinding, route.ToResult().StartExit!.Binding);
        Assert.Equal(candidate.EndBinding, route.ToResult().EndExit!.Binding);
    }

    [Fact]
    public void ExclusionDpUsesExactBoundaryAndConversionPreservesCandidateMetadata()
    {
        var boundary = new CircleBoundary(centerY: 25, firstY: 0, lastY: 50);
        var exclusions = new[] { (40d, 15d, 60d, 35d) };
        var candidates = Serpentine.ComputeCandidatesComponents(
            Outer(50), exclusions, Parameters, MisleadingGuide, PatternDirection.FrameX, boundary);

        Assert.True(candidates.Succeeded, candidates.BoundaryFailure.ToString());
        Assert.NotEmpty(candidates.Candidates);
        Assert.Equal(candidates.Candidates.Count, candidates.Candidates.Select(c => c.CandidateId).Distinct().Count());
        var candidate = candidates.Candidates[0];
        var displayCandidate = candidate.ToAnchoredCandidate();
        Assert.Equal(candidate.CandidateId, displayCandidate.CandidateId);
        Assert.Equal(candidate.StartBinding, displayCandidate.StartBinding);
        Assert.Equal(candidate.EndBinding, displayCandidate.EndBinding);
        Assert.NotNull(candidate.StartBinding);
        Assert.NotNull(candidate.EndBinding);

        var generated = Serpentine.GenerateAnchoredComponents(Outer(50), exclusions, Parameters,
            MisleadingGuide, candidate, PatternDirection.FrameX, boundary);

        Assert.True(generated.Succeeded, generated.BoundaryFailure.ToString());
        Assert.Equal(candidate.CandidateId, generated.Pattern!.CandidateId);
        Assert.Equal(candidate.StartBinding, generated.Pattern.StartExit!.Binding);
        Assert.Equal(candidate.EndBinding, generated.Pattern.EndExit!.Binding);
        // 마진 5가 제외영역에도 적용되어 (35,10)-(65,40) 경계가 레인 하나를 더 분할한다.
        Assert.Equal(7, generated.Pattern.Segments.Count);
        Assert.Equal(2, generated.Pattern.RoutePieces[0].Points.Count);
        Assert.Equal(2, generated.Pattern.RoutePieces[^1].Points.Count);
    }

    [Fact]
    public void SavedNativeParametersSubrangesAndOrientationSurviveGeneration()
    {
        // The selected subrange ends exactly at the first/last lane. Parameter is an
        // angle offset by 7, not a normalized chain index or a linear interpolation.
        var boundary = new CircleBoundary(firstY: 10, lastY: 60);
        var candidate = Assert.Single(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, MisleadingGuide, PatternDirection.FrameX, boundary));

        Assert.True(candidate.StartBinding!.Value.Reversed);
        Assert.True(candidate.StartBinding.Value.FirstParameter > 6);
        Assert.Equal(boundary.FirstParameter, candidate.StartBinding.Value.FirstParameter);
        Assert.Equal(boundary.LastParameter, candidate.EndBinding!.Value.LastParameter);
        var generated = Serpentine.GenerateAnchored(
            Outer(), null, Parameters, MisleadingGuide, candidate, PatternDirection.FrameX, boundary);

        Assert.True(generated.Succeeded, generated.BoundaryFailure.ToString());
        Assert.Equal(candidate.StartBinding, generated.Pattern!.StartExit!.Binding);
        Assert.Equal(candidate.EndBinding, generated.Pattern.EndExit!.Binding);
    }

    [Fact]
    public void EqualProjectedCoordinatesDoNotPermitSwitchingOriginalEdge()
    {
        var boundary = new CircleBoundary();
        var candidate = Assert.Single(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, ChordGuide, PatternDirection.FrameX, boundary));
        boundary.ResolutionEdgeKey = 22; // A second original edge at a different depth, with the same projection.

        var generated = Serpentine.GenerateAnchored(
            Outer(), null, Parameters, ChordGuide, candidate, PatternDirection.FrameX, boundary);

        Assert.False(generated.Succeeded);
        Assert.Equal(ExitBoundaryFailureReason.GeometryFailure, generated.BoundaryFailure);
        Assert.Equal(11, candidate.StartBinding!.Value.EdgeKey);
    }

    [Theory]
    [InlineData(ExitBoundaryFailureReason.StaleContext)]
    [InlineData(ExitBoundaryFailureReason.MissingReference)]
    [InlineData(ExitBoundaryFailureReason.GeometryFailure)]
    public void SelectedBindingFailureDoesNotFallBackToGuide(ExitBoundaryFailureReason reason)
    {
        var boundary = new CircleBoundary();
        var candidate = Assert.Single(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, ChordGuide, PatternDirection.FrameX, boundary));
        boundary.EvaluationFailure = reason;

        var generated = Serpentine.GenerateAnchored(
            Outer(), null, Parameters, ChordGuide, candidate, PatternDirection.FrameX, boundary);

        Assert.False(generated.Succeeded);
        Assert.Null(generated.Pattern);
        Assert.Equal(reason, generated.BoundaryFailure);
    }

    [Theory]
    [InlineData(ExitBoundaryFailureReason.MultipleIntersections)]
    [InlineData(ExitBoundaryFailureReason.TangentOrOverlap)]
    [InlineData(ExitBoundaryFailureReason.NoIntersection)]
    public void ExactRayFailureRejectsEvenWhenGuideWouldSucceed(ExitBoundaryFailureReason reason)
    {
        var boundary = new CircleBoundary { OutwardFailure = reason };

        Assert.Empty(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, ChordGuide, PatternDirection.FrameX, boundary));
        var excluded = Serpentine.ComputeCandidatesComponents(Outer(),
            Array.Empty<(double, double, double, double)>(), Parameters, ChordGuide,
            PatternDirection.FrameX, boundary);

        Assert.Empty(excluded.Candidates);
        Assert.Equal(SerpentineComponentsFailureReason.ExitBoundaryConflict, excluded.Failure);
        Assert.Equal(reason, excluded.BoundaryFailure);
    }

    [Fact]
    public void CadQueriesCannotReturnSuccessfulUnboundCandidates()
    {
        var boundary = new CircleBoundary { DropBindings = true };

        Assert.Empty(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, ChordGuide, PatternDirection.FrameX, boundary));
        var excluded = Serpentine.ComputeCandidatesComponents(Outer(),
            Array.Empty<(double, double, double, double)>(), Parameters, ChordGuide,
            PatternDirection.FrameX, boundary);

        Assert.Empty(excluded.Candidates);
        Assert.Equal(ExitBoundaryFailureReason.MissingReference, excluded.BoundaryFailure);
        var result = ExitBoundaryConstraint.TryResolve(new Vec2(95, 10), new Vec2(5, 10),
            ChordGuide, boundary: boundary);
        Assert.Equal(ExitBoundaryFailureReason.MissingReference, result.Failure);
    }

    [Fact]
    public void CadGenerationRequiresBindingsOnSelectedCandidate()
    {
        var boundary = new CircleBoundary();
        var selected = Assert.Single(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, ChordGuide, PatternDirection.FrameX, boundary));
        var unbound = selected with { StartBinding = null };

        var failed = Serpentine.GenerateAnchored(
            Outer(), null, Parameters, ChordGuide, unbound, PatternDirection.FrameX, boundary);
        var noQuery = Serpentine.GenerateAnchored(
            Outer(), null, Parameters, ChordGuide, selected, PatternDirection.FrameX);

        Assert.Equal(ExitBoundaryFailureReason.MissingReference, failed.BoundaryFailure);
        Assert.Equal(ExitBoundaryFailureReason.MissingReference, noQuery.BoundaryFailure);
        Assert.Equal(string.Empty, new AnchoredCandidate(default, default, 0).CandidateId);
    }

    [Fact]
    public void RetainAndConstrainValidateSavedParametersWithoutUnboundSearch()
    {
        var boundary = new CircleBoundary();
        CircuitRoute route = GenerateRoute(boundary);
        int previousProjects = boundary.ProjectCount, previousRays = boundary.OutwardCount;

        var result = CircuitRouteTopology.Constrain(route, Array.Empty<Vec2>(), boundary);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Same(route, Assert.Single(result.Routes));
        Assert.Equal(previousProjects, boundary.ProjectCount);
        Assert.Equal(previousRays, boundary.OutwardCount);

        boundary.EvaluationFailure = ExitBoundaryFailureReason.StaleContext;
        var stale = CircuitRouteTopology.Constrain(route, ChordGuide, boundary);
        Assert.False(stale.Succeeded);
        Assert.Empty(stale.Routes);
        Assert.Contains(nameof(ExitBoundaryFailureReason.StaleContext), stale.Detail);
    }

    [Fact]
    public void NonFiniteSavedEndpointCannotPassParameterValidation()
    {
        var boundary = new CircleBoundary();
        CircuitRoute route = GenerateRoute(boundary);
        var invalid = route with { StartExit = route.StartExit with { Point = new Vec2(double.NaN, 10) } };

        var result = CircuitRouteTopology.Constrain(invalid, ChordGuide, boundary);

        Assert.False(result.Succeeded);
        Assert.Contains(nameof(ExitBoundaryFailureReason.InvalidInput), result.Detail);
    }

    private static CircuitRoute GenerateRoute(CircleBoundary boundary)
    {
        var candidate = Assert.Single(Serpentine.ComputeCandidates(
            Outer(), null, Parameters, MisleadingGuide, PatternDirection.FrameX, boundary));
        var generated = Serpentine.GenerateAnchored(
            Outer(), null, Parameters, MisleadingGuide, candidate, PatternDirection.FrameX, boundary);
        Assert.True(generated.Succeeded, generated.BoundaryFailure.ToString());
        return CircuitRoute.FromResult(generated.Pattern!);
    }

    private sealed class CircleBoundary : IExitBoundary2D
    {
        private const double Radius = 100, ParameterOffset = 7;
        private readonly double _centerY;
        public double FirstParameter { get; }
        public double LastParameter { get; }
        public long ResolutionEdgeKey { get; set; } = 11;
        public bool DropBindings { get; set; }
        public ExitBoundaryFailureReason? EvaluationFailure { get; set; }
        public ExitBoundaryFailureReason? OutwardFailure { get; set; }
        public double FailOutwardAboveY { get; set; } = double.PositiveInfinity;
        public int ProjectCount { get; private set; }
        public int OutwardCount { get; private set; }
        public int EvaluateCount { get; private set; }
        public int ProjectBoundCount { get; private set; }

        public CircleBoundary(double centerY = 35, double firstY = 0, double lastY = 70)
        {
            _centerY = centerY;
            FirstParameter = ParameterOffset + Math.Asin((firstY - centerY) / Radius);
            LastParameter = ParameterOffset + Math.Asin((lastY - centerY) / Radius);
        }

        public ExitBoundaryResolution Project(Vec2 point, double tolerance)
        {
            ProjectCount++;
            double parameter = Math.Clamp(ParameterOffset + Math.Atan2(point.Y - _centerY, point.X),
                FirstParameter, LastParameter);
            return PointAt(Binding(parameter));
        }

        public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance)
        {
            OutwardCount++;
            if (OutwardFailure.HasValue) return ExitBoundaryResolution.Fail(OutwardFailure.Value);
            if (endpoint.Y > FailOutwardAboveY) return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.GeometryFailure);
            Vec2 direction = endpoint - next;
            if (direction.Length <= tolerance) return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);
            direction *= 1 / direction.Length;
            var offset = new Vec2(endpoint.X, endpoint.Y - _centerY);
            double b = 2 * Dot(offset, direction), c = Dot(offset, offset) - Radius * Radius;
            double discriminant = b * b - 4 * c;
            if (discriminant <= tolerance)
                return ExitBoundaryResolution.Fail(discriminant >= -tolerance
                    ? ExitBoundaryFailureReason.TangentOrOverlap : ExitBoundaryFailureReason.NoIntersection);
            var hits = new List<ExitBoundaryPoint>();
            foreach (double distance in new[] { (-b - Math.Sqrt(discriminant)) / 2, (-b + Math.Sqrt(discriminant)) / 2 })
            {
                if (distance < -tolerance) continue;
                Vec2 point = endpoint + direction * distance;
                double parameter = ParameterOffset + Math.Atan2(point.Y - _centerY, point.X);
                if (parameter < FirstParameter - tolerance || parameter > LastParameter + tolerance) continue;
                var hit = new ExitBoundaryPoint(point, -1, 0)
                { Binding = DropBindings ? null : Binding(parameter) };
                if (!hits.Any(h => h.Point.DistanceTo(point) <= tolerance)) hits.Add(hit);
            }
            return hits.Count == 1 ? ExitBoundaryResolution.Ok(hits[0]) : ExitBoundaryResolution.Fail(
                hits.Count == 0 ? ExitBoundaryFailureReason.NoIntersection : ExitBoundaryFailureReason.MultipleIntersections);
        }

        public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance)
        {
            ProjectBoundCount++;
            var failure = Validate(binding);
            if (failure.HasValue) return ExitBoundaryResolution.Fail(failure.Value);
            double parameter = Math.Clamp(ParameterOffset + Math.Atan2(point.Y - _centerY, point.X),
                binding.FirstParameter, binding.LastParameter);
            return PointAt(binding with { Parameter = parameter });
        }

        public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding)
        {
            EvaluateCount++;
            var failure = Validate(binding);
            return failure.HasValue ? ExitBoundaryResolution.Fail(failure.Value) : PointAt(binding);
        }

        private ExitBoundaryFailureReason? Validate(ExitEdgeBinding binding)
        {
            if (EvaluationFailure.HasValue) return EvaluationFailure;
            if (binding.ModelRevision != 9 || binding.WorkingContextId != 41 || binding.WorkingContextRevision != 3)
                return ExitBoundaryFailureReason.StaleContext;
            if (binding.EdgeKey != 11 && binding.EdgeKey != 22) return ExitBoundaryFailureReason.MissingReference;
            if (binding.FirstParameter != FirstParameter || binding.LastParameter != LastParameter
                || binding.Parameter < FirstParameter - 1e-12 || binding.Parameter > LastParameter + 1e-12)
                return ExitBoundaryFailureReason.GeometryFailure;
            return null;
        }

        private ExitEdgeBinding Binding(double parameter) =>
            new(9, 41, 3, ResolutionEdgeKey, FirstParameter, LastParameter, parameter, true);

        private ExitBoundaryResolution PointAt(ExitEdgeBinding binding)
        {
            double angle = binding.Parameter - ParameterOffset;
            var point = new Vec2(Radius * Math.Cos(angle), _centerY + Radius * Math.Sin(angle));
            return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(point, -1, 0)
            { Binding = DropBindings ? null : binding });
        }

        private static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    }
}
