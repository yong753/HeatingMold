using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Persistence;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Infrastructure.Export;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests;

/// <summary>프로젝트 패키지의 도메인 복원, 원본 무결성과 취소 시 파일 보존을 검증합니다.</summary>
public sealed class ProjectPackageStoreTests
{
    /// <summary>빈 설계와 원본 STEP을 ZIP 패키지로 왕복합니다.</summary>
    [Fact]
    public void ReadWrite_RoundTripsProjectAndModelSource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "fixture.hmproj");
        (DesignProject project, ModelSource source) = CreateProject();
        var snapshot = CreateSnapshot(project, source);
        var store = new ProjectPackageStore();

        OperationResult<ProjectPackageWriteReceipt> write = store.Write(path, snapshot);

        Assert.True(write.IsSuccess, string.Join(Environment.NewLine, write.Issues.SelectMany(issue => issue.Evidence)));
        ProjectPackageWriteReceipt receipt = write.GetValueOrThrow();
        Assert.True(File.Exists(path));
        Assert.Equal(project.Revision, receipt.SavedRevision);
        Assert.Equal(new FileInfo(path).Length, receipt.FileLengthBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            receipt.PackageSha256);

        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "source/model.step");
            ZipArchiveEntry manifestEntry = Assert.Single(archive.Entries.Where(entry => entry.FullName == "manifest.json"));
            using Stream manifestStream = manifestEntry.Open();
            using JsonDocument manifest = JsonDocument.Parse(manifestStream);
            Assert.Equal("HeatingMoldProject", manifest.RootElement.GetProperty("fileType").GetString());
            Assert.Equal(1, manifest.RootElement.GetProperty("packageVersion").GetInt32());
            Assert.Equal("mm", manifest.RootElement.GetProperty("units").GetProperty("length").GetString());
            Assert.Equal("mm2", manifest.RootElement.GetProperty("units").GetProperty("area").GetString());
            Assert.Equal("source/model.step", manifest.RootElement.GetProperty("source").GetProperty("path").GetString());

            ZipArchiveEntry projectEntry = Assert.Single(archive.Entries.Where(entry => entry.FullName == "project.json"));
            using Stream projectStream = projectEntry.Open();
            using JsonDocument projectJson = JsonDocument.Parse(projectStream);
            JsonElement projectRoot = projectJson.RootElement;
            Assert.Equal(project.Id.Value.ToString("D"), projectRoot.GetProperty("projectId").GetString());
            Assert.Equal(1, projectRoot.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(project.Revision.Value, projectRoot.GetProperty("designRevision").GetInt64());
            Assert.Equal(source.Identity.SourceSha256, projectRoot.GetProperty("sourceModel").GetProperty("sourceSha256").GetString());
            Assert.False(projectRoot.TryGetProperty("runtimeDocument", out _));
            Assert.False(projectRoot.TryGetProperty("nativeHandle", out _));
        }

        OperationResult<ProjectPackageReadResult> read = store.Read(path);

        Assert.True(read.IsSuccess, string.Join(Environment.NewLine, read.Issues.SelectMany(issue => issue.Evidence)));
        ProjectPackageReadResult restored = read.GetValueOrThrow();
        Assert.Equal(project.Id, restored.Project.Id);
        Assert.Equal(project.Revision, restored.Project.Revision);
        Assert.Equal(project.Name, restored.Project.Name);
        Assert.Equal(project.SourceModel, restored.Project.SourceModel);
        Assert.Equal(
            FaceReferenceFeatureBuilder.FingerprintVersion,
            restored.Project.SourceModel.FaceFingerprintVersion);
        Assert.Equal(project.WorkingCoordinateSystem, restored.Project.WorkingCoordinateSystem);
        Assert.Equal(project.Rules.GeometryTolerances, restored.Project.Rules.GeometryTolerances);
        Assert.Equal(source.Identity.SourceSha256, restored.SourceModel.Identity.SourceSha256);
        Assert.Equal(source.ModelBytes.ToArray(), restored.SourceModel.ModelBytes.ToArray());
    }

    /// <summary>여러 읽기 단위에 걸친 원본 STEP 바이트를 순서와 해시 그대로 복원합니다.</summary>
    [Fact]
    public void ReadWrite_RoundTripsModelSourceAcrossReadBufferBoundaries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "large-source.hmproj");
        string storageDirectory = Path.Combine(temporaryDirectory.Path, "session-models");
        byte[] sourceBytes = Enumerable.Range(0, 256 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();
        (DesignProject project, ModelSource source) = CreateProject(sourceBytes);
        var store = new ProjectPackageStore(inMemoryThresholdBytes: 1024, temporaryDirectory: storageDirectory);

        OperationResult<ProjectPackageWriteReceipt> write = store.Write(path, CreateSnapshot(project, source));
        Assert.True(write.IsSuccess, string.Join(Environment.NewLine, write.Issues.SelectMany(issue => issue.Evidence)));

        OperationResult<ProjectPackageReadResult> read = store.Read(path);

        Assert.True(read.IsSuccess, string.Join(Environment.NewLine, read.Issues.SelectMany(issue => issue.Evidence)));
        using ModelSource restored = read.GetValueOrThrow().SourceModel;
        Assert.Equal(source.Identity.SourceSha256, restored.Identity.SourceSha256);
        Assert.True(restored.IsTemporaryFileBacked);
        using (Stream restoredStream = restored.OpenRead())
        using (var restoredBytes = new MemoryStream())
        {
            restoredStream.CopyTo(restoredBytes);
            Assert.Equal(sourceBytes, restoredBytes.ToArray());
        }

        Assert.Single(Directory.GetFiles(storageDirectory, "*.step"));
        restored.Dispose();
        Assert.Empty(Directory.GetFiles(storageDirectory, "*.step"));
    }

    /// <summary>Face 특징, 단위, 좌표계와 도체 제원을 스키마 DTO로 왕복합니다.</summary>
    [Fact]
    public void ReadWrite_RoundTripsFaceFeaturesAndWireSpecification()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "features.hmproj");
        (DesignProject baseProject, ModelSource source) = CreateProject();
        FaceReference face = new(
            FaceRefId.New(),
            source.Identity.SourceSha256,
            importIndexHint: 7,
            FaceGeometryKind.Cylinder,
            new AreaMm2(12_345.67),
            new Vec3(4, 5, 6),
            "boundary-fingerprint-7",
            ["adjacent-face-a", "adjacent-face-b"]);
        BoundaryLoop originalLoop = CreateSquareLoop();
        BoundaryLoop effectiveLoop = CreateSquareLoop();
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(effectiveLoop.Segments[0].Id, 0.1, 0.9)]);
        var region = new HeatingRegion(
            RegionId.New(),
            [face.Id],
            [originalLoop],
            [],
            [],
            [effectiveLoop],
            exitSide);
        var startAnchor = new SurfaceAnchor(face.Id, new Vec2(0.1, 0.2), new Vec2(5, 6), new Vec3(5, 6, 0));
        var endAnchor = new SurfaceAnchor(face.Id, new Vec2(0.8, 0.7), new Vec2(40, 30), new Vec3(40, 30, 0));
        var constraints = new PlacementConstraints(
            new LengthMm(0.3),
            new LengthMm(2),
            new LengthMm(1.5),
            PlacementDirection.AlongY,
            startAnchor,
            endAnchor,
            exitSide);
        var wire = new WireSpecification("NiCr80", new LengthMm(1.2), new ResistanceOhmsPerMeter(4.75));
        DesignProject project = new(
            baseProject.Id,
            baseProject.SchemaVersion,
            new DesignRevision(3),
            baseProject.Name,
            baseProject.CreatedUtc,
            baseProject.ModifiedUtc,
            baseProject.SourceModel,
            baseProject.WorkingCoordinateSystem,
            [face],
            [region],
            wire,
            constraints,
            [],
            [],
            [],
            [],
            baseProject.Rules);
        var store = new ProjectPackageStore();

        OperationResult<ProjectPackageWriteReceipt> write = store.Write(path, CreateSnapshot(project, source));
        Assert.True(write.IsSuccess, string.Join(Environment.NewLine, write.Issues.SelectMany(issue => issue.Evidence)));
        OperationResult<ProjectPackageReadResult> read = store.Read(path);

        Assert.True(read.IsSuccess, string.Join(Environment.NewLine, read.Issues.SelectMany(issue => issue.Evidence)));
        using (ZipArchive archive = ZipFile.OpenRead(path))
        using (Stream projectStream = archive.GetEntry("project.json")!.Open())
        using (JsonDocument projectJson = JsonDocument.Parse(projectStream))
        {
            Assert.Equal("Cylinder", projectJson.RootElement.GetProperty("faceReferences")[0]
                .GetProperty("geometryKind").GetString());
            Assert.Equal("MoldSurfaceToWireOutsideAlongWorkingAxis", projectJson.RootElement
                .GetProperty("placementConstraints").GetProperty("insertionDepthReference").GetString());
        }

        DesignProject restored = read.GetValueOrThrow().Project;
        FaceReference restoredFace = Assert.Single(restored.FaceReferences);
        Assert.Equal(face.Id, restoredFace.Id);
        Assert.Equal(face.SourceModelSha256, restoredFace.SourceModelSha256);
        Assert.Equal(face.ImportIndexHint, restoredFace.ImportIndexHint);
        Assert.Equal(face.GeometryKind, restoredFace.GeometryKind);
        Assert.Equal(face.SurfaceArea, restoredFace.SurfaceArea);
        Assert.Equal(face.Center, restoredFace.Center);
        Assert.Equal(face.BoundaryFingerprint, restoredFace.BoundaryFingerprint);
        Assert.Equal(face.AdjacentFaceFingerprints, restoredFace.AdjacentFaceFingerprints);
        Assert.Equal(wire, restored.WireSpecification);
        HeatingRegion restoredRegion = Assert.Single(restored.HeatingRegions);
        Assert.Equal(effectiveLoop.Id, Assert.Single(restoredRegion.EffectiveBoundaryLoops).Id);
        Assert.Equal(originalLoop.SignedAreaMm2, Assert.Single(restoredRegion.OriginalBoundaryLoops).SignedAreaMm2);
        Assert.Equal(constraints.Direction, restored.PlacementConstraints!.Direction);
        Assert.Equal(constraints.InsertionDepthMm, restored.PlacementConstraints.InsertionDepthMm);
        Assert.Equal(constraints.InsertionDepthReference, restored.PlacementConstraints.InsertionDepthReference);
        Assert.Equal(project.WorkingCoordinateSystem, restored.WorkingCoordinateSystem);
        Assert.Equal(project.Rules.GeometryTolerances, restored.Rules.GeometryTolerances);
    }

    /// <summary>수동 편집 경로의 곡선 정의와 회로 연결 자료를 패키지 왕복으로 보존합니다.</summary>
    [Fact]
    public void ReadWrite_RoundTripsManualPathCurvesCircuitsAndConnections()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "manual-design.hmproj");
        (DesignProject baseProject, ModelSource source) = CreateProject();
        FaceReference face = new(
            FaceRefId.New(), source.Identity.SourceSha256, 1, FaceGeometryKind.Plane,
            new AreaMm2(6_000), new Vec3(0, 0, 0), "manual-face", []);
        BoundaryLoop boundary = CreateSquareLoop();
        BoundaryLoop effectiveBoundary = CreateSquareLoop();
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(effectiveBoundary.Segments[0].Id, 0.2, 0.8)]);
        var region = new HeatingRegion(
            RegionId.New(), [face.Id], [boundary], [], [], [effectiveBoundary], exitSide);
        SurfaceAnchor startAnchor = new(face.Id, new Vec2(0.1, 0.2), new Vec2(10, 20), new Vec3(10, 20, 0));
        SurfaceAnchor endAnchor = new(face.Id, new Vec2(0.8, 0.7), new Vec2(80, 70), new Vec3(80, 70, 0));
        WireSpecification wire = new("NiCr80", new LengthMm(1.2), new ResistanceOhmsPerMeter(4.75));
        var placement = new PlacementConstraints(
            new LengthMm(1.5), new LengthMm(2), new LengthMm(0.8), PlacementDirection.AlongX,
            startAnchor, endAnchor, exitSide);

        ControlPoint[] controlPoints =
        [
            new(ControlPointId.New(), new Vec3(0, 0, 0)),
            new(ControlPointId.New(), new Vec3(10, 0, 1)),
            new(ControlPointId.New(), new Vec3(20, 5, 2)),
            new(ControlPointId.New(), new Vec3(30, 5, 3)),
            new(ControlPointId.New(), new Vec3(40, 0, 4))
        ];
        HeatingPathSegment[] generatedSegments = controlPoints.Zip(controlPoints.Skip(1))
            .Select((pair, index) => new HeatingPathSegment(
                HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                pair.First.Id, pair.Second.Id, index, true,
                new LineCurveDefinition(pair.First.Position, pair.Second.Position), index))
            .ToArray();
        ProjectionFrame arcPlane = new(new Vec3(2, 3, 4), new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        CurveDefinition[] editedCurves =
        [
            new LineCurveDefinition(controlPoints[0].Position, controlPoints[1].Position, CurveDirection.Reversed),
            new PolylineCurveDefinition(
                [controlPoints[1].Position, new Vec3(15, 3, 2), controlPoints[2].Position],
                CurveDirection.Reversed),
            new ArcCurveDefinition(arcPlane, new Vec3(20, 10, 2), new LengthMm(5), 0.25, 1.75),
            new BSplineCurveDefinition(
                2,
                [controlPoints[3].Position, new Vec3(35, 8, 5), controlPoints[4].Position],
                [0.75, 1.25, 0.75],
                [0, 1],
                [3, 3],
                isPeriodic: false,
                trimStart: 0.1,
                trimEnd: 0.9,
                direction: CurveDirection.Reversed)
        ];
        HeatingPathSegment[] editedSegments = generatedSegments.Select((segment, index) => new HeatingPathSegment(
            segment.Id,
            segment.Kind,
            index == 2 ? segment.EndControlPointId : segment.StartControlPointId,
            index == 2 ? segment.StartControlPointId : segment.EndControlPointId,
            segment.Sequence,
            isActive: true,
            editedCurves[index],
            segment.LaneIndex)).ToArray();
        var heatingPath = new HeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(region.Id, placement, wire, "manual-edit-v2"),
            controlPoints,
            generatedSegments,
            editedSegments);

        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startConnectionId = ConnectionPointId.New();
        ConnectionPointId endConnectionId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId,
            0,
            editedSegments.Select((segment, index) => new CircuitSegmentReference(segment.Id, index, index == 2)),
            startConnectionId,
            endConnectionId,
            new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(12), new PowerWatts(2_000)));
        ConnectionPoint[] connectionPoints =
        [
            new(startConnectionId, circuitId, ConnectionPointRole.Start, editedSegments[0].Id,
                controlPoints[0].Id, startAnchor, new Vec3(-10, 20, 0)),
            new(endConnectionId, circuitId, ConnectionPointRole.End, editedSegments[^1].Id,
                controlPoints[^1].Id, endAnchor, new Vec3(90, 70, 0))
        ];
        ConnectionPath[] connectionPaths = connectionPoints.Select(point => new ConnectionPath(
            ConnectionPathId.New(),
            point.Id,
            [
                new ConnectionPathSegment(
                    ConnectionPathSegmentId.New(), 0, ConductorRole.HeatingWireExtension,
                    GeometryDomain.OnMold, new LengthMm(0.15),
                    new LineCurveDefinition(point.SurfaceAnchor.ModelPoint, new Vec3(85, 70, 0))),
                new ConnectionPathSegment(
                    ConnectionPathSegmentId.New(), 1, ConductorRole.SeparateConnectionWire,
                    GeometryDomain.External, new LengthMm(0.25),
                    new LineCurveDefinition(new Vec3(85, 70, 0), point.ExternalConnectionPoint),
                    isMoldExitTransition: true)
            ]))
            .ToArray();
        DesignProject project = new(
            baseProject.Id,
            baseProject.SchemaVersion,
            new DesignRevision(4),
            baseProject.Name,
            baseProject.CreatedUtc,
            baseProject.ModifiedUtc,
            baseProject.SourceModel,
            baseProject.WorkingCoordinateSystem,
            [face],
            [region],
            wire,
            placement,
            [heatingPath],
            [circuit],
            connectionPoints,
            connectionPaths,
            baseProject.Rules);
        var store = new ProjectPackageStore();

        OperationResult<ProjectPackageWriteReceipt> write = store.Write(path, CreateSnapshot(project, source));
        Assert.True(write.IsSuccess, string.Join(Environment.NewLine, write.Issues.SelectMany(issue => issue.Evidence)));
        OperationResult<ProjectPackageReadResult> read = store.Read(path);

        Assert.True(read.IsSuccess, string.Join(Environment.NewLine, read.Issues.SelectMany(issue => issue.Evidence)));
        DesignProject restored = read.GetValueOrThrow().Project;
        HeatingPath restoredPath = Assert.Single(restored.HeatingPaths);
        Assert.Equal(5, restoredPath.ControlPoints.Count);
        Assert.Equal(controlPoints.Select(point => point.Position), restoredPath.ControlPoints.Select(point => point.Position));
        Assert.All(restoredPath.GeneratedSegments, segment => Assert.IsType<LineCurveDefinition>(segment.Curve));
        Assert.Equal(editedSegments.Select(segment => segment.IsActive), restoredPath.Segments.Select(segment => segment.IsActive));
        Assert.Equal(editedSegments.Select(segment => segment.LaneIndex), restoredPath.Segments.Select(segment => segment.LaneIndex));

        LineCurveDefinition restoredLine = Assert.IsType<LineCurveDefinition>(restoredPath.Segments[0].Curve);
        Assert.Equal(CurveDirection.Reversed, restoredLine.Direction);
        PolylineCurveDefinition restoredPolyline = Assert.IsType<PolylineCurveDefinition>(restoredPath.Segments[1].Curve);
        Assert.Equal(((PolylineCurveDefinition)editedCurves[1]).Points, restoredPolyline.Points);
        ArcCurveDefinition restoredArc = Assert.IsType<ArcCurveDefinition>(restoredPath.Segments[2].Curve);
        Assert.Equal(arcPlane.Origin, restoredArc.Plane.Origin);
        Assert.Equal(arcPlane.AxisX, restoredArc.Plane.AxisX);
        Assert.Equal(arcPlane.Normal, restoredArc.Plane.Normal);
        Assert.Equal(new Vec3(20, 10, 2), restoredArc.Center);
        Assert.Equal(5, restoredArc.RadiusMm.Value);
        Assert.Equal(0.25, restoredArc.StartAngleRad);
        Assert.Equal(1.75, restoredArc.EndAngleRad);
        BSplineCurveDefinition restoredSpline = Assert.IsType<BSplineCurveDefinition>(restoredPath.Segments[3].Curve);
        Assert.Equal(2, restoredSpline.Degree);
        Assert.Equal(((BSplineCurveDefinition)editedCurves[3]).Poles, restoredSpline.Poles);
        Assert.Equal(new[] { 0.75, 1.25, 0.75 }, restoredSpline.Weights);
        Assert.Equal(new[] { 0d, 1d }, restoredSpline.Knots);
        Assert.Equal(new[] { 3, 3 }, restoredSpline.Multiplicities);
        Assert.False(restoredSpline.IsPeriodic);
        Assert.Equal(0.1, restoredSpline.TrimStart);
        Assert.Equal(0.9, restoredSpline.TrimEnd);
        Assert.Equal(CurveDirection.Reversed, restoredSpline.Direction);

        Circuit restoredCircuit = Assert.Single(restored.Circuits);
        Assert.Equal(circuit.Segments, restoredCircuit.Segments);
        Assert.Equal(circuit.Supply, restoredCircuit.Supply);
        Assert.Equal(circuit.StartConnectionPointId, restoredCircuit.StartConnectionPointId);
        Assert.Equal(circuit.EndConnectionPointId, restoredCircuit.EndConnectionPointId);
        Assert.Equal(connectionPoints.Select(point => point.ExternalConnectionPoint),
            restored.ConnectionPoints.Select(point => point.ExternalConnectionPoint));
        Assert.Equal(connectionPoints.Select(point => point.SurfaceAnchor),
            restored.ConnectionPoints.Select(point => point.SurfaceAnchor));
        Assert.Equal(connectionPaths.Select(path => path.ConnectionPointId),
            restored.ConnectionPaths.Select(path => path.ConnectionPointId));
        Assert.Equal(connectionPaths.SelectMany(path => path.Segments).Select(segment => segment.ConductorRole),
            restored.ConnectionPaths.SelectMany(path => path.Segments).Select(segment => segment.ConductorRole));
        Assert.Equal(connectionPaths.SelectMany(path => path.Segments).Select(segment => segment.GeometryDomain),
            restored.ConnectionPaths.SelectMany(path => path.Segments).Select(segment => segment.GeometryDomain));
        Assert.Equal(connectionPaths.SelectMany(path => path.Segments).Select(segment => segment.AdditionalAllowanceMm),
            restored.ConnectionPaths.SelectMany(path => path.Segments).Select(segment => segment.AdditionalAllowanceMm));
        Assert.Equal(connectionPaths.SelectMany(path => path.Segments).Select(segment => segment.IsMoldExitTransition),
            restored.ConnectionPaths.SelectMany(path => path.Segments).Select(segment => segment.IsMoldExitTransition));

        string malformedPath = Path.Combine(temporaryDirectory.Path, "mismatched-curve-payload.hmproj");
        File.Copy(path, malformedPath);
        MutateProjectJson(malformedPath, json =>
            json["heatingPaths"]![0]!["segments"]![0]!["curve"]!["degree"] = 2);
        OperationResult<ProjectPackageReadResult> malformedRead = store.Read(malformedPath);
        Assert.False(malformedRead.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(malformedRead.Issues).Code);

        string nullSegmentPath = Path.Combine(temporaryDirectory.Path, "null-segment.hmproj");
        File.Copy(path, nullSegmentPath);
        MutateProjectJson(nullSegmentPath, json => json["heatingPaths"]![0]!["segments"]![0] = null);
        OperationResult<ProjectPackageReadResult> nullSegmentRead = store.Read(nullSegmentPath);
        Assert.False(nullSegmentRead.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(nullSegmentRead.Issues).Code);
    }

    /// <summary>취소된 교체는 기존 패키지 바이트를 보존합니다.</summary>
    [Fact]
    public void Write_CancelledBeforeCommitLeavesExistingPackageUntouched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "fixture.hmproj");
        (DesignProject project, ModelSource source) = CreateProject();
        var store = new ProjectPackageStore();
        Assert.True(store.Write(path, CreateSnapshot(project, source)).IsSuccess);
        byte[] originalBytes = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationResult<ProjectPackageWriteReceipt> result = store.Write(
            path,
            CreateSnapshot(project, source),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("OperationCancelled", Assert.Single(result.Issues).Code);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    /// <summary>manifest에 기록된 JSON 또는 STEP 해시가 달라지면 읽기를 거부합니다.</summary>
    [Fact]
    public void Read_RejectsTamperedProjectJson()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "fixture.hmproj");
        (DesignProject project, ModelSource source) = CreateProject();
        var store = new ProjectPackageStore();
        Assert.True(store.Write(path, CreateSnapshot(project, source)).IsSuccess);
        string tamperedPath = Path.Combine(temporaryDirectory.Path, "tampered.hmproj");

        using (var input = ZipFile.OpenRead(path))
        using (var output = ZipFile.Open(tamperedPath, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry sourceEntry in input.Entries)
            {
                ZipArchiveEntry targetEntry = output.CreateEntry(sourceEntry.FullName);
                using Stream target = targetEntry.Open();
                using Stream content = sourceEntry.Open();
                if (sourceEntry.FullName == "project.json")
                {
                    using var reader = new StreamReader(content);
                    string json = reader.ReadToEnd();
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json + " ");
                    target.Write(bytes);
                }
                else
                {
                    content.CopyTo(target);
                }
            }
        }

        OperationResult<ProjectPackageReadResult> read = store.Read(tamperedPath);

        Assert.False(read.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(read.Issues).Code);
    }

    /// <summary>필수 revision 필드가 빠진 JSON을 기본값 0으로 복원하지 않습니다.</summary>
    [Fact]
    public void Read_RejectsMissingRequiredScalarField()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "missing-revision.hmproj");
        (DesignProject project, ModelSource source) = CreateProject();
        var store = new ProjectPackageStore();
        OperationResult<ProjectPackageWriteReceipt> write = store.Write(path, CreateSnapshot(project, source));
        Assert.True(write.IsSuccess);
        RemoveProjectJsonProperty(path, "designRevision");

        OperationResult<ProjectPackageReadResult> read = store.Read(path);

        Assert.False(read.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(read.Issues).Code);
    }

    /// <summary>버전 스키마에 선언되지 않은 프로젝트 필드를 거부합니다.</summary>
    [Fact]
    public void Read_RejectsUnknownProjectField()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "unknown-field.hmproj");
        (DesignProject project, ModelSource source) = CreateProject();
        var store = new ProjectPackageStore();
        OperationResult<ProjectPackageWriteReceipt> write = store.Write(path, CreateSnapshot(project, source));
        Assert.True(write.IsSuccess);
        MutateProjectJson(path, json => json["unrecognizedField"] = true);

        OperationResult<ProjectPackageReadResult> read = store.Read(path);

        Assert.False(read.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(read.Issues).Code);
    }

    /// <summary>허용하지 않은 상위 경로 항목이 있는 ZIP을 프로젝트 패키지로 읽지 않습니다.</summary>
    [Fact]
    public void Read_RejectsUnexpectedTraversalEntry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "traversal.hmproj");
        CreatePackageWithEntries(path, "manifest.json", "project.json", "source/model.step", "../escaped.step");

        OperationResult<ProjectPackageReadResult> read = new ProjectPackageStore().Read(path);

        Assert.False(read.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(read.Issues).Code);
        Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "escaped.step")));
    }

    /// <summary>같은 이름의 ZIP 항목이 중복되면 패키지 복원을 거부합니다.</summary>
    [Fact]
    public void Read_RejectsDuplicateArchiveEntry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "duplicate.hmproj");
        CreatePackageWithEntries(path, "manifest.json", "project.json", "source/model.step", "manifest.json");

        OperationResult<ProjectPackageReadResult> read = new ProjectPackageStore().Read(path);

        Assert.False(read.IsSuccess);
        Assert.Equal("PackageIntegrityMismatch", Assert.Single(read.Issues).Code);
    }

    /// <summary>receipt 메타데이터가 확정된 출력 파일 바이트와 일치하는지 확인합니다.</summary>
    private static void AssertExportReceiptMatchesFile(string path, ExportReceipt receipt)
    {
        byte[] exportedBytes = File.ReadAllBytes(path);
        string expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(exportedBytes)).ToLowerInvariant();

        Assert.Equal(exportedBytes.LongLength, receipt.FileLengthBytes);
        Assert.Equal(expectedHash, receipt.Sha256);
    }

    /// <summary>DXF에는 검증된 프로젝트 사양과 요청한 도면 레이어가 포함됩니다.</summary>
    [Fact]
    public void DxfExporter_WritesSpecificationAndExpectedLayers()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "fixture.dxf");
        (DesignProject project, ModelSource source) = CreateProject();
        ExportSnapshot snapshot = CreateExportSnapshot(project, source);
        var exporter = new DxfDesignExporter();

        OperationResult<ExportReceipt> result = exporter.Export(
            snapshot,
            new ExportOptions(ExportFormat.Dxf),
            path);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        ExportReceipt receipt = result.GetValueOrThrow();
        AssertExportReceiptMatchesFile(path, receipt);
        netDxf.DxfDocument document = netDxf.DxfDocument.Load(path);
        Assert.Contains("MOLD_OUTLINE", document.Layers.Names);
        Assert.Contains("HEAT_WIRE", document.Layers.Names);
        Assert.Contains("CONNECTION_WIRE", document.Layers.Names);
        Assert.Contains("SPECIFICATION", document.Layers.Names);
        Assert.NotEmpty(document.Entities.Texts);
        Assert.NotEmpty(document.Entities.Lines);
        Assert.Equal(netDxf.Units.DrawingUnits.Millimeters, document.DrawingVariables.InsUnits);
        string[] textValues = document.Entities.Texts.Select(text => text.Value).ToArray();
        Assert.Contains("Circuit", textValues);
        Assert.Contains("Unavailable", textValues);
        Assert.Contains("No circuits", textValues);
    }

    /// <summary>연결점 기호와 회로별 시작·종료 표식을 같은 작업 좌표계 위치에 출력합니다.</summary>
    [Fact]
    public void DxfProjectionBuilder_ProjectsAndLabelsCircuitConnectionPoints()
    {
        ExportSnapshot snapshot = CreateConnectedExportSnapshot();

        DxfProjectedDesignData projected = new DxfProjectionBuilder().Build(snapshot);

        DxfPolylineData[] markers = projected.Polylines
            .Where(item => item.LayerName == DxfLayerCatalog.ConnectionPoint)
            .ToArray();
        Assert.Equal(4, markers.Length);
        Assert.Equal(
            new Vec2[] { new(38, 40), new(42, 40) },
            markers[0].Points);
        Assert.Equal(
            new Vec2[] { new(40, 38), new(40, 42) },
            markers[1].Points);
        Assert.Equal(
            ["C1-START", "C1-END"],
            projected.Labels.Select(label => label.Value));
        Assert.All(projected.Labels, label => Assert.Equal(DxfLayerCatalog.ConnectionPoint, label.LayerName));
    }

    /// <summary>연결점 표식과 이름을 저장한 DXF에서 다시 읽을 수 있습니다.</summary>
    [Fact]
    public void DxfExporter_WritesConnectionPointSymbolsAndLabels()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "connection-points.dxf");
        ExportSnapshot snapshot = CreateConnectedExportSnapshot();

        OperationResult<ExportReceipt> result = new DxfDesignExporter().Export(
            snapshot,
            new ExportOptions(ExportFormat.Dxf),
            path);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        netDxf.DxfDocument document = netDxf.DxfDocument.Load(path);
        Assert.Contains(DxfLayerCatalog.ConnectionPoint, document.Layers.Names);
        Assert.Equal(4, document.Entities.Polylines2D.Count(
            polyline => polyline.Layer.Name == DxfLayerCatalog.ConnectionPoint));
        string[] labels = document.Entities.Texts
            .Where(text => text.Layer.Name == DxfLayerCatalog.ConnectionPoint)
            .Select(text => text.Value)
            .ToArray();
        Assert.Equal(["C1-START", "C1-END"], labels);
    }

    /// <summary>임시 DXF 작성 뒤 취소되면 이전 목적 파일을 보존하고 임시 파일을 지웁니다.</summary>
    [Fact]
    public void DxfExporter_CancellationAfterTemporaryWritePreservesExistingFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "cancelled.dxf");
        const string previousContents = "previous DXF";
        File.WriteAllText(path, previousContents);
        (DesignProject project, ModelSource source) = CreateProject();
        ExportSnapshot snapshot = CreateExportSnapshot(project, source);
        using var cancellation = new CancellationTokenSource();
        var exporter = new DxfDesignExporter(
            projectionBuilder: null,
            specificationBuilder: null,
            saveDocument: (document, temporaryPath) =>
            {
                document.Save(temporaryPath);
                cancellation.Cancel();
            });

        OperationResult<ExportReceipt> result = exporter.Export(
            snapshot,
            new ExportOptions(ExportFormat.Dxf),
            path,
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("OperationCancelled", Assert.Single(result.Issues).Code);
        Assert.Equal(previousContents, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(temporaryDirectory.Path));
    }

    /// <summary>임시 DXF 저장 오류가 발생하면 이전 목적 파일을 보존합니다.</summary>
    [Fact]
    public void DxfExporter_WriteFailurePreservesExistingFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "failed.dxf");
        const string previousContents = "previous DXF";
        File.WriteAllText(path, previousContents);
        (DesignProject project, ModelSource source) = CreateProject();
        ExportSnapshot snapshot = CreateExportSnapshot(project, source);
        var exporter = new DxfDesignExporter(
            projectionBuilder: null,
            specificationBuilder: null,
            saveDocument: (document, temporaryPath) =>
            {
                document.Save(temporaryPath);
                throw new IOException("fixture write failure");
            });

        OperationResult<ExportReceipt> result = exporter.Export(
            snapshot,
            new ExportOptions(ExportFormat.Dxf),
            path);

        Assert.False(result.IsSuccess);
        Assert.Equal("DxfExportFailed", Assert.Single(result.Issues).Code);
        Assert.Equal(previousContents, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(temporaryDirectory.Path));
    }

    /// <summary>파일 해시를 계산하는 도중 취소되면 다음 청크를 읽기 전에 중단합니다.</summary>
    [Fact]
    public void AtomicExportFile_HashChecksCancellationBetweenReads()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancellationOnReadStream(new byte[128 * 1024], cancellation);

        Assert.Throws<OperationCanceledException>((Action)(() =>
            AtomicExportFile.ComputeSha256(stream, cancellation.Token)));

        Assert.True(cancellation.IsCancellationRequested);
    }

    /// <summary>DXF 사양표는 결과가 없는 면적이나 회로를 0 또는 합격으로 표시하지 않습니다.</summary>
    [Fact]
    public void SpecificationBuilder_ReportsUnavailableValuesWithoutInventingResults()
    {
        (DesignProject project, ModelSource source) = CreateProject();
        ExportSnapshot snapshot = CreateExportSnapshot(project, source);

        DxfSpecificationTable table = new SpecificationDataBuilder().Build(snapshot);

        Assert.Contains(table.Rows, row => row.Item == "Applied area" && row.Value == "Unavailable");
        Assert.Contains(table.Rows, row => row.Item == "Circuit status" && row.Value == "Unavailable");
        Assert.DoesNotContain(table.Rows, row => row.Value == "Pass");
        Assert.Contains(table.Notes, note => note.Contains("measured in 3D", StringComparison.Ordinal));
    }

    /// <summary>기존 HLR 정사영을 레이어와 치수 엔티티를 갖춘 DXF로 저장합니다.</summary>
    [Fact]
    public void OrthographicDxfExporter_WritesVisibleHiddenAndHeatingLayers()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "orthographic.dxf");
        var view = new OrthographicDxfView(
            [new OrthographicDxfPolyline([new Vec2(10, 5), new Vec2(20, 5)])],
            [new OrthographicDxfPolyline([new Vec2(10, 10), new Vec2(20, 10)])],
            [],
            [new OrthographicDxfPolyline([new Vec2(12, 7), new Vec2(18, 7)])]);

        OperationResult<ExportReceipt> result = new OrthographicDxfExporter()
            .ExportViews(path, [view]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        AssertExportReceiptMatchesFile(path, result.GetValueOrThrow());
        netDxf.DxfDocument document = netDxf.DxfDocument.Load(path);
        Assert.Contains("MOLD_OUTLINE", document.Layers.Names);
        Assert.Contains("HIDDEN_LINE", document.Layers.Names);
        Assert.Contains("HEAT_WIRE", document.Layers.Names);
        Assert.Contains("DIMENSION", document.Layers.Names);
        Assert.NotEmpty(document.Entities.Polylines2D);
        Assert.NotEmpty(document.Entities.Dimensions);
    }

    /// <summary>STEP writer가 없을 때는 성공이나 파일 생성을 보고하지 않습니다.</summary>
    [Fact]
    public void CompositeExporter_ReportsUnavailableStepBackend()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "fixture.step");
        (DesignProject project, ModelSource source) = CreateProject();
        var exporter = new CompositeDesignExporter();

        OperationResult<ExportReceipt> result = exporter.Export(
            CreateExportSnapshot(project, source),
            new ExportOptions(ExportFormat.Step),
            path);

        Assert.False(result.IsSuccess);
        Assert.Equal("StepExportBackendUnavailable", Assert.Single(result.Issues).Code);
        Assert.False(File.Exists(path));
    }

    /// <summary>STEP payload가 회로 진행 방향과 연결부별 경로 묶음을 보존합니다.</summary>
    [Fact]
    public void StepPayloadBuilder_PreservesCircuitAndConnectionWireStructure()
    {
        ExportSnapshot snapshot = CreateConnectedExportSnapshot();

        OperationResult<IReadOnlyList<StepDesignGroup>> result = new StepDesignPayloadBuilder().Build(snapshot);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        StepDesignGroup group = Assert.Single(result.GetValueOrThrow());
        Assert.Equal(snapshot.Project.Circuits[0].Id.Value, group.CircuitId);
        Assert.Equal("C001", group.Name);
        Assert.Collection(
            group.Wires,
            heatingWire =>
            {
                Assert.Equal("C001-HEATING", heatingWire.Name);
                Assert.Single(heatingWire.Curves);
            },
            startConnectionWire =>
            {
                Assert.Equal("C001-START-CONNECTION", startConnectionWire.Name);
                Assert.Equal(2, startConnectionWire.Curves.Count);
            },
            endConnectionWire =>
            {
                Assert.Equal("C001-END-CONNECTION", endConnectionWire.Name);
                Assert.Equal(2, endConnectionWire.Curves.Count);
            });
        Assert.All(group.Wires.SelectMany(wire => wire.Curves), curve => Assert.Equal("Line", curve.Definition.Kind));
    }

    /// <summary>STEP 출력 실패가 기존 파일을 보존하고 성공하면 완성된 임시 파일로 교체됩니다.</summary>
    [Fact]
    public void StepDesignWriter_PreservesExistingFileOnFailureAndReplacesOnSuccess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "design.step");
        File.WriteAllText(path, "previous design");
        ExportSnapshot snapshot = CreateConnectedExportSnapshot();
        var bridge = new FakeStepGeometryWriterBridge
        {
            Status = StepGeometryWriteStatus.WireBuildFailed
        };
        var writer = new CompositeDesignExporter(stepWriter: new StepDesignWriter(bridge));

        OperationResult<ExportReceipt> failed = writer.Export(
            snapshot,
            new ExportOptions(ExportFormat.Step, includeSourceModel: true),
            path);

        Assert.False(failed.IsSuccess);
        Assert.Equal("StepWireBuildFailed", Assert.Single(failed.Issues).Code);
        Assert.Equal("previous design", File.ReadAllText(path));
        Assert.True(bridge.IncludeSourceModel);
        Assert.Equal(snapshot.SourceModel.ModelBytes.ToArray(), bridge.SourceModelBytes);
        Assert.Single(bridge.Groups);

        bridge.Status = StepGeometryWriteStatus.Success;
        OperationResult<ExportReceipt> succeeded = writer.Export(
            snapshot,
            new ExportOptions(ExportFormat.Step),
            path);

        Assert.True(succeeded.IsSuccess, string.Join(Environment.NewLine, succeeded.Issues.SelectMany(issue => issue.Evidence)));
        Assert.Equal("ISO-10303-21;", File.ReadAllText(path));
        ExportReceipt receipt = succeeded.GetValueOrThrow();
        AssertExportReceiptMatchesFile(path, receipt);
        Assert.Equal(ExportFormat.Step, receipt.Format);
        Assert.False(bridge.IncludeSourceModel);
    }

    /// <summary>Native STEP 출력 전에 취소되면 브리지 호출이나 파일 변경이 없습니다.</summary>
    [Fact]
    public void StepDesignWriter_CancellationDoesNotCallBridgeOrChangeDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "design.step");
        File.WriteAllText(path, "previous design");
        var bridge = new FakeStepGeometryWriterBridge();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationResult<ExportReceipt> result = new StepDesignWriter(bridge).Export(
            CreateConnectedExportSnapshot(),
            new ExportOptions(ExportFormat.Step),
            path,
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("OperationCancelled", Assert.Single(result.Issues).Code);
        Assert.False(bridge.WasCalled);
        Assert.Equal("previous design", File.ReadAllText(path));
    }

    /// <summary>Native 기록 도중 취소되면 임시 출력만 버리고 기존 목적 파일을 보존합니다.</summary>
    [Fact]
    public void StepDesignWriter_CancellationDuringBridgeCallDoesNotReplaceDestination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "design.step");
        File.WriteAllText(path, "previous design");
        using var cancellation = new CancellationTokenSource();
        var bridge = new FakeStepGeometryWriterBridge
        {
            AfterWrite = cancellation.Cancel
        };

        OperationResult<ExportReceipt> result = new StepDesignWriter(bridge).Export(
            CreateConnectedExportSnapshot(),
            new ExportOptions(ExportFormat.Step),
            path,
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("OperationCancelled", Assert.Single(result.Issues).Code);
        Assert.True(bridge.WasCalled);
        Assert.Equal("previous design", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temporaryDirectory.Path));
    }

    private static (DesignProject Project, ModelSource Source) CreateProject(byte[]? modelBytes = null)
    {
        byte[] sourceBytes = modelBytes ?? [11, 23, 37, 41, 59, 67];
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var identity = new ModelIdentity(
            hash,
            1,
            "Fixture STEP",
            "1",
            FaceReferenceFeatureBuilder.FingerprintVersion);
        var source = new ModelSource(identity, sourceBytes);
        var axis = new WorkingCoordinateSystemDefinition(
            new Vec3(1, 2, 3),
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        DateTimeOffset created = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        DesignProject project = DesignProject.CreateEmpty(
            ProjectId.New(), identity, axis, "Package fixture", created);
        return (project, source);
    }

    private sealed class FakeStepGeometryWriterBridge : IStepGeometryWriterBridge
    {
        public StepGeometryWriteStatus Status { get; set; } = StepGeometryWriteStatus.Success;
        public bool WasCalled { get; private set; }
        public bool IncludeSourceModel { get; private set; }
        public byte[] SourceModelBytes { get; private set; } = Array.Empty<byte>();
        public IReadOnlyList<StepDesignGroup> Groups { get; private set; } = Array.Empty<StepDesignGroup>();
        public Action? AfterWrite { get; init; }

        public StepGeometryWriteResult Write(
            string outputPath,
            ModelSource sourceModel,
            IReadOnlyList<StepDesignGroup> groups,
            bool includeSourceModel,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            IncludeSourceModel = includeSourceModel;
            using (Stream source = sourceModel.OpenRead())
            using (var capturedSource = new MemoryStream())
            {
                source.CopyTo(capturedSource);
                SourceModelBytes = capturedSource.ToArray();
            }
            Groups = groups;
            if (Status == StepGeometryWriteStatus.Success)
                File.WriteAllText(outputPath, "ISO-10303-21;");
            AfterWrite?.Invoke();

            return Status == StepGeometryWriteStatus.Success
                ? new StepGeometryWriteResult(Status)
                : new StepGeometryWriteResult(Status, "테스트용 Native writer 실패입니다.");
        }
    }

    private static ProjectSaveSnapshot CreateSnapshot(DesignProject project, ModelSource source) =>
        new(new SessionSnapshot(
            project,
            runtimeDocument: null,
            geometry: null,
            electricalResults: [],
            areaAllocation: null,
            electricalAreaResult: null,
            currentInputDraft: null,
            dirtySinceSave: true,
            lastSavedRevision: null,
            isPresentationOutOfSync: false,
            sourceModel: source));

    private static ExportSnapshot CreateExportSnapshot(
        DesignProject project,
        ModelSource source,
        IEnumerable<CurveGeometrySnapshot>? curves = null)
    {
        var runtime = new RuntimeDocument(
            GeometryDocumentId.New(),
            new ModelRevision(2),
            source.Identity.SourceSha256);
        var stamp = new DependencyStamp(
            project.Revision,
            project.SourceModel.SourceSha256,
            new string('a', 64),
            new string('b', 64),
            "fixture-geometry-v1");
        var geometry = new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, curves ?? []);
        var session = new SessionSnapshot(
            project,
            runtime,
            geometry,
            [],
            null,
            null,
            null,
            dirtySinceSave: true,
            lastSavedRevision: null,
            isPresentationOutOfSync: false,
            sourceModel: source);

        return new ExportSnapshot(session);
    }

    private static ExportSnapshot CreateConnectedExportSnapshot()
    {
        (DesignProject baseProject, ModelSource source) = CreateProject();
        string sourceHash = baseProject.SourceModel.SourceSha256;
        FaceRefId faceId = FaceRefId.New();
        var face = new FaceReference(
            faceId,
            sourceHash,
            0,
            FaceGeometryKind.Plane,
            new AreaMm2(6_000),
            new Vec3(50, 30, 3),
            "dxf-fixture-face",
            []);
        BoundaryLoop originalBoundary = CreateSquareLoop();
        BoundaryLoop effectiveBoundary = CreateSquareLoop();
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(effectiveBoundary.Segments[0].Id, 0, 1)]);
        var region = new HeatingRegion(
            RegionId.New(), [faceId], [originalBoundary], [], [], [effectiveBoundary], exitSide);
        var startAnchor = new SurfaceAnchor(faceId, default, new Vec2(10, 20), new Vec3(11, 22, 3));
        var endAnchor = new SurfaceAnchor(faceId, default, new Vec2(30, 30), new Vec3(31, 32, 3));
        var wire = new WireSpecification("Fixture alloy", new LengthMm(0.3), new ResistanceOhmsPerMeter(100));
        var constraints = new PlacementConstraints(
            new LengthMm(1), new LengthMm(1), new LengthMm(0), PlacementDirection.AlongX,
            startAnchor, endAnchor, exitSide);
        ControlPointId startControlPointId = ControlPointId.New();
        ControlPointId endControlPointId = ControlPointId.New();
        var heatingCurve = new LineCurveDefinition(startAnchor.ModelPoint, endAnchor.ModelPoint);
        var heatingSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startControlPointId, endControlPointId, 0, true, heatingCurve);
        var heatingPath = new HeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(region.Id, constraints, wire, "dxf-fixture-v1"),
            [
                new ControlPoint(startControlPointId, startAnchor.ModelPoint),
                new ControlPoint(endControlPointId, endAnchor.ModelPoint)
            ],
            [heatingSegment],
            [heatingSegment]);

        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startConnectionPointId = ConnectionPointId.New();
        ConnectionPointId endConnectionPointId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId,
            0,
            [new CircuitSegmentReference(heatingSegment.Id, 0, isReversed: false)],
            startConnectionPointId,
            endConnectionPointId,
            new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(10)));
        ConnectionPoint[] connectionPoints =
        [
            new ConnectionPoint(
                startConnectionPointId, circuitId, ConnectionPointRole.Start,
                heatingSegment.Id, startControlPointId, startAnchor, new Vec3(41, 42, 3)),
            new ConnectionPoint(
                endConnectionPointId, circuitId, ConnectionPointRole.End,
                heatingSegment.Id, endControlPointId, endAnchor, new Vec3(51, 52, 3))
        ];
        ConnectionPath[] connectionPaths =
        [
            CreateConnectionPath(connectionPoints[0], new Vec3(13, 22, 3)),
            CreateConnectionPath(connectionPoints[1], new Vec3(33, 32, 3))
        ];
        DesignProject project = new(
            baseProject.Id,
            baseProject.SchemaVersion,
            baseProject.Revision,
            baseProject.Name,
            baseProject.CreatedUtc,
            baseProject.ModifiedUtc,
            baseProject.SourceModel,
            baseProject.WorkingCoordinateSystem,
            [face],
            [region],
            wire,
            constraints,
            [heatingPath],
            [circuit],
            connectionPoints,
            connectionPaths,
            baseProject.Rules);

        ValidationReport report = project.ValidateReferences();
        Assert.True(report.IsValid, string.Join(", ", report.Issues.Select(issue => issue.Code)));

        CurveGeometrySnapshot[] curves =
        [
            CreateCurveSnapshot(heatingSegment.Id, null, heatingCurve),
            .. connectionPaths.SelectMany(path => path.Segments)
                .Select(segment => CreateCurveSnapshot(null, segment.Id, Assert.IsType<LineCurveDefinition>(segment.Curve)))
        ];
        return CreateExportSnapshot(project, source, curves);
    }

    private static ConnectionPath CreateConnectionPath(ConnectionPoint point, Vec3 exitPoint)
    {
        var onMold = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 0, ConductorRole.HeatingWireExtension,
            GeometryDomain.OnMold, new LengthMm(0),
            new LineCurveDefinition(point.SurfaceAnchor.ModelPoint, exitPoint));
        var external = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 1, ConductorRole.SeparateConnectionWire,
            GeometryDomain.External, new LengthMm(0),
            new LineCurveDefinition(exitPoint, point.ExternalConnectionPoint),
            isMoldExitTransition: true);
        return new ConnectionPath(ConnectionPathId.New(), point.Id, [onMold, external]);
    }

    private static CurveGeometrySnapshot CreateCurveSnapshot(
        HeatingPathSegmentId? heatingSegmentId,
        ConnectionPathSegmentId? connectionSegmentId,
        LineCurveDefinition curve)
    {
        Vec3[] samples = [curve.Start, curve.End];
        return new CurveGeometrySnapshot(
            heatingSegmentId,
            connectionSegmentId,
            curve,
            new LengthMm((curve.End - curve.Start).Length),
            curve.Start,
            curve.End,
            samples,
            new CurveApproximationInfo(0.01, 0.1, 0, (curve.End - curve.Start).Length, 2, true, "dxf-fixture-v1"));
    }

    private static void CreatePackageWithEntries(string path, params string[] entryNames)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach (string entryName in entryNames)
            archive.CreateEntry(entryName, CompressionLevel.NoCompression);
    }

    private static BoundaryLoop CreateSquareLoop()
    {
        Vec2[] points = [new(0, 0), new(100, 0), new(100, 60), new(0, 60)];
        BoundarySegment[] segments = points.Select((point, index) => new BoundarySegment(
            BoundarySegmentId.New(),
            point,
            points[(index + 1) % points.Length])).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }

    private static void RemoveProjectJsonProperty(string packagePath, string propertyName)
    {
        MutateProjectJson(packagePath, project => project.Remove(propertyName));
    }

    private static void MutateProjectJson(string packagePath, Action<JsonObject> update)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ZipArchiveEntry projectEntry = archive.GetEntry("project.json")
            ?? throw new InvalidDataException("project.json 항목이 없습니다.");
        JsonObject projectJson = ReadJsonObject(projectEntry);
        update(projectJson);
        byte[] projectBytes = JsonSerializer.SerializeToUtf8Bytes(projectJson, new JsonSerializerOptions { WriteIndented = true });

        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("manifest.json 항목이 없습니다.");
        JsonObject manifestJson = ReadJsonObject(manifestEntry);
        string projectHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(projectBytes)).ToLowerInvariant();
        manifestJson["project"]!["sha256"] = projectHash;
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifestJson, new JsonSerializerOptions { WriteIndented = true });

        projectEntry.Delete();
        manifestEntry.Delete();
        WriteArchiveEntry(archive, "project.json", projectBytes);
        WriteArchiveEntry(archive, "manifest.json", manifestBytes);
    }

    private static JsonObject ReadJsonObject(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return JsonNode.Parse(stream)?.AsObject()
            ?? throw new InvalidDataException($"{entry.FullName} JSON이 비어 있습니다.");
    }

    private static void WriteArchiveEntry(ZipArchive archive, string entryName, byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        stream.Write(content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"HeatingMold-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class CancellationOnReadStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellationOnReadStream(byte[] contents, CancellationTokenSource cancellation)
            : base(contents)
        {
            _cancellation = cancellation;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = base.Read(buffer, offset, count);
            _cancellation.Cancel();
            return bytesRead;
        }
    }
}
