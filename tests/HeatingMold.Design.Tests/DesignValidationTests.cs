using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;
using HeatingMold.Design.Validation;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;

namespace HeatingMold.Design.Tests;

/// <summary>출구 측면·연속성·깊이·간격 검증이 허용오차를 차단 기준으로 쓰는지 확인합니다.</summary>
public sealed class DesignValidationTests
{
    /// <summary>지정된 경계 구간의 부분 범위 안에 있는 앵커만 출구 측면으로 해석합니다.</summary>
    [Fact]
    public void ExitSideResolver_RejectsPointOutsideReferencedRange()
    {
        HeatingRegion region = CreateRegion(out _, exitStart: 0.25, exitEnd: 0.75);
        BoundarySegment bottom = region.EffectiveBoundaryLoops[0].Segments[0];

        OperationResult<ResolvedExitSideLocation> inside = new ExitSideResolver().Resolve(
            region, new Vec2(50, 0), 0.01);
        OperationResult<ResolvedExitSideLocation> outside = new ExitSideResolver().Resolve(
            region, new Vec2(10, 0), 0.01);

        Assert.True(inside.IsSuccess);
        Assert.Equal(bottom.Id, inside.GetValueOrThrow().BoundarySegmentId);
        Assert.Equal(0.5, inside.GetValueOrThrow().Parameter, 6);
        Assert.False(outside.IsSuccess);
        Assert.Equal("AnchorOutsideExitSide", Assert.Single(outside.Issues).Code);
    }

    /// <summary>회로 시작·끝의 두 표면 앵커가 지정 경계 측면을 공유하면 통과합니다.</summary>
    [Fact]
    public void SameSideValidator_AcceptsBothCircuitAnchorsOnExitSide()
    {
        DesignProject project = CreateSingleCircuitProject(out _, endAnchorUv: new Vec2(80, 0));

        ValidationReport report = new SameSideValidator().Validate(project, 0.01);

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    /// <summary>끝 앵커가 같은 경계라도 지정 범위 밖이면 회로를 차단합니다.</summary>
    [Fact]
    public void SameSideValidator_RejectsEndAnchorOutsideConfiguredRange()
    {
        DesignProject project = CreateSingleCircuitProject(out _, endAnchorUv: new Vec2(95, 0));

        ValidationReport report = new SameSideValidator().Validate(project, 0.01);

        Assert.False(report.IsValid);
        Assert.Equal("CircuitEndOutsideExitSide", Assert.Single(report.Issues).Code);
    }

    /// <summary>측정된 깊이와 오차 상한이 요구값을 만족하지 않으면 오류를 반환합니다.</summary>
    [Fact]
    public void DepthValidator_RejectsMeasuredDepthOutsideErrorBound()
    {
        CurveGeometrySnapshot curve = CreateCurveSnapshot(HeatingPathSegmentId.New(), new Vec3(0, 0, -1), new Vec3(10, 0, -1));
        SurfaceAnchor anchor = CreateAnchor(FaceRefId.New(), new Vec2(0, 0));
        var queries = new GeometryQueryStub
        {
            DepthHandler = (_, _, _, _, requiredDepth, _) => GeometryQueryResult<DepthMeasurement>.Succeeded(
                new DepthMeasurement(requiredDepth, new LengthMm(1.2), new LengthMm(0.01), default, new Vec3(5, 0, -1), true))
        };

        ValidationReport report = new DepthValidator().Validate(
            [curve],
            new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10)),
            new LengthMm(1),
            [anchor],
            CreateCoordinateSystem(),
            queries,
            comparisonToleranceMm: 0.01,
            ruleVersion: "depth-test-v1");

        Assert.False(report.IsValid);
        Assert.Equal("InsertionDepthMismatch", Assert.Single(report.Issues).Code);
        Assert.Equal(1, queries.DepthCallCount);
    }

    /// <summary>평균 깊이가 요구값이어도 곡선 깊이 범위가 벗어나면 오류를 반환합니다.</summary>
    [Fact]
    public void DepthValidator_RejectsDepthEnvelopeOutsideTolerance()
    {
        CurveGeometrySnapshot curve = CreateCurveSnapshot(HeatingPathSegmentId.New(), new Vec3(0, 0, -1), new Vec3(10, 0, -1));
        SurfaceAnchor anchor = CreateAnchor(FaceRefId.New(), new Vec2(0, 0));
        var queries = new GeometryQueryStub
        {
            DepthHandler = (_, _, _, _, requiredDepth, _) => GeometryQueryResult<DepthMeasurement>.Succeeded(
                new DepthMeasurement(
                    requiredDepth,
                    new LengthMm(1),
                    new LengthMm(0.9),
                    new LengthMm(1.1),
                    new LengthMm(0.01),
                    default,
                    new Vec3(2, 0, 0),
                    new Vec3(8, 0, 0),
                    new Vec3(2, 0, -0.9),
                    new Vec3(8, 0, -1.1),
                    probeCount: 3,
                    isResolved: true,
                    isComplete: true,
                    measurementRuleVersion: "depth-envelope-test-v1"))
        };

        ValidationReport report = new DepthValidator().Validate(
            [curve],
            new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10)),
            new LengthMm(1),
            [anchor],
            CreateCoordinateSystem(),
            queries,
            comparisonToleranceMm: 0.01,
            ruleVersion: "depth-test-v1",
            selectedFacesBySegment: new Dictionary<HeatingPathSegmentId, IReadOnlyList<FaceRefId>>
            {
                [curve.HeatingPathSegmentId!.Value] = [anchor.FaceReferenceId]
            });

        Assert.False(report.IsValid);
        ValidationIssue issue = Assert.Single(report.Issues);
        Assert.Equal("InsertionDepthMismatch", issue.Code);
        Assert.Equal(new Vec3(8, 0, -1.1), issue.Location3D);
        Assert.Equal(1.1, issue.MeasuredValue!.Value, precision: 10);
        Assert.Equal(anchor.FaceReferenceId, Assert.Single(queries.LastSelectedDepthFaces!));
    }

    /// <summary>앵커 단면만 반환하는 adapter는 곡선 envelope 검증에서 성공하지 않습니다.</summary>
    [Fact]
    public void DepthValidator_RejectsSingleSectionForRegionEnvelopeRequest()
    {
        CurveGeometrySnapshot curve = CreateCurveSnapshot(HeatingPathSegmentId.New(), new Vec3(0, 0, -1), new Vec3(10, 0, -1));
        SurfaceAnchor anchor = CreateAnchor(FaceRefId.New(), new Vec2(0, 0));
        var singleSectionMeasurement = new DepthMeasurement(
            new LengthMm(1),
            new LengthMm(1),
            new LengthMm(0.01),
            default,
            new Vec3(0, 0, -1),
            isResolved: true);
        var queries = new GeometryQueryStub
        {
            DepthHandler = (_, _, _, _, _, _) => GeometryQueryResult<DepthMeasurement>.Succeeded(singleSectionMeasurement)
        };

        ValidationReport report = new DepthValidator().Validate(
            [curve],
            new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10)),
            new LengthMm(1),
            [anchor],
            CreateCoordinateSystem(),
            queries,
            comparisonToleranceMm: 0.01,
            ruleVersion: "depth-test-v1",
            selectedFacesBySegment: new Dictionary<HeatingPathSegmentId, IReadOnlyList<FaceRefId>>
            {
                [curve.HeatingPathSegmentId!.Value] = [anchor.FaceReferenceId]
            });

        Assert.False(report.IsValid);
        Assert.Equal("InsertionDepthUnresolved", Assert.Single(report.Issues).Code);
        Assert.False(singleSectionMeasurement.IsCurveEnvelope);
    }

    /// <summary>Native 깊이 질의가 Unresolved를 반환하면 경고가 아닌 차단 오류로 보존합니다.</summary>
    [Fact]
    public void DepthValidator_TreatsUnresolvedNativeResultAsBlockingError()
    {
        CurveGeometrySnapshot curve = CreateCurveSnapshot(HeatingPathSegmentId.New(), new Vec3(0, 0, -1), new Vec3(10, 0, -1));
        SurfaceAnchor anchor = CreateAnchor(FaceRefId.New(), new Vec2(0, 0));
        var queries = new GeometryQueryStub
        {
            DepthHandler = (_, _, _, _, _, _) => GeometryQueryResult<DepthMeasurement>.Unresolved([])
        };

        ValidationReport report = new DepthValidator().Validate(
            [curve],
            new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10)),
            new LengthMm(1),
            [anchor],
            CreateCoordinateSystem(),
            queries,
            comparisonToleranceMm: 0.01,
            ruleVersion: "depth-test-v1");

        Assert.False(report.IsValid);
        Assert.Equal("DepthQueryUnresolved", Assert.Single(report.Issues).Code);
    }

    /// <summary>최단거리 오차 범위가 g 경계를 가로지르면 정밀화가 필요하다고 차단합니다.</summary>
    [Fact]
    public void ClearanceValidator_RejectsMeasurementWhoseErrorCrossesRequiredGap()
    {
        CurveGeometrySnapshot curve = CreateCurveSnapshot(HeatingPathSegmentId.New(), new Vec3(0, 0, -1), new Vec3(10, 0, -1));
        var queries = new GeometryQueryStub
        {
            ClearanceHandler = (_, _, _, _, _) => GeometryQueryResult<ClearanceMeasurement>.Succeeded(
                new ClearanceMeasurement(new LengthMm(5.4), new LengthMm(4.9), new Vec3(5, 0, 0), new Vec3(5, 5.4, 0), new LengthMm(0.2), true))
        };

        ValidationReport report = new ClearanceValidator().Validate(
            [curve],
            new LengthMm(0.5),
            new LengthMm(5),
            GeometryToleranceProfile.Initial,
            queries,
            "clearance-test-v1");

        Assert.False(report.IsValid);
        Assert.Equal("MinimumClearGapUnresolved", Assert.Single(report.Issues).Code);
        Assert.Equal(1, queries.ClearanceCallCount);
    }

    /// <summary>간격 검증기가 설계에서 얻은 안정 구간 연결 관계를 기하 질의에 전달합니다.</summary>
    [Fact]
    public void ClearanceValidator_ForwardsDeclaredLocalAdjacencies()
    {
        HeatingPathSegmentId firstId = HeatingPathSegmentId.New();
        HeatingPathSegmentId secondId = HeatingPathSegmentId.New();
        CurveGeometrySnapshot first = CreateCurveSnapshot(firstId, new Vec3(0, 0, -1), new Vec3(10, 0, -1));
        CurveGeometrySnapshot second = CreateCurveSnapshot(secondId, new Vec3(10, 0, -1), new Vec3(20, 0, -1));
        CurveSegmentAdjacency adjacency = new(firstId, secondId);
        var queries = new GeometryQueryStub
        {
            ClearanceHandler = (_, _, _, _, _) => GeometryQueryResult<ClearanceMeasurement>.Succeeded(
                new ClearanceMeasurement(new LengthMm(5.4), new LengthMm(4.9), new Vec3(5, 0, 0),
                    new Vec3(5, 5.4, 0), new LengthMm(0.2), true))
        };

        ValidationReport report = new ClearanceValidator().Validate(
            [first, second],
            new LengthMm(0.5),
            new LengthMm(5),
            GeometryToleranceProfile.Initial,
            queries,
            "clearance-adjacency-test-v1",
            [adjacency]);

        Assert.False(report.IsValid);
        Assert.Equal("MinimumClearGapUnresolved", Assert.Single(report.Issues).Code);
        Assert.True(Assert.Single(queries.LastLocalAdjacencies!).Connects(firstId, secondId));
    }

    /// <summary>간격 검사 국소 접점은 활성 구간의 순서와 공유 절점이 모두 맞을 때만 전달합니다.</summary>
    [Fact]
    public void HeatingPathCurveAdjacencyResolver_UsesOnlyActiveOrderedSharedControlPoints()
    {
        HeatingRegion region = CreateRegion(out FaceRefId faceId, exitStart: 0.1, exitEnd: 0.9);
        SurfaceAnchor startAnchor = CreateAnchor(faceId, new Vec2(20, 0));
        SurfaceAnchor endAnchor = CreateAnchor(faceId, new Vec2(80, 0));
        var placement = new PlacementConstraints(
            new LengthMm(5), new LengthMm(2), new LengthMm(1), PlacementDirection.AlongX,
            startAnchor, endAnchor, region.ExitSide);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10));

        ControlPoint[] points =
        [
            new(ControlPointId.New(), new Vec3(0, 0, 0)),
            new(ControlPointId.New(), new Vec3(1, 0, 0)),
            new(ControlPointId.New(), new Vec3(2, 0, 0)),
            new(ControlPointId.New(), new Vec3(3, 0, 0)),
            new(ControlPointId.New(), new Vec3(4, 0, 0))
        ];
        HeatingPathSegment[] segments =
        [
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                points[0].Id, points[1].Id, 0, true, new LineCurveDefinition(points[0].Position, points[1].Position)),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
                points[1].Id, points[2].Id, 1, true, new LineCurveDefinition(points[1].Position, points[2].Position)),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
                points[2].Id, points[3].Id, 2, false, new LineCurveDefinition(points[2].Position, points[3].Position)),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                points[3].Id, points[4].Id, 3, true, new LineCurveDefinition(points[3].Position, points[4].Position))
        ];
        var path = new CoreHeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(region.Id, placement, wire, "adjacency-test-v1"),
            points,
            segments,
            segments);
        CurveGeometrySnapshot[] snapshots =
        [
            CreateCurveSnapshot(segments[0].Id, points[0].Position, points[1].Position),
            CreateCurveSnapshot(segments[1].Id, points[1].Position, points[2].Position),
            CreateCurveSnapshot(segments[3].Id, points[3].Position, points[4].Position)
        ];

        IReadOnlyList<CurveSegmentAdjacency> adjacencies = new HeatingPathCurveAdjacencyResolver()
            .Resolve([path], snapshots);

        CurveSegmentAdjacency adjacency = Assert.Single(adjacencies);
        Assert.True(adjacency.Connects(segments[0].Id, segments[1].Id));
    }

    /// <summary>Native 곡선 끝점이 안정 절점과 다르면 연속성 진단을 반환합니다.</summary>
    [Fact]
    public void ContinuityValidator_ReportsMeasuredEndpointMismatch()
    {
        DesignProject project = CreateSinglePathProject(out HeatingPathSegment segment);
        CurveGeometrySnapshot curve = CreateCurveSnapshot(
            segment.Id,
            new Vec3(20, 50, -1),
            new Vec3(80, 52, -1));
        GeometrySnapshot geometry = CreateGeometrySnapshot(project, [curve]);

        ValidationReport report = new ContinuityValidator().Validate(project, geometry, 0.01);

        Assert.False(report.IsValid);
        Assert.Equal("HeatingCurveEndMismatch", Assert.Single(report.Issues).Code);
    }

    private static DesignProject CreateSingleCircuitProject(out HeatingPathSegment segment, Vec2 endAnchorUv)
    {
        HeatingRegion region = CreateRegion(out FaceRefId faceId, exitStart: 0.1, exitEnd: 0.9);
        BoundarySegment exitSegment = region.EffectiveBoundaryLoops[0].Segments[0];
        var exitSide = new ExitSideReference([new BoundarySegmentRange(exitSegment.Id, 0.1, 0.9)]);
        SurfaceAnchor startAnchor = CreateAnchor(faceId, new Vec2(20, 0));
        SurfaceAnchor endAnchor = CreateAnchor(faceId, endAnchorUv);
        WireSpecification wire = new("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10));
        var placement = new PlacementConstraints(
            new LengthMm(5), new LengthMm(2), new LengthMm(1), PlacementDirection.AlongX,
            startAnchor, endAnchor, exitSide);

        var startControlPoint = new ControlPoint(ControlPointId.New(), new Vec3(20, 50, -1));
        var endControlPoint = new ControlPoint(ControlPointId.New(), new Vec3(80, 50, -1));
        segment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startControlPoint.Id, endControlPoint.Id, 0, true,
            new LineCurveDefinition(startControlPoint.Position, endControlPoint.Position), 0);
        var path = new CoreHeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(region.Id, placement, wire, "path-test-v1"),
            [startControlPoint, endControlPoint],
            [segment],
            [segment]);

        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startConnectionId = ConnectionPointId.New();
        ConnectionPointId endConnectionId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId,
            0,
            [new CircuitSegmentReference(segment.Id, 0, false)],
            startConnectionId,
            endConnectionId,
            new Supply(null, null, null));
        var startConnection = new ConnectionPoint(
            startConnectionId, circuitId, ConnectionPointRole.Start, segment.Id, startControlPoint.Id,
            startAnchor, new Vec3(20, 0, 0));
        var endConnection = new ConnectionPoint(
            endConnectionId, circuitId, ConnectionPointRole.End, segment.Id, endControlPoint.Id,
            endAnchor, new Vec3(80, 0, 0));

        return CreateProject(region, faceId, wire, placement, [path], [circuit], [startConnection, endConnection]);
    }

    private static DesignProject CreateSinglePathProject(out HeatingPathSegment segment)
    {
        HeatingRegion region = CreateRegion(out FaceRefId faceId, exitStart: 0.1, exitEnd: 0.9);
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(region.EffectiveBoundaryLoops[0].Segments[0].Id, 0.1, 0.9)]);
        WireSpecification wire = new("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10));
        SurfaceAnchor startAnchor = CreateAnchor(faceId, new Vec2(20, 0));
        SurfaceAnchor endAnchor = CreateAnchor(faceId, new Vec2(80, 0));
        var placement = new PlacementConstraints(
            new LengthMm(5), new LengthMm(2), new LengthMm(1), PlacementDirection.AlongX,
            startAnchor, endAnchor, exitSide);
        var startPoint = new ControlPoint(ControlPointId.New(), new Vec3(20, 50, -1));
        var endPoint = new ControlPoint(ControlPointId.New(), new Vec3(80, 50, -1));
        segment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startPoint.Id, endPoint.Id, 0, true,
            new LineCurveDefinition(startPoint.Position, endPoint.Position), 0);
        var path = new CoreHeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(region.Id, placement, wire, "path-test-v1"),
            [startPoint, endPoint],
            [segment],
            [segment]);
        return CreateProject(region, faceId, wire, placement, [path], [], []);
    }

    private static DesignProject CreateProject(
        HeatingRegion region,
        FaceRefId faceId,
        WireSpecification wire,
        PlacementConstraints placement,
        IReadOnlyList<CoreHeatingPath> paths,
        IReadOnlyList<Circuit> circuits,
        IReadOnlyList<ConnectionPoint> connectionPoints)
    {
        byte[] modelBytes = [8, 6, 7, 5, 3, 0, 9];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant();
        var identity = new ModelIdentity(sourceHash, modelBytes.Length, "DesignValidatorTests", "1", "face-v1");
        var face = new FaceReference(faceId, sourceHash, 0, FaceGeometryKind.Plane, new AreaMm2(10_000), default, "fixture-face", []);
        DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        return new DesignProject(
            ProjectId.New(), 1, new DesignRevision(0), "Validation fixture", now, now,
            identity,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [face], [region], wire, placement, paths, circuits, connectionPoints, [], DesignRuleSet.Initial);
    }

    private static HeatingRegion CreateRegion(out FaceRefId faceId, double exitStart, double exitEnd)
    {
        FaceRefId createdFaceId = FaceRefId.New();
        faceId = createdFaceId;
        Vec2[] vertices = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        BoundaryLoop MakeLoop() => new(
            BoundaryLoopId.New(),
            vertices.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], createdFaceId)),
            isHole: false);
        BoundaryLoop original = MakeLoop();
        BoundaryLoop effective = MakeLoop();
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(effective.Segments[0].Id, exitStart, exitEnd)]);
        return new HeatingRegion(
            RegionId.New(), [faceId], [original], [], [], [effective], exitSide);
    }

    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec2 uv) => new(
        faceId,
        uv,
        uv,
        new Vec3(uv.X, uv.Y, 0));

    private static CurveGeometrySnapshot CreateCurveSnapshot(
        HeatingPathSegmentId id,
        Vec3 start,
        Vec3 end) => new(
        id,
        null,
        new LineCurveDefinition(start, end),
        new LengthMm((end - start).Length),
        start,
        end,
        [start, end],
        new CurveApproximationInfo(0.05, 0.1, 0.01, 1, 2, true, "fixture-v1"));

    private static GeometrySnapshot CreateGeometrySnapshot(
        DesignProject project,
        IEnumerable<CurveGeometrySnapshot> curves) => new(
        GeometryDocumentId.New(),
        new ModelRevision(1),
        new DependencyStamp(project.Revision, project.SourceModel.SourceSha256, new string('a', 64), new string('b', 64), "test-v1"),
        curves);

    private static WorkingCoordinateSystem CreateCoordinateSystem() => new(
        new WorkingCoordinateSystemId(1),
        new ModelRevision(1),
        new ProjectionFrame(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0)));

    private sealed class GeometryQueryStub : IGeometryQueries
    {
        public int DepthCallCount { get; private set; }
        public int ClearanceCallCount { get; private set; }
        public IReadOnlyCollection<CurveSegmentAdjacency>? LastLocalAdjacencies { get; private set; }
        public IReadOnlyList<FaceRefId>? LastSelectedDepthFaces { get; private set; }

        public Func<CurveGeometrySnapshot, LengthMm, SurfaceAnchor, WorkingCoordinateSystem, LengthMm, CancellationToken,
            GeometryQueryResult<DepthMeasurement>> DepthHandler { get; init; } =
            (_, _, _, _, _, _) => new GeometryQueryResult<DepthMeasurement>(GeometryQueryStatus.Failure, null);

        public Func<IReadOnlyList<CurveGeometrySnapshot>, LengthMm, LengthMm, GeometryToleranceProfile, CancellationToken,
            GeometryQueryResult<ClearanceMeasurement>> ClearanceHandler { get; init; } =
            (_, _, _, _, _) => new GeometryQueryResult<ClearanceMeasurement>(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<SurfaceHit> QueryFirstHit(WorkingCoordinateSystem coordinateSystem, Vec2 workingUv, FaceRefId selectedFaceReferenceId, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<SelectedTopology> GetSelectedTopology(IReadOnlyList<FaceRefId> selectedFaceReferences, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<bool> AreAdjacent(FaceRefId first, FaceRefId second, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, default);

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(IReadOnlyList<CurveGeometrySnapshot> curves, LengthMm wireDiameterMm, LengthMm requiredClearGapMm, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default)
        {
            ClearanceCallCount++;
            return ClearanceHandler(curves, wireDiameterMm, requiredClearGapMm, tolerances, cancellationToken);
        }

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
            IReadOnlyList<CurveGeometrySnapshot> curves,
            LengthMm wireDiameterMm,
            LengthMm requiredClearGapMm,
            GeometryToleranceProfile tolerances,
            IReadOnlyCollection<CurveSegmentAdjacency> localAdjacencies,
            CancellationToken cancellationToken = default)
        {
            ClearanceCallCount++;
            LastLocalAdjacencies = localAdjacencies.ToArray();
            return ClearanceHandler(curves, wireDiameterMm, requiredClearGapMm, tolerances, cancellationToken);
        }

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(CurveGeometrySnapshot curve, LengthMm wireDiameterMm, SurfaceAnchor anchor, WorkingCoordinateSystem coordinateSystem, LengthMm requiredInsertionDepthMm, CancellationToken cancellationToken = default)
        {
            DepthCallCount++;
            return DepthHandler(curve, wireDiameterMm, anchor, coordinateSystem, requiredInsertionDepthMm, cancellationToken);
        }

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(
            CurveGeometrySnapshot curve,
            LengthMm wireDiameterMm,
            SurfaceAnchor anchor,
            WorkingCoordinateSystem coordinateSystem,
            LengthMm requiredInsertionDepthMm,
            IReadOnlyList<FaceRefId> selectedSurfaceFaces,
            CancellationToken cancellationToken = default)
        {
            LastSelectedDepthFaces = selectedSurfaceFaces.ToArray();
            return MeasureDepth(
                curve,
                wireDiameterMm,
                anchor,
                coordinateSystem,
                requiredInsertionDepthMm,
                cancellationToken);
        }

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(HeatingRegion region, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(CoreConnectionPath connectionPath, WorkingCoordinateSystem coordinateSystem, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);
    }
}
