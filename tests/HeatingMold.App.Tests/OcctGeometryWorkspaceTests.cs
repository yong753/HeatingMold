using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>Native 후보, 관리형 Face map과 geometry snapshot 조립 흐름을 검사합니다.</summary>
[Collection("WPF Bridge")]
public sealed class OcctGeometryWorkspaceTests
{
    /// <summary>모델 원본에서 후보를 준비하고 검증 후 폐기하면 활성 문서는 유지합니다.</summary>
    [Fact]
    public void PrepareModel_BuildsCandidateSnapshotAndDisposePreservesActiveDocument()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreateCandidate(81, 4);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(801, 5);
        Bridge.PreparedCandidateFaceGeometryFeatures = CreateFaceFeatures();
        (DesignProject project, ModelSource source) = CreateProjectAndSource();
        var workspace = new OcctGeometryWorkspace();

        IPreparedGeometry prepared = workspace.PrepareModel(project, source);

        Assert.Empty(prepared.Snapshot.Curves);
        Assert.Equal(5, prepared.Snapshot.ModelRevision.Value);
        Assert.Equal(prepared.RuntimeDocument.ModelRevision, prepared.Snapshot.ModelRevision);
        Assert.Equal(prepared.CoordinateSystem!.Revision, prepared.Snapshot.ModelRevision);
        Assert.Equal(prepared.CoordinateSystem.Projection.Origin,
            prepared.EffectiveProject!.WorkingCoordinateSystem.Origin);
        Assert.Equal(0, Assert.Single(prepared.EffectiveProject.FaceReferences).ImportIndexHint);
        Assert.Equal(81, Bridge.LastPreparedCandidateFaceQueryId);
        Assert.Equal(81, Bridge.LastPreparedCandidateBoundaryQueryId);
        Assert.Equal(81, Bridge.LastPreparedCandidateSampleQueryId);
        Assert.False(File.Exists(Bridge.LastPreparedStepCandidatePath));
        Assert.Equal(1, Bridge.PreparedCandidatePathCommitCount);

        prepared.Dispose();

        Assert.Equal([(81L, 801L)], Bridge.ReleasedPreparedCandidateContexts);
        Assert.Equal([81L], Bridge.DiscardedStepCandidateIds);
        Assert.Equal(1, Bridge.GetModelRevision());
    }

    /// <summary>현재 지원하지 않는 저장 fingerprint 버전은 Native 후보 확정 전에 거부합니다.</summary>
    [Fact]
    public void Restore_RejectsUnsupportedFingerprintVersionAndDiscardsCandidate()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreateCandidate(86, 4);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(806, 5);
        Bridge.PreparedCandidateFaceGeometryFeatures = CreateFaceFeatures();
        (DesignProject project, ModelSource source) = CreateProjectAndSource(
            faceFingerprintVersion: "face-fingerprint-legacy");
        var workspace = new OcctGeometryWorkspace();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => workspace.Restore(project, source));

        Assert.Contains("UnsupportedFaceFingerprintVersion", exception.Message);
        Assert.Contains(FaceReferenceFeatureBuilder.FingerprintVersion, exception.Message);
        Assert.Equal([86L], Bridge.DiscardedStepCandidateIds);
        Assert.Equal(1, Bridge.GetModelRevision());
    }

    /// <summary>발열·연결 경로의 exact 곡선을 후보에 넣고 측정 snapshot과 함께 확정합니다.</summary>
    [Fact]
    public void PrepareModel_StagesAndPublishesHeatingAndConnectionPaths()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreateCandidate(84, 4);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(804, 5);
        Bridge.PreparedCandidateFaceGeometryFeatures = CreateFaceFeatures();
        Bridge.NextPreparedStepPublishResult = new ModelLoadResult
        {
            Status = ModelLoadStatus.Committed,
            ModelRevision = 5
        };
        (DesignProject project, ModelSource source) = CreateProjectAndSource(
            includeHeatingPath: true,
            includeConnectionPath: true);
        var featureReader = new FaceFeatureBridgeAdapter();
        Assert.True(featureReader.TryReadPreparedCandidateFeatures(
            84, out IReadOnlyList<NativeFaceFeature> nativeFeatures, out string featureDiagnostic), featureDiagnostic);
        project = AddFaceReference(project, source.Identity, Assert.Single(nativeFeatures));
        var workspace = new OcctGeometryWorkspace();

        using IPreparedGeometry prepared = workspace.Restore(project, source);

        Assert.Equal(3, prepared.Snapshot.Curves.Count);
        HeatingPathSegment sourceSegment = Assert.Single(project.HeatingPaths).Segments[0];
        Assert.Contains(prepared.Snapshot.Curves,
            curve => curve.HeatingPathSegmentId == sourceSegment.Id && curve.Definition == sourceSegment.Curve);
        Assert.Equal(2, Assert.Single(project.ConnectionPaths).Segments.Count);
        Assert.Equal(3, Bridge.PreparedCandidateExactCurveStageCount);
        Assert.Equal("ConnectionPathSegment", Bridge.LastProjectedPathKind);
        Assert.Equal([int.MinValue, int.MinValue + 1, int.MinValue + 2], Bridge.LastPreparedCandidatePathStagedIds);
        Assert.Equal(1, Bridge.PreparedCandidatePathCommitCount);

        GeometryCommitReceipt receipt = prepared.Publish();

        Assert.True(receipt.IsCommitted);
        Assert.Equal(5, receipt.RuntimeDocument!.ModelRevision.Value);
        Assert.Equal([int.MinValue, int.MinValue + 1, int.MinValue + 2], Bridge.LastPreparedCandidatePathStagedIds);
        Assert.Equal(3, Bridge.LastPreparedCandidatePathFinalIds.Length);
        Assert.DoesNotContain(int.MinValue, Bridge.LastPreparedCandidatePathFinalIds);
    }

    /// <summary>비활성 회전 구간도 안정 ID와 Native 표현을 유지해 다시 켤 수 있습니다.</summary>
    [Fact]
    public void Restore_StagesInactiveTurnWithoutAddingItToGeometrySnapshot()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreateCandidate(85, 4);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(805, 5);
        Bridge.PreparedCandidateFaceGeometryFeatures = CreateFaceFeatures();
        Bridge.NextPreparedStepPublishResult = new ModelLoadResult
        {
            Status = ModelLoadStatus.Committed,
            ModelRevision = 5
        };
        (DesignProject project, ModelSource source) = CreateProjectAndSource(
            includeHeatingPath: true,
            includeInactiveTurn: true);
        var featureReader = new FaceFeatureBridgeAdapter();
        Assert.True(featureReader.TryReadPreparedCandidateFeatures(
            85, out IReadOnlyList<NativeFaceFeature> nativeFeatures, out string featureDiagnostic), featureDiagnostic);
        project = AddFaceReference(project, source.Identity, Assert.Single(nativeFeatures));
        HeatingPathSegment inactiveTurn = Assert.Single(project.HeatingPaths).Segments[0];
        var workspace = new OcctGeometryWorkspace();

        using IPreparedGeometry prepared = workspace.Restore(project, source);

        Assert.Empty(prepared.Snapshot.Curves);
        Assert.Equal(1, Bridge.PreparedCandidateExactCurveStageCount);
        GeometryCommitReceipt receipt = prepared.Publish();

        Assert.True(receipt.IsCommitted);
        Assert.True(workspace.TryGetActiveRuntimeId(inactiveTurn.Id, out int runtimeId));
        Assert.Contains((runtimeId, false), Bridge.TurnSegmentEnabledCalls);
        Assert.True(workspace.TryResolveActiveHeatingPathSegmentId(runtimeId, out HeatingPathSegmentId resolvedId));
        Assert.Equal(inactiveTurn.Id, resolvedId);
    }

    /// <summary>Native publish 후처리 실패를 commit된 문서와 복구 필요 상태로 반환합니다.</summary>
    [Fact]
    public void Publish_PreservesCommittedRuntimeDocumentWhenPresentationIsOutOfSync()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreateCandidate(82, 5);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(802, 6);
        Bridge.PreparedCandidateFaceGeometryFeatures = CreateFaceFeatures();
        Bridge.NextPreparedStepPublishResult = new ModelLoadResult
        {
            Status = ModelLoadStatus.CommittedPresentationOutOfSync,
            ModelRevision = 6,
            Diagnostic = "ActiveModelPresentationFailed"
        };
        (DesignProject project, ModelSource source) = CreateProjectAndSource();
        var workspace = new OcctGeometryWorkspace();
        IPreparedGeometry prepared = workspace.PrepareModel(project, source);

        GeometryCommitReceipt receipt = prepared.Publish();
        prepared.Dispose();

        Assert.True(receipt.IsCommitted);
        Assert.True(receipt.PresentationOutOfSync);
        Assert.Equal(82, Bridge.LastPublishedStepCandidateId);
        Assert.Equal(6, receipt.RuntimeDocument!.ModelRevision.Value);
        Assert.Equal(6, Bridge.GetModelRevision());
        Assert.Equal([(82L, 802L)], Bridge.ReleasedPreparedCandidateContexts);
        Assert.Empty(Bridge.DiscardedStepCandidateIds);

        Bridge.NextPreparedStepCandidateResult = CreateCandidate(83, 6);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(803, 7);
        IPreparedGeometry nextCandidate = workspace.Prepare(project);

        Assert.Equal(83, Bridge.LastPreparedCandidateFaceQueryId);
        Assert.Equal(83, Bridge.LastPreparedCandidateContextCreationCandidateId);
        nextCandidate.Dispose();
        Assert.Equal([83L], Bridge.DiscardedStepCandidateIds);
    }

    private static (DesignProject Project, ModelSource Source) CreateProjectAndSource(
        bool includeHeatingPath = false,
        bool includeConnectionPath = false,
        bool includeInactiveTurn = false,
        string faceFingerprintVersion = FaceReferenceFeatureBuilder.FingerprintVersion)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("workspace source fixture");
        string sourceHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var identity = new ModelIdentity(
            sourceHash,
            sourceUnitToMillimeter: 1,
            importerName: "STEP",
            importerVersion: "reader-v1",
            faceFingerprintVersion: faceFingerprintVersion);
        var frame = new WorkingCoordinateSystemDefinition(
            default,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DesignProject emptyProject = DesignProject.CreateEmpty(ProjectId.New(), identity, frame);
        DesignProject project = includeHeatingPath || includeConnectionPath
            ? CreateProjectWithPaths(emptyProject, includeHeatingPath, includeConnectionPath, includeInactiveTurn)
            : emptyProject;
        return (project, new ModelSource(identity, bytes));
    }

    private static DesignProject CreateProjectWithPaths(
        DesignProject project,
        bool includeHeatingPath,
        bool includeConnectionPath,
        bool includeInactiveTurn = false)
    {
        HeatingPath[] heatingPaths = includeHeatingPath
            ? [includeInactiveTurn ? CreateInactiveTurnPath() : CreateHeatingPath()]
            : Array.Empty<HeatingPath>();
        ConnectionPath[] connectionPaths = includeConnectionPath
            ? [CreateConnectionPath()]
            : Array.Empty<ConnectionPath>();

        return new DesignProject(
            project.Id, project.SchemaVersion, project.Revision, project.Name,
            project.CreatedUtc, project.ModifiedUtc, project.SourceModel,
            project.WorkingCoordinateSystem, project.FaceReferences, project.HeatingRegions,
            project.WireSpecification, project.PlacementConstraints, heatingPaths, project.Circuits,
            project.ConnectionPoints, connectionPaths, project.Rules);
    }

    /// <summary>기존 곡선과 절점을 유지하면서 현재 상태만 비활성 회전 구간으로 만든 경로입니다.</summary>
    private static HeatingPath CreateInactiveTurnPath()
    {
        HeatingPath source = CreateHeatingPath();
        HeatingPathSegment original = source.Segments.Single();
        var inactiveTurn = new HeatingPathSegment(
            original.Id,
            HeatingPathSegmentKind.Turn,
            original.StartControlPointId,
            original.EndControlPointId,
            original.Sequence,
            isActive: false,
            curve: original.Curve);

        return new HeatingPath(
            source.Id,
            source.GenerationSource,
            source.ControlPoints,
            source.GeneratedSegments,
            [inactiveTurn]);
    }

    private static HeatingPath CreateHeatingPath()
    {
        FaceRefId faceId = FaceRefId.New();
        var anchor = new SurfaceAnchor(faceId, default, default, default);
        var exitSide = new ExitSideReference([
            new BoundarySegmentRange(BoundarySegmentId.New(), 0, 1)]);
        var constraints = new PlacementConstraints(
            new LengthMm(1), new LengthMm(1), new LengthMm(0), PlacementDirection.AlongX,
            anchor, anchor, exitSide);
        var wire = new WireSpecification("SUS 316L", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var generation = new HeatingPathGenerationSource(RegionId.New(), constraints, wire, "path-v1");
        var start = new ControlPoint(ControlPointId.New(), new Vec3(0, 0, 0));
        var end = new ControlPoint(ControlPointId.New(), new Vec3(10, 0, 0));
        var segment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            start.Id, end.Id, 0, isActive: true,
            curve: new LineCurveDefinition(start.Position, end.Position));
        return new HeatingPath(HeatingPathId.New(), generation, [start, end], [segment], [segment]);
    }

    private static ConnectionPath CreateConnectionPath()
    {
        var onMoldSegment = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 0, ConductorRole.HeatingWireExtension,
            GeometryDomain.OnMold, new LengthMm(0),
            new LineCurveDefinition(new Vec3(10, 0, 0), new Vec3(12, 0, 0)));
        var externalSegment = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 1, ConductorRole.SeparateConnectionWire,
            GeometryDomain.External, new LengthMm(0),
            new LineCurveDefinition(new Vec3(12, 0, 0), new Vec3(15, 0, 0)),
            isMoldExitTransition: true);
        return new ConnectionPath(
            ConnectionPathId.New(), ConnectionPointId.New(), [onMoldSegment, externalSegment]);
    }

    private static DesignProject AddFaceReference(
        DesignProject project,
        ModelIdentity modelIdentity,
        NativeFaceFeature nativeFeature)
    {
        var faceReference = new FaceReference(
            FaceRefId.New(),
            modelIdentity.SourceSha256,
            nativeFeature.NativeFaceIndex,
            nativeFeature.GeometryKind,
            nativeFeature.SurfaceArea,
            nativeFeature.Center,
            nativeFeature.BoundaryFingerprint,
            nativeFeature.AdjacentFaceFingerprints);

        return new DesignProject(
            project.Id, project.SchemaVersion, project.Revision, project.Name,
            project.CreatedUtc, project.ModifiedUtc, project.SourceModel,
            project.WorkingCoordinateSystem, [faceReference], project.HeatingRegions,
            project.WireSpecification, project.PlacementConstraints, project.HeatingPaths,
            project.Circuits, project.ConnectionPoints, project.ConnectionPaths, project.Rules);
    }

    private static ModelLoadCandidateResult CreateCandidate(long candidateId, long baseRevision) => new()
    {
        Status = ModelLoadCandidateStatus.Prepared,
        CandidateId = candidateId,
        BaseModelRevision = baseRevision
    };

    private static WorkingSurfaceContext CreateWorkingContext(long contextId, long revision) => new()
    {
        Id = contextId,
        Revision = revision,
        Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
    };

    private static FaceGeometryFeaturesResult CreateFaceFeatures() => new()
    {
        Status = FaceFeatureQueryStatus.Success,
        Features =
        [
            new FaceGeometryFeatureData
            {
                FaceIndex = 0,
                SurfaceType = "Plane",
                SurfaceAreaMm2 = 1,
                Center = [0.5, 0.5, 0],
                AdjacentFaceIndices = []
            }
        ]
    };
}
