using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>준비 후보 bridge 어댑터가 모든 표면 질의를 후보 문서에 한정하는지 확인합니다.</summary>
public sealed class PreparedCandidateWorkingSurfaceBridgeAdapterTests
{
    /// <summary>first-hit, 인접, topology, 표면 요소 질의가 동일 후보 ID를 사용합니다.</summary>
    [Fact]
    public void CandidateAdapter_UsesCandidateScopedApiForEverySurfaceQuery()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceHit = new WorkingSurfaceHit
        {
            Status = WorkingSurfaceQueryStatus.Hit,
            FaceId = 2,
            Point = [1, 2, 3],
            Normal = [0, 0, 1],
            FaceUv = [.2, .4]
        };
        Bridge.PreparedCandidateAdjacencyStatus = WorkingFaceAdjacencyStatus.Adjacent;
        Bridge.PreparedCandidateSelectedTopology = new WorkingSelectedTopology
        {
            Status = WorkingTopologyQueryStatus.Success
        };
        Bridge.PreparedCandidateSurfaceElements = new WorkingSurfaceElementsResult
        {
            Status = WorkingSurfaceElementQueryStatus.Success,
            AreaErrorBoundMm2 = .01,
            Elements =
            [
                new WorkingSurfaceElementData
                {
                    FaceIndex = 2,
                    SurfaceAreaMm2 = .5,
                    ModelCentroid = [1, 2, 3],
                    FaceUvCentroid = [.2, .4],
                    WorkingUvCentroid = [3, 4],
                    ModelVertices = [0, 0, 0, 1, 0, 0, 0, 1, 0],
                    MaximumMeasuredDeviationMm = .001
                }
            ]
        };

        var adapter = new WorkingSurfaceBridgeAdapter(preparedCandidateId: 73);
        var coordinateSystemId = new WorkingCoordinateSystemId(501);
        var revision = new ModelRevision(12);

        OcctSurfaceQueryResult hit = adapter.QueryFirstHit(coordinateSystemId, revision, new Vec2(2, 3));
        OcctFaceAdjacencyStatus adjacency = adapter.QueryFaceAdjacency(coordinateSystemId, revision, 2, 3);
        OcctSelectedTopologyResult topology = adapter.QuerySelectedTopology(
            coordinateSystemId,
            revision,
            [2, 3],
            deflectionMm: .05);
        OcctSurfaceElementQueryResult elements = adapter.QuerySurfaceElements(
            coordinateSystemId,
            revision,
            [2, 3],
            [new OcctRegionLoop([new Vec2(0, 0), new Vec2(5, 0), new Vec2(5, 5)], isHole: false)],
            deflectionMm: .05,
            angularToleranceRad: .1);

        Assert.Equal(OcctSurfaceQueryStatus.Hit, hit.Status);
        Assert.Equal(OcctFaceAdjacencyStatus.Adjacent, adjacency);
        Assert.Equal(OcctSelectedTopologyStatus.Success, topology.Status);
        Assert.Equal(OcctSurfaceElementQueryStatus.Success, elements.Status);
        Assert.Single(elements.Elements);
        Assert.Equal(73, Bridge.LastPreparedCandidateWorkingSurfaceCandidateId);
        Assert.Equal(501, Bridge.LastPreparedCandidateWorkingSurfaceContextId);
        Assert.Equal(12, Bridge.LastPreparedCandidateWorkingSurfaceRevision);
        Assert.Equal(73, Bridge.LastPreparedCandidateAdjacencyQueryId);
        Assert.Equal(73, Bridge.LastPreparedCandidateTopologyQueryId);
        Assert.Equal(73, Bridge.LastPreparedCandidateSurfaceElementsQueryId);
        Assert.Equal(0, Bridge.ActiveWorkingGeometryQueryCount);
    }

    /// <summary>곡선 간격 질의도 exact DTO와 후보 context를 Native API에 전달합니다.</summary>
    [Fact]
    public void CandidateAdapter_MapsExactArcClearanceResultAndContext()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateCurveClearanceResult = new CurveClearanceResultData
        {
            Status = WorkingCurveClearanceStatus.Success,
            MinimumCenterlineDistanceMm = 2,
            FirstLocation = [1, 2, 3],
            SecondLocation = [1, 2, 5],
            ErrorBoundMm = .001
        };

        var plane = new ProjectionFrame(
            new Vec3(0, 0, 0),
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0));
        var arc = new ArcCurveDefinition(
            plane,
            new Vec3(10, 20, 30),
            new HeatingMold.Core.Primitives.LengthMm(5),
            .25,
            1.75);
        var mapper = new CurveDefinitionMapper();
        CurveDefinitionBridgeDto definition = mapper.ToBridgeDto(arc);
        CurveDefinitionBridgeDto adjacentDefinition = mapper.ToBridgeDto(
            new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
        var adapter = new WorkingSurfaceBridgeAdapter(preparedCandidateId: 73);

        OcctCurveClearanceQueryResult result = adapter.MeasureCurveClearance(
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            [definition, adjacentDefinition],
            [(0, 1)],
            new HeatingMold.Core.Primitives.LengthMm(.4),
            maximumErrorMm: .01,
            maximumPairChecks: 1234);

        Assert.Equal(OcctCurveClearanceStatus.Success, result.Status);
        Assert.Equal(2, result.Measurement!.MinimumCenterlineDistanceMm.Value);
        Assert.Equal(1.6, result.Measurement.MinimumClearGapMm.Value, precision: 10);
        Assert.Equal(new Vec3(1, 2, 3), result.Measurement.FirstLocation);
        Assert.Equal(.001, result.Measurement.ErrorBoundMm.Value);
        Assert.Equal(73, Bridge.LastPreparedCandidateCurveClearanceId);
        Assert.Equal(new long[] { 501, 12 }, Bridge.LastCurveClearanceContext);
        Assert.Equal(new double[] { .4, .01, 1234 }, Bridge.LastCurveClearanceSettings);
        Assert.Equal(new[] { 0, 1 }, Bridge.LastCurveClearanceLocalPairs);
        CurveDefinitionData nativeDefinition = Bridge.LastCurveClearanceDefinitions[0];
        Assert.Equal("Arc", nativeDefinition.Kind);
        Assert.Equal(5, nativeDefinition.RadiusMm);
        Assert.Equal(new double[] { 10, 20, 30 }, nativeDefinition.Center);
    }

    /// <summary>후보 깊이 질의가 표본·앵커·Face·오차 설정을 전달하고 결과 위치를 복원합니다.</summary>
    [Fact]
    public void CandidateAdapter_MapsNativeDepthEnvelopeAndUsesCandidateSession()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateCurveDepthEnvelopeResult = new CurveDepthEnvelopeResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.Success,
            MeasuredInsertionDepthMm = 1.1,
            MinimumInsertionDepthMm = .9,
            MaximumInsertionDepthMm = 1.3,
            ErrorBoundMm = .02,
            SurfacePoint = [2, 0, 0],
            MinimumSurfacePoint = [1, 0, 0],
            MaximumSurfacePoint = [3, 0, 0],
            MinimumWireOutsidePoint = [1, 0, -1],
            MaximumWireOutsidePoint = [3, 0, -1.3],
            ProbeCount = 8
        };
        var request = new OcctCurveDepthEnvelopeRequest(
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            [new Vec3(0, 0, -1.15), new Vec3(10, 0, -1.15)],
            [2, 3],
            anchorNativeFaceIndex: 2,
            new Vec2(1.5, -2.5),
            new Vec3(0, 0, 0),
            new HeatingMold.Core.Primitives.LengthMm(.3),
            new HeatingMold.Core.Primitives.LengthMm(1),
            maximumSampleSpacingMm: .1,
            requestedAngularToleranceRad: .05,
            maximumMeasuredDeviationMm: .002,
            connectionToleranceMm: .01,
            maximumProbeCount: 1000);
        var adapter = new WorkingSurfaceBridgeAdapter(preparedCandidateId: 73);

        OcctCurveDepthEnvelopeQueryResult result = adapter.MeasureDepthEnvelope(request);

        Assert.Equal(OcctCurveDepthEnvelopeStatus.Success, result.Status);
        Assert.Equal(.9, result.Measurement!.MinimumMeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(1.3, result.Measurement.MaximumMeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(new Vec3(2, 0, 0), result.Measurement.SurfacePoint);
        Assert.Equal(8, result.Measurement.ProbeCount);
        Assert.Equal("occt-native-sampled-depth-envelope-v2", result.Measurement.MeasurementRuleVersion);
        Assert.Equal(73, Bridge.LastPreparedCandidateDepthEnvelopeId);
        Assert.Equal(new long[] { 501, 12 }, Bridge.LastDepthEnvelopeContext);
        Assert.Equal(new double[] { 0, 0, -1.15, 10, 0, -1.15 }, Bridge.LastDepthEnvelopeSamples);
        Assert.Equal(new[] { 2, 3 }, Bridge.LastDepthEnvelopeFaceIndices);
        Assert.Equal(new double[] { 2, 1.5, -2.5, 0, 0, 0 }, Bridge.LastDepthEnvelopeAnchor);
        Assert.Equal(new double[] { .3, 1, .1, .05, .002, .01, 1000 }, Bridge.LastDepthEnvelopeSettings);
        Assert.Equal(0, Bridge.ActiveWorkingGeometryQueryCount);
    }

    /// <summary>후보 깊이 보정이 경로·Face·오차 설정을 Native에 보내고 중심선을 복원합니다.</summary>
    [Fact]
    public void CandidateAdapter_MapsNativeDepthAdjustmentAndUsesCandidateSession()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateCurveDepthAdjustmentResult = new CurveDepthAdjustmentResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.Success,
            CenterlineSamples = [0, 0, -1.3, 2, 0, -1.3],
            MaximumDepthResidualMm = .0005,
            ErrorBoundMm = .02,
            ProbeCount = 9
        };
        var request = new OcctCurveDepthAdjustmentRequest(
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            [new Vec2(1, 2), new Vec2(3, 2)],
            [2, 3],
            anchorNativeFaceIndex: 2,
            new Vec2(1.5, -2.5),
            new Vec3(0, 0, 0),
            new HeatingMold.Core.Primitives.LengthMm(.3),
            new HeatingMold.Core.Primitives.LengthMm(1),
            maximumSampleSpacingMm: .1,
            requestedAngularToleranceRad: .05,
            maximumSurfaceDeviationMm: .002,
            connectionToleranceMm: .01,
            maximumProbeCount: 1000);
        var adapter = new WorkingSurfaceBridgeAdapter(preparedCandidateId: 73);

        OcctCurveDepthAdjustmentQueryResult result = adapter.AdjustInsertionDepth(request);

        Assert.Equal(OcctCurveDepthEnvelopeStatus.Success, result.Status);
        Assert.Equal(new Vec3(0, 0, -1.3), result.Adjustment!.Curve.Points[0]);
        Assert.Equal(new Vec3(2, 0, -1.3), result.Adjustment.Curve.Points[1]);
        Assert.Equal(.0005, result.Adjustment.MaximumDepthResidualMm.Value);
        Assert.Equal(.02, result.Adjustment.ErrorBoundMm.Value);
        Assert.Equal(9, result.Adjustment.ProbeCount);
        Assert.Equal("native-axis-ray-bracket-v2", result.Adjustment.AlgorithmVersion);
        Assert.Equal(73, Bridge.LastPreparedCandidateDepthAdjustmentId);
        Assert.Equal(new long[] { 501, 12 }, Bridge.LastDepthAdjustmentContext);
        Assert.Equal(new double[] { 1, 2, 3, 2 }, Bridge.LastDepthAdjustmentSamples);
        Assert.Equal(new[] { 2, 3 }, Bridge.LastDepthAdjustmentFaceIndices);
        Assert.Equal(new double[] { 2, 1.5, -2.5, 0, 0, 0 }, Bridge.LastDepthAdjustmentAnchor);
        Assert.Equal(new double[] { .3, 1, .1, .05, .002, .01, 1000 }, Bridge.LastDepthAdjustmentSettings);
        Assert.Equal(0, Bridge.ActiveWorkingGeometryQueryCount);
    }

    /// <summary>활성 경로 깊이 보정은 준비 후보 API를 건너뛰고 Native 미확정 상태를 보존합니다.</summary>
    [Fact]
    public void ActiveAdapter_PreservesUnresolvedNativeDepthAdjustment()
    {
        Bridge.Reset();
        Bridge.ActiveCurveDepthAdjustmentResult = new CurveDepthAdjustmentResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.Unresolved,
            Diagnostic = "축방향 ray root가 수렴하지 않았습니다."
        };
        var request = new OcctCurveDepthAdjustmentRequest(
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            [new Vec2(1, 2), new Vec2(3, 2)],
            [2],
            anchorNativeFaceIndex: 2,
            new Vec2(1, 2),
            new Vec3(0, 0, 0),
            new HeatingMold.Core.Primitives.LengthMm(.3),
            new HeatingMold.Core.Primitives.LengthMm(1),
            maximumSampleSpacingMm: .1,
            requestedAngularToleranceRad: .05,
            maximumSurfaceDeviationMm: .002,
            connectionToleranceMm: .01,
            maximumProbeCount: 1000);

        OcctCurveDepthAdjustmentQueryResult result = new WorkingSurfaceBridgeAdapter().AdjustInsertionDepth(request);

        Assert.Equal(OcctCurveDepthEnvelopeStatus.Unresolved, result.Status);
        Assert.Equal("축방향 ray root가 수렴하지 않았습니다.", result.Diagnostic);
        Assert.Equal(1, Bridge.ActiveWorkingGeometryQueryCount);
        Assert.Equal(0, Bridge.LastPreparedCandidateDepthAdjustmentId);
        Assert.Equal(new long[] { 501, 12 }, Bridge.LastDepthAdjustmentContext);
    }

    /// <summary>활성 깊이 질의는 후보 API를 건너뛰고 Native 상태를 보존합니다.</summary>
    [Fact]
    public void ActiveAdapter_PreservesNativeDepthEnvelopeMiss()
    {
        Bridge.Reset();
        Bridge.ActiveCurveDepthEnvelopeResult = new CurveDepthEnvelopeResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.Miss,
            Diagnostic = "선택 Face 밖"
        };
        var request = new OcctCurveDepthEnvelopeRequest(
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            [new Vec3(0, 0, -1), new Vec3(1, 0, -1)],
            [2],
            anchorNativeFaceIndex: 2,
            new Vec2(0, 0),
            new Vec3(0, 0, 0),
            new HeatingMold.Core.Primitives.LengthMm(.3),
            new HeatingMold.Core.Primitives.LengthMm(1),
            .1,
            .05,
            0,
            .01,
            100);

        OcctCurveDepthEnvelopeQueryResult result = new WorkingSurfaceBridgeAdapter().MeasureDepthEnvelope(request);

        Assert.Equal(OcctCurveDepthEnvelopeStatus.Miss, result.Status);
        Assert.Equal("선택 Face 밖", result.Diagnostic);
        Assert.Equal(1, Bridge.ActiveWorkingGeometryQueryCount);
        Assert.Equal(0, Bridge.LastPreparedCandidateDepthEnvelopeId);
    }

    /// <summary>연결 충돌 질의가 후보 ID와 이탈 순서를 포함한 exact 곡선을 전달합니다.</summary>
    [Fact]
    public void CandidateAdapter_MapsConnectionObstructionAndPreservesCurveOrder()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateConnectionObstacleResult = new ConnectionObstacleResultData
        {
            Status = WorkingConnectionObstacleStatus.Obstructed,
            FirstObstruction = [4, 5, 6],
            DistanceToObstructionMm = 27.5
        };

        CurveDefinitionBridgeDto onMold = new CurveDefinitionMapper().ToBridgeDto(
            new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
        CurveDefinitionBridgeDto exit = new CurveDefinitionMapper().ToBridgeDto(
            new ArcCurveDefinition(
                new ProjectionFrame(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)),
                new Vec3(1, 0, 0),
                new HeatingMold.Core.Primitives.LengthMm(2),
                0,
                1));
        var adapter = new WorkingSurfaceBridgeAdapter(preparedCandidateId: 73);

        OcctConnectionObstacleQueryResult result = adapter.CheckConnectionPathObstacles(
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            [onMold, exit],
            moldExitTransitionIndex: 1,
            connectionToleranceMm: 0.25);

        Assert.Equal(OcctConnectionObstacleStatus.Obstructed, result.Status);
        Assert.Equal(new Vec3(4, 5, 6), result.FirstObstruction);
        Assert.Equal(27.5, result.DistanceToObstruction!.Value.Value);
        Assert.Equal(73, Bridge.LastPreparedCandidateConnectionObstacleId);
        Assert.Equal(new long[] { 501, 12 }, Bridge.LastConnectionObstacleContext);
        Assert.Equal(1, Bridge.LastConnectionObstacleTransitionIndex);
        Assert.Equal(0.25, Bridge.LastConnectionObstacleToleranceMm);
        Assert.Equal(2, Bridge.LastConnectionObstacleCurves.Length);
        Assert.Equal("Arc", Bridge.LastConnectionObstacleCurves[1].Kind);
    }

    /// <summary>활성 Native 문서에도 곡선 배열과 이탈 순서를 전달합니다.</summary>
    [Fact]
    public void ActiveAdapter_UsesActiveConnectionObstacleApi()
    {
        Bridge.Reset();
        Bridge.ActiveConnectionObstacleResult = new ConnectionObstacleResultData
        {
            Status = WorkingConnectionObstacleStatus.Clear
        };
        var mapper = new CurveDefinitionMapper();
        CurveDefinitionBridgeDto onMold = mapper.ToBridgeDto(
            new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
        CurveDefinitionBridgeDto external = mapper.ToBridgeDto(
            new LineCurveDefinition(new Vec3(1, 0, 0), new Vec3(1, 0, 5)));

        OcctConnectionObstacleQueryResult result = new WorkingSurfaceBridgeAdapter().CheckConnectionPathObstacles(
            new WorkingCoordinateSystemId(42),
            new ModelRevision(9),
            [onMold, external],
            moldExitTransitionIndex: 1,
            connectionToleranceMm: 0.3);

        Assert.Equal(OcctConnectionObstacleStatus.Clear, result.Status);
        Assert.Equal(new long[] { 42, 9 }, Bridge.LastConnectionObstacleContext);
        Assert.Equal(1, Bridge.LastConnectionObstacleTransitionIndex);
        Assert.Equal(0.3, Bridge.LastConnectionObstacleToleranceMm);
        Assert.Equal(2, Bridge.LastConnectionObstacleCurves.Length);
    }

    /// <summary>유효하지 않은 연결 공차는 Native 브리지를 호출하기 전에 거부합니다.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Adapter_RejectsInvalidConnectionObstacleTolerance(double connectionToleranceMm)
    {
        Bridge.Reset();
        var mapper = new CurveDefinitionMapper();
        CurveDefinitionBridgeDto onMold = mapper.ToBridgeDto(
            new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
        CurveDefinitionBridgeDto external = mapper.ToBridgeDto(
            new LineCurveDefinition(new Vec3(1, 0, 0), new Vec3(1, 0, 5)));

        OcctConnectionObstacleQueryResult result = new WorkingSurfaceBridgeAdapter().CheckConnectionPathObstacles(
            new WorkingCoordinateSystemId(42),
            new ModelRevision(9),
            [onMold, external],
            moldExitTransitionIndex: 1,
            connectionToleranceMm: connectionToleranceMm);

        Assert.Equal(OcctConnectionObstacleStatus.InvalidInput, result.Status);
        Assert.Null(Bridge.LastConnectionObstacleContext);
    }
}
