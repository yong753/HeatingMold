using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Area;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Quality;

namespace HeatingMold.Application.Tests;

/// <summary>프로젝트 복원 시 실제 Native 길이로 회로 원장을 다시 만드는지 검사합니다.</summary>
public sealed class ProjectDerivedStateBuilderTests
{
    /// <summary>발열사 연장은 저항 길이에 더하고 별도 연결선은 제외합니다.</summary>
    [Fact]
    public void Build_UsesMeasuredCurvesAndMaterialRolesForResistanceLength()
    {
        (DesignProject project, FakePreparedGeometry prepared) = CreateCircuitFixture();

        var result = CreateBuilder().Build(project, prepared);

        Assert.True(result.IsSuccess);
        ElectricalResult electrical = Assert.Single(result.GetValueOrThrow().ElectricalResults);
        Assert.Equal(100, electrical.LengthBreakdown.HeatingBodyMm.Value, 8);
        Assert.Equal(10, electrical.LengthBreakdown.HeatingExtensionMm.Value, 8);
        Assert.Equal(20, electrical.LengthBreakdown.SeparateConnectionWireMm.Value, 8);
        Assert.Equal(4, electrical.LengthBreakdown.HeatingExtraAllowanceMm.Value, 8);
        Assert.Equal(0.114, electrical.ResistanceLengthMeters!.Value, 8);
        Assert.Equal(11.4, electrical.ResistanceOhms!.Value.Value, 8);
        Assert.Equal(ElectricalStatus.Pass, electrical.Status);
    }

    /// <summary>복원한 기하의 품질 관측값을 후보 파생 상태에 함께 보관합니다.</summary>
    [Fact]
    public void Build_ReportsCenterPitchQualityForCurrentGeometry()
    {
        (DesignProject project, FakePreparedGeometry prepared) = CreateCircuitFixture();

        OperationResult<CandidateDerivedState> result = CreateBuilder().Build(project, prepared);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CenterPitchQualityReport report = Assert.Single(result.GetValueOrThrow().CenterPitchQualityReports);
        Assert.Equal(project.HeatingRegions.Single().Id, report.RegionId);
        Assert.Equal(prepared.Snapshot.DependencyStamp, report.DependencyStamp);
        Assert.Equal(4L, report.SurfaceProbeCount);
        Assert.Equal(4, report.SurfaceSamples.Count);
    }

    /// <summary>면적 캐시는 기하 stamp뿐 아니라 프로젝트의 면적 규칙 버전도 일치해야 합니다.</summary>
    [Fact]
    public void Build_RecalculatesAreaWhenCachedRuleVersionIsStale()
    {
        (DesignProject project, FakePreparedGeometry prepared) = CreateCircuitFixture();
        var staleAllocation = new AppliedAreaAllocation(
            prepared.Snapshot.DependencyStamp,
            new AreaMm2(0),
            [],
            new AreaMm2(0),
            new AreaAllocationQuality(0.05, 0.1, 1, 0, 0.1, 1, true, "obsolete-area-rule-v1"));

        OperationResult<CandidateDerivedState> result = CreateBuilder().Build(project, prepared, staleAllocation);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation rebuiltAllocation = Assert.IsType<AppliedAreaAllocation>(
            result.GetValueOrThrow().AreaAllocation);
        Assert.NotSame(staleAllocation, rebuiltAllocation);
        Assert.Equal(project.Rules.AreaRuleVersion, rebuiltAllocation.Quality.RuleVersion);
    }

    /// <summary>회로가 최종 곡선 스냅샷에 없으면 복원 파생 결과를 만들지 않습니다.</summary>
    [Fact]
    public void Build_MissingMeasuredSegmentReturnsDiagnostic()
    {
        (DesignProject project, FakePreparedGeometry prepared) = CreateCircuitFixture();
        prepared.ReplaceSnapshot(new GeometrySnapshot(
            prepared.RuntimeDocument.DocumentId,
            prepared.RuntimeDocument.ModelRevision,
            prepared.Snapshot.DependencyStamp,
            []));

        var result = CreateBuilder().Build(project, prepared);

        Assert.False(result.IsSuccess);
        Assert.Equal("MissingHeatingCurveMeasurement", Assert.Single(result.Issues).Code);
    }

    private static (DesignProject Project, FakePreparedGeometry Prepared) CreateCircuitFixture()
    {
        byte[] sourceBytes = [2, 4, 6, 8];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var modelIdentity = new ModelIdentity(sourceHash, 1, "FixtureImporter", "1", "face-v1");
        var sourceModel = new ModelSource(modelIdentity, sourceBytes);
        WorkingCoordinateSystemDefinition axis = CreateAxis();
        FaceRefId faceId = FaceRefId.New();
        FaceReference face = CreateFaceReference(faceId, sourceHash);
        BoundaryLoop originalBoundary = CreateLoop(faceId);
        BoundaryLoop effectiveBoundary = CreateLoop(faceId);
        var exitSide = new ExitSideReference(
        [new BoundarySegmentRange(effectiveBoundary.Segments[0].Id, 0, 1)]);
        var region = new HeatingRegion(
            RegionId.New(),
            [faceId],
            [originalBoundary],
            [],
            [],
            [effectiveBoundary],
            exitSide);

        var wire = new WireSpecification("Fixture alloy", new LengthMm(0.3), new ResistanceOhmsPerMeter(100));
        var anchorStart = new SurfaceAnchor(faceId, default, new Vec2(5, 50), default);
        var anchorEnd = new SurfaceAnchor(faceId, default, new Vec2(95, 50), new Vec3(100, 0, 0));
        var constraints = new PlacementConstraints(
            new LengthMm(10), new LengthMm(5), new LengthMm(0),
            PlacementDirection.AlongX, anchorStart, anchorEnd, exitSide);
        var generationSource = new HeatingPathGenerationSource(region.Id, constraints, wire, "path-v1");

        ControlPointId startControlId = ControlPointId.New();
        ControlPointId endControlId = ControlPointId.New();
        var heatingSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startControlId, endControlId, 0, true,
            new LineCurveDefinition(default, new Vec3(100, 0, 0)));
        var heatingPath = new HeatingPath(
            HeatingPathId.New(),
            generationSource,
            [new ControlPoint(startControlId, default), new ControlPoint(endControlId, new Vec3(100, 0, 0))],
            [heatingSegment],
            [heatingSegment]);

        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startPointId = ConnectionPointId.New();
        ConnectionPointId endPointId = ConnectionPointId.New();
        var startPoint = new ConnectionPoint(
            startPointId, circuitId, ConnectionPointRole.Start, heatingSegment.Id, startControlId,
            anchorStart, new Vec3(-20, 0, 0));
        var endPoint = new ConnectionPoint(
            endPointId, circuitId, ConnectionPointRole.End, heatingSegment.Id, endControlId,
            anchorEnd, new Vec3(120, 0, 0));
        var circuit = new Circuit(
            circuitId, 0, [new CircuitSegmentReference(heatingSegment.Id, 0, false)],
            startPointId, endPointId,
            new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(25)));

        (ConnectionPath startPath, ConnectionPathSegment[] startSegments) = CreateConnectionPath(startPointId);
        (ConnectionPath endPath, ConnectionPathSegment[] endSegments) = CreateConnectionPath(endPointId);
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(4), "Circuit fixture",
            new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero),
            modelIdentity, axis,
            [face], [region], wire, constraints,
            [heatingPath], [circuit], [startPoint, endPoint], [startPath, endPath]);
        Assert.True(project.ValidateReferences().IsValid);

        var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(6), sourceHash);
        var stamp = CreateStamp(project.Revision, sourceHash);
        var curves = new List<CurveGeometrySnapshot>
        {
            CreateCurve(heatingSegment.Id, new Vec3(100, 0, 0), 100)
        };
        curves.AddRange(startSegments.Select(segment => CreateCurve(segment.Id, segment.Curve, SegmentLength(segment))));
        curves.AddRange(endSegments.Select(segment => CreateCurve(segment.Id, segment.Curve, SegmentLength(segment))));
        var geometry = new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, curves);
        return (project, new FakePreparedGeometry(runtime, geometry));
    }

    private static (ConnectionPath Path, ConnectionPathSegment[] Segments) CreateConnectionPath(ConnectionPointId pointId)
    {
        var onMold = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 0,
            ConductorRole.HeatingWireExtension, GeometryDomain.OnMold,
            new LengthMm(2), new LineCurveDefinition(default, new Vec3(5, 0, 0)));
        var external = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 1,
            ConductorRole.SeparateConnectionWire, GeometryDomain.External,
            new LengthMm(0), new LineCurveDefinition(new Vec3(5, 0, 0), new Vec3(15, 0, 0)),
            isMoldExitTransition: true);
        return (new ConnectionPath(ConnectionPathId.New(), pointId, [onMold, external]), [onMold, external]);
    }

    private static double SegmentLength(ConnectionPathSegment segment) =>
        segment.Curve is LineCurveDefinition line
            ? (line.Start - line.End).Length
            : throw new InvalidOperationException();

    private static CurveGeometrySnapshot CreateCurve(HeatingPathSegmentId id, Vec3 end, double length) =>
        CreateCurve(id, new LineCurveDefinition(default, end), length);

    private static CurveGeometrySnapshot CreateCurve(ConnectionPathSegmentId id, CurveDefinition definition, double length)
    {
        Vec3 start = ((LineCurveDefinition)definition).Start;
        Vec3 end = ((LineCurveDefinition)definition).End;
        return new CurveGeometrySnapshot(
            null, id, definition, new LengthMm(length), start, end, [start, end],
            new CurveApproximationInfo(0.05, 0.1, 0, length, 2, true, "curve-v1"));
    }

    private static CurveGeometrySnapshot CreateCurve(HeatingPathSegmentId id, CurveDefinition definition, double length)
    {
        Vec3 start = ((LineCurveDefinition)definition).Start;
        Vec3 end = ((LineCurveDefinition)definition).End;
        return new CurveGeometrySnapshot(
            id, null, definition, new LengthMm(length), start, end, [start, end],
            new CurveApproximationInfo(0.05, 0.1, 0, length, 2, true, "curve-v1"));
    }

    private static FaceReference CreateFaceReference(FaceRefId faceId, string sourceHash) => new(
        faceId, sourceHash, 0, FaceGeometryKind.Plane, new AreaMm2(10_000), default,
        "face-boundary-fixture", []);

    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] points = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        BoundarySegment[] segments = points.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(), start, points[(index + 1) % points.Length], faceId)).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }

    private static WorkingCoordinateSystemDefinition CreateAxis() => new(
        default, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

    private static DependencyStamp CreateStamp(DesignRevision revision, string sourceHash) => new(
        revision, sourceHash, new string('b', 64), new string('c', 64), "fixture-v1");

    private static ProjectDerivedStateBuilder CreateBuilder() => new(
        designValidationOverride: (_, _) => new Core.Validation.ValidationReport([]));

    private sealed class FakePreparedGeometry : IPreparedGeometry
    {
        public GeometrySnapshot Snapshot { get; private set; }
        public IGeometryQueries Queries { get; } = new FixtureGeometryQueries();
        public RuntimeDocument RuntimeDocument { get; }

        public FakePreparedGeometry(RuntimeDocument runtimeDocument, GeometrySnapshot snapshot)
        {
            RuntimeDocument = runtimeDocument;
            Snapshot = snapshot;
        }

        public void ReplaceSnapshot(GeometrySnapshot snapshot) => Snapshot = snapshot;
        public GeometryCommitReceipt Publish() => new(true, RuntimeDocument);
        public void Dispose() { }
    }

    private sealed class FixtureGeometryQueries : IGeometryQueries
    {
        public GeometryQueryResult<SurfaceHit> QueryFirstHit(WorkingCoordinateSystem coordinateSystem, Vec2 workingUv, FaceRefId selectedFaceReferenceId, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SurfaceHit>.Miss();

        public GeometryQueryResult<SelectedTopology> GetSelectedTopology(IReadOnlyList<FaceRefId> selectedFaceReferences, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SelectedTopology>.Succeeded(new SelectedTopology([], []));

        public GeometryQueryResult<bool> AreAdjacent(FaceRefId first, FaceRefId second, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<bool>.Succeeded(false);

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(IReadOnlyList<CurveGeometrySnapshot> curves, LengthMm wireDiameterMm, LengthMm requiredClearGapMm, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<ClearanceMeasurement>.Unresolved([]);

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(CurveGeometrySnapshot curve, LengthMm wireDiameterMm, SurfaceAnchor anchor, WorkingCoordinateSystem coordinateSystem, LengthMm requiredInsertionDepthMm, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<DepthMeasurement>.Unresolved([]);

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(HeatingRegion region, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default)
        {
            var element = new SurfaceElement(
                0,
                region.FaceReferences[0],
                new AreaMm2(100),
                new Vec3(50, 50, 0),
                new Vec2(50, 50),
                new Vec2(50, 50),
                [new Vec3(48, 48, 0), new Vec3(52, 48, 0), new Vec3(50, 52, 0)],
                new CurveApproximationInfo(0.01, 0.01, 0.001, 0.1, 4, true, "derived-quality-fixture-v1"));
            return GeometryQueryResult<SurfaceElementSet>.Succeeded(
                new SurfaceElementSet([element], new AreaMm2(0)));
        }

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(ConnectionPath connectionPath, WorkingCoordinateSystem coordinateSystem, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<ConnectionObstacleCheck>.Unresolved([]);
    }
}
