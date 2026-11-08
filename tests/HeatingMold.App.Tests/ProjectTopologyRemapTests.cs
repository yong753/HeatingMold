using System;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

public class ProjectTopologyRemapTests
{
    [Fact]
    public void FaceSignaturesMapToFreshRuntimeIdsBySignature()
    {
        var saved = new[]
        {
            new ProjectFaceTopologySignature { FaceId = 0, Signature = "face-a" },
            new ProjectFaceTopologySignature { FaceId = 1, Signature = "face-b" }
        };
        string[] fresh = { "face-b", "face-a" };

        Assert.True(ProjectTopologyRemap.TryMapFaceIds(saved, fresh, out int[] map, out string error), error);
        Assert.Equal(new[] { 1, 0 }, map);
    }

    [Fact]
    public void FaceSignaturesRejectMissingSignature()
    {
        var saved = new[] { new ProjectFaceTopologySignature { FaceId = 0, Signature = "face-a" } };
        string[] fresh = { "face-b" };

        Assert.False(ProjectTopologyRemap.TryMapFaceIds(saved, fresh, out _, out string error));
        Assert.Contains("찾지 못했습니다", error);
    }

    [Fact]
    public void FaceSignaturesRejectDuplicateFreshSignature()
    {
        var saved = new[]
        {
            new ProjectFaceTopologySignature { FaceId = 0, Signature = "face-a" },
            new ProjectFaceTopologySignature { FaceId = 1, Signature = "face-b" }
        };
        string[] fresh = { "face-a", "face-a" };

        Assert.False(ProjectTopologyRemap.TryMapFaceIds(saved, fresh, out _, out string error));
        Assert.Contains("중복", error);
    }

    [Fact]
    public void FaceSignaturesRejectCountMismatchAndDuplicateSavedId()
    {
        var saved = new[] { new ProjectFaceTopologySignature { FaceId = 0, Signature = "face-a" } };
        string[] fresh = { "face-a", "face-b" };
        Assert.False(ProjectTopologyRemap.TryMapFaceIds(saved, fresh, out _, out string countError));
        Assert.Contains("수가", countError);

        var duplicateIds = new[]
        {
            new ProjectFaceTopologySignature { FaceId = 0, Signature = "face-a" },
            new ProjectFaceTopologySignature { FaceId = 0, Signature = "face-b" }
        };
        string[] twoFresh = { "face-a", "face-b" };
        Assert.False(ProjectTopologyRemap.TryMapFaceIds(duplicateIds, twoFresh, out _, out string idError));
        Assert.Contains("Face ID", idError);
    }

    [Fact]
    public void ExitReferencesMapToFreshKeysAndPreserveTrimAndOrientation()
    {
        var saved = new[]
        {
            SavedReference(edgeKey: 10, faceId: 0, signature: "edge-a", first: 1, last: 2)
        };
        var fresh = new[]
        {
            FreshReference(edgeKey: 77, faceId: 1, signature: "other", first: 1, last: 2),
            FreshReference(edgeKey: 99, faceId: 1, signature: "edge-a", first: 1, last: 2)
        };

        Assert.True(ProjectTopologyRemap.TryMapExitEdgeReferences(saved, fresh, new[] { 1 },
            out ExitEdgeReference[] mapped, out string error), error);

        Assert.Single(mapped);
        Assert.Equal(99, mapped[0].EdgeKey);
        Assert.Equal(7, mapped[0].Revision);
        Assert.Equal(1, mapped[0].FaceId);
        Assert.Equal("edge-a", mapped[0].EdgeSignature);
        Assert.Equal(1, mapped[0].FirstParameter);
        Assert.Equal(2, mapped[0].LastParameter);
    }

    [Fact]
    public void ExitReferencesRejectMissingOrAmbiguousMatch()
    {
        var saved = new[]
        {
            SavedReference(edgeKey: 10, faceId: 0, signature: "edge-a", first: 1, last: 2)
        };

        var wrongTrim = new[]
        {
            FreshReference(edgeKey: 99, faceId: 1, signature: "edge-a", first: 1, last: 3)
        };
        Assert.False(ProjectTopologyRemap.TryMapExitEdgeReferences(saved, wrongTrim, new[] { 1 },
            out _, out string missingError));
        Assert.Contains("일치하는", missingError);

        var ambiguous = new[]
        {
            FreshReference(edgeKey: 99, faceId: 1, signature: "edge-a", first: 1, last: 2),
            FreshReference(edgeKey: 100, faceId: 1, signature: "edge-a", first: 1, last: 2)
        };
        Assert.False(ProjectTopologyRemap.TryMapExitEdgeReferences(saved, ambiguous, new[] { 1 },
            out _, out string ambiguousError));
        Assert.Contains("여러", ambiguousError);
    }

    [Fact]
    public void BindingUsesFreshContextAndKeyButKeepsSavedParameter()
    {
        var savedReference = SavedReference(edgeKey: 10, faceId: 0, signature: "edge-a", first: 1, last: 2);
        var savedBinding = new ProjectExitEdgeBinding
        {
            ModelRevision = 5,
            WorkingContextId = 3,
            WorkingContextRevision = 5,
            EdgeKey = 10,
            FirstParameter = 1,
            LastParameter = 2,
            Parameter = 1.5,
            Reversed = false
        };
        ExitEdgeReference freshReference = FreshReference(
            edgeKey: 99, faceId: 1, signature: "edge-a", first: 1, last: 2);

        Assert.True(ProjectTopologyRemap.TryMapExitBinding(savedBinding, savedReference, freshReference,
            freshModelRevision: 7, contextId: 8, contextRevision: 9,
            out ExitEdgeBinding mapped, out string error), error);

        Assert.Equal(7, mapped.ModelRevision);
        Assert.Equal(8, mapped.WorkingContextId);
        Assert.Equal(9, mapped.WorkingContextRevision);
        Assert.Equal(99, mapped.EdgeKey);
        Assert.Equal(1, mapped.FirstParameter);
        Assert.Equal(2, mapped.LastParameter);
        Assert.Equal(1.5, mapped.Parameter);
        Assert.False(mapped.Reversed);
    }

    [Fact]
    public void BindingRejectsMismatchedReferenceOrRevision()
    {
        var savedReference = SavedReference(edgeKey: 10, faceId: 0, signature: "edge-a", first: 1, last: 2);
        var savedBinding = new ProjectExitEdgeBinding
        {
            EdgeKey = 10,
            FirstParameter = 1,
            LastParameter = 2,
            Parameter = 1.5
        };
        ExitEdgeReference freshReference = FreshReference(
            edgeKey: 99, faceId: 1, signature: "edge-a", first: 1, last: 2);

        Assert.False(ProjectTopologyRemap.TryMapExitBinding(savedBinding, savedReference, freshReference,
            freshModelRevision: 8, contextId: 8, contextRevision: 9,
            out _, out string revisionError));
        Assert.Contains("revision", revisionError);

        var wrongTrim = savedBinding;
        wrongTrim.LastParameter = 3;
        Assert.False(ProjectTopologyRemap.TryMapExitBinding(wrongTrim, savedReference, freshReference,
            freshModelRevision: 7, contextId: 8, contextRevision: 9,
            out _, out string trimError));
        Assert.Contains("일치하지 않습니다", trimError);
    }

    [Fact]
    public void ArtificialBindingRebindsContextAndPreservesRectangle()
    {
        var saved = new ProjectArtificialEdgeBinding
        {
            WorkingContextId = 1,
            WorkingContextRevision = 2,
            MinX = -3,
            MinY = 4,
            MaxX = 5,
            MaxY = 6,
            Side = 3,
            FirstParameter = 0.25,
            LastParameter = 0.75,
            Parameter = 0.5
        };

        Assert.True(ProjectTopologyRemap.TryMapArtificialBinding(saved, contextId: 11, contextRevision: 12,
            out ArtificialEdgeBinding mapped, out string error), error);

        Assert.Equal(11, mapped.WorkingContextId);
        Assert.Equal(12, mapped.WorkingContextRevision);
        Assert.Equal(-3, mapped.MinX);
        Assert.Equal(4, mapped.MinY);
        Assert.Equal(5, mapped.MaxX);
        Assert.Equal(6, mapped.MaxY);
        Assert.Equal(3, mapped.Side);
        Assert.Equal(0.25, mapped.FirstParameter);
        Assert.Equal(0.75, mapped.LastParameter);
        Assert.Equal(0.5, mapped.Parameter);
    }

    private static ProjectExitEdgeReference SavedReference(long edgeKey, int faceId, string signature,
        double first, double last) => new()
    {
        Revision = 5,
        EdgeKey = edgeKey,
        EdgeSignature = signature,
        FaceId = faceId,
        FirstParameter = first,
        LastParameter = last,
        Reversed = false,
        StartIndex = 0,
        EndIndex = 1
    };

    private static ExitEdgeReference FreshReference(long edgeKey, int faceId, string signature,
        double first, double last) => new()
    {
        Revision = 7,
        EdgeKey = edgeKey,
        EdgeSignature = signature,
        FaceId = faceId,
        FirstParameter = first,
        LastParameter = last,
        Reversed = false,
        StartIndex = 0,
        EndIndex = 1
    };
}
