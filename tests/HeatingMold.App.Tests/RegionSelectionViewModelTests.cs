using System;
using System.Linq;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;

namespace HeatingMold.App.Tests;

public sealed class RegionSelectionViewModelTests
{
    [Fact]
    public void ReplaceSelection_DeduplicatesAndPreservesStableReferenceOrder()
    {
        var viewModel = new RegionSelectionViewModel();
        FaceRefId first = FaceRefId.New();
        FaceRefId second = FaceRefId.New();

        viewModel.ReplaceSelection([first, second, first]);

        Assert.Equal(new[] { first, second }, viewModel.SelectedFaceReferences);
        Assert.Equal(2, viewModel.Count);
    }

    [Fact]
    public void Toggle_ChangesSelectionByStableReference()
    {
        var viewModel = new RegionSelectionViewModel();
        FaceRefId faceReferenceId = FaceRefId.New();

        Assert.True(viewModel.Toggle(faceReferenceId));
        Assert.True(viewModel.Contains(faceReferenceId));
        Assert.False(viewModel.Toggle(faceReferenceId));
        Assert.False(viewModel.Contains(faceReferenceId));
    }

    [Fact]
    public void ReplaceSelection_RejectsEmptyStableReference()
    {
        var viewModel = new RegionSelectionViewModel();
        FaceRefId retainedFaceReferenceId = FaceRefId.New();
        viewModel.Add(retainedFaceReferenceId);

        Assert.Throws<ArgumentException>(() => viewModel.ReplaceSelection([default]));
        Assert.Equal(new[] { retainedFaceReferenceId }, viewModel.SelectedFaceReferences);
    }

    [Fact]
    public void ReplaceSelection_AcceptsItsReadOnlySelectionView()
    {
        var viewModel = new RegionSelectionViewModel();
        viewModel.ReplaceSelection([FaceRefId.New(), FaceRefId.New()]);
        FaceRefId[] originalSelection = viewModel.SelectedFaceReferences.ToArray();

        viewModel.ReplaceSelection(viewModel.SelectedFaceReferences);

        Assert.Equal(originalSelection, viewModel.SelectedFaceReferences);
    }

    [Fact]
    public void Clear_RemovesSelectionAndDerivedTopology()
    {
        var viewModel = new RegionSelectionViewModel();
        FaceRefId faceReferenceId = FaceRefId.New();
        viewModel.Add(faceReferenceId);
        ProjectionFrame projectionFrame = CreateProjectionFrame();
        viewModel.SetTopology(
            GeometryQueryResult<SelectedTopology>.Succeeded(CreateTopology(faceReferenceId)),
            projectionFrame);
        viewModel.FailureMessage = "진단";

        viewModel.Clear();

        Assert.Empty(viewModel.SelectedFaceReferences);
        Assert.Empty(viewModel.BoundaryLoops);
        Assert.Null(viewModel.TopologyResult);
        Assert.Null(viewModel.ProjectionFrame);
        Assert.Null(viewModel.FailureMessage);
        Assert.Equal(0, viewModel.Count);
    }

    [Fact]
    public void ClearTopology_KeepsFaceSelectionAndClearsItsDerivedGeometry()
    {
        var viewModel = new RegionSelectionViewModel();
        FaceRefId faceReferenceId = FaceRefId.New();
        viewModel.Add(faceReferenceId);
        GeometryQueryResult<SelectedTopology> topologyResult =
            GeometryQueryResult<SelectedTopology>.Succeeded(CreateTopology(faceReferenceId));
        ProjectionFrame projectionFrame = CreateProjectionFrame();
        viewModel.SetTopology(topologyResult, projectionFrame);
        viewModel.FailureMessage = "진단";

        viewModel.ClearTopology();

        Assert.Equal(new[] { faceReferenceId }, viewModel.SelectedFaceReferences);
        Assert.Empty(viewModel.BoundaryLoops);
        Assert.Null(viewModel.TopologyResult);
        Assert.Null(viewModel.ProjectionFrame);
        Assert.Null(viewModel.FailureMessage);
    }

    private static SelectedTopology CreateTopology(FaceRefId faceReferenceId)
    {
        var loop = new BoundaryLoop(
            BoundaryLoopId.New(),
            [
                new BoundarySegment(BoundarySegmentId.New(), new Vec2(0, 0), new Vec2(3, 0), faceReferenceId),
                new BoundarySegment(BoundarySegmentId.New(), new Vec2(3, 0), new Vec2(0, 2), faceReferenceId),
                new BoundarySegment(BoundarySegmentId.New(), new Vec2(0, 2), new Vec2(0, 0), faceReferenceId)
            ],
            isHole: false);
        var component = new TopologyComponent([faceReferenceId], [loop]);
        return new SelectedTopology([component], Array.Empty<FaceAdjacency>());
    }

    private static ProjectionFrame CreateProjectionFrame() =>
        new(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0));
}
