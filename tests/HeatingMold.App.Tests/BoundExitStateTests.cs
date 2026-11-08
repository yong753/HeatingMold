using System.Reflection;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// State/selection tests only. The recording boundary is deliberately non-CAD;
/// exact original-edge evaluation is covered by WorkingSide.Regression.
/// </summary>
public class BoundExitStateTests
{
    private static readonly ExitEdgeBinding StartBinding = new(7, 11, 7, 21, 2, 12, 4, false);
    private static readonly ExitEdgeBinding EndBinding = StartBinding with { Parameter = 10 };

    private static AnchoredCandidate Candidate(string id = "candidate-1") =>
        new(new Vec2(20, 4), new Vec2(20, 10), 0)
        {
            CandidateId = id,
            StartBinding = StartBinding,
            EndBinding = EndBinding,
            StartLeftToRight = false
        };

    private static WorkingArea Area()
    {
        var area = new WorkingArea
        {
            ExitFaceId = 3,
            ExitFaceRevision = 7,
            ExitChain = new() { new(20, 2), new(20, 12) },
            ExitBoundary = new RecordingBoundary()
        };
        area.ExitEdgeRefs.Add(new ExitEdgeReference
        {
            Revision = 7, EdgeKey = 21, FaceId = 3,
            FirstParameter = 2, LastParameter = 12, StartIndex = 0, EndIndex = 1
        });
        return area;
    }

    private static bool Select(WorkingArea area, AnchoredCandidate candidate, out string error)
    {
        var method = typeof(MainWindow).GetMethod("TrySelectAnchorCandidate",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object[] args = { area, candidate, null };
        bool result = (bool)method.Invoke(null, args)!;
        error = (string)args[2];
        return result;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExitFaceRequiresBothOriginalReferencesAndBoundary(bool hasRefs, bool hasBoundary)
    {
        var area = Area();
        if (!hasRefs) area.ExitEdgeRefs.Clear();
        if (!hasBoundary) area.ExitBoundary = null;

        Assert.Equal(hasRefs && hasBoundary, area.HasExitFace);
    }

    [Fact]
    public void ExitFaceStillRequiresFaceAndDisplayChain()
    {
        var area = Area();
        area.ExitFaceId = -1;
        Assert.False(area.HasExitFace);
        area.ExitFaceId = 3;
        area.ExitChain.RemoveAt(1);
        Assert.False(area.HasExitFace);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnchorStateRequiresBothSelectedBindings(bool hasStart, bool hasEnd)
    {
        var area = Area();
        area.StartAnchor = new Vec2(20, 4);
        area.EndAnchor = new Vec2(20, 10);
        area.SelectedAnchorCandidate = Candidate() with
        {
            StartBinding = hasStart ? StartBinding : null,
            EndBinding = hasEnd ? EndBinding : null
        };

        Assert.Equal(hasStart && hasEnd, area.HasExitAnchors);
    }

    [Fact]
    public void CoordinatesAloneDoNotConfirmAnchors()
    {
        var area = Area();
        area.StartAnchor = new Vec2(20, 4);
        area.EndAnchor = new Vec2(20, 10);
        Assert.False(area.HasExitAnchors);
        area.SelectedAnchorCandidate = Candidate();
        Assert.True(area.HasExitAnchors);
        area.ExitEdgeRefs.Clear();
        Assert.False(area.HasExitAnchors);
    }

    [Fact]
    public void SelectionEvaluatesSavedParametersWithoutProjectingOrSearching()
    {
        var area = Area();
        var candidate = Candidate();
        area.AnchorCandidates.Add(candidate);

        Assert.True(Select(area, candidate, out string error), error);
        Assert.Same(candidate, area.SelectedAnchorCandidate);
        Assert.Equal(candidate.Start, area.StartAnchor);
        Assert.Equal(candidate.End, area.EndAnchor);
        Assert.Equal(new[] { StartBinding, EndBinding },
            ((RecordingBoundary)area.ExitBoundary).Evaluated);
    }

    [Fact]
    public void EqualCoordinatesDoNotAllowSubstitutionOfAnotherCandidateObject()
    {
        var area = Area();
        var current = Candidate();
        area.AnchorCandidates.Add(current);
        Assert.True(Select(area, current, out _));
        var substitute = current with { StartBinding = StartBinding with { EdgeKey = 22 } };

        Assert.False(Select(area, substitute, out string error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Same(current, area.SelectedAnchorCandidate);
        Assert.Equal(current.Start, area.StartAnchor);
        Assert.Equal(current.End, area.EndAnchor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("duplicate")]
    public void EmptyOrDuplicateIdentityCannotBeSelected(string id)
    {
        var area = Area();
        var candidate = Candidate(id);
        area.AnchorCandidates.Add(candidate);
        if (id.Length > 0) area.AnchorCandidates.Add(candidate with { EndBinding = StartBinding });

        Assert.False(Select(area, candidate, out _));
        Assert.Null(area.SelectedAnchorCandidate);
        Assert.Null(area.StartAnchor);
        Assert.Null(area.EndAnchor);
        Assert.Empty(((RecordingBoundary)area.ExitBoundary).Evaluated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingBindingIsRejectedBeforeEvaluation(bool startMissing)
    {
        var area = Area();
        var candidate = Candidate() with
        {
            StartBinding = startMissing ? null : StartBinding,
            EndBinding = startMissing ? EndBinding : null
        };
        area.AnchorCandidates.Add(candidate);

        Assert.False(Select(area, candidate, out _));
        Assert.Null(area.SelectedAnchorCandidate);
        Assert.Empty(((RecordingBoundary)area.ExitBoundary).Evaluated);
    }

    [Theory]
    [InlineData(ExitBoundaryFailureReason.StaleContext)]
    [InlineData(ExitBoundaryFailureReason.MissingReference)]
    [InlineData(ExitBoundaryFailureReason.MultipleIntersections)]
    public void FailedEvaluationPreservesPreviousSelectionAndCommittedReferences(ExitBoundaryFailureReason failure)
    {
        var area = Area();
        var original = Candidate();
        area.AnchorCandidates.Add(original);
        Assert.True(Select(area, original, out _));
        var circuits = area.Circuits;
        var ids = area.NativePieceIds;
        var lengths = area.RoutePieceLengths;
        area.CommittedMapping = CommittedRouteContext.Capture(area);
        var mapping = area.CommittedMapping;
        var candidate = Candidate("replacement");
        area.AnchorCandidates.Add(candidate);
        ((RecordingBoundary)area.ExitBoundary).Failure = failure;

        Assert.False(Select(area, candidate, out string error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Same(original, area.SelectedAnchorCandidate);
        Assert.Equal(original.Start, area.StartAnchor);
        Assert.Equal(original.End, area.EndAnchor);
        Assert.Same(circuits, area.Circuits);
        Assert.Same(ids, area.NativePieceIds);
        Assert.Same(lengths, area.RoutePieceLengths);
        Assert.Same(mapping, area.CommittedMapping);
    }

    [Fact]
    public void EvaluatedCoordinateMismatchCannotConfirmACandidate()
    {
        var area = Area();
        var candidate = Candidate() with { Start = new Vec2(20.001, 4) };
        area.AnchorCandidates.Add(candidate);

        Assert.False(Select(area, candidate, out _));
        Assert.Null(area.SelectedAnchorCandidate);
        Assert.Null(area.StartAnchor);
    }

    [Fact]
    public void EvaluatedBindingMismatchCannotConfirmACandidate()
    {
        var area = Area();
        var candidate = Candidate();
        area.AnchorCandidates.Add(candidate);
        ((RecordingBoundary)area.ExitBoundary).SubstituteBinding = true;

        Assert.False(Select(area, candidate, out _));
        Assert.Null(area.SelectedAnchorCandidate);
    }

    [Fact]
    public void ExclusionConversionAndSelectionPreserveCandidateIdentityAndBindings()
    {
        var area = Area();
        area.Exclusions.Add((1, 1, 2, 2));
        var plan = new ExcludedAnchorCandidate(new(20, 4), new(20, 10), 0,
            Array.Empty<SerpentineLaneNode>(), Array.Empty<int>(), Array.Empty<bool>(), 0)
        {
            CandidateId = "excluded-1", StartBinding = StartBinding, EndBinding = EndBinding
        };
        var candidate = plan.ToAnchoredCandidate();
        area.ExcludedCandidates.Add(plan);
        area.AnchorCandidates.Add(candidate);

        Assert.Equal(plan.CandidateId, candidate.CandidateId);
        Assert.Equal(plan.StartBinding, candidate.StartBinding);
        Assert.Equal(plan.EndBinding, candidate.EndBinding);
        Assert.True(Select(area, candidate, out string error), error);
        Assert.Same(plan, area.ExcludedPlan);
        Assert.Same(candidate, area.SelectedAnchorCandidate);
    }

    [Fact]
    public void ExclusionPlanCannotBeReplacedByEqualCoordinatesWithDifferentBinding()
    {
        var area = Area();
        area.Exclusions.Add((1, 1, 2, 2));
        var candidate = Candidate();
        area.AnchorCandidates.Add(candidate);
        area.ExcludedCandidates.Add(new ExcludedAnchorCandidate(candidate.Start, candidate.End, 0,
            Array.Empty<SerpentineLaneNode>(), Array.Empty<int>(), Array.Empty<bool>(), 0)
        {
            CandidateId = candidate.CandidateId,
            StartBinding = StartBinding with { EdgeKey = 22 }, EndBinding = EndBinding
        });

        Assert.False(Select(area, candidate, out _));
        Assert.Null(area.SelectedAnchorCandidate);
        Assert.Null(area.ExcludedPlan);
    }

    [Fact]
    public void CommittedContextCopiesOriginalReferenceMetadataAndKeepsSelectedIdentity()
    {
        var area = Area();
        area.SelectedAnchorCandidate = Candidate();
        var source = area.ExitEdgeRefs[0];
        var context = CommittedRouteContext.Capture(area);
        source.EdgeKey = 999;
        source.FirstParameter = -100;
        area.ExitChain[0] = new Vec2(-1, -1);
        area.ExitEdgeRefs.Clear();

        Assert.NotSame(source, context.ExitEdgeRefs[0]);
        Assert.Equal(21, context.ExitEdgeRefs[0].EdgeKey);
        Assert.Equal(2, context.ExitEdgeRefs[0].FirstParameter);
        Assert.Equal(new Vec2(20, 2), context.ExitChain[0]);
        Assert.Same(area.ExitBoundary, context.Boundary);
        Assert.Same(area.SelectedAnchorCandidate, context.SelectedAnchorCandidate);
        Assert.Equal(StartBinding, context.SelectedAnchorCandidate.StartBinding);
    }

    [Fact]
    public void CircuitExitPointRetainsOriginalParameterAndOrientation()
    {
        var binding = StartBinding with { Parameter = 5.25, Reversed = true };
        var moved = new CircuitExitPoint(CircuitExitRole.End, new Vec2(20, 5.25), 4, 5.25)
        { Binding = binding };

        Assert.Equal(binding, moved.Binding);
        Assert.Equal(binding, (moved with { Role = CircuitExitRole.Start }).Binding);
        Assert.Null(new CircuitExitPoint(CircuitExitRole.End, moved.Point, 4, 5.25).Binding);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CadAdapterRejectsWrongContextOrMissingReferenceBeforeNativeEvaluation(bool wrongContext)
    {
        var area = Area();
        var boundary = new CadExitBoundary2D(11, 7, area.ExitEdgeRefs);
        var invalid = wrongContext
            ? StartBinding with { WorkingContextId = 12 }
            : StartBinding with { EdgeKey = 99 };

        var result = boundary.Evaluate(invalid);
        Assert.False(result.Succeeded);
        Assert.Equal(wrongContext ? ExitBoundaryFailureReason.StaleContext :
            ExitBoundaryFailureReason.MissingReference, result.Failure);
    }

    [Fact]
    public void CadBoundaryCopyPreservesTopologySignature()
    {
        var source = new ExitEdgeReference
        {
            Revision = 7,
            EdgeKey = 21,
            EdgeSignature = "edge-signature",
            FaceId = 3,
            FirstParameter = 2,
            LastParameter = 8,
            Reversed = true,
            StartIndex = 1,
            EndIndex = 4
        };

        ExitEdgeReference copy = CadExitBoundary2D.CopyReference(source);

        Assert.NotSame(source, copy);
        Assert.Equal("edge-signature", copy.EdgeSignature);
        Assert.Equal(source.EdgeKey, copy.EdgeKey);
        Assert.Equal(source.FirstParameter, copy.FirstParameter);
        Assert.Equal(source.LastParameter, copy.LastParameter);
        Assert.Equal(source.Reversed, copy.Reversed);
    }

    private sealed class RecordingBoundary : IExitBoundary2D
    {
        public List<ExitEdgeBinding> Evaluated { get; } = new();
        public ExitBoundaryFailureReason? Failure { get; set; }
        public bool SubstituteBinding { get; set; }

        public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding)
        {
            Evaluated.Add(binding);
            if (Failure.HasValue) return ExitBoundaryResolution.Fail(Failure.Value);
            if (binding != StartBinding && binding != EndBinding)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.MissingReference);
            return ExitBoundaryResolution.Ok(new(new Vec2(20, binding.Parameter), -1, binding.Parameter)
            {
                Binding = SubstituteBinding ? binding with { EdgeKey = 999 } : binding
            });
        }

        public ExitBoundaryResolution Project(Vec2 point, double tolerance) =>
            throw new InvalidOperationException("Selection must not search for a different edge.");
        public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance) =>
            throw new InvalidOperationException("Selection must not regenerate an endpoint.");
        public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance) =>
            throw new InvalidOperationException("Selection must evaluate its saved parameter directly.");
    }
}
