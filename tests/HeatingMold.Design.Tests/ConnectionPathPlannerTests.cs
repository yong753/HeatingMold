using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.ConnectionPath;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;

namespace HeatingMold.Design.Tests;

/// <summary>명시 절점과 실제 끝점, 재료 역할을 유지하는 연결 경로 계획을 검사합니다.</summary>
public sealed class ConnectionPathPlannerTests
{
    /// <summary>실제 발열 끝점에서 표면 앵커로 이어지고 전이 후 외부 연결점까지 보존합니다.</summary>
    [Fact]
    public void Plan_PreservesExplicitMoldExitAndMaterialLegs()
    {
        ConnectionPathPlanningRequest request = CreateRequest(out Vec3 heatingEndpoint, out Vec3 surfacePoint, out Vec3 externalPoint);
        var queries = new GeometryQueryStub
        {
            ObstacleHandler = (_, _, _) => GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                new ConnectionObstacleCheck(true, null, null, true))
        };

        OperationResult<CoreConnectionPath> result = new ConnectionPathPlanner().Plan(
            request,
            queries,
            connectionToleranceMm: 0.01);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CoreConnectionPath path = result.GetValueOrThrow();
        Assert.Equal(2, path.Segments.Count);
        Assert.Equal(new LineCurveDefinition(heatingEndpoint, surfacePoint), path.Segments[0].Curve);
        Assert.Equal(GeometryDomain.OnMold, path.Segments[0].GeometryDomain);
        Assert.Equal(ConductorRole.HeatingWireExtension, path.Segments[0].ConductorRole);
        Assert.Equal(GeometryDomain.External, path.Segments[1].GeometryDomain);
        Assert.True(path.Segments[1].IsMoldExitTransition);
        Assert.Equal(ConductorRole.SeparateConnectionWire, path.Segments[1].ConductorRole);
        Assert.Equal(new LengthMm(3), path.Segments[0].AdditionalAllowanceMm);
        Assert.Equal(new LineCurveDefinition(surfacePoint, externalPoint), path.Segments[1].Curve);
        Assert.Equal(1, queries.ObstacleCallCount);
    }

    /// <summary>Native 충돌 질의의 미확정 상태는 연결 경로 성공으로 바뀌지 않습니다.</summary>
    [Fact]
    public void Plan_BlocksUnresolvedNativeObstacleCheck()
    {
        ConnectionPathPlanningRequest request = CreateRequest(out _, out _, out _);
        var queries = new GeometryQueryStub
        {
            ObstacleHandler = (_, _, _) => GeometryQueryResult<ConnectionObstacleCheck>.Unresolved([])
        };

        OperationResult<CoreConnectionPath> result = new ConnectionPathPlanner().Plan(
            request,
            queries,
            connectionToleranceMm: 0.01);

        Assert.False(result.IsSuccess);
        Assert.Equal("ConnectionObstacleQueryUnresolved", Assert.Single(result.Issues).Code);
    }

    /// <summary>표면 이탈 절점이 앵커와 다르면 임의로 스냅하지 않고 경로를 거부합니다.</summary>
    [Fact]
    public void Plan_RejectsExitPointDifferentFromSurfaceAnchor()
    {
        ConnectionPathPlanningRequest request = CreateRequest(
            out _, out _, out _,
            transitionAnchorUv: new Vec2(50, 0),
            onMoldEnd: new Vec3(49, 0, 0));
        var queries = new GeometryQueryStub();

        OperationResult<CoreConnectionPath> result = new ConnectionPathPlanner().Plan(
            request,
            queries,
            connectionToleranceMm: 0.01);

        Assert.False(result.IsSuccess);
        Assert.Equal("MoldExitAnchorMismatch", Assert.Single(result.Issues).Code);
        Assert.Equal(0, queries.ObstacleCallCount);
    }

    private static ConnectionPathPlanningRequest CreateRequest(
        out Vec3 heatingEndpoint,
        out Vec3 surfacePoint,
        out Vec3 externalPoint,
        Vec2? transitionAnchorUv = null,
        Vec3? onMoldEnd = null)
    {
        heatingEndpoint = new Vec3(20, 10, -1);
        Vec2 anchorUv = transitionAnchorUv ?? new Vec2(20, 0);
        surfacePoint = onMoldEnd ?? new Vec3(anchorUv.X, anchorUv.Y, 0);
        externalPoint = new Vec3(20, -20, 0);
        CircuitId circuitId = CircuitId.New();
        HeatingPathSegmentId segmentId = HeatingPathSegmentId.New();
        ControlPointId controlPointId = ControlPointId.New();
        ConnectionPointId connectionPointId = ConnectionPointId.New();
        var segment = new HeatingPathSegment(
            segmentId,
            HeatingPathSegmentKind.HeatingRun,
            controlPointId,
            ControlPointId.New(),
            0,
            true,
            new LineCurveDefinition(new Vec3(20, 10, -1), new Vec3(80, 10, -1)));
        var circuit = new Circuit(
            circuitId,
            0,
            [new CircuitSegmentReference(segmentId, 0, false)],
            connectionPointId,
            ConnectionPointId.New(),
            new Supply(null, null, null));
        var anchor = new SurfaceAnchor(
            FaceRefId.New(),
            anchorUv,
            anchorUv,
            new Vec3(anchorUv.X, anchorUv.Y, 0));
        var connectionPoint = new ConnectionPoint(
            connectionPointId,
            circuitId,
            ConnectionPointRole.Start,
            segmentId,
            controlPointId,
            anchor,
            externalPoint);
        var coordinates = new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(9),
            new ModelRevision(6),
            new ProjectionFrame(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
        ConnectionPathLegRequest[] legs =
        [
            new(surfacePoint, GeometryDomain.OnMold, ConductorRole.HeatingWireExtension, new LengthMm(3)),
            new(externalPoint, GeometryDomain.External, ConductorRole.SeparateConnectionWire, new LengthMm(2), isMoldExitTransition: true)
        ];
        return new ConnectionPathPlanningRequest(
            circuit,
            connectionPoint,
            segment,
            heatingEndpoint,
            legs,
            coordinates,
            new ModelRevision(6));
    }

    private sealed class GeometryQueryStub : IGeometryQueries
    {
        public int ObstacleCallCount { get; private set; }

        public Func<CoreConnectionPath, WorkingCoordinateSystem, CancellationToken,
            GeometryQueryResult<ConnectionObstacleCheck>> ObstacleHandler { get; init; } =
            (_, _, _) => new GeometryQueryResult<ConnectionObstacleCheck>(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<SurfaceHit> QueryFirstHit(WorkingCoordinateSystem coordinateSystem, Vec2 workingUv, FaceRefId selectedFaceReferenceId, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<SelectedTopology> GetSelectedTopology(IReadOnlyList<FaceRefId> selectedFaceReferences, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<bool> AreAdjacent(FaceRefId first, FaceRefId second, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, default);

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(IReadOnlyList<CurveGeometrySnapshot> curves, LengthMm wireDiameterMm, LengthMm requiredClearGapMm, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(CurveGeometrySnapshot curve, LengthMm wireDiameterMm, SurfaceAnchor anchor, WorkingCoordinateSystem coordinateSystem, LengthMm requiredInsertionDepthMm, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(HeatingRegion region, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(CoreConnectionPath connectionPath, WorkingCoordinateSystem coordinateSystem, CancellationToken cancellationToken = default)
        {
            ObstacleCallCount++;
            return ObstacleHandler(connectionPath, coordinateSystem, cancellationToken);
        }
    }
}
