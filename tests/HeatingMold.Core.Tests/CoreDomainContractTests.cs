using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;

namespace HeatingMold.Core.Tests;

/// <summary>영속 경로·영역·회로·기하 결과 계약의 불변식을 검증합니다.</summary>
public sealed class CoreDomainContractTests
{
    /// <summary>유효한 B-spline 정의와 그 배열 방어 복사를 확인합니다.</summary>
    [Fact]
    public void BSplineDefinition_ValidatesAndCopiesControlData()
    {
        Vec3[] poles = [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0)];
        var curve = new BSplineCurveDefinition(
            degree: 2,
            poles,
            weights: [1, 1, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);
        poles[0] = new Vec3(99, 99, 99);

        Assert.Equal(CurveKind.BSpline, curve.Kind);
        Assert.Equal(new Vec3(0, 0, 0), curve.Poles[0]);
        Assert.Throws<ArgumentException>(() => new BSplineCurveDefinition(
            2, [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0)], [1, 1], [0, 1], [3, 3], false, 0, 1));
        Assert.Throws<ArgumentException>(() => new BSplineCurveDefinition(
            2, [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0)], [1, double.NaN, 1], [0, 1], [3, 3], false, 0, 1));
    }

    /// <summary>별도 배열로 복원한 곡선 정의를 값으로 비교하고 방향 차이는 구분합니다.</summary>
    [Fact]
    public void CurveDefinitionComparer_UsesDefinitionValuesInsteadOfArrayIdentity()
    {
        var first = new BSplineCurveDefinition(
            degree: 2,
            poles: [new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 0, 0)],
            weights: [1, 0.75, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0.1,
            trimEnd: 0.9);
        var sameDefinition = new BSplineCurveDefinition(
            degree: 2,
            poles: [new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 0, 0)],
            weights: [1, 0.75, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0.1,
            trimEnd: 0.9);
        var reversedDefinition = new BSplineCurveDefinition(
            degree: 2,
            poles: [new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 0, 0)],
            weights: [1, 0.75, 1],
            knots: [0, 1],
            multiplicities: [3, 3],
            isPeriodic: false,
            trimStart: 0.1,
            trimEnd: 0.9,
            direction: CurveDirection.Reversed);

        Assert.True(CurveDefinitionComparer.AreEquivalent(first, sameDefinition));
        Assert.False(CurveDefinitionComparer.AreEquivalent(first, reversedDefinition));
    }

    /// <summary>직선·다중선·원호도 보관 배열과 좌표계 값을 비교합니다.</summary>
    [Fact]
    public void CurveDefinitionComparer_UsesValuesForLinePolylineAndArc()
    {
        var firstLine = new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(5, 0, 0));
        var sameLine = new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(5, 0, 0));
        var firstPolyline = new PolylineCurveDefinition(
            [new Vec3(0, 0, 0), new Vec3(2, 1, 0), new Vec3(5, 0, 0)]);
        var samePolyline = new PolylineCurveDefinition(
            [new Vec3(0, 0, 0), new Vec3(2, 1, 0), new Vec3(5, 0, 0)]);
        var changedPolyline = new PolylineCurveDefinition(
            [new Vec3(0, 0, 0), new Vec3(2, 2, 0), new Vec3(5, 0, 0)]);
        ProjectionFrame firstFrame = new(new Vec3(1, 2, 3), new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        ProjectionFrame sameFrame = new(new Vec3(1, 2, 3), new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        var firstArc = new ArcCurveDefinition(
            firstFrame, new Vec3(2, 3, 4), new LengthMm(5), 0, Math.PI / 2);
        var sameArc = new ArcCurveDefinition(
            sameFrame, new Vec3(2, 3, 4), new LengthMm(5), 0, Math.PI / 2);
        var changedRadiusArc = new ArcCurveDefinition(
            sameFrame, new Vec3(2, 3, 4), new LengthMm(6), 0, Math.PI / 2);

        Assert.True(CurveDefinitionComparer.AreEquivalent(firstLine, sameLine));
        Assert.True(CurveDefinitionComparer.AreEquivalent(firstPolyline, samePolyline));
        Assert.False(CurveDefinitionComparer.AreEquivalent(firstPolyline, changedPolyline));
        Assert.True(CurveDefinitionComparer.AreEquivalent(firstArc, sameArc));
        Assert.False(CurveDefinitionComparer.AreEquivalent(firstArc, changedRadiusArc));
    }

    /// <summary>OCCT가 만들 수 없는 knot 중복도 합과 주기 경계 구성을 거부합니다.</summary>
    [Fact]
    public void BSplineDefinition_ValidatesKnotMultiplicityStructure()
    {
        Vec3[] poles = [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0)];

        Assert.Throws<ArgumentException>(() => new BSplineCurveDefinition(
            2, poles, [1, 1, 1], [0, 1], [3, 2], false, 0, 1));
        Assert.Throws<ArgumentException>(() => new BSplineCurveDefinition(
            2,
            [new(0, 0, 0), new(1, 0, 0), new(2, 1, 0), new(3, 0, 0)],
            [1, 1, 1, 1], [0, 1, 2], [3, 3, 1], false, 0, 2));
        Assert.Throws<ArgumentException>(() => new BSplineCurveDefinition(
            2, poles, [1, 1, 1], [0, 1, 2], [1, 2, 2], true, 0, 2));

        var periodic = new BSplineCurveDefinition(
            2, poles, [1, 1, 1], [0, 1, 2], [1, 2, 1], true, 0.1, 1.9);
        Assert.True(periodic.IsPeriodic);
    }

    /// <summary>곡선 정의에서 0 길이 직선과 연속 중복점을 거부합니다.</summary>
    [Fact]
    public void CurveDefinitions_RejectDegenerateSegments()
    {
        Assert.Throws<ArgumentException>(() => new LineCurveDefinition(default, default));
        Assert.Throws<ArgumentException>(() => new PolylineCurveDefinition([new(0, 0, 0), new(0, 0, 0)]));
    }

    /// <summary>곡선 측정 스냅샷이 표본 목록과 규칙 근거를 보존합니다.</summary>
    [Fact]
    public void GeometrySnapshot_CopiesSamplesAndCarriesRuleEvidence()
    {
        Vec3[] samples = [new(0, 0, 0), new(10, 0, 0)];
        var curve = new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(), null,
            new LineCurveDefinition(samples[0], samples[1]), new LengthMm(10),
            samples[0], samples[1], samples,
            new CurveApproximationInfo(0.05, 0.1, 0.01, 2, 8, true, "curve-sampling-v1"));
        var snapshot = new GeometrySnapshot(
            GeometryDocumentId.New(), new ModelRevision(3), CreateStamp(), [curve]);
        samples[1] = new Vec3(20, 0, 0);

        Assert.Single(snapshot.Curves);
        Assert.Equal(new Vec3(10, 0, 0), snapshot.Curves[0].Samples[1]);
        Assert.Equal("curve-sampling-v1", snapshot.Curves[0].Approximation.RuleVersion);
        Assert.Throws<ArgumentException>(() => new GeometrySnapshot(
            GeometryDocumentId.New(), new ModelRevision(3), CreateStamp(), [curve, curve]));
    }

    /// <summary>곡선 표본 양 끝점이 별도 Native 측정 끝점과 다르면 스냅샷 생성을 거부합니다.</summary>
    [Fact]
    public void CurveGeometrySnapshot_RequiresSamplesToMatchMeasuredEndpoints()
    {
        Vec3 start = new(0, 0, 0);
        Vec3 end = new(10, 0, 0);
        var definition = new LineCurveDefinition(start, end);
        var approximation = new CurveApproximationInfo(0.05, 0.1, 0.01, 2, 8, true, "curve-sampling-v1");

        Assert.Throws<ArgumentException>(() => new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(), null, definition, new LengthMm(10), start, end,
            [new Vec3(1, 0, 0), end], approximation));
        Assert.Throws<ArgumentException>(() => new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(), null, definition, new LengthMm(10), start, end,
            [start, new Vec3(9, 0, 0)], approximation));
    }

    /// <summary>도체 외곽 간격이 중심선 거리보다 큰 모순된 측정값을 거부합니다.</summary>
    [Fact]
    public void ClearanceMeasurement_RejectsClearGapGreaterThanCenterlineDistance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClearanceMeasurement(
            new LengthMm(2.0),
            new LengthMm(2.01),
            new Vec3(0, 0, 0),
            new Vec3(0, 2, 0),
            new LengthMm(0.001),
            isComplete: true));
    }

    /// <summary>닫힌 경계 면적과 정규화 출구 측면 참조를 확인합니다.</summary>
    [Fact]
    public void BoundaryLoop_RequiresClosedOrderedSegmentsAndReportsArea()
    {
        BoundaryLoop loop = CreateSquareLoop();

        Assert.Equal(100, loop.SignedAreaMm2);
        Assert.Equal(4, loop.Segments.Count);
        Assert.Throws<ArgumentException>(() => new BoundaryLoop(
            BoundaryLoopId.New(),
            [
                new BoundarySegment(BoundarySegmentId.New(), new Vec2(0, 0), new Vec2(10, 0)),
                new BoundarySegment(BoundarySegmentId.New(), new Vec2(10, 1), new Vec2(10, 10)),
                new BoundarySegment(BoundarySegmentId.New(), new Vec2(10, 10), new Vec2(0, 10))
            ],
            isHole: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundarySegmentRange(
            loop.Segments[0].Id, 0.8, 0.2));
    }

    /// <summary>영역의 Face와 경계 컬렉션을 방어 복사하고 잘못된 출구 참조를 거부합니다.</summary>
    [Fact]
    public void HeatingRegion_CopiesBoundaryCollectionsAndValidatesExitSide()
    {
        FaceRefId faceId = FaceRefId.New();
        BoundaryLoop original = CreateSquareLoop();
        BoundaryLoop effective = CreateSquareLoop();
        var exitSide = new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0, 1)]);
        var faces = new List<FaceRefId> { faceId };
        var effectiveLoops = new List<BoundaryLoop> { effective };
        var region = new HeatingRegion(
            RegionId.New(), faces, [original], [], [], effectiveLoops, exitSide);
        faces.Clear();
        effectiveLoops.Clear();

        Assert.Single(region.FaceReferences);
        Assert.Single(region.EffectiveBoundaryLoops);
        Assert.Throws<ArgumentException>(() => new HeatingRegion(
            RegionId.New(), [faceId], [original], [], [], [effective],
            new ExitSideReference([new BoundarySegmentRange(BoundarySegmentId.New(), 0, 1)])));
    }

    /// <summary>배치 조건에서 중심 피치와 도체 반지름 기반 경계 여유를 계산합니다.</summary>
    [Theory]
    [InlineData(0.3, 10.3, 1)]
    [InlineData(0.15, 10.15, 1)]
    public void PlacementConstraints_DerivesPitchWithoutChangingClearGap(double diameterMm, double pitchMm, double marginMm)
    {
        var constraints = CreateConstraints();

        Assert.Equal(10, constraints.MinimumClearGapMm.Value);
        Assert.Equal(pitchMm, constraints.CenterPitch(new LengthMm(diameterMm)).Value, 12);
        Assert.Equal(marginMm, constraints.EffectiveCenterlineBoundaryMargin(new LengthMm(diameterMm)).Value);
        Assert.Equal(
            InsertionDepthReference.MoldSurfaceToWireOutsideAlongWorkingAxis,
            constraints.InsertionDepthReference);
    }

    /// <summary>지원하지 않는 삽입 깊이 기준 enum을 배치 조건에서 거부합니다.</summary>
    [Fact]
    public void PlacementConstraints_RejectsUnknownInsertionDepthReference()
    {
        PlacementConstraints valid = CreateConstraints();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PlacementConstraints(
            valid.MinimumClearGapMm,
            valid.BoundaryMarginMm,
            valid.InsertionDepthMm,
            valid.Direction,
            valid.StartAnchor,
            valid.EndAnchor,
            valid.ExitSide,
            (InsertionDepthReference)int.MaxValue));
    }

    /// <summary>영역 없는 새 프로젝트와 확정 설계의 컬렉션 불변성을 확인합니다.</summary>
    [Fact]
    public void DesignProject_CreateEmptyIsValidAndCopiesInputs()
    {
        DesignProject project = ProjectFixture.Create();

        Assert.Equal(new DesignRevision(0), DesignProject.CreateEmpty(
            ProjectId.New(), CreateModelIdentity(), CreateWorkingCoordinateSystem()).Revision);
        Assert.True(project.ValidateReferences().IsValid);
        Assert.Equal("Fixture", project.Name);
        Assert.Single(project.FaceReferences);
        Assert.Single(project.HeatingPaths);
        Assert.Single(project.Circuits);
    }

    /// <summary>존재하지 않는 Face 참조를 프로젝트 수준 문제로 돌려줍니다.</summary>
    [Fact]
    public void DesignProject_ReportsMissingFaceReference()
    {
        DesignProject project = ProjectFixture.Create(regionFaceReferenceId: FaceRefId.New());

        ValidationReport report = project.ValidateReferences();

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "MissingFaceReference");
    }

    /// <summary>회로·연결 계약이 공유 절점과 각 끝의 안정 ID를 사용합니다.</summary>
    [Fact]
    public void CircuitAndConnectionPaths_KeepStableEndpointAndMaterialRoles()
    {
        DesignProject project = ProjectFixture.Create();
        Circuit circuit = Assert.Single(project.Circuits);
        ConnectionPath path = project.ConnectionPaths[0];

        Assert.Equal(circuit.Id, Assert.Single(project.ConnectionPoints.Where(point => point.Role == ConnectionPointRole.Start)).CircuitId);
        ConnectionPathSegment transition = path.Segments.Single(segment => segment.IsMoldExitTransition);
        Assert.Equal(ConductorRole.SeparateConnectionWire, transition.ConductorRole);
        Assert.Equal(GeometryDomain.External, transition.GeometryDomain);
        Assert.Throws<ArgumentException>(() => new ConnectionPath(
            ConnectionPathId.New(), ConnectionPointId.New(),
            [
                new ConnectionPathSegment(ConnectionPathSegmentId.New(), 0, ConductorRole.HeatingWireExtension,
                    GeometryDomain.External, default, new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(1, 0, 0))),
                new ConnectionPathSegment(ConnectionPathSegmentId.New(), 1, ConductorRole.HeatingWireExtension,
                    GeometryDomain.OnMold, default, new LineCurveDefinition(new Vec3(1, 0, 0), new Vec3(2, 0, 0)))
            ]));
    }

    /// <summary>면적 분할이 회로·미배정 합계와 수렴 근거를 보존합니다.</summary>
    [Fact]
    public void AppliedAreaAllocation_ConservesSurfaceAreaAndCarriesQuality()
    {
        var firstCircuit = CircuitId.New();
        var secondCircuit = CircuitId.New();
        var allocation = new AppliedAreaAllocation(
            CreateStamp(),
            new AreaMm2(100),
            [
                new KeyValuePair<CircuitId, AreaMm2>(firstCircuit, new AreaMm2(60)),
                new KeyValuePair<CircuitId, AreaMm2>(secondCircuit, new AreaMm2(35))
            ],
            new AreaMm2(5),
            CreateAreaQuality(isConverged: true));

        Assert.Equal(100, allocation.TotalAppliedAreaMm2.Value);
        Assert.Equal(100, allocation.TotalEffectiveSurfaceAreaMm2.Value);
        Assert.Equal(0, allocation.AreaResidualMm2);
        Assert.Equal(2, allocation.CircuitAppliedAreas.Count);
        Assert.Throws<ArgumentException>(() => new AppliedAreaAllocation(
            CreateStamp(), new AreaMm2(100),
            [new KeyValuePair<CircuitId, AreaMm2>(firstCircuit, new AreaMm2(60))], new AreaMm2(5),
            CreateAreaQuality(isConverged: true)));
    }

    /// <summary>기하 질의의 Miss는 예외나 가짜 성공값 없이 상태로 전달합니다.</summary>
    [Fact]
    public void GeometryQueryResult_SeparatesMissFromSuccess()
    {
        GeometryQueryResult<SurfaceHit> result = GeometryQueryResult<SurfaceHit>.Miss();

        Assert.Equal(GeometryQueryStatus.Miss, result.Status);
        Assert.Null(result.Value);
        Assert.Throws<ArgumentException>(() => new GeometryQueryResult<SurfaceHit>(
            GeometryQueryStatus.Success, null));
    }

    /// <summary>값 타입의 false 성공 결과와 값 없는 실패 상태를 구분합니다.</summary>
    [Fact]
    public void GeometryQueryResult_BoolFalseValueRemainsDistinctFromFailure()
    {
        GeometryQueryResult<bool> notAdjacent = GeometryQueryResult<bool>.Succeeded(false);
        var issue = new ValidationIssue("QueryUnresolved", ValidationSeverity.Error,
            "Geometry.QueryUnresolved", [], "test-v1");
        GeometryQueryResult<bool> unresolved = GeometryQueryResult<bool>.Unresolved([issue]);

        Assert.True(notAdjacent.HasValue);
        Assert.False(notAdjacent.Value);
        Assert.False(unresolved.HasValue);
        Assert.False(unresolved.Value);
        Assert.Throws<ArgumentException>(() => new GeometryQueryResult<bool>(
            GeometryQueryStatus.Unresolved, true));
    }

    /// <summary>연산 결과는 성공값과 실패 진단을 함께 보관하지 않습니다.</summary>
    [Fact]
    public void OperationResult_SeparatesValueAndFailureIssues()
    {
        DesignProject project = ProjectFixture.Create();
        OperationResult<DesignProject> success = OperationResult<DesignProject>.Success(project);
        var issue = new ValidationIssue("InvalidInput", ValidationSeverity.Error,
            "Validation.InvalidInput", [], "test-v1");
        OperationResult<DesignProject> failure = OperationResult<DesignProject>.Failure([issue]);

        Assert.Same(project, success.GetValueOrThrow());
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Throws<InvalidOperationException>(() => failure.GetValueOrThrow());
    }

    /// <summary>값 타입 연산의 false 성공값과 실패 상태를 구분합니다.</summary>
    [Fact]
    public void OperationResult_BoolFalseValueRemainsDistinctFromFailure()
    {
        OperationResult<bool> success = OperationResult<bool>.Success(false);
        var issue = new ValidationIssue("InvalidInput", ValidationSeverity.Error,
            "Validation.InvalidInput", [], "test-v1");
        OperationResult<bool> failure = OperationResult<bool>.Failure([issue]);

        Assert.True(success.HasValue);
        Assert.False(success.Value);
        Assert.False(success.GetValueOrThrow());
        Assert.False(failure.HasValue);
        Assert.False(failure.Value);
        Assert.Throws<InvalidOperationException>(() => failure.GetValueOrThrow());
    }

    private static BoundaryLoop CreateSquareLoop(FaceRefId? faceId = null)
    {
        Vec2[] points = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        var segments = Enumerable.Range(0, points.Length)
            .Select(index => new BoundarySegment(
                BoundarySegmentId.New(), points[index], points[(index + 1) % points.Length], faceId))
            .ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }

    private static PlacementConstraints CreateConstraints()
    {
        var faceId = FaceRefId.New();
        var anchor = new SurfaceAnchor(faceId, default, default, default);
        var exitSide = new ExitSideReference([
            new BoundarySegmentRange(BoundarySegmentId.New(), 0, 1)]);
        return new PlacementConstraints(
            new LengthMm(10), new LengthMm(1), default, PlacementDirection.AlongX,
            anchor, anchor, exitSide);
    }

    private static ModelIdentity CreateModelIdentity() => new(
        new string('a', 64), 1, "FixtureImporter", "1.0", "face-v1");

    private static WorkingCoordinateSystemDefinition CreateWorkingCoordinateSystem() => new(
        default, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

    private static DependencyStamp CreateStamp() => new(
        new DesignRevision(1), new string('a', 64), new string('b', 64), new string('c', 64), "test-v1");

    private static AreaAllocationQuality CreateAreaQuality(bool isConverged) => new(
        0.05, 0.1, 1, 0.02, 0.01, 4, isConverged, "area-v1");

    private sealed class ProjectFixture
    {
        public static DesignProject Create(FaceRefId? regionFaceReferenceId = null)
        {
            ModelIdentity model = CreateModelIdentity();
            FaceRefId faceId = FaceRefId.New();
            var face = new FaceReference(faceId, model.SourceSha256, 0, FaceGeometryKind.Plane,
                new AreaMm2(100), default, "boundary-a", Array.Empty<string>());
            BoundaryLoop original = CreateSquareLoop(faceId);
            BoundaryLoop effective = CreateSquareLoop(faceId);
            var exitSide = new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0, 1)]);
            var region = new HeatingRegion(
                RegionId.New(), [regionFaceReferenceId ?? faceId], [original], [], [], [effective], exitSide);
            SurfaceAnchor startAnchor = new(faceId, default, new Vec2(-1, 0), new Vec3(-1, 0, 0));
            SurfaceAnchor endAnchor = new(faceId, default, new Vec2(11, 0), new Vec3(11, 0, 0));
            var constraints = new PlacementConstraints(
                new LengthMm(10), new LengthMm(1), default, PlacementDirection.AlongX,
                startAnchor, endAnchor, exitSide);
            var wire = new WireSpecification("SUS 316L", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
            var generation = new HeatingPathGenerationSource(region.Id, constraints, wire, "path-v1");

            ControlPoint start = new(ControlPointId.New(), new Vec3(0, 0, 0));
            ControlPoint end = new(ControlPointId.New(), new Vec3(10, 0, 0));
            HeatingPathSegment segment = new(
                HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                start.Id, end.Id, 0, true, new LineCurveDefinition(start.Position, end.Position), laneIndex: 0);
            var heatingPath = new HeatingPath(
                HeatingPathId.New(), generation, [start, end], [segment], [segment]);
            CircuitId circuitId = CircuitId.New();
            ConnectionPointId startConnectionId = ConnectionPointId.New();
            ConnectionPointId endConnectionId = ConnectionPointId.New();
            var circuit = new Circuit(
                circuitId, 0, [new CircuitSegmentReference(segment.Id, 0, isReversed: false)],
                startConnectionId, endConnectionId,
                new Supply(new VoltageVrms(220), new VoltageVrms(220), new CurrentAmps(16)));
            var startConnection = new ConnectionPoint(
                startConnectionId, circuitId, ConnectionPointRole.Start, segment.Id, start.Id,
                startAnchor, new Vec3(-5, 0, 0));
            var endConnection = new ConnectionPoint(
                endConnectionId, circuitId, ConnectionPointRole.End, segment.Id, end.Id,
                endAnchor, new Vec3(15, 0, 0));
            var startConnectionPath = new ConnectionPath(
                ConnectionPathId.New(), startConnectionId,
                [
                    new ConnectionPathSegment(ConnectionPathSegmentId.New(), 0,
                        ConductorRole.HeatingWireExtension, GeometryDomain.OnMold, default,
                        new LineCurveDefinition(start.Position, startAnchor.ModelPoint)),
                    new ConnectionPathSegment(ConnectionPathSegmentId.New(), 1,
                        ConductorRole.SeparateConnectionWire, GeometryDomain.External, default,
                        new LineCurveDefinition(startAnchor.ModelPoint, new Vec3(-5, 0, 0)), isMoldExitTransition: true)
                ]);
            var endConnectionPath = new ConnectionPath(
                ConnectionPathId.New(), endConnectionId,
                [
                    new ConnectionPathSegment(ConnectionPathSegmentId.New(), 0,
                        ConductorRole.HeatingWireExtension, GeometryDomain.OnMold, default,
                        new LineCurveDefinition(end.Position, endAnchor.ModelPoint)),
                    new ConnectionPathSegment(ConnectionPathSegmentId.New(), 1,
                        ConductorRole.SeparateConnectionWire, GeometryDomain.External, default,
                        new LineCurveDefinition(endAnchor.ModelPoint, new Vec3(15, 0, 0)), isMoldExitTransition: true)
                ]);
            DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

            return new DesignProject(
                ProjectId.New(), 1, new DesignRevision(1), "Fixture", now, now,
                model, CreateWorkingCoordinateSystem(), [face], [region], wire, constraints,
                [heatingPath], [circuit], [startConnection, endConnection],
                [startConnectionPath, endConnectionPath]);
        }
    }
}
