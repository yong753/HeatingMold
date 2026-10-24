using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>OCCT 표면 결과를 안정 Face 참조와 공통 질의 상태로 바꾸는 경계를 검증합니다.</summary>
public sealed class OcctGeometryQueriesTests
{
    /// <summary>first-hit 점·법선·Face UV를 요청한 안정 Face ID와 함께 반환합니다.</summary>
    [Fact]
    public void QueryFirstHit_MapsNativeHitToStableFaceReference()
    {
        FaceRefId faceReferenceId = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryResult = Hit(nativeFaceIndex: 4, new Vec3(1, 2, 3), new Vec3(0, 0, 1), new Vec2(0.2, 0.7))
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (faceReferenceId, 4));

        GeometryQueryResult<SurfaceHit> result = queries.QueryFirstHit(
            CreateCoordinateSystem(),
            new Vec2(5, 8),
            faceReferenceId);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(faceReferenceId, result.Value!.FaceReferenceId);
        Assert.Equal(new Vec3(1, 2, 3), result.Value.ModelPoint);
        Assert.Equal(new Vec3(0, 0, 1), result.Value.SurfaceNormal);
        Assert.Equal(new Vec2(0.2, 0.7), result.Value.FaceUv);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastCoordinateSystemId!.Value);
        Assert.Equal(new ModelRevision(7), bridge.LastRevision!.Value);
        Assert.Equal(new Vec2(5, 8), bridge.LastWorkingUv);
    }

    /// <summary>first-hit이 선택 Face 집합 밖이면 뒤쪽 선택 면으로 대체하지 않습니다.</summary>
    [Fact]
    public void QueryFirstHit_RejectsFirstHitOutsideSelectedFaceSet()
    {
        FaceRefId selectedFace = FaceRefId.New();
        FaceRefId occludingFace = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryResult = Hit(nativeFaceIndex: 9)
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (selectedFace, 4), (occludingFace, 9));

        GeometryQueryResult<SurfaceHit> result = queries.QueryFirstHit(
            CreateCoordinateSystem(),
            new Vec2(1, 1),
            [selectedFace]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "FirstHitOutsideSelectedFaces");
    }

    /// <summary>현재 문서에 대응되지 않는 Face ID는 네이티브 질의를 시작하기 전에 거부합니다.</summary>
    [Fact]
    public void QueryFirstHit_RejectsMissingFaceMappingWithoutCallingBridge()
    {
        FaceRefId unknownFace = FaceRefId.New();
        var bridge = new FakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge);

        GeometryQueryResult<SurfaceHit> result = queries.QueryFirstHit(
            CreateCoordinateSystem(),
            new Vec2(1, 1),
            unknownFace);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "UnresolvedFaceReference");
        Assert.Equal(0, bridge.QueryCallCount);
    }

    /// <summary>Native miss, 접선과 stale context를 각각 공통 상태로 유지합니다.</summary>
    [Theory]
    [InlineData(OcctSurfaceQueryStatus.Miss, GeometryQueryStatus.Miss)]
    [InlineData(OcctSurfaceQueryStatus.Tangent, GeometryQueryStatus.Tangent)]
    [InlineData(OcctSurfaceQueryStatus.StaleContext, GeometryQueryStatus.StaleGeometryContext)]
    [InlineData(OcctSurfaceQueryStatus.InvalidInput, GeometryQueryStatus.Unresolved)]
    [InlineData(OcctSurfaceQueryStatus.GeometryFailure, GeometryQueryStatus.Failure)]
    [InlineData(OcctSurfaceQueryStatus.Unknown, GeometryQueryStatus.Unresolved)]
    public void QueryFirstHit_PreservesNativeFailureMeaning(
        OcctSurfaceQueryStatus nativeStatus,
        GeometryQueryStatus expectedStatus)
    {
        FaceRefId faceReferenceId = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = new OcctSurfaceQueryResult(nativeStatus) };
        OcctGeometryQueries queries = CreateQueries(bridge, (faceReferenceId, 4));

        GeometryQueryResult<SurfaceHit> result = queries.QueryFirstHit(
            CreateCoordinateSystem(),
            new Vec2(0, 0),
            faceReferenceId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Value);
    }

    /// <summary>Face 인접 질의에 안정 ID를 현재 Native 번호로 바꾸고 문서 context를 전달합니다.</summary>
    [Fact]
    public void AreAdjacent_MapsFacesAndUsesBoundCoordinateSystem()
    {
        FaceRefId first = FaceRefId.New();
        FaceRefId second = FaceRefId.New();
        var bridge = new FakeBridge { AdjacencyStatus = OcctFaceAdjacencyStatus.Adjacent };
        OcctGeometryQueries queries = CreateQueries(bridge, (first, 4), (second, 9));

        GeometryQueryResult<bool> result = queries.AreAdjacent(first, second);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value);
        Assert.Equal((4, 9), bridge.LastAdjacentNativeFaces!.Value);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastCoordinateSystemId!.Value);
        Assert.Equal(new ModelRevision(7), bridge.LastRevision!.Value);
    }

    /// <summary>Native 인접 질의의 비인접·stale·입력 오류·기하 실패를 구분해 보존합니다.</summary>
    [Theory]
    [InlineData(OcctFaceAdjacencyStatus.Adjacent, GeometryQueryStatus.Success, true, null)]
    [InlineData(OcctFaceAdjacencyStatus.NotAdjacent, GeometryQueryStatus.Success, false, null)]
    [InlineData(OcctFaceAdjacencyStatus.InvalidFace, GeometryQueryStatus.Unresolved, false, "InvalidNativeFaceAdjacencyRequest")]
    [InlineData(OcctFaceAdjacencyStatus.StaleContext, GeometryQueryStatus.StaleGeometryContext, false, "StaleGeometryContext")]
    [InlineData(OcctFaceAdjacencyStatus.GeometryFailure, GeometryQueryStatus.Failure, false, "OcctFaceAdjacencyFailed")]
    [InlineData(OcctFaceAdjacencyStatus.Unknown, GeometryQueryStatus.Unresolved, false, "UnknownFaceAdjacencyStatus")]
    public void AreAdjacent_PreservesTypedNativeStatus(
        OcctFaceAdjacencyStatus nativeStatus,
        GeometryQueryStatus expectedStatus,
        bool expectedValue,
        string? expectedIssueCode)
    {
        FaceRefId first = FaceRefId.New();
        FaceRefId second = FaceRefId.New();
        var bridge = new FakeBridge { AdjacencyStatus = nativeStatus };
        OcctGeometryQueries queries = CreateQueries(bridge, (first, 4), (second, 9));

        GeometryQueryResult<bool> result = queries.AreAdjacent(first, second);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedValue, result.Value);
        if (expectedIssueCode is not null)
            Assert.Contains(result.Issues, issue => issue.Code == expectedIssueCode);
    }

    /// <summary>같은 안정 Face는 네이티브 질의 없이 인접으로 처리합니다.</summary>
    [Fact]
    public void AreAdjacent_ReturnsTrueForTheSameFace()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<bool> result = queries.AreAdjacent(face, face);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value);
        Assert.Null(bridge.LastAdjacentNativeFaces);
    }

    /// <summary>선택 topology의 공유 Edge 성분과 홀 경계를 안정 Face 참조로 변환합니다.</summary>
    [Fact]
    public void GetSelectedTopology_MapsComponentsAdjacenciesAndHoleLoops()
    {
        FaceRefId first = FaceRefId.New();
        FaceRefId second = FaceRefId.New();
        OcctTopologyBoundaryLoop outerLoop = CreateLoop(
            [new(0, 0, 0), new(20, 0, 0), new(20, 10, 0), new(0, 10, 0)],
            [4, 9, 9, 4]);
        OcctTopologyBoundaryLoop holeLoop = CreateLoop(
            [new(2, 2, 0), new(2, 8, 0), new(8, 8, 0), new(8, 2, 0)],
            [4, 4, 4, 4]);
        var bridge = new FakeBridge
        {
            TopologyResult = new OcctSelectedTopologyResult(
                OcctSelectedTopologyStatus.Success,
                [new OcctTopologyComponent([4, 9], [outerLoop, holeLoop])],
                [new OcctTopologyAdjacency(4, 9)])
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (first, 4), (second, 9));

        GeometryQueryResult<SelectedTopology> result = queries.GetSelectedTopology([first, second]);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(new ModelRevision(7), result.Value!.ModelRevision);
        TopologyComponent component = Assert.Single(result.Value!.Components);
        Assert.Equal([first, second], component.FaceReferences);
        Assert.Equal([false, true], component.BoundaryLoops.Select(loop => loop.IsHole));
        Assert.Equal(first, component.BoundaryLoops[0].Segments[0].SourceFaceReferenceId);
        Assert.Equal(second, component.BoundaryLoops[0].Segments[1].SourceFaceReferenceId);
        Assert.Equal(new FaceAdjacency(first, second), Assert.Single(result.Value.Adjacencies));
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastCoordinateSystemId);
        Assert.Equal(new ModelRevision(7), bridge.LastRevision);
        Assert.Equal([4, 9], bridge.LastSelectedNativeFaceIndices);
        Assert.Equal(GeometryToleranceProfile.Initial.MeshDeflectionMm, bridge.LastDeflectionMm);
    }

    /// <summary>Native topology 오류 상태를 stale·미확정·기하 실패로 구분합니다.</summary>
    [Theory]
    [InlineData(OcctSelectedTopologyStatus.InvalidInput, GeometryQueryStatus.Unresolved, "InvalidSelectedTopologyRequest")]
    [InlineData(OcctSelectedTopologyStatus.InvalidFace, GeometryQueryStatus.Unresolved, "InvalidNativeSelectedFace")]
    [InlineData(OcctSelectedTopologyStatus.StaleContext, GeometryQueryStatus.StaleGeometryContext, "StaleGeometryContext")]
    [InlineData(OcctSelectedTopologyStatus.InvalidTopology, GeometryQueryStatus.Unresolved, "InvalidBoundaryTopology")]
    [InlineData(OcctSelectedTopologyStatus.GeometryFailure, GeometryQueryStatus.Failure, "OcctSelectedTopologyFailed")]
    [InlineData(OcctSelectedTopologyStatus.Unknown, GeometryQueryStatus.Unresolved, "UnknownSelectedTopologyStatus")]
    public void GetSelectedTopology_PreservesNativeFailureStatus(
        OcctSelectedTopologyStatus nativeStatus,
        GeometryQueryStatus expectedStatus,
        string expectedIssueCode)
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { TopologyResult = new OcctSelectedTopologyResult(nativeStatus) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<SelectedTopology> result = queries.GetSelectedTopology([face]);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == expectedIssueCode);
    }

    /// <summary>자기 교차 경계는 유효한 영역 루프로 반환하지 않습니다.</summary>
    [Fact]
    public void GetSelectedTopology_RejectsSelfIntersectingProjectedLoop()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge
        {
            TopologyResult = new OcctSelectedTopologyResult(
                OcctSelectedTopologyStatus.Success,
                [new OcctTopologyComponent(
                    [4],
                    [CreateLoop(
                        [new(0, 0, 0), new(10, 10, 0), new(0, 10, 0), new(10, 0, 0)],
                        [4, 4, 4, 4])])])
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<SelectedTopology> result = queries.GetSelectedTopology([face]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "InvalidSelectedBoundaryTopology");
    }

    /// <summary>표면 삼각 요소를 안정 Face ID와 경계 구멍 정보 및 면적 오차와 함께 변환합니다.</summary>
    [Fact]
    public void GetSurfaceElements_MapsClippedElementsAndEffectiveBoundary()
    {
        FaceRefId face = FaceRefId.New();
        var element = new OcctSurfaceElementData(
            4,
            12.5,
            new Vec3(1, 2, 3),
            new Vec2(0.25, 0.75),
            new Vec2(5, 6),
            [new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(0, 5, 0)],
            0.02);
        var bridge = new FakeBridge
        {
            SurfaceElementResult = new OcctSurfaceElementQueryResult(
                OcctSurfaceElementQueryStatus.Success,
                [element],
                areaErrorBoundMm2: 0.15)
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<SurfaceElementSet> result = queries.GetSurfaceElements(
            CreateRegion(face),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        SurfaceElement mapped = Assert.Single(result.Value!.Elements);
        Assert.Equal(face, mapped.FaceReferenceId);
        Assert.Equal(new AreaMm2(12.5), mapped.SurfaceAreaMm2);
        Assert.Equal(new Vec3(1, 2, 3), mapped.ModelCentroid);
        Assert.Equal(new Vec2(0.25, 0.75), mapped.FaceUvCentroid);
        Assert.Equal(new Vec2(5, 6), mapped.WorkingUvCentroid);
        Assert.Equal(0.02, mapped.Approximation.MaximumMeasuredDeviationMm);
        Assert.Equal(4, mapped.Approximation.ProbeCount);
        Assert.Equal(new AreaMm2(0.15), result.Value.AreaErrorBoundMm2);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastCoordinateSystemId);
        Assert.Equal(new ModelRevision(7), bridge.LastRevision);
        Assert.Equal([4], bridge.LastSelectedNativeFaceIndices);
        Assert.Equal(GeometryToleranceProfile.Initial.MeshDeflectionMm, bridge.LastSurfaceDeflectionMm);
        Assert.Equal(GeometryToleranceProfile.Initial.MeshAngularToleranceRad, bridge.LastSurfaceAngularToleranceRad);
        OcctRegionLoop[] capturedBoundary = bridge.LastEffectiveBoundary!.ToArray();
        Assert.Equal([false, true], capturedBoundary.Select(loop => loop.IsHole));
        Assert.All(capturedBoundary, loop => Assert.Equal(4, loop.Vertices.Count));
    }

    /// <summary>매핑되지 않은 Face 참조는 표면 요소 Native 질의를 시작하기 전에 거부합니다.</summary>
    [Fact]
    public void GetSurfaceElements_RejectsMissingFaceMappingWithoutCallingBridge()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge);

        GeometryQueryResult<SurfaceElementSet> result = queries.GetSurfaceElements(
            CreateRegion(face),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "UnresolvedFaceReference");
        Assert.Equal(0, bridge.SurfaceElementQueryCallCount);
    }

    /// <summary>Native가 선택 집합 밖의 Face를 반환하면 표면 요소 결과를 미확정으로 닫습니다.</summary>
    [Fact]
    public void GetSurfaceElements_RejectsElementFromUnselectedFace()
    {
        FaceRefId selectedFace = FaceRefId.New();
        var unexpectedElement = new OcctSurfaceElementData(
            9,
            1,
            new Vec3(0, 0, 0),
            new Vec2(0, 0),
            new Vec2(0, 0),
            [new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0)],
            0);
        var bridge = new FakeBridge
        {
            SurfaceElementResult = new OcctSurfaceElementQueryResult(
                OcctSurfaceElementQueryStatus.Success,
                [unexpectedElement])
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (selectedFace, 4));

        GeometryQueryResult<SurfaceElementSet> result = queries.GetSurfaceElements(
            CreateRegion(selectedFace),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "InvalidNativeSurfaceElements");
        Assert.Null(result.Value);
    }

    /// <summary>표면 요소 Native 상태를 성공, stale, 입력 오류 및 기하 실패로 구분합니다.</summary>
    [Theory]
    [InlineData(OcctSurfaceElementQueryStatus.InvalidInput, GeometryQueryStatus.Unresolved, "InvalidSurfaceElementQuery")]
    [InlineData(OcctSurfaceElementQueryStatus.InvalidFace, GeometryQueryStatus.Unresolved, "UnresolvedFaceReference")]
    [InlineData(OcctSurfaceElementQueryStatus.StaleContext, GeometryQueryStatus.StaleGeometryContext, "StaleGeometryContext")]
    [InlineData(OcctSurfaceElementQueryStatus.InvalidRegion, GeometryQueryStatus.Unresolved, "InvalidEffectiveRegionBoundary")]
    [InlineData(OcctSurfaceElementQueryStatus.ElementBudgetExceeded, GeometryQueryStatus.Unresolved, "SurfaceElementBudgetExceeded")]
    [InlineData(OcctSurfaceElementQueryStatus.GeometryFailure, GeometryQueryStatus.Failure, "OcctSurfaceElementQueryFailed")]
    [InlineData(OcctSurfaceElementQueryStatus.Unknown, GeometryQueryStatus.Unresolved, "UnknownSurfaceElementQueryStatus")]
    public void GetSurfaceElements_PreservesNativeFailureStatus(
        OcctSurfaceElementQueryStatus nativeStatus,
        GeometryQueryStatus expectedStatus,
        string expectedIssueCode)
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge
        {
            SurfaceElementResult = new OcctSurfaceElementQueryResult(nativeStatus)
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<SurfaceElementSet> result = queries.GetSurfaceElements(
            CreateRegion(face),
            GeometryToleranceProfile.Initial);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == expectedIssueCode);
        Assert.Null(result.Value);
    }

    /// <summary>취소된 표면 요소 질의는 Native 브리지에 전달하지 않습니다.</summary>
    [Fact]
    public void GetSurfaceElements_DoesNotCallBridgeAfterCancellation()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        GeometryQueryResult<SurfaceElementSet> result = queries.GetSurfaceElements(
            CreateRegion(face),
            GeometryToleranceProfile.Initial,
            cancellation.Token);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "OperationCancelled");
        Assert.Equal(0, bridge.SurfaceElementQueryCallCount);
    }

    /// <summary>취소된 미지원 질의에는 기능 미지원보다 취소 상태를 먼저 보고합니다.</summary>
    [Fact]
    public void UnsupportedQuery_ReportsCancellationBeforeUnsupportedFeature()
    {
        var bridge = new FakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        GeometryQueryResult<SelectedTopology> result = queries.GetSelectedTopology([], cancellation.Token);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "OperationCancelled");
    }

    /// <summary>미리 취소된 요청은 Native 브리지에 전달하지 않습니다.</summary>
    [Fact]
    public void QueryFirstHit_DoesNotCallBridgeAfterCancellation()
    {
        FaceRefId faceReferenceId = FaceRefId.New();
        var bridge = new FakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge, (faceReferenceId, 4));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        GeometryQueryResult<SurfaceHit> result = queries.QueryFirstHit(
            CreateCoordinateSystem(),
            new Vec2(1, 2),
            faceReferenceId,
            cancellation.Token);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "OperationCancelled");
        Assert.Equal(0, bridge.QueryCallCount);
    }

    /// <summary>중복 Native 번호는 문서 Face 지도의 모호성으로 거부합니다.</summary>
    [Fact]
    public void Constructor_RejectsDuplicateNativeFaceIndices()
    {
        FaceRefId first = FaceRefId.New();
        FaceRefId second = FaceRefId.New();
        var bridge = new FakeBridge();

        Assert.Throws<ArgumentException>(() => CreateQueries(bridge, (first, 4), (second, 4)));
    }

    /// <summary>선분으로 정의된 평행 곡선의 중심선 거리와 외곽 간격을 계산합니다.</summary>
    [Fact]
    public void MeasureClearance_ComputesExactParallelLineGap()
    {
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(10, 0, 0));
        CurveGeometrySnapshot second = CreateLine(
            new Vec3(2, 2, 0),
            new Vec3(8, 2, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([first, second], wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsComplete);
        Assert.Equal(2, result.Value.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.Equal(1.6, result.Value.MinimumClearGapMm.Value, precision: 10);
        Assert.True(result.Value.ErrorBoundMm.Value > 0);
        Assert.Equal(2, (result.Value.FirstLocation - result.Value.SecondLocation).Length, precision: 10);
    }

    /// <summary>공간상 떨어진 많은 선분은 전체 쌍 한도에 걸리지 않고 최소 간격을 계산합니다.</summary>
    [Fact]
    public void MeasureClearance_UsesSpatialCandidatesForLargeSparseInput()
    {
        CurveGeometrySnapshot[] curves = Enumerable.Range(0, 4_500)
            .Select(index =>
            {
                double startX = index * 10;
                return CreateLine(new Vec3(startX, 0, 0), new Vec3(startX + 1, 0, 0));
            })
            .ToArray();

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(curves, wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(9, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.Equal(8.6, result.Value.MinimumClearGapMm.Value, precision: 10);
    }

    /// <summary>서로 만나지 않는 끝점을 가진 두 선분의 내부 교차를 최소거리 0으로 찾습니다.</summary>
    [Fact]
    public void MeasureClearance_DetectsInteriorCrossing()
    {
        CurveGeometrySnapshot horizontal = CreateLine(
            new Vec3(-1, 0, 0),
            new Vec3(1, 0, 0));
        CurveGeometrySnapshot vertical = CreateLine(
            new Vec3(0, -1, 0),
            new Vec3(0, 1, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([horizontal, vertical], wireDiameterMm: 0.6);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(0, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.Equal(-0.6, result.Value.MinimumClearGapMm.Value, precision: 10);
        Assert.Equal(result.Value.FirstLocation, result.Value.SecondLocation);
    }

    /// <summary>같은 다중선의 비이웃 선분이 교차하면 자기 접촉으로 검출합니다.</summary>
    [Fact]
    public void MeasureClearance_DetectsNonlocalSelfIntersection()
    {
        CurveGeometrySnapshot selfCrossing = CreatePolyline(
        [
            new Vec3(-2, -2, 0),
            new Vec3(2, 2, 0),
            new Vec3(2, -2, 0),
            new Vec3(-2, 2, 0)
        ]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([selfCrossing], wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(0, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.True(result.Value.MinimumClearGapMm.Value < 0);
    }

    /// <summary>비주기 유리 B-spline과 직선 사이의 거리를 근사 오차 안에서 계산합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresNonperiodicRationalBSplineWithinErrorBound()
    {
        double diagonal = Math.Sqrt(0.5);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            CreateRationalQuarterSpline(),
            new Vec3(diagonal, 0, -2 + diagonal),
            new Vec3(0, 0, -1));
        CurveGeometrySnapshot line = CreateLine(
            new Vec3(-2, 5, -1.3),
            new Vec3(2, 5, -1.3));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([spline, line], wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsComplete);
        Assert.InRange(
            Math.Abs(result.Value.MinimumCenterlineDistanceMm.Value - 5),
            0,
            result.Value.ErrorBoundMm.Value);
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>여러 매듭 구간의 이웃 접점을 연결로 분류하고 표본 간격을 자기 간격으로 세지 않습니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresMultiSpanBSplineWithoutLocalSampleGap()
    {
        BSplineCurveDefinition definition = CreateKnotJoinedSpline(trimStart: 0, trimEnd: 2);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            definition.Poles[0],
            definition.Poles[^1]);
        CurveGeometrySnapshot distantLine = CreateLine(
            new Vec3(-10, -10, 5),
            new Vec3(10, -10, 5));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, distantLine],
            wireDiameterMm: 0.4);

        Assert.True(result.HasValue, string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => $"{issue.Code}: {string.Join("; ", issue.Evidence)}")));
        Assert.True(
            result.Value!.MinimumCenterlineDistanceMm.Value > 10,
            $"측정된 중심선 거리: {result.Value.MinimumCenterlineDistanceMm.Value:R}");
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>인접 2차 매듭 구간의 경계 상자가 접점 밖에서 겹치면 자기 접촉 가능성을 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesOverlappingAdjacentQuadraticSpansUnresolved()
    {
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles:
            [
                new Vec3(-1, 0, 0),
                new Vec3(1, 2, 0),
                new Vec3(0, 0, 0),
                new Vec3(1, -2, 0),
                new Vec3(1, 0, 0)
            ],
            weights: [1, 1, 1, 1, 1],
            knots: [0, 1, 2],
            multiplicities: [3, 2, 3],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 2);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            definition.Poles[0],
            new Vec3(0, 3, 0),
            definition.Poles[^1]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, CreateLine(new Vec3(10, 10, 0), new Vec3(11, 10, 0))],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "BSplineSelfContactUnresolved");
    }

    /// <summary>선분을 되짚는 단일 2차 B-spline의 간격을 확정하지 않습니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesRetracingQuadraticBSplineSelfContactUnresolved()
    {
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles:
            [
                new Vec3(0, 0, 0),
                new Vec3(2, 0, 0),
                new Vec3(1, 0, 0)
            ],
            weights: [1, 1, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            definition.Poles[0],
            definition.Poles[^1]);
        CurveGeometrySnapshot distantLine = CreateLine(
            new Vec3(0, 5, 0),
            new Vec3(1, 5, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, distantLine],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "BSplineSelfContactUnresolved");
    }

    /// <summary>어느 축도 단조롭지 않은 3차 B-spline은 자기 접촉을 배제하지 못해 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesNonMonotoneCubicBSplineSelfContactUnresolved()
    {
        var definition = new BSplineCurveDefinition(
            degree: 3,
            poles:
            [
                new Vec3(-1, -1, 0),
                new Vec3(1, 1, 0),
                new Vec3(-1, 1, 0),
                new Vec3(1, -1, 0)
            ],
            weights: [1, 1, 1, 1],
            knots: [0, 1],
            multiplicities: [4, 4],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            definition.Poles[0],
            definition.Poles[^1]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, CreateLine(new Vec3(10, 10, 0), new Vec3(11, 10, 0))],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "BSplineSelfContactUnresolved");
    }

    /// <summary>증가·감소 극점과 비균등 가중치를 가진 다중 구간 3차 B-spline의 간격을 계산합니다.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasureClearance_MeasuresMonotoneMultiSpanCubicBSplineWithinErrorBound(bool decreasingPoleAxis)
    {
        double[] xCoordinates = decreasingPoleAxis
            ? [0.6, 0.5, 0.4, 0.3, 0.2, 0.1, 0]
            : [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6];
        double[] yCoordinates = [0, 0.1, 0.1, 0.3, 0.2, 0.2, 0];
        Vec3[] poles = Enumerable.Range(0, xCoordinates.Length)
            .Select(index => new Vec3(xCoordinates[index], yCoordinates[index], 0))
            .ToArray();
        double[] weights = [1, 2, 0.75, 1.5, 1, 0.8, 1.2];
        double[] knots = [0, 1, 2];
        int[] multiplicities = [4, 3, 4];
        var definition = new BSplineCurveDefinition(
            degree: 3,
            poles: poles,
            weights: weights,
            knots: knots,
            multiplicities: multiplicities,
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 2);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(definition, poles[0], poles[^1]);
        CurveGeometrySnapshot verticalLine = CreateLine(
            new Vec3(0.3, 0.3, 0.5),
            new Vec3(0.3, 0.3, 0.6));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, verticalLine],
            wireDiameterMm: 0.4);

        Assert.True(result.HasValue, string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => $"{issue.Code}: {string.Join("; ", issue.Evidence)}")));
        Assert.True(result.Value!.IsComplete);
        Assert.InRange(
            Math.Abs(result.Value.MinimumCenterlineDistanceMm.Value - 0.5),
            0,
            result.Value.ErrorBoundMm.Value);
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>관리형 B-spline 간격 fallback의 최대 지원 차수인 8차 곡선을 처리합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresMonotoneDegreeEightBSplineWithinErrorBound()
    {
        const int degree = 8;
        BSplineCurveDefinition definition = CreateMonotoneBezierSpline(degree, curveInterior: true);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            definition.Poles[0],
            definition.Poles[^1]);
        CurveGeometrySnapshot parallelLine = CreateLine(
            new Vec3(0, -0.4, 0),
            new Vec3(1, -0.4, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, parallelLine],
            wireDiameterMm: 0.4);

        Assert.True(result.HasValue, string.Join(
            Environment.NewLine,
            result.Issues.Select(issue => $"{issue.Code}: {string.Join("; ", issue.Evidence)}")));
        Assert.True(result.Value!.IsComplete);
        Assert.InRange(
            Math.Abs(result.Value.MinimumCenterlineDistanceMm.Value - 0.4),
            0,
            result.Value.ErrorBoundMm.Value);
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>관리형 근사 차수 한도를 넘는 9차 B-spline을 미확정으로 반환합니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesDegreeNineBSplineUnresolved()
    {
        BSplineCurveDefinition definition = CreateMonotoneBezierSpline(degree: 9);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            definition.Poles[0],
            definition.Poles[^1]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, CreateLine(new Vec3(2, 2, 0), new Vec3(3, 2, 0))],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "BSplineApproximationBudgetExceeded");
    }

    /// <summary>비주기 1차 B-spline의 비이웃 매듭 구간 교차를 자기 접촉으로 찾습니다.</summary>
    [Fact]
    public void MeasureClearance_DetectsNonlocalBSplineSelfIntersection()
    {
        Vec3[] poles =
        [
            new Vec3(-1, -1, 0),
            new Vec3(1, 1, 0),
            new Vec3(-1, 1, 0),
            new Vec3(1, -1, 0)
        ];
        var definition = new BSplineCurveDefinition(
            degree: 1,
            poles,
            weights: [1, 1, 1, 1],
            knots: [0, 1, 2, 3],
            multiplicities: [2, 1, 1, 2],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 3);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(definition, poles);
        CurveGeometrySnapshot distantLine = CreateLine(
            new Vec3(10, 0, 0),
            new Vec3(11, 0, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, distantLine],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(0, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.True(result.Value.MinimumClearGapMm.Value < 0);
    }

    /// <summary>주기 B-spline의 seam 자기접촉은 정확 질의 없이 확정하지 않습니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesPeriodicBSplineUnresolved()
    {
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles:
            [
                new Vec3(1, 0, 0),
                new Vec3(0, 1, 0),
                new Vec3(-1, 0, 0)
            ],
            weights: [1, 1, 1],
            knots: [0, 1, 2, 3],
            multiplicities: [1, 1, 1, 1],
            isPeriodic: true,
            trimStart: 0,
            trimEnd: 3);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            new Vec3(1, 0, 0),
            new Vec3(1, 0, 0.1));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, CreateLine(new Vec3(5, 0, 0), new Vec3(6, 0, 0))],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "PeriodicBSplineClearanceUnsupported");
    }

    /// <summary>B-spline 연결 끝점은 국소 접촉 외의 추가 접촉을 배제하지 못해 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesDeclaredBSplineJointUnresolved()
    {
        double diagonal = Math.Sqrt(0.5);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            CreateRationalQuarterSpline(),
            new Vec3(diagonal, 0, -2 + diagonal),
            new Vec3(0, 0, -1));
        CurveGeometrySnapshot line = CreateLine(spline.End, spline.End + new Vec3(1, 0, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [spline, line],
            wireDiameterMm: 0.4,
            localAdjacencies:
            [new CurveSegmentAdjacency(
                spline.HeatingPathSegmentId!.Value,
                line.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "CurvedEndpointContactNotCertified");
    }

    /// <summary>연결되지 않은 원호와 B-spline을 각각 근사해 같은 거리 계산으로 검사합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresMixedArcAndBSpline()
    {
        Vec3[] poles =
        [
            new Vec3(10, 0, 0),
            new Vec3(11, 1, 0),
            new Vec3(12, 0, 0)
        ];
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles,
            weights: [1, 1, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            definition,
            poles[0],
            poles[^1]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [CreateArc(2, 0, Math.PI / 2), spline],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsComplete);
        Assert.InRange(result.Value.MinimumCenterlineDistanceMm.Value, 7.9, 8.1);
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>원호와 B-spline의 선언된 공유 끝점은 연결 근접부를 인증하지 못해 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesDeclaredArcAndBSplineJointUnresolved()
    {
        CurveGeometrySnapshot arc = CreateArc(2, 0, Math.PI / 2);
        Vec3[] poles =
        [
            new Vec3(5, 3, 0),
            new Vec3(2, 3, 0),
            arc.End
        ];
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles,
            weights: [1, 1, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1,
            direction: CurveDirection.Reversed);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(definition, poles[^1], poles[0]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [arc, spline],
            wireDiameterMm: 0.4,
            localAdjacencies:
            [new CurveSegmentAdjacency(
                arc.HeatingPathSegmentId!.Value,
                spline.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "CurvedEndpointContactNotCertified");
    }

    /// <summary>요구 오차보다 수치 오차가 크면 B-spline 거리 근사를 확정하지 않습니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesBSplineApproximationOverBudgetUnresolved()
    {
        var strictTolerances = new GeometryToleranceProfile(
            connectionToleranceMm: 1e-12,
            meshDeflectionMm: 0.05,
            meshAngularToleranceRad: 0.1,
            mappingLeafLengthMm: 0.1,
            mappingChordDeviationMm: 0.05,
            mappingMaximumDepth: 18,
            mappingMaximumProbes: 100_000,
            maximumContinuousSampleSpacingMm: 2);
        double diagonal = Math.Sqrt(0.5);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(
            CreateRationalQuarterSpline(),
            new Vec3(diagonal, 0, -2 + diagonal),
            new Vec3(0, 0, -1));

        GeometryQueryResult<ClearanceMeasurement> result = CreateQueries(new FakeBridge()).MeasureClearance(
            [spline, CreateLine(new Vec3(-2, 5, -1.3), new Vec3(2, 5, -1.3))],
            new LengthMm(0.4),
            new LengthMm(0),
            strictTolerances);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "BSplineClearanceErrorBoundExceeded");
    }

    /// <summary>공간에서 비스듬히 놓인 선분의 내부 최단점과 간격을 계산합니다.</summary>
    [Fact]
    public void MeasureClearance_ComputesInteriorDistanceForSkewLines()
    {
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(-1, 0, 0),
            new Vec3(1, 0, 0));
        CurveGeometrySnapshot second = CreateLine(
            new Vec3(0, -1, 3),
            new Vec3(0, 1, 3));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([first, second], wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(3, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.Equal(0, result.Value.FirstLocation.X, precision: 10);
        Assert.Equal(0, result.Value.FirstLocation.Y, precision: 10);
        Assert.Equal(3, result.Value.SecondLocation.Z, precision: 10);
    }

    /// <summary>다중선에서 공유 접점을 지나 되돌아 겹치는 선분을 간격 0으로 검출합니다.</summary>
    [Fact]
    public void MeasureClearance_DetectsCollinearPolylineBacktrack()
    {
        CurveGeometrySnapshot backtracking = CreatePolyline(
        [
            new Vec3(0, 0, 0),
            new Vec3(2, 0, 0),
            new Vec3(1, 0, 0)
        ]);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([backtracking], wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(0, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.True(result.Value.MinimumClearGapMm.Value < 0);
    }

    /// <summary>같은 곡선의 비공선 접점은 제외하고 다른 비국소 간격을 계산합니다.</summary>
    [Fact]
    public void MeasureClearance_ExcludesOnlySharedCornerContact()
    {
        CurveGeometrySnapshot bentPath = CreatePolyline(
        [
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0),
            new Vec3(1, 1, 0)
        ]);
        CurveGeometrySnapshot distantPath = CreateLine(
            new Vec3(0, 0, 5),
            new Vec3(1, 0, 5));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [bentPath, distantPath],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(5, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
        Assert.Equal(4.6, result.Value.MinimumClearGapMm.Value, precision: 10);
    }

    /// <summary>공유 접점만 있는 이웃 구간을 제외하고 다른 비국소 구간의 간격을 측정합니다.</summary>
    [Fact]
    public void MeasureClearance_ExcludesOnlyLocalSharedEndpointContact()
    {
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0));
        CurveGeometrySnapshot adjacent = CreateLine(
            new Vec3(1, 0, 0),
            new Vec3(2, 0, 0));
        CurveGeometrySnapshot distant = CreateLine(
            new Vec3(0, 0, 5),
            new Vec3(1, 0, 5));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [first, adjacent, distant],
            wireDiameterMm: 0.4,
            localAdjacencies:
            [new CurveSegmentAdjacency(
                first.HeatingPathSegmentId!.Value,
                adjacent.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(5, result.Value!.MinimumCenterlineDistanceMm.Value, precision: 10);
    }

    /// <summary>선언된 곡선 연결의 끝점이 만나지 않으면 다른 쌍의 간격이 있어도 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureClearance_RejectsDeclaredAdjacencyWithSeparatedEndpoints()
    {
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0));
        CurveGeometrySnapshot separated = CreateLine(
            new Vec3(1, 0.5, 0),
            new Vec3(2, 0.5, 0));
        CurveGeometrySnapshot distant = CreateLine(
            new Vec3(0, 5, 0),
            new Vec3(1, 5, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [first, separated, distant],
            wireDiameterMm: 0.4,
            localAdjacencies:
            [new CurveSegmentAdjacency(
                first.HeatingPathSegmentId!.Value,
                separated.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "DeclaredCurveEndpointsDoNotMeet");
        Assert.Null(result.Value);
    }

    /// <summary>연속 끝점 불일치가 있으면 Native 거리 브리지도 호출하지 않습니다.</summary>
    [Fact]
    public void MeasureClearance_RejectsDeclaredAdjacencyBeforeNativeQuery()
    {
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0));
        CurveGeometrySnapshot separated = CreateLine(
            new Vec3(1, 0.5, 0),
            new Vec3(2, 0.5, 0));
        var bridge = new NativeClearanceFakeBridge();

        GeometryQueryResult<ClearanceMeasurement> result = CreateQueries(bridge).MeasureClearance(
            [first, separated],
            new LengthMm(0.4),
            new LengthMm(0),
            GeometryToleranceProfile.Initial,
            [new CurveSegmentAdjacency(
                first.HeatingPathSegmentId!.Value,
                separated.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "DeclaredCurveEndpointsDoNotMeet");
        Assert.Equal(0, bridge.ClearanceCallCount);
    }

    /// <summary>다른 곡선의 공유 끝점은 topology 연결 근거가 없으면 전체 간격을 미확정으로 닫습니다.</summary>
    [Fact]
    public void MeasureClearance_RejectsUnclassifiedCrossCurveEndpointEvenWhenOtherPairsExist()
    {
        CurveGeometrySnapshot first = CreateLine(new Vec3(0, 0, 0), new Vec3(1, 0, 0));
        CurveGeometrySnapshot touching = CreateLine(new Vec3(1, 0, 0), new Vec3(1, 1, 0));
        CurveGeometrySnapshot distant = CreateLine(new Vec3(0, 4, 0), new Vec3(1, 4, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [first, touching, distant],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "UnclassifiedCurveEndpointContact");
    }

    /// <summary>안정 구간 ID의 연속 관계를 현재 곡선 인덱스 쌍으로 Native 브리지에 전달합니다.</summary>
    [Fact]
    public void MeasureClearance_PassesDeclaredCurveAdjacencyToNativeBridge()
    {
        CurveGeometrySnapshot first = CreateLine(new Vec3(0, 0, 0), new Vec3(1, 0, 0));
        CurveGeometrySnapshot adjacent = CreateLine(new Vec3(1, 0, 0), new Vec3(2, 0, 0));
        CurveGeometrySnapshot distant = CreateLine(new Vec3(0, 5, 0), new Vec3(1, 5, 0));
        var measurement = new ClearanceMeasurement(
            new LengthMm(5), new LengthMm(4.6), new Vec3(0, 0, 0), new Vec3(0, 5, 0),
            new LengthMm(0.001), isComplete: true);
        var bridge = new NativeClearanceFakeBridge
        {
            ClearanceResult = new OcctCurveClearanceQueryResult(OcctCurveClearanceStatus.Success, measurement)
        };
        var queries = CreateQueries(bridge);

        GeometryQueryResult<ClearanceMeasurement> result = queries.MeasureClearance(
            [first, adjacent, distant],
            new LengthMm(0.4),
            new LengthMm(1),
            GeometryToleranceProfile.Initial,
            [new CurveSegmentAdjacency(
                first.HeatingPathSegmentId!.Value,
                adjacent.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal((0, 1), Assert.Single(bridge.LastLocalCurvePairs!));
    }

    /// <summary>원호와 직선 사이의 거리를 오차 한도 안에서 근사합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresArcAgainstLineWithinErrorBound()
    {
        CurveGeometrySnapshot arc = CreateArc(2, 0, Math.PI / 3);
        CurveGeometrySnapshot line = CreateLine(
            new Vec3(0, 0, 3),
            new Vec3(2, 0, 3));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([arc, line], wireDiameterMm: 0.4);

        Assert.True(
            result.Status == GeometryQueryStatus.Success,
            string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence.Prepend(issue.Code))));
        Assert.True(result.Value!.IsComplete);
        Assert.InRange(
            Math.Abs(result.Value.MinimumCenterlineDistanceMm.Value - 3),
            0,
            result.Value.ErrorBoundMm.Value);
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>서로 다른 반지름의 같은 평면 원호에서 최단거리 오차를 보존합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresConcentricArcsWithinErrorBound()
    {
        CurveGeometrySnapshot innerArc = CreateArc(2, 0, Math.PI / 2);
        CurveGeometrySnapshot outerArc = CreateArc(5, 0, Math.PI / 2);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [innerArc, outerArc],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.InRange(
            Math.Abs(result.Value!.MinimumCenterlineDistanceMm.Value - 3),
            0,
            result.Value.ErrorBoundMm.Value);
    }

    /// <summary>닫힌 한 바퀴 원호는 끝점 닫힘 오차를 포함해 측정합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresFullCircleAgainstLineWithinErrorBound()
    {
        CurveGeometrySnapshot circle = CreateArc(2, 0, 2 * Math.PI);
        CurveGeometrySnapshot line = CreateLine(
            new Vec3(-2, 0, 3),
            new Vec3(2, 0, 3));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [circle, line],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.InRange(
            Math.Abs(result.Value!.MinimumCenterlineDistanceMm.Value - 3),
            0,
            result.Value.ErrorBoundMm.Value);
    }

    /// <summary>역방향 원호도 스냅샷 시작점과 곡선 방향을 보존해 측정합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresReversedArcAgainstLine()
    {
        CurveGeometrySnapshot reversedArc = CreateArc(2, 0, Math.PI / 3, CurveDirection.Reversed);
        CurveGeometrySnapshot line = CreateLine(
            new Vec3(0, 0, 3),
            new Vec3(2, 0, 3));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [reversedArc, line],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.InRange(
            Math.Abs(result.Value!.MinimumCenterlineDistanceMm.Value - 3),
            0,
            result.Value.ErrorBoundMm.Value);
    }

    /// <summary>요구 오차가 너무 작아 원호 분할 예산을 넘으면 미확정으로 반환합니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesArcApproximationOverBudgetUnresolved()
    {
        var strictTolerances = new GeometryToleranceProfile(
            connectionToleranceMm: 1e-12,
            meshDeflectionMm: 0.05,
            meshAngularToleranceRad: 0.1,
            mappingLeafLengthMm: 0.1,
            mappingChordDeviationMm: 0.05,
            mappingMaximumDepth: 18,
            mappingMaximumProbes: 100_000,
            maximumContinuousSampleSpacingMm: 2);

        GeometryQueryResult<ClearanceMeasurement> result = CreateQueries(new FakeBridge()).MeasureClearance(
            [CreateArc(2, 0, Math.PI / 2), CreateLine(new Vec3(10, 0, 3), new Vec3(11, 0, 3))],
            new LengthMm(0.4),
            new LengthMm(0),
            strictTolerances);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "ArcApproximationBudgetExceeded");
    }

    /// <summary>원호가 포함된 선언 연결 끝점은 추가 접촉을 배제하지 못해 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesDeclaredArcJointUnresolved()
    {
        CurveGeometrySnapshot arc = CreateArc(2, 0, Math.PI / 2);
        CurveGeometrySnapshot line = CreateLine(arc.End, arc.End + new Vec3(1, 0, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [arc, line],
            wireDiameterMm: 0.4,
            localAdjacencies:
            [new CurveSegmentAdjacency(
                arc.HeatingPathSegmentId!.Value,
                line.HeatingPathSegmentId!.Value)]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "CurvedEndpointContactNotCertified");
    }

    /// <summary>한 바퀴를 넘는 원호는 관리형 근사로 자기접촉 판정을 확정하지 않습니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesRepeatedArcSweepUnresolved()
    {
        CurveGeometrySnapshot repeatedArc = CreateArc(2, 0, 3 * Math.PI);

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance(
            [repeatedArc, CreateLine(new Vec3(10, 0, 0), new Vec3(11, 0, 0))],
            wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "RepeatedArcSweepNotCertified");
    }

    /// <summary>거의 평행한 선분은 경계 거리로 최솟값을 제한해 측정합니다.</summary>
    [Fact]
    public void MeasureClearance_MeasuresNearParallelLinesWithinErrorBound()
    {
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(1_000, 0, 0));
        CurveGeometrySnapshot second = CreateLine(
            new Vec3(0, 1, 0),
            new Vec3(1_000, 1.00000001, 0));

        GeometryQueryResult<ClearanceMeasurement> result = MeasureClearance([first, second], wireDiameterMm: 0.4);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsComplete);
        Assert.InRange(
            Math.Abs(result.Value.MinimumCenterlineDistanceMm.Value - 1),
            0,
            result.Value.ErrorBoundMm.Value);
        Assert.True(result.Value.ErrorBoundMm.Value <= GeometryToleranceProfile.Initial.ConnectionToleranceMm);
    }

    /// <summary>근접 평행 선분의 상·하한 간격이 크면 미확정으로 남깁니다.</summary>
    [Fact]
    public void MeasureClearance_LeavesNearParallelLinesUnresolvedWhenBoundsDoNotConverge()
    {
        var strictTolerances = new GeometryToleranceProfile(
            connectionToleranceMm: 1e-12,
            meshDeflectionMm: 0.05,
            meshAngularToleranceRad: 0.1,
            mappingLeafLengthMm: 0.1,
            mappingChordDeviationMm: 0.05,
            mappingMaximumDepth: 18,
            mappingMaximumProbes: 100_000,
            maximumContinuousSampleSpacingMm: 2);
        CurveGeometrySnapshot first = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(1_000, 0, 0));
        CurveGeometrySnapshot second = CreateLine(
            new Vec3(0, -0.0004, 0.5),
            new Vec3(1_000, 0.0004, 0.5));

        GeometryQueryResult<ClearanceMeasurement> result = CreateQueries(new FakeBridge()).MeasureClearance(
            [first, second],
            new LengthMm(0.4),
            new LengthMm(0),
            strictTolerances);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "ClearanceNumericallyUnresolved");
    }

    /// <summary>선분 쌍 계산 중 취소되면 부분 최소거리 대신 미확정 상태를 반환합니다.</summary>
    [Fact]
    public void MeasureClearance_ReportsCancellationWithoutPartialValue()
    {
        CurveGeometrySnapshot first = CreateLine(new Vec3(0, 0, 0), new Vec3(1, 0, 0));
        CurveGeometrySnapshot second = CreateLine(new Vec3(0, 2, 0), new Vec3(1, 2, 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        GeometryQueryResult<ClearanceMeasurement> result = CreateQueries(new FakeBridge())
            .MeasureClearance([first, second], new LengthMm(0.4), new LengthMm(1), GeometryToleranceProfile.Initial, cancellation.Token);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "OperationCancelled");
    }

    /// <summary>정확 곡선 질의 대역이 있으면 Native 측정값을 우선 사용합니다.</summary>
    [Fact]
    public void MeasureClearance_UsesNativeCurveDistanceWhenAvailable()
    {
        CurveGeometrySnapshot first = CreateLine(new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        CurveGeometrySnapshot second = CreateLine(new Vec3(0, 2, 0), new Vec3(10, 2, 0));
        var measurement = new ClearanceMeasurement(
            new LengthMm(2),
            new LengthMm(1.6),
            new Vec3(5, 0, 0),
            new Vec3(5, 2, 0),
            new LengthMm(.001),
            isComplete: true);
        var bridge = new NativeClearanceFakeBridge
        {
            ClearanceResult = new OcctCurveClearanceQueryResult(
                OcctCurveClearanceStatus.Success,
                measurement)
        };
        var queries = new OcctGeometryQueries(
            bridge,
            CreateCoordinateSystem(),
            new Dictionary<FaceRefId, int>());

        GeometryQueryResult<ClearanceMeasurement> result = queries.MeasureClearance(
            [first, second],
            new LengthMm(.4),
            new LengthMm(1),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(measurement, result.Value);
        Assert.Equal(1, bridge.ClearanceCallCount);
        Assert.Equal(2, bridge.LastCurveDefinitions!.Count);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastClearanceCoordinateSystemId);
        Assert.Equal(new ModelRevision(7), bridge.LastClearanceRevision);
        Assert.Equal(.4, bridge.LastWireDiameterMm!.Value.Value);
        Assert.Equal(GeometryToleranceProfile.Initial.ConnectionToleranceMm, bridge.LastMaximumErrorMm);
    }

    /// <summary>Native가 미확정한 B-spline을 관리형 지원 범위로 확정할 수 있으면 fallback 결과를 사용합니다.</summary>
    [Fact]
    public void MeasureClearance_UsesManagedFallbackWhenNativeLeavesBSplineUnresolved()
    {
        Vec3[] poles =
        [
            new Vec3(10, 0, 0),
            new Vec3(11, 1, 0),
            new Vec3(12, 0, 0)
        ];
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles,
            weights: [1, 1, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(definition, poles[0], poles[^1]);
        var bridge = new NativeClearanceFakeBridge
        {
            ClearanceResult = new OcctCurveClearanceQueryResult(
                OcctCurveClearanceStatus.Unresolved,
                diagnostic: "B-spline 내부 자기접촉을 확인하지 못했습니다.")
        };

        GeometryQueryResult<ClearanceMeasurement> result = CreateQueries(bridge).MeasureClearance(
            [CreateArc(2, 0, Math.PI / 2), spline],
            new LengthMm(0.4),
            new LengthMm(0),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.InRange(result.Value!.MinimumCenterlineDistanceMm.Value, 7.9, 8.1);
        Assert.Equal(1, bridge.ClearanceCallCount);
    }

    /// <summary>관리형 근사가 B-spline 지원 범위를 벗어나면 Native 미확정 사유를 보존합니다.</summary>
    [Fact]
    public void MeasureClearance_PreservesNativeUnresolvedReasonWhenFallbackCannotCertifySpline()
    {
        Vec3[] poles =
        [
            new Vec3(0, 0, 0),
            new Vec3(1, 2, 0),
            new Vec3(2, 2, 0),
            new Vec3(3, 0, 0)
        ];
        var definition = new BSplineCurveDefinition(
            degree: 3,
            poles,
            weights: [1, 1, 1, 1],
            knots: [0, 1],
            multiplicities: [4, 4],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
        CurveGeometrySnapshot spline = CreateBSplineSnapshot(definition, poles[0], poles[^1]);
        var bridge = new NativeClearanceFakeBridge
        {
            ClearanceResult = new OcctCurveClearanceQueryResult(
                OcctCurveClearanceStatus.Unresolved,
                diagnostic: "B-spline 내부 자기접촉을 확인하지 못했습니다.")
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (FaceRefId.New(), 4));

        GeometryQueryResult<ClearanceMeasurement> result = queries.MeasureClearance(
            [spline],
            new LengthMm(.4),
            new LengthMm(.2),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("OcctClearanceUnresolved", issue.Code);
        Assert.Contains(issue.Evidence, evidence => evidence.Contains("B-spline 내부 자기접촉", StringComparison.Ordinal));
    }

    /// <summary>비어 있는 곡선 집합은 Native 거리 측정을 호출하지 않고 미확정으로 닫습니다.</summary>
    [Fact]
    public void MeasureClearance_RejectsEmptyCurveSetBeforeCallingNativeBridge()
    {
        var bridge = new NativeClearanceFakeBridge();
        OcctGeometryQueries queries = CreateQueries(bridge);

        GeometryQueryResult<ClearanceMeasurement> result = queries.MeasureClearance(
            [],
            new LengthMm(.4),
            new LengthMm(.2),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "NoClearanceCurves");
        Assert.Equal(0, bridge.ClearanceCallCount);
    }

    /// <summary>평판 first-hit과 평면 내 직선 끝에서 도체 바깥면까지의 깊이를 측정합니다.</summary>
    [Fact]
    public void MeasureDepth_ComputesInsertionDepthFromActualWireOutsidePoint()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = Hit(4, new Vec3(0, 0, 0), new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        SurfaceAnchor anchor = CreateAnchor(face, new Vec3(0, 0, 0));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(10, 0, -1.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            anchor,
            CreateCoordinateSystem(),
            new LengthMm(1),
            CancellationToken.None);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsResolved);
        Assert.Equal(1, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(0, result.Value.WireOutsidePoint.X, precision: 10);
        Assert.Equal(0, result.Value.WireOutsidePoint.Y, precision: 10);
        Assert.Equal(-1, result.Value.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(1, bridge.QueryCallCount);
        Assert.Equal(new Vec2(0, 0), bridge.LastWorkingUv);
    }

    /// <summary>Native envelope 결과를 우선 사용하고 표면별 관리형 질의를 생략합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesNativeEnvelopeBridgeWhenAvailable()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new NativeDepthBridge
        {
            DepthResult = new OcctCurveDepthEnvelopeQueryResult(
                OcctCurveDepthEnvelopeStatus.Success,
                new DepthMeasurement(
                    new LengthMm(1),
                    new LengthMm(1.1),
                    new LengthMm(.9),
                    new LengthMm(1.3),
                    new LengthMm(.02),
                    new Vec3(0, 0, 0),
                    new Vec3(1, 0, 0),
                    new Vec3(2, 0, 0),
                    new Vec3(1, 0, -1),
                    new Vec3(2, 0, -1.3),
                    probeCount: 8,
                    isResolved: true,
                    isComplete: true,
                    measurementRuleVersion: "occt-native-test-v1"))
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            CreateLine(new Vec3(0, 0, -1.15), new Vec3(10, 0, -1.15)),
            new LengthMm(.3),
            CreateAnchor(face, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(.9, result.Value!.MinimumMeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(1.3, result.Value.MaximumMeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal("occt-native-test-v1", result.Value.MeasurementRuleVersion);
        Assert.Equal(0, bridge.QueryCallCount);
        Assert.NotNull(bridge.LastRequest);
        Assert.Equal(new[] { 4 }, bridge.LastRequest!.SelectedNativeFaceIndices);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastRequest.CoordinateSystemId);
        Assert.Equal(new ModelRevision(7), bridge.LastRequest.Revision);
        Assert.Equal(new Vec3(10, 0, -1.15), bridge.LastRequest.CenterlineSamples[^1]);
    }

    /// <summary>Native 깊이 envelope의 미확정 결과를 재계산 성공으로 바꾸지 않습니다.</summary>
    [Fact]
    public void MeasureDepth_PreservesNativeEnvelopeUnresolvedStatus()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new NativeDepthBridge
        {
            DepthResult = new OcctCurveDepthEnvelopeQueryResult(
                OcctCurveDepthEnvelopeStatus.Unresolved,
                diagnostic: "Native probe 한도")
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            CreateLine(new Vec3(0, 0, -1.15), new Vec3(10, 0, -1.15)),
            new LengthMm(.3),
            CreateAnchor(face, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Contains(result.Issues, issue => issue.Evidence.Contains("Native probe 한도"));
        Assert.Equal(0, bridge.QueryCallCount);
    }

    /// <summary>Native 깊이 보정 결과를 보존하고 작업 좌표·Face·치수 계약을 전달합니다.</summary>
    [Fact]
    public void AdjustInsertionDepth_ForwardsSurfacePathAndMapsNativeCurve()
    {
        FaceRefId face = FaceRefId.New();
        var adjustedCurve = new PolylineCurveDefinition(
        [
            new Vec3(0, 0, -0.3),
            new Vec3(1, 0, -0.3),
            new Vec3(2, 0, -0.3)
        ]);
        var bridge = new NativeDepthAdjustmentFakeBridge
        {
            AdjustmentResult = new OcctCurveDepthAdjustmentQueryResult(
                OcctCurveDepthEnvelopeStatus.Success,
                new InsertionDepthAdjustedPath(
                    adjustedCurve,
                    new LengthMm(0.0005),
                    new LengthMm(0.02),
                    probeCount: 4,
                    "native-axis-ray-bracket-v2")),
            DepthResult = CreateDepthEnvelope(0.15, 0.149, 0.151)
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        WorkingCoordinateSystem coordinateSystem = CreateCoordinateSystem();
        SurfaceAnchor anchor = CreateAnchor(face, new Vec3(0, 0, 0));

        GeometryQueryResult<InsertionDepthAdjustedPath> result = queries.AdjustInsertionDepth(
            [new Vec2(0, 0), new Vec2(2, 0)],
            [face],
            anchor,
            new LengthMm(0.3),
            new LengthMm(0.15),
            coordinateSystem,
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(adjustedCurve, result.Value!.Curve);
        Assert.Equal("native-axis-ray-bracket-v2", result.Value.AlgorithmVersion);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastRequest!.CoordinateSystemId);
        Assert.Equal(new ModelRevision(7), bridge.LastRequest.Revision);
        Assert.Equal(new[] { new Vec2(0, 0), new Vec2(2, 0) }, bridge.LastRequest.SurfacePathWorkingSamples);
        Assert.Equal(new[] { 4 }, bridge.LastRequest.SelectedNativeFaceIndices);
        Assert.Equal(4, bridge.LastRequest.AnchorNativeFaceIndex);
        Assert.Equal(0.3, bridge.LastRequest.WireDiameterMm.Value);
        Assert.Equal(0.15, bridge.LastRequest.RequiredInsertionDepthMm.Value);
        Assert.Equal(GeometryToleranceProfile.Initial.MappingMaximumProbes, bridge.LastRequest.MaximumProbeCount);
        Assert.Equal(1, bridge.DepthCallCount);
        Assert.NotNull(bridge.LastDepthRequest);
        Assert.Equal(adjustedCurve.Points, bridge.LastDepthRequest!.CenterlineSamples);
        Assert.Equal(0, bridge.QueryCallCount);
    }

    /// <summary>보정 성공이어도 전체 깊이 envelope가 목표와 다르면 경로를 반환하지 않습니다.</summary>
    [Fact]
    public void AdjustInsertionDepth_RejectsCenterlineOutsideMeasuredDepthEnvelope()
    {
        FaceRefId face = FaceRefId.New();
        var adjustedCurve = new PolylineCurveDefinition(
        [
            new Vec3(0, 0, -0.3),
            new Vec3(1, 0, -0.3),
            new Vec3(2, 0, -0.3)
        ]);
        var bridge = new NativeDepthAdjustmentFakeBridge
        {
            AdjustmentResult = new OcctCurveDepthAdjustmentQueryResult(
                OcctCurveDepthEnvelopeStatus.Success,
                new InsertionDepthAdjustedPath(
                    adjustedCurve,
                    new LengthMm(0.0005),
                    new LengthMm(0.02),
                    probeCount: 4,
                    "native-axis-ray-bracket-v2")),
            DepthResult = CreateDepthEnvelope(0.15, 0.4, 0.5)
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<InsertionDepthAdjustedPath> result = queries.AdjustInsertionDepth(
            [new Vec2(0, 0), new Vec2(2, 0)],
            [face],
            CreateAnchor(face, new Vec3(0, 0, 0)),
            new LengthMm(0.3),
            new LengthMm(0.15),
            CreateCoordinateSystem(),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue => issue.Code == "DepthAdjustmentEnvelopeMismatch");
        Assert.Equal(1, bridge.DepthCallCount);
    }

    /// <summary>Native root 미확정 결과는 깊이 중심선 성공으로 바꾸지 않습니다.</summary>
    [Fact]
    public void AdjustInsertionDepth_PreservesNativeUnresolvedStatus()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new NativeDepthAdjustmentFakeBridge
        {
            AdjustmentResult = new OcctCurveDepthAdjustmentQueryResult(
                OcctCurveDepthEnvelopeStatus.Unresolved,
                diagnostic: "root bracket did not converge")
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));

        GeometryQueryResult<InsertionDepthAdjustedPath> result = queries.AdjustInsertionDepth(
            [new Vec2(0, 0), new Vec2(2, 0)],
            [face],
            CreateAnchor(face, new Vec3(0, 0, 0)),
            new LengthMm(0.3),
            new LengthMm(0.15),
            CreateCoordinateSystem(),
            GeometryToleranceProfile.Initial);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue => issue.Evidence.Contains("root bracket did not converge"));
    }

    /// <summary>비스듬한 직선 끝에서 작업축 ray와 원형 단면의 교점을 계산합니다.</summary>
    [Fact]
    public void MeasureDepth_IntersectsObliqueWireWithWorkingAxisRay()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = Hit(4, new Vec3(0, 0, 0), new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        SurfaceAnchor anchor = CreateAnchor(face, new Vec3(0, 0, 0));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(Math.Sqrt(3), 0, -0.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            anchor,
            CreateCoordinateSystem(),
            new LengthMm(1),
            CancellationToken.None);

        double expectedOutsideZ = -1.15 + 0.15 / Math.Sqrt(0.75);
        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(expectedOutsideZ, result.Value!.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(-expectedOutsideZ, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>접선 오차 범위에 작업축 평행 방향이 있으면 깊이 측정을 미확정으로 둡니다.</summary>
    [Fact]
    public void MeasureDepth_RejectsUnboundedAxisRayAngularError()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = Hit(4, new Vec3(0, 0, 0), new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -10),
            new Vec3(0.05, 0, -9.001));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue => issue.Code == "DepthAngularToleranceUnbounded");
    }

    /// <summary>원호 끝 접선으로 비스듬한 원형 단면의 축방향 깊이를 계산합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesExactArcEndpointTangent()
    {
        FaceRefId face = FaceRefId.New();
        double startAngle = Math.PI / 4;
        double endAngle = Math.PI / 2;
        var arcFrame = new ProjectionFrame(
            new Vec3(0, 0, -2),
            new Vec3(0, -1, 0),
            new Vec3(1, 0, 0));
        var definition = new ArcCurveDefinition(
            arcFrame,
            new Vec3(0, 0, -2),
            new LengthMm(1),
            startAngle,
            endAngle);
        double diagonal = Math.Sqrt(0.5);
        Vec3 start = new(diagonal, 0, -2 + diagonal);
        Vec3 end = new(0, 0, -1);
        Vec3 surfacePoint = new(start.X, start.Y, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(endAngle - startAngle),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        double expectedOutsideZ = -2 + diagonal + 0.15 / diagonal;
        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(diagonal, result.Value!.WireOutsidePoint.X, precision: 10);
        Assert.Equal(expectedOutsideZ, result.Value.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(-expectedOutsideZ, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>역방향 원호도 진행 방향에 맞춘 시작 끝점 접선을 사용합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesReversedArcEndpointTangent()
    {
        FaceRefId face = FaceRefId.New();
        var arcFrame = new ProjectionFrame(
            new Vec3(0, 0, -2),
            new Vec3(0, -1, 0),
            new Vec3(1, 0, 0));
        var definition = new ArcCurveDefinition(
            arcFrame,
            new Vec3(0, 0, -2),
            new LengthMm(1),
            Math.PI / 4,
            Math.PI / 2,
            CurveDirection.Reversed);
        Vec3 start = new(0, 0, -1);
        Vec3 end = new(Math.Sqrt(0.5), 0, -2 + Math.Sqrt(0.5));
        Vec3 surfacePoint = new(0, 0, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(Math.PI / 4),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(0, result.Value!.WireOutsidePoint.X, precision: 10);
        Assert.Equal(-0.85, result.Value.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(0.85, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>각도가 감소하는 원호도 매개변수 진행 방향의 끝 접선을 사용합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesDecreasingArcParameterTangent()
    {
        FaceRefId face = FaceRefId.New();
        var arcFrame = new ProjectionFrame(
            new Vec3(0, 0, -2),
            new Vec3(0, -1, 0),
            new Vec3(1, 0, 0));
        var definition = new ArcCurveDefinition(
            arcFrame,
            new Vec3(0, 0, -2),
            new LengthMm(1),
            Math.PI / 2,
            Math.PI / 4);
        double diagonal = Math.Sqrt(0.5);
        Vec3 start = new(0, 0, -1);
        Vec3 end = new(diagonal, 0, -2 + diagonal);
        Vec3 surfacePoint = new(0, 0, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(Math.PI / 4),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(-0.85, result.Value!.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(0.85, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>유리 비주기 B-spline의 끝점과 해석 도함수로 깊이를 측정합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesRationalBSplineEndpointDerivative()
    {
        FaceRefId face = FaceRefId.New();
        double diagonal = Math.Sqrt(0.5);
        BSplineCurveDefinition definition = CreateRationalQuarterSpline();
        Vec3 start = new(diagonal, 0, -2 + diagonal);
        Vec3 end = new(0, 0, -1);
        Vec3 surfacePoint = new(start.X, start.Y, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(0.8),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        double expectedOutsideZ = -2 + diagonal + 0.15 / diagonal;
        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(diagonal, result.Value!.WireOutsidePoint.X, precision: 10);
        Assert.Equal(expectedOutsideZ, result.Value.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(-expectedOutsideZ, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>역방향 B-spline 끝에서 왼쪽 도함수를 진행 방향에 맞게 뒤집습니다.</summary>
    [Fact]
    public void MeasureDepth_UsesReversedRationalBSplineEndpointDerivative()
    {
        FaceRefId face = FaceRefId.New();
        BSplineCurveDefinition definition = CreateRationalQuarterSpline(CurveDirection.Reversed);
        Vec3 start = new(0, 0, -1);
        Vec3 end = new(Math.Sqrt(0.5), 0, -2 + Math.Sqrt(0.5));
        Vec3 surfacePoint = new(0, 0, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(0.8),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(-0.85, result.Value!.WireOutsidePoint.Z, precision: 10);
        Assert.Equal(0.85, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>반복 knot의 왼쪽 끝 도함수로 trim 끝점 깊이를 측정합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesLeftDerivativeAtInternalKnotEndpoint()
    {
        FaceRefId face = FaceRefId.New();
        BSplineCurveDefinition definition = CreateKnotJoinedSpline(trimStart: 0, trimEnd: 1);
        Vec3 start = new(0, 0, -2);
        Vec3 end = new(2, 0, -1);
        Vec3 surfacePoint = new(2, 0, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(2.5),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        double expectedDepth = 1 - 0.15 / Math.Sqrt(0.5);
        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(expectedDepth, result.Value!.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>반복 knot의 오른쪽 시작 도함수와 왼쪽 끝 도함수를 구분합니다.</summary>
    [Fact]
    public void MeasureDepth_UsesRightDerivativeAtInternalKnotEndpoint()
    {
        FaceRefId face = FaceRefId.New();
        BSplineCurveDefinition definition = CreateKnotJoinedSpline(trimStart: 1, trimEnd: 2);
        Vec3 start = new(2, 0, -1);
        Vec3 end = new(2, 2, 0);
        Vec3 surfacePoint = new(2, 0, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, surfacePoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(2.5),
            start,
            end,
            [start, end],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, surfacePoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(0.85, result.Value!.MeasuredInsertionDepthMm.Value, precision: 10);
    }

    /// <summary>주기 B-spline 끝점은 지원하지 않는 깊이 곡선으로 명시합니다.</summary>
    [Fact]
    public void MeasureDepth_LeavesPeriodicBSplineUnresolved()
    {
        FaceRefId face = FaceRefId.New();
        var definition = new BSplineCurveDefinition(
            degree: 2,
            poles:
            [
                new Vec3(0, 0, -1),
                new Vec3(1, 0, -1),
                new Vec3(1, 1, -1),
                new Vec3(0, 1, -1)
            ],
            weights: [1, 1, 1, 1],
            knots: [0, 1, 2, 3],
            multiplicities: [1, 1, 2, 1],
            isPeriodic: true,
            trimStart: 0,
            trimEnd: 3);
        Vec3 anchorPoint = new(0, 0, 0);
        var bridge = new FakeBridge { QueryResult = Hit(4, anchorPoint, new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(1),
            new Vec3(0, 0, -1),
            new Vec3(1, 0, -1),
            [new Vec3(0, 0, -1), new Vec3(1, 0, -1)],
            CompleteApproximation());

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, anchorPoint),
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "UnsupportedDepthCurve");
    }

    /// <summary>first-hit이 miss이면 깊이 성공값을 만들지 않고 miss 상태를 전달합니다.</summary>
    [Fact]
    public void MeasureDepth_PreservesSurfaceMissWithoutValue()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Miss) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        SurfaceAnchor anchor = CreateAnchor(face, new Vec3(0, 0, 0));
        CurveGeometrySnapshot curve = CreateLine(new Vec3(0, 0, -1), new Vec3(10, 0, -1));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            anchor,
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Miss, result.Status);
        Assert.False(result.HasValue);
        Assert.Equal(1, bridge.QueryCallCount);
    }

    /// <summary>first-hit 표면이 저장된 앵커 위치와 달라지면 깊이 복원을 미확정 처리합니다.</summary>
    [Fact]
    public void MeasureDepth_RejectsSurfacePointThatNoLongerMatchesAnchor()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = Hit(4, new Vec3(1, 0, 0), new Vec3(0, 0, 1)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        SurfaceAnchor anchor = CreateAnchor(face, new Vec3(0, 0, 0));
        CurveGeometrySnapshot curve = CreateLine(new Vec3(0, 0, -1), new Vec3(10, 0, -1));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            anchor,
            CreateCoordinateSystem(),
            new LengthMm(1));

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "DepthAnchorChanged");
    }

    /// <summary>곡선 표본 전체의 깊이 범위와 선택 Face 전환을 함께 측정합니다.</summary>
    [Fact]
    public void MeasureDepth_WithSelectedFacesMeasuresWholeCurveEnvelope()
    {
        FaceRefId firstFace = FaceRefId.New();
        FaceRefId secondFace = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryHandler = uv =>
            {
                int nativeFace = uv.X <= 0.5 ? 4 : 5;
                Vec3 surfacePoint = new(uv.X, uv.Y, uv.X * 0.1);
                return Hit(nativeFace, surfacePoint, new Vec3(0, 0, 1));
            }
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (firstFace, 4), (secondFace, 5));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(1, 0, -1.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(firstFace, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [firstFace, secondFace]);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsResolved);
        Assert.True(result.Value.IsComplete);
        Assert.True(result.Value.IsCurveEnvelope);
        Assert.Equal(1, result.Value.MinimumMeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(1.1, result.Value.MaximumMeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(1.05, result.Value.MeasuredInsertionDepthMm.Value, precision: 10);
        Assert.Equal(0, result.Value.MinimumDepthWireOutsidePoint.X, precision: 10);
        Assert.Equal(-1, result.Value.MinimumDepthWireOutsidePoint.Z, precision: 10);
        Assert.Equal(1, result.Value.MaximumDepthWireOutsidePoint.X, precision: 10);
        Assert.Equal(-1, result.Value.MaximumDepthWireOutsidePoint.Z, precision: 10);
        Assert.Equal(6, bridge.QueryCallCount);
    }

    /// <summary>큰 유한 깊이의 중간값 계산에서 합산 overflow를 방지합니다.</summary>
    [Fact]
    public void MeasureDepth_WithLargeFiniteDepthsKeepsFiniteEnvelopeMidpoint()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryHandler = uv => Hit(
                4,
                new Vec3(uv.X, uv.Y, 1.3e308 + uv.X * 2e307),
                new Vec3(0, 0, 1))
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, new Vec3(0, 0, 1.3e308)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.True(
            result.HasValue,
            string.Join("; ", result.Issues.Select(issue =>
                $"{issue.Code}: {string.Join(", ", issue.Evidence)}")));
        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(double.IsFinite(result.Value!.MeasuredInsertionDepthMm.Value));
        Assert.True(double.IsFinite(result.Value.ErrorBoundMm.Value));
        Assert.Equal(1.3, result.Value.MinimumMeasuredInsertionDepthMm.Value / 1e308, precision: 12);
        Assert.Equal(1.5, result.Value.MaximumMeasuredInsertionDepthMm.Value / 1e308, precision: 12);
        Assert.Equal(1.4, result.Value.MeasuredInsertionDepthMm.Value / 1e308, precision: 12);
    }

    /// <summary>끝점 사이에 숨은 깊이 극값을 중점 재질의로 찾아냅니다.</summary>
    [Fact]
    public void MeasureDepth_WithSelectedFacesRefinesInteriorDepthExtremum()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryHandler = uv =>
            {
                double normalizedOffset = (uv.X - 0.25) / 0.75;
                double surfaceHeight = 0.4 * (1 - normalizedOffset * normalizedOffset);
                return Hit(4, new Vec3(uv.X, uv.Y, surfaceHeight), new Vec3(0, 0, 1));
            }
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(1, 0, -1.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, new Vec3(0, 0, 0.4 * (1 - 1.0 / 9))),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.MaximumMeasuredInsertionDepthMm.Value > 1.399);
        Assert.Equal(0.25, result.Value.MaximumDepthWireOutsidePoint.X, precision: 3);
        Assert.Equal(0.25, result.Value.MaximumDepthSurfacePoint.X, precision: 3);
        Assert.True(result.Value.ProbeCount > 3);
        Assert.True(bridge.QueryCallCount > 4);
    }

    /// <summary>깊이 극값 정밀화가 probe 한도를 넘으면 부분 envelope를 반환하지 않습니다.</summary>
    [Fact]
    public void MeasureDepth_WithSelectedFacesRejectsRefinementProbeBudgetOverflow()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryHandler = uv =>
            {
                double surfaceHeight = 1.6 * uv.X * (1 - uv.X);
                return Hit(4, new Vec3(uv.X, uv.Y, surfaceHeight), new Vec3(0, 0, 1));
            }
        };
        var tolerances = new GeometryToleranceProfile(
            connectionToleranceMm: 0.01,
            meshDeflectionMm: 0.05,
            meshAngularToleranceRad: 0.1,
            mappingLeafLengthMm: 0.1,
            mappingChordDeviationMm: 0.05,
            mappingMaximumDepth: 18,
            mappingMaximumProbes: 5,
            maximumContinuousSampleSpacingMm: 2);
        var queries = new OcctGeometryQueries(
            bridge,
            CreateCoordinateSystem(),
            new Dictionary<FaceRefId, int> { [face] = 4 },
            tolerances);
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(1, 0, -1.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "DepthEnvelopeRefinementBudgetExceeded");
        Assert.Equal(5, bridge.QueryCallCount);
    }

    /// <summary>깊이 질의가 영역 선택 Face 집합 밖의 first-hit을 만나면 성공값 없이 끝냅니다.</summary>
    [Fact]
    public void MeasureDepth_WithSelectedFacesRejectsFirstHitOutsideRegion()
    {
        FaceRefId selectedFace = FaceRefId.New();
        FaceRefId occludingFace = FaceRefId.New();
        var bridge = new FakeBridge
        {
            QueryHandler = uv => Hit(
                uv.X == 0 ? 4 : 9,
                new Vec3(uv.X, uv.Y, 0),
                new Vec3(0, 0, 1))
        };
        OcctGeometryQueries queries = CreateQueries(bridge, (selectedFace, 4), (occludingFace, 9));
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(1, 0, -1.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(selectedFace, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [selectedFace]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "FirstHitOutsideSelectedFaces");
    }

    /// <summary>깊이 envelope 표본 생성이 질의 한도를 넘으면 부분 결과를 반환하지 않습니다.</summary>
    [Fact]
    public void MeasureDepth_WithSelectedFacesRejectsProbeBudgetOverflow()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = Hit(4, new Vec3(0, 0, 0)) };
        var tolerances = new GeometryToleranceProfile(
            connectionToleranceMm: 0.01,
            meshDeflectionMm: 0.05,
            meshAngularToleranceRad: 0.1,
            mappingLeafLengthMm: 0.1,
            mappingChordDeviationMm: 0.05,
            mappingMaximumDepth: 18,
            mappingMaximumProbes: 3,
            maximumContinuousSampleSpacingMm: 2);
        var queries = new OcctGeometryQueries(
            bridge,
            CreateCoordinateSystem(),
            new Dictionary<FaceRefId, int> { [face] = 4 },
            tolerances);
        CurveGeometrySnapshot curve = CreateLine(
            new Vec3(0, 0, -1.15),
            new Vec3(10, 0, -1.15));

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "DepthEnvelopeBudgetExceeded");
        Assert.Equal(1, bridge.QueryCallCount);
    }

    /// <summary>접선이 불연속인 다중선은 단면 방향을 정할 수 없어 깊이를 확정하지 않습니다.</summary>
    [Fact]
    public void MeasureDepth_WithSelectedFacesRejectsSharpTangentChange()
    {
        FaceRefId face = FaceRefId.New();
        var bridge = new FakeBridge { QueryResult = Hit(4, new Vec3(0, 0, 0)) };
        OcctGeometryQueries queries = CreateQueries(bridge, (face, 4));
        CurveGeometrySnapshot curve = CreatePolyline(
        [
            new Vec3(0, 0, -1.15),
            new Vec3(1, 0, -1.15),
            new Vec3(1, 1, -1.15)
        ]);

        GeometryQueryResult<DepthMeasurement> result = queries.MeasureDepth(
            curve,
            new LengthMm(0.3),
            CreateAnchor(face, new Vec3(0, 0, 0)),
            CreateCoordinateSystem(),
            new LengthMm(1),
            [face]);

        Assert.Equal(GeometryQueryStatus.Unresolved, result.Status);
        Assert.False(result.HasValue);
        Assert.Contains(result.Issues, issue => issue.Code == "DepthTangentDiscontinuity");
        Assert.Equal(1, bridge.QueryCallCount);
    }

    /// <summary>연결 충돌 질의에 경로 순서, 이탈 인덱스, 공차와 bound context를 전달합니다.</summary>
    [Fact]
    public void CheckConnectionPathObstacles_ForwardsExactCurvesAndMapsClearResult()
    {
        var bridge = new NativeConnectionObstacleFakeBridge
        {
            ObstacleResult = new OcctConnectionObstacleQueryResult(OcctConnectionObstacleStatus.Clear)
        };
        OcctGeometryQueries queries = CreateQueries(
            bridge,
            new GeometryToleranceProfile(
                connectionToleranceMm: 0.25,
                meshDeflectionMm: 0.05,
                meshAngularToleranceRad: 0.1,
                mappingLeafLengthMm: 0.1,
                mappingChordDeviationMm: 0.05,
                mappingMaximumDepth: 18,
                mappingMaximumProbes: 100_000,
                maximumContinuousSampleSpacingMm: 2));
        ConnectionPath path = CreateConnectionPath();

        GeometryQueryResult<ConnectionObstacleCheck> result = queries.CheckConnectionPathObstacles(
            path,
            CreateCoordinateSystem());

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.True(result.Value!.IsClear);
        Assert.True(result.Value.MoldExitTransitionFound);
        Assert.Equal(new WorkingCoordinateSystemId(12), bridge.LastObstacleCoordinateSystemId);
        Assert.Equal(new ModelRevision(7), bridge.LastObstacleRevision);
        Assert.Equal(1, bridge.LastTransitionIndex);
        Assert.Equal(0.25, bridge.LastConnectionToleranceMm);
        Assert.Equal(new double[] { 0, 0, 0 }, bridge.LastCurves![0].Start);
        Assert.Equal(new double[] { 1, 0, 0 }, bridge.LastCurves[1].Start);
    }

    /// <summary>Native 충돌 위치와 경로 누적 거리를 공통 결과로 보존합니다.</summary>
    [Fact]
    public void CheckConnectionPathObstacles_MapsFirstObstructionAndDistance()
    {
        var bridge = new NativeConnectionObstacleFakeBridge
        {
            ObstacleResult = new OcctConnectionObstacleQueryResult(
                OcctConnectionObstacleStatus.Obstructed,
                new Vec3(4, 5, 6),
                new LengthMm(27.5))
        };
        OcctGeometryQueries queries = CreateQueries(bridge);

        GeometryQueryResult<ConnectionObstacleCheck> result = queries.CheckConnectionPathObstacles(
            CreateConnectionPath(),
            CreateCoordinateSystem());

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.False(result.Value!.IsClear);
        Assert.Equal(new Vec3(4, 5, 6), result.Value.FirstObstruction);
        Assert.Equal(27.5, result.Value.DistanceToObstruction!.Value.Value);
        Assert.True(result.Value.MoldExitTransitionFound);
    }

    /// <summary>stale·기하 실패·미확정 Native 상태를 성공으로 올리지 않습니다.</summary>
    [Theory]
    [InlineData(OcctConnectionObstacleStatus.StaleContext, GeometryQueryStatus.StaleGeometryContext)]
    [InlineData(OcctConnectionObstacleStatus.GeometryFailure, GeometryQueryStatus.Failure)]
    [InlineData(OcctConnectionObstacleStatus.Unresolved, GeometryQueryStatus.Unresolved)]
    [InlineData(OcctConnectionObstacleStatus.Unknown, GeometryQueryStatus.Unresolved)]
    public void CheckConnectionPathObstacles_PreservesNonSuccessStatus(
        OcctConnectionObstacleStatus nativeStatus,
        GeometryQueryStatus expectedStatus)
    {
        var bridge = new NativeConnectionObstacleFakeBridge
        {
            ObstacleResult = new OcctConnectionObstacleQueryResult(nativeStatus, diagnostic: "native diagnostic")
        };
        OcctGeometryQueries queries = CreateQueries(bridge);

        GeometryQueryResult<ConnectionObstacleCheck> result = queries.CheckConnectionPathObstacles(
            CreateConnectionPath(),
            CreateCoordinateSystem());

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Value);
    }

    private static GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        double wireDiameterMm,
        IReadOnlyCollection<CurveSegmentAdjacency>? localAdjacencies = null)
    {
        OcctGeometryQueries queries = CreateQueries(new FakeBridge());
        return localAdjacencies is null
            ? queries.MeasureClearance(
                curves,
                new LengthMm(wireDiameterMm),
                new LengthMm(0),
                GeometryToleranceProfile.Initial)
            : queries.MeasureClearance(
                curves,
                new LengthMm(wireDiameterMm),
                new LengthMm(0),
                GeometryToleranceProfile.Initial,
                localAdjacencies);
    }

    private static CurveGeometrySnapshot CreateLine(Vec3 start, Vec3 end)
    {
        var definition = new LineCurveDefinition(start, end);
        return new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm((end - start).Length),
            start,
            end,
            [start, end],
            CompleteApproximation());
    }

    private static CurveGeometrySnapshot CreateArc(
        double radiusMm,
        double startAngleRad,
        double endAngleRad,
        CurveDirection direction = CurveDirection.Forward)
    {
        var frame = new ProjectionFrame(
            new Vec3(0, 0, 0),
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0));
        var definition = new ArcCurveDefinition(
            frame,
            new Vec3(0, 0, 0),
            new LengthMm(radiusMm),
            startAngleRad,
            endAngleRad,
            direction);
        double startAngle = direction == CurveDirection.Forward ? startAngleRad : endAngleRad;
        double endAngle = direction == CurveDirection.Forward ? endAngleRad : startAngleRad;
        Vec3 start = frame.Unproject(new Vec2(radiusMm * Math.Cos(startAngle), radiusMm * Math.Sin(startAngle)));
        Vec3 end = frame.Unproject(new Vec2(radiusMm * Math.Cos(endAngle), radiusMm * Math.Sin(endAngle)));

        return new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(radiusMm * Math.Abs(endAngle - startAngle)),
            start,
            end,
            [start, end],
            CompleteApproximation());
    }

    private static BSplineCurveDefinition CreateRationalQuarterSpline(
        CurveDirection direction = CurveDirection.Forward)
    {
        double diagonal = Math.Sqrt(0.5);
        return new BSplineCurveDefinition(
            degree: 2,
            poles:
            [
                new Vec3(1, 0, -2),
                new Vec3(1, 0, -1),
                new Vec3(0, 0, -1)
            ],
            weights: [1, diagonal, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0.5,
            trimEnd: 1,
            direction: direction);
    }

    private static BSplineCurveDefinition CreateKnotJoinedSpline(double trimStart, double trimEnd) =>
        new(
            degree: 2,
            poles:
            [
                new Vec3(0, 0, -2),
                new Vec3(1, 0, -2),
                new Vec3(2, 0, -1),
                new Vec3(2, 1, -1),
                new Vec3(2, 2, 0)
            ],
            weights: [1, 1, 1, 1, 1],
            knots: [0, 1, 2],
            multiplicities: [3, 2, 3],
            isPeriodic: false,
            trimStart,
            trimEnd);

    private static BSplineCurveDefinition CreateMonotoneBezierSpline(
        int degree,
        bool curveInterior = false)
    {
        Vec3[] poles = Enumerable.Range(0, degree + 1)
            .Select(index => new Vec3(
                index / (double)degree,
                curveInterior && index > 0 && index < degree
                    ? 0.5 * Math.Sin(Math.PI * index / degree)
                    : 0,
                0))
            .ToArray();
        double[] weights = Enumerable.Range(0, degree + 1)
            .Select(index => index % 2 == 0 ? 1.0 : 2.0)
            .ToArray();

        return new BSplineCurveDefinition(
            degree,
            poles,
            weights,
            knots: [0, 1],
            multiplicities: [degree + 1, degree + 1],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
    }

    private static CurveGeometrySnapshot CreateBSplineSnapshot(
        BSplineCurveDefinition definition,
        params Vec3[] samples)
    {
        double lengthMm = samples
            .Zip(samples.Skip(1), (start, end) => (end - start).Length)
            .Sum();
        return new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            definition,
            new LengthMm(lengthMm),
            samples[0],
            samples[^1],
            samples,
            CompleteApproximation());
    }

    private static CurveGeometrySnapshot CreatePolyline(IReadOnlyList<Vec3> points)
    {
        double length = points.Zip(points.Skip(1), (start, end) => (end - start).Length).Sum();
        return new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            new PolylineCurveDefinition(points),
            new LengthMm(length),
            points[0],
            points[^1],
            points,
            CompleteApproximation());
    }

    private static SurfaceAnchor CreateAnchor(FaceRefId face, Vec3 point) => new(
        face,
        new Vec2(0, 0),
        new Vec2(0, 0),
        point);

    private static CurveApproximationInfo CompleteApproximation() =>
        new(0.05, 0.1, 0, 1, 2, true, "clearance-test-v1");

    private static OcctGeometryQueries CreateQueries(
        FakeBridge bridge,
        params (FaceRefId FaceReferenceId, int NativeFaceIndex)[] faces) => new(
        bridge,
        CreateCoordinateSystem(),
        faces.ToDictionary(face => face.FaceReferenceId, face => face.NativeFaceIndex));

    private static OcctGeometryQueries CreateQueries(
        FakeBridge bridge,
        GeometryToleranceProfile tolerances,
        params (FaceRefId FaceReferenceId, int NativeFaceIndex)[] faces) => new(
        bridge,
        CreateCoordinateSystem(),
        faces.ToDictionary(face => face.FaceReferenceId, face => face.NativeFaceIndex),
        tolerances);

    private static OcctCurveDepthEnvelopeQueryResult CreateDepthEnvelope(
        double requiredDepthMm,
        double minimumDepthMm,
        double maximumDepthMm) => new(
        OcctCurveDepthEnvelopeStatus.Success,
        new DepthMeasurement(
            new LengthMm(requiredDepthMm),
            new LengthMm((minimumDepthMm + maximumDepthMm) / 2),
            new LengthMm(minimumDepthMm),
            new LengthMm(maximumDepthMm),
            new LengthMm(0.001),
            new Vec3(0, 0, 0),
            new Vec3(0, 0, 0),
            new Vec3(2, 0, 0),
            new Vec3(0, 0, -minimumDepthMm),
            new Vec3(2, 0, -maximumDepthMm),
            probeCount: 4,
            isResolved: true,
            isComplete: true,
            measurementRuleVersion: "test-depth-envelope-v1"));

    private static ConnectionPath CreateConnectionPath() => new(
        ConnectionPathId.New(),
        ConnectionPointId.New(),
        [
            new ConnectionPathSegment(
                ConnectionPathSegmentId.New(),
                0,
                ConductorRole.HeatingWireExtension,
                GeometryDomain.OnMold,
                new LengthMm(0),
                new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0))),
            new ConnectionPathSegment(
                ConnectionPathSegmentId.New(),
                1,
                ConductorRole.SeparateConnectionWire,
                GeometryDomain.External,
                new LengthMm(0),
                new LineCurveDefinition(new Vec3(1, 0, 0), new Vec3(2, 0, 0)),
                isMoldExitTransition: true)
        ]);

    private static WorkingCoordinateSystem CreateCoordinateSystem() => new(
        new WorkingCoordinateSystemId(12),
        new ModelRevision(7),
        new ProjectionFrame(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));

    private static OcctTopologyBoundaryLoop CreateLoop(
        IEnumerable<Vec3> vertices,
        IEnumerable<int> sourceFaceIndices) => new(vertices, sourceFaceIndices, isClosed: true);

    private static HeatingRegion CreateRegion(FaceRefId face)
    {
        BoundaryLoop originalBoundary = CreateBoundaryLoop(
            isHole: false,
            [new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10)]);
        BoundaryLoop effectiveOuter = CreateBoundaryLoop(
            isHole: false,
            [new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10)]);
        BoundaryLoop effectiveHole = CreateBoundaryLoop(
            isHole: true,
            [new Vec2(2, 2), new Vec2(2, 8), new Vec2(8, 8), new Vec2(8, 2)]);

        return new HeatingRegion(
            RegionId.New(),
            [face],
            [originalBoundary],
            [],
            [],
            [effectiveOuter, effectiveHole],
            new ExitSideReference([
                new BoundarySegmentRange(effectiveOuter.Segments[0].Id, 0, 1)
            ]));
    }

    private static BoundaryLoop CreateBoundaryLoop(bool isHole, IReadOnlyList<Vec2> vertices)
    {
        BoundarySegment[] segments = vertices
            .Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(),
                start,
                vertices[(index + 1) % vertices.Count]))
            .ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole);
    }

    private static OcctSurfaceQueryResult Hit(
        int nativeFaceIndex,
        Vec3? point = null,
        Vec3? normal = null,
        Vec2? faceUv = null) => new(
        OcctSurfaceQueryStatus.Hit,
        nativeFaceIndex,
        point ?? new Vec3(0, 0, 0),
        normal ?? new Vec3(0, 0, 1),
        faceUv ?? new Vec2(0, 0));

    private class FakeBridge : IOcctWorkingSurfaceBridge
    {
        public OcctSurfaceQueryResult QueryResult { get; init; } = new(OcctSurfaceQueryStatus.Miss);

        public Func<Vec2, OcctSurfaceQueryResult>? QueryHandler { get; init; }

        public OcctFaceAdjacencyStatus AdjacencyStatus { get; init; } = OcctFaceAdjacencyStatus.NotAdjacent;

        public OcctSelectedTopologyResult TopologyResult { get; init; } =
            new(OcctSelectedTopologyStatus.InvalidInput);

        public OcctSurfaceElementQueryResult SurfaceElementResult { get; init; } =
            new(OcctSurfaceElementQueryStatus.InvalidInput);

        public int QueryCallCount { get; private set; }

        public WorkingCoordinateSystemId? LastCoordinateSystemId { get; private set; }

        public ModelRevision? LastRevision { get; private set; }

        public Vec2? LastWorkingUv { get; private set; }

        public (int First, int Second)? LastAdjacentNativeFaces { get; private set; }

        public IReadOnlyList<int>? LastSelectedNativeFaceIndices { get; private set; }

        public double? LastDeflectionMm { get; private set; }

        public int SurfaceElementQueryCallCount { get; private set; }

        public IReadOnlyList<OcctRegionLoop>? LastEffectiveBoundary { get; private set; }

        public double? LastSurfaceDeflectionMm { get; private set; }

        public double? LastSurfaceAngularToleranceRad { get; private set; }

        public OcctSurfaceQueryResult QueryFirstHit(
            WorkingCoordinateSystemId coordinateSystemId,
            ModelRevision revision,
            Vec2 workingUv)
        {
            QueryCallCount++;
            LastCoordinateSystemId = coordinateSystemId;
            LastRevision = revision;
            LastWorkingUv = workingUv;
            return QueryHandler?.Invoke(workingUv) ?? QueryResult;
        }

        public OcctFaceAdjacencyStatus QueryFaceAdjacency(
            WorkingCoordinateSystemId coordinateSystemId,
            ModelRevision revision,
            int firstNativeFaceIndex,
            int secondNativeFaceIndex)
        {
            LastCoordinateSystemId = coordinateSystemId;
            LastRevision = revision;
            LastAdjacentNativeFaces = (firstNativeFaceIndex, secondNativeFaceIndex);
            return AdjacencyStatus;
        }

        public OcctSelectedTopologyResult QuerySelectedTopology(
            WorkingCoordinateSystemId coordinateSystemId,
            ModelRevision revision,
            IReadOnlyList<int> selectedNativeFaceIndices,
            double deflectionMm)
        {
            LastCoordinateSystemId = coordinateSystemId;
            LastRevision = revision;
            LastSelectedNativeFaceIndices = selectedNativeFaceIndices.ToArray();
            LastDeflectionMm = deflectionMm;
            return TopologyResult;
        }

        public OcctSurfaceElementQueryResult QuerySurfaceElements(
            WorkingCoordinateSystemId coordinateSystemId,
            ModelRevision revision,
            IReadOnlyList<int> selectedNativeFaceIndices,
            IReadOnlyList<OcctRegionLoop> effectiveBoundary,
            double deflectionMm,
            double angularToleranceRad)
        {
            SurfaceElementQueryCallCount++;
            LastCoordinateSystemId = coordinateSystemId;
            LastRevision = revision;
            LastSelectedNativeFaceIndices = selectedNativeFaceIndices.ToArray();
            LastEffectiveBoundary = effectiveBoundary.ToArray();
            LastSurfaceDeflectionMm = deflectionMm;
            LastSurfaceAngularToleranceRad = angularToleranceRad;
            return SurfaceElementResult;
        }
    }

    private sealed class NativeDepthBridge : FakeBridge, IOcctCurveDepthBridge
    {
        public OcctCurveDepthEnvelopeQueryResult DepthResult { get; init; } =
            new(OcctCurveDepthEnvelopeStatus.Unresolved, diagnostic: "테스트 결과가 없습니다.");

        public OcctCurveDepthEnvelopeRequest? LastRequest { get; private set; }

        public OcctCurveDepthEnvelopeQueryResult MeasureDepthEnvelope(OcctCurveDepthEnvelopeRequest request)
        {
            LastRequest = request;
            return DepthResult;
        }
    }

    private sealed class NativeDepthAdjustmentFakeBridge :
        FakeBridge,
        IOcctCurveDepthAdjustmentBridge,
        IOcctCurveDepthBridge
    {
        public OcctCurveDepthAdjustmentQueryResult AdjustmentResult { get; init; } =
            new(OcctCurveDepthEnvelopeStatus.Unresolved, diagnostic: "테스트 보정 결과가 없습니다.");

        public OcctCurveDepthEnvelopeQueryResult DepthResult { get; init; } =
            new(OcctCurveDepthEnvelopeStatus.Unresolved, diagnostic: "테스트 깊이 envelope 결과가 없습니다.");

        public OcctCurveDepthAdjustmentRequest? LastRequest { get; private set; }

        public OcctCurveDepthEnvelopeRequest? LastDepthRequest { get; private set; }

        public int DepthCallCount { get; private set; }

        public OcctCurveDepthAdjustmentQueryResult AdjustInsertionDepth(OcctCurveDepthAdjustmentRequest request)
        {
            LastRequest = request;
            return AdjustmentResult;
        }

        public OcctCurveDepthEnvelopeQueryResult MeasureDepthEnvelope(OcctCurveDepthEnvelopeRequest request)
        {
            LastDepthRequest = request;
            DepthCallCount++;
            return DepthResult;
        }
    }

    private sealed class NativeConnectionObstacleFakeBridge : FakeBridge, IOcctConnectionObstacleBridge
    {
        public OcctConnectionObstacleQueryResult ObstacleResult { get; init; } =
            new(OcctConnectionObstacleStatus.Unresolved, diagnostic: "테스트 결과가 없습니다.");

        public WorkingCoordinateSystemId? LastObstacleCoordinateSystemId { get; private set; }

        public ModelRevision? LastObstacleRevision { get; private set; }

        public IReadOnlyList<CurveDefinitionBridgeDto>? LastCurves { get; private set; }

        public int LastTransitionIndex { get; private set; } = -1;

        public double LastConnectionToleranceMm { get; private set; } = double.NaN;

        public OcctConnectionObstacleQueryResult CheckConnectionPathObstacles(
            WorkingCoordinateSystemId coordinateSystemId,
            ModelRevision revision,
            IReadOnlyList<CurveDefinitionBridgeDto> curves,
            int moldExitTransitionIndex,
            double connectionToleranceMm)
        {
            LastObstacleCoordinateSystemId = coordinateSystemId;
            LastObstacleRevision = revision;
            LastCurves = curves.ToArray();
            LastTransitionIndex = moldExitTransitionIndex;
            LastConnectionToleranceMm = connectionToleranceMm;
            return ObstacleResult;
        }
    }

    private sealed class NativeClearanceFakeBridge : FakeBridge, IOcctCurveClearanceBridge
    {
        public OcctCurveClearanceQueryResult ClearanceResult { get; init; } =
            new(OcctCurveClearanceStatus.Unresolved, diagnostic: "테스트 간격 결과가 없습니다.");

        public int ClearanceCallCount { get; private set; }

        public IReadOnlyList<CurveDefinitionBridgeDto>? LastCurveDefinitions { get; private set; }

        public IReadOnlyList<(int FirstCurveIndex, int SecondCurveIndex)>? LastLocalCurvePairs { get; private set; }

        public WorkingCoordinateSystemId? LastClearanceCoordinateSystemId { get; private set; }

        public ModelRevision? LastClearanceRevision { get; private set; }

        public LengthMm? LastWireDiameterMm { get; private set; }

        public double? LastMaximumErrorMm { get; private set; }

        public OcctCurveClearanceQueryResult MeasureCurveClearance(
            WorkingCoordinateSystemId coordinateSystemId,
            ModelRevision revision,
            IReadOnlyList<CurveDefinitionBridgeDto> curves,
            IReadOnlyList<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
            LengthMm wireDiameterMm,
            double maximumErrorMm,
            int maximumPairChecks)
        {
            ClearanceCallCount++;
            LastClearanceCoordinateSystemId = coordinateSystemId;
            LastClearanceRevision = revision;
            LastCurveDefinitions = curves.ToArray();
            LastLocalCurvePairs = localCurvePairs.ToArray();
            LastWireDiameterMm = wireDiameterMm;
            LastMaximumErrorMm = maximumErrorMm;
            return ClearanceResult;
        }
    }
}
