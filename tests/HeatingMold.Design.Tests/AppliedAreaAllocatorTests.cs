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
using HeatingMold.Design.Area;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>실제 표면 요소를 회로별로 배정하고 모호한 경계 요소를 보존하는지 검사합니다.</summary>
public sealed class AppliedAreaAllocatorTests
{
    /// <summary>요소가 회로 간격의 한쪽에 놓이면 실제 요소 면적을 해당 회로에 한 번만 더합니다.</summary>
    [Fact]
    public void Allocate_AssignsSurfaceElementsToNearestCircuit()
    {
        Fixture fixture = CreateFixture(
            faceId =>
            [
                CreateElement(faceId, 0, 50, 25, 20, 30),
                CreateElement(faceId, 1, 50, 75, 70, 80)
            ],
            circuitCount: 2);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(50, allocation.CircuitAppliedAreas[fixture.Circuits[0].Id].Value);
        Assert.Equal(50, allocation.CircuitAppliedAreas[fixture.Circuits[1].Id].Value);
        Assert.Equal(0, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.Equal(100, allocation.TotalEffectiveSurfaceAreaMm2.Value);
        Assert.Equal(0, allocation.AreaResidualMm2, 8);
        Assert.True(allocation.Quality.IsConverged);
        Assert.Equal("surface-area-v3", allocation.Quality.RuleVersion);
    }

    /// <summary>새 규칙은 원호의 실제 각도 구간을 포함해 삼각 요소 전체의 회로 승자를 확인합니다.</summary>
    [Fact]
    public void Allocate_CertifiesElementAgainstAnExactArcCircuit()
    {
        var plane = new ProjectionFrame(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        Fixture fixture = CreateFixture(
            faceId =>
            [
                CreateElement(faceId, 0, 50, 40, 39, 41),
                CreateElement(faceId, 1, 52, 102, 101, 103)
            ],
            circuitCount: 2,
            circuitCurveDefinitions:
            [
                new LineCurveDefinition(new Vec3(0, 0, 0), new Vec3(100, 0, 0)),
                new ArcCurveDefinition(
                    plane,
                    new Vec3(50, 100, 0),
                    new LengthMm(5),
                    0,
                    Math.PI / 2)
            ]);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(50, allocation.CircuitAppliedAreas[fixture.Circuits[0].Id].Value);
        Assert.Equal(50, allocation.CircuitAppliedAreas[fixture.Circuits[1].Id].Value);
        Assert.Equal(0, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.True(allocation.Quality.IsConverged);
    }

    /// <summary>100×100에서 제외된 20×20을 뺀 유효 실면적 9,600mm²를 두 회로에 보존합니다.</summary>
    [Fact]
    public void Allocate_ConservesEffectiveAreaAcrossTwoCircuits()
    {
        Fixture fixture = CreateFixture(
            faceId =>
            [
                CreateElement(faceId, 0, 50, 25, 24, 26, areaMm2: 4_800),
                CreateElement(faceId, 1, 50, 75, 74, 76, areaMm2: 4_800)
            ],
            circuitCount: 2);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(9_600, allocation.TotalEffectiveSurfaceAreaMm2.Value);
        Assert.Equal(4_800, allocation.CircuitAppliedAreas[fixture.Circuits[0].Id].Value);
        Assert.Equal(4_800, allocation.CircuitAppliedAreas[fixture.Circuits[1].Id].Value);
        Assert.Equal(0, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.Equal(0, allocation.AreaResidualMm2, precision: 8);
        Assert.True(allocation.Quality.IsConverged);
    }

    /// <summary>수치 동률 범위의 모든 표본을 CircuitId 오름차순으로 한 회로에만 배정합니다.</summary>
    [Fact]
    public void Allocate_ResolvesDistanceTiesByCircuitId()
    {
        Fixture fixture = CreateFixture(
            faceId => [CreateElement(faceId, 0, 50, 50, 49, 51, areaMm2: 10)],
            circuitCount: 2);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);
        var settings = new AppliedAreaAllocationSettings(
            0.05, 0.1, 3, 0.1, 100, "area-distance-tie-test-v1");

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            settings);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        CircuitId expectedOwner = fixture.Circuits.OrderBy(circuit => circuit.Id.Value).First().Id;
        CircuitId otherCircuit = fixture.Circuits.Single(circuit => circuit.Id != expectedOwner).Id;
        Assert.Equal(10, allocation.CircuitAppliedAreas[expectedOwner].Value);
        Assert.Equal(0, allocation.CircuitAppliedAreas[otherCircuit].Value);
        Assert.Equal(0, allocation.UnassignedAppliedAreaMm2.Value);
    }

    /// <summary>Native 실면적 오차가 크면 메시를 재요청하고 허용 오차 안에서만 수렴합니다.</summary>
    [Fact]
    public void Allocate_RefinesSurfaceWhenAreaMeasurementErrorIsTooLarge()
    {
        Fixture fixture = CreateFixture(
            faceId => [CreateElement(faceId, 0, 50, 25, 20, 30)],
            circuitCount: 1);
        var coarseSurface = new SurfaceElementSet(fixture.SurfaceElements.Elements, new AreaMm2(1));
        var refinedSurface = new SurfaceElementSet(fixture.SurfaceElements.Elements, new AreaMm2(0.05));
        var queries = new GeometryQueryStub((_, _, _) =>
            GeometryQueryResult<SurfaceElementSet>.Succeeded(refinedSurface));
        queries.Enqueue(coarseSurface);
        var settings = new AppliedAreaAllocationSettings(
            0.05, 0.1, 0.01, 0.1, 100, "area-error-refinement-test-v1", maximumRefinementIterations: 1);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            settings);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(2, allocation.Quality.IterationCount);
        Assert.True(allocation.Quality.IsConverged);
        Assert.Equal(0.05, allocation.Quality.MeasuredAreaErrorBoundMm2, precision: 8);
        Assert.Equal(2, queries.CallCount);
    }

    /// <summary>프로젝트와 다른 설계 지문의 면적 요청은 표면 질의 전에 거부합니다.</summary>
    [Fact]
    public void Allocate_RejectsStaleGeometryBeforeSurfaceQuery()
    {
        Fixture fixture = CreateFixture(
            faceId => [CreateElement(faceId, 0, 50, 25, 20, 30)],
            circuitCount: 1);
        var staleStamp = new DependencyStamp(
            new DesignRevision(0),
            fixture.Project.SourceModel.SourceSha256,
            new string('c', 64),
            new string('d', 64),
            "stale-area-test-v1");
        var staleGeometry = new GeometrySnapshot(
            fixture.Geometry.DocumentId,
            fixture.Geometry.ModelRevision,
            staleStamp,
            fixture.Geometry.Curves);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            staleGeometry,
            queries);

        Assert.False(result.IsSuccess);
        Assert.Equal("StaleAreaGeometry", Assert.Single(result.Issues).Code);
        Assert.Equal(0, queries.CallCount);
    }

    /// <summary>꼭짓점과 중심이 서로 다른 회로를 선택하면 요소 전체를 미배정으로 남깁니다.</summary>
    [Fact]
    public void Allocate_LeavesElementUnassignedWhenItCrossesCircuitBoundary()
    {
        Fixture fixture = CreateFixture(
            faceId => [CreateElement(faceId, 0, 50, 50, 49, 51)],
            circuitCount: 2);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(0, allocation.CircuitAppliedAreas.Values.Sum(area => area.Value));
        Assert.Equal(25, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.False(allocation.Quality.IsConverged);
        Assert.Equal("SurfaceElementCrossesCircuitBoundary", Assert.Single(result.Issues).Code);
    }

    /// <summary>꼭짓점과 중심이 같은 회로를 선택해도 변 중점의 경계 통과를 찾아 미배정합니다.</summary>
    [Fact]
    public void Allocate_LeavesElementUnassignedWhenCircuitBoundaryCrossesAnEdgeBetweenVertices()
    {
        Fixture fixture = CreateEdgeBoundaryFixture();
        var queries = new GeometryQueryStub(fixture.SurfaceElements);
        var settings = new AppliedAreaAllocationSettings(
            0.05,
            0.1,
            0.01,
            0.1,
            100,
            "surface-area-v2",
            maximumRefinementIterations: 0);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            settings);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(0, allocation.CircuitAppliedAreas.Values.Sum(area => area.Value));
        Assert.Equal(25, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.False(allocation.Quality.IsConverged);
        Assert.Equal("SurfaceElementCrossesCircuitBoundary", Assert.Single(result.Issues).Code);
    }

    /// <summary>저장된 v1 면적 규칙은 변 중점 표본 없이 기존 배정 결과를 유지합니다.</summary>
    [Fact]
    public void Allocate_PreservesVertexAndCenterSamplingForLegacyRuleVersion()
    {
        Fixture fixture = CreateEdgeBoundaryFixture();
        var queries = new GeometryQueryStub(fixture.SurfaceElements);
        var settings = new AppliedAreaAllocationSettings(
            0.05,
            0.1,
            0.01,
            0.1,
            100,
            "surface-area-v1",
            maximumRefinementIterations: 0);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            settings);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(25, allocation.CircuitAppliedAreas[fixture.Circuits[0].Id].Value);
        Assert.Equal(0, allocation.CircuitAppliedAreas[fixture.Circuits[1].Id].Value);
        Assert.Equal(0, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.Equal("surface-area-v1", allocation.Quality.RuleVersion);
    }

    /// <summary>새 규칙은 모든 표본을 통과한 작은 내부 회로 영역도 거리 여유로 찾아 미배정합니다.</summary>
    [Fact]
    public void Allocate_LeavesElementUnassignedWhenInteriorCircuitBoundaryMissesAllProbes()
    {
        Fixture fixture = CreateUnprobedBoundaryFixture();
        var queries = new GeometryQueryStub(fixture.SurfaceElements);
        var settings = new AppliedAreaAllocationSettings(
            0.05,
            0.1,
            0.01,
            0.1,
            100,
            "surface-area-v3",
            maximumRefinementIterations: 0);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            settings);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(0, allocation.CircuitAppliedAreas.Values.Sum(area => area.Value));
        Assert.Equal(5_000, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.False(allocation.Quality.IsConverged);
        Assert.Equal("SurfaceElementCrossesCircuitBoundary", Assert.Single(result.Issues).Code);
    }

    /// <summary>저장된 v2 규칙은 내부 경계를 증명하지 않고 기존 표본 승자 배정을 유지합니다.</summary>
    [Fact]
    public void Allocate_PreservesSampledAssignmentForSurfaceAreaV2()
    {
        Fixture fixture = CreateUnprobedBoundaryFixture();
        var queries = new GeometryQueryStub(fixture.SurfaceElements);
        var settings = new AppliedAreaAllocationSettings(
            0.05,
            0.1,
            0.01,
            0.1,
            100,
            "surface-area-v2",
            maximumRefinementIterations: 0);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            settings);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(5_000, allocation.CircuitAppliedAreas[fixture.Circuits[0].Id].Value);
        Assert.Equal(0, allocation.CircuitAppliedAreas[fixture.Circuits[1].Id].Value);
        Assert.Equal(0, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.Equal("surface-area-v2", allocation.Quality.RuleVersion);
    }

    /// <summary>회로가 없을 때도 실제 유효 표면적을 0으로 숨기지 않고 미배정 합계로 기록합니다.</summary>
    [Fact]
    public void Allocate_WithNoCircuitsReturnsEntireSurfaceAsUnassigned()
    {
        Fixture fixture = CreateFixture(
            faceId => [CreateElement(faceId, 0, 50, 50, 40, 60)],
            circuitCount: 0);
        var queries = new GeometryQueryStub(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries);

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Empty(allocation.CircuitAppliedAreas);
        Assert.Equal(25, allocation.UnassignedAppliedAreaMm2.Value);
        Assert.Equal(25, allocation.TotalEffectiveSurfaceAreaMm2.Value);
        Assert.True(allocation.Quality.IsConverged);
    }

    /// <summary>경계가 걸친 요소는 다음 더 촘촘한 Native 질의에서 분리되면 수렴합니다.</summary>
    [Fact]
    public void Allocate_RefinesBoundaryElementsBeforeAssigningArea()
    {
        Fixture fixture = CreateFixture(
            faceId => [CreateElement(faceId, 0, 50, 50, 45, 55)],
            circuitCount: 2);
        var fineSurface = new SurfaceElementSet(
        [
            CreateElement(fixture.FaceId, 0, 50, 25, 20, 30),
            CreateElement(fixture.FaceId, 1, 50, 75, 70, 80)
        ],
        new AreaMm2(0));
        var queries = new GeometryQueryStub((_, _, _) =>
            GeometryQueryResult<SurfaceElementSet>.Succeeded(fineSurface));
        queries.Enqueue(fixture.SurfaceElements);

        OperationResult<AppliedAreaAllocation> result = new AppliedAreaAllocator().Allocate(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            queries,
            new AppliedAreaAllocationSettings(0.05, 0.1, 0.01, 0.1, 100, "area-refinement-test-v1", 2));

        Assert.True(result.IsSuccess);
        AppliedAreaAllocation allocation = result.GetValueOrThrow();
        Assert.Equal(2, allocation.Quality.IterationCount);
        Assert.True(allocation.Quality.IsConverged);
        Assert.Equal(2, queries.CallCount);
        Assert.Equal(0.025, queries.RequestedTolerances[1].MeshDeflectionMm, 10);
        Assert.Equal(50, allocation.CircuitAppliedAreas[fixture.Circuits[0].Id].Value);
        Assert.Equal(50, allocation.CircuitAppliedAreas[fixture.Circuits[1].Id].Value);
    }

    /// <summary>변 중점에서만 두 최근접 회로 영역이 갈리는 삼각 표면 fixture를 만듭니다.</summary>
    private static Fixture CreateEdgeBoundaryFixture() => CreateFixture(
        faceId =>
        [
            new SurfaceElement(
                0,
                faceId,
                new AreaMm2(25),
                new Vec3(0, 2.9 / 3, 0),
                new Vec2(0, 2.9 / 3),
                new Vec2(0, 2.9 / 3),
                [
                    new Vec3(-2, 1.2, 0),
                    new Vec3(2, 1.2, 0),
                    new Vec3(0, 0.5, 0)
                ],
                new CurveApproximationInfo(0.05, 0.1, 0.01, 1, 4, true, "area-edge-probe-test-v1"))
        ],
        circuitCount: 2,
        circuitLines:
        [
            (new Vec3(-10, 0, 0), new Vec3(10, 0, 0)),
            (new Vec3(-0.1, 2, 0), new Vec3(0.1, 2, 0))
        ]);

    private static Fixture CreateUnprobedBoundaryFixture() => CreateFixture(
        faceId =>
        [
            new SurfaceElement(
                0,
                faceId,
                new AreaMm2(5_000),
                new Vec3(100d / 3, 100d / 3, 0),
                new Vec2(100d / 3, 100d / 3),
                new Vec2(100d / 3, 100d / 3),
                [
                    new Vec3(0, 0, 0),
                    new Vec3(100, 0, 0),
                    new Vec3(0, 100, 0)
                ],
                new CurveApproximationInfo(0.05, 0.1, 0, 100, 4, true, "area-hidden-boundary-test-v1"))
        ],
        circuitCount: 2,
        circuitLines:
        [
            (new Vec3(0, 0, 0), new Vec3(100, 0, 0)),
            (new Vec3(19.9, 0.1, 0), new Vec3(20.1, 0.1, 0))
        ]);

    private static Fixture CreateFixture(
        Func<FaceRefId, IReadOnlyList<SurfaceElement>> createElements,
        int circuitCount,
        IReadOnlyList<(Vec3 Start, Vec3 End)>? circuitLines = null,
        IReadOnlyList<CurveDefinition>? circuitCurveDefinitions = null)
    {
        if (circuitLines is not null && circuitLines.Count != circuitCount)
            throw new ArgumentException("회로 선분 fixture 수가 회로 수와 일치해야 합니다.", nameof(circuitLines));
        if (circuitCurveDefinitions is not null && circuitCurveDefinitions.Count != circuitCount)
            throw new ArgumentException("회로 곡선 fixture 수가 회로 수와 일치해야 합니다.", nameof(circuitCurveDefinitions));
        if (circuitLines is not null && circuitCurveDefinitions is not null)
            throw new ArgumentException("회로 fixture에는 선분 또는 곡선 정의 중 하나만 지정합니다.");

        byte[] modelBytes = [1, 3, 3, 7];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant();
        var identity = new ModelIdentity(sourceHash, modelBytes.Length, "AreaFixture", "1", "face-v1");
        FaceRefId faceId = FaceRefId.New();
        IReadOnlyList<SurfaceElement> elements = createElements(faceId);
        var face = new FaceReference(faceId, sourceHash, 0, FaceGeometryKind.Plane, new AreaMm2(10_000), default, "area-face", []);
        BoundaryLoop original = CreateLoop(faceId, 0, 100);
        BoundaryLoop effective = CreateLoop(faceId, 0, 100);
        var exitSide = new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0.1, 0.9)]);
        var region = new HeatingRegion(RegionId.New(), [faceId], [original], [], [], [effective], exitSide);
        SurfaceAnchor startAnchor = CreateAnchor(faceId, new Vec2(20, 0));
        SurfaceAnchor endAnchor = CreateAnchor(faceId, new Vec2(80, 0));
        var placement = new PlacementConstraints(
            new LengthMm(10), new LengthMm(2), new LengthMm(0), PlacementDirection.AlongX,
            startAnchor, endAnchor, exitSide);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(10));

        var paths = new List<CoreHeatingPath>();
        var circuits = new List<Circuit>();
        var curves = new List<CurveGeometrySnapshot>();
        for (int index = 0; index < circuitCount; index++)
        {
            CurveDefinition definition;
            if (circuitCurveDefinitions is not null)
            {
                definition = circuitCurveDefinitions[index];
            }
            else
            {
                double y = index == 0 ? 25 : 75;
                (Vec3 lineStart, Vec3 lineEnd) = circuitLines is null
                    ? (new Vec3(0, y, 0), new Vec3(100, y, 0))
                    : circuitLines[index];
                definition = new LineCurveDefinition(lineStart, lineEnd);
            }

            Vec3[] samples = CreateCurveSamples(definition);
            Vec3 startPosition = samples[0];
            Vec3 endPosition = samples[^1];
            var start = new ControlPoint(ControlPointId.New(), startPosition);
            var end = new ControlPoint(ControlPointId.New(), endPosition);
            var segment = new HeatingPathSegment(
                HeatingPathSegmentId.New(),
                HeatingPathSegmentKind.HeatingRun,
                start.Id,
                end.Id,
                0,
                true,
                definition,
                0);
            paths.Add(new CoreHeatingPath(
                HeatingPathId.New(),
                new HeatingPathGenerationSource(region.Id, placement, wire, "area-test-v1"),
                [start, end],
                [segment],
                [segment]));
            circuits.Add(new Circuit(
                CircuitId.New(),
                index,
                [new CircuitSegmentReference(segment.Id, 0, false)],
                ConnectionPointId.New(),
                ConnectionPointId.New(),
                new Supply(null, null, null)));
            curves.Add(new CurveGeometrySnapshot(
                segment.Id,
                null,
                definition,
                new LengthMm(MeasureCurveLength(definition, samples)),
                start.Position,
                end.Position,
                samples,
                new CurveApproximationInfo(
                    0.05,
                    0.1,
                    0.01,
                    samples.Zip(samples.Skip(1)).Max(pair => (pair.Second - pair.First).Length),
                    samples.Length,
                    true,
                    "area-test-v1")));
        }

        DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(1), "Area fixture", now, now,
            identity,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [face], [region], wire, placement, paths, circuits, [], [], DesignRuleSet.Initial);
        var geometry = new GeometrySnapshot(
            GeometryDocumentId.New(),
            new ModelRevision(3),
            new DependencyStamp(project.Revision, sourceHash, new string('a', 64), new string('b', 64), "area-test-v1"),
            curves);
        return new Fixture(project, region, geometry, new SurfaceElementSet(elements, new AreaMm2(0)), circuits, faceId);
    }

    private static Vec3[] CreateCurveSamples(CurveDefinition definition)
    {
        Vec3[] samples = definition switch
        {
            LineCurveDefinition line => [line.Start, line.End],
            PolylineCurveDefinition polyline => polyline.Points.ToArray(),
            ArcCurveDefinition arc => CreateArcSamples(arc),
            _ => throw new NotSupportedException("이 테스트 fixture는 직선·다중선·원호만 지원합니다.")
        };

        if (definition.Direction == CurveDirection.Reversed)
            Array.Reverse(samples);
        return samples;
    }

    private static Vec3[] CreateArcSamples(ArcCurveDefinition arc)
    {
        int subdivisionCount = Math.Max(2, (int)Math.Ceiling(
            Math.Abs(arc.EndAngleRad - arc.StartAngleRad) / (Math.PI / 16)));
        return Enumerable.Range(0, subdivisionCount + 1)
            .Select(index =>
            {
                double angle = arc.StartAngleRad +
                    (arc.EndAngleRad - arc.StartAngleRad) * index / subdivisionCount;
                return arc.Center +
                    arc.Plane.AxisX * (arc.RadiusMm.Value * Math.Cos(angle)) +
                    arc.Plane.AxisY * (arc.RadiusMm.Value * Math.Sin(angle));
            })
            .ToArray();
    }

    private static double MeasureCurveLength(CurveDefinition definition, IReadOnlyList<Vec3> samples) => definition switch
    {
        LineCurveDefinition line => (line.End - line.Start).Length,
        PolylineCurveDefinition polyline => Enumerable.Range(0, polyline.Points.Count - 1)
            .Sum(index => (polyline.Points[index + 1] - polyline.Points[index]).Length),
        ArcCurveDefinition arc => arc.RadiusMm.Value * Math.Abs(arc.EndAngleRad - arc.StartAngleRad),
        _ => Enumerable.Range(0, samples.Count - 1).Sum(index => (samples[index + 1] - samples[index]).Length)
    };

    private static SurfaceElement CreateElement(
        FaceRefId faceId,
        int index,
        double x,
        double y,
        double lowerY,
        double upperY,
        double? areaMm2 = null)
    {
        Vec3[] vertices =
        [
            new(x - 2, lowerY, 0),
            new(x + 1, upperY, 0),
            new(x + 1, y, 0)
        ];
        return new SurfaceElement(
            index,
            faceId,
            new AreaMm2(areaMm2 ?? (index == 0 && x == 50 && y == 50 ? 25 : 50)),
            new Vec3(x, y, 0),
            new Vec2(x, y),
            new Vec2(x, y),
            vertices,
            new CurveApproximationInfo(0.05, 0.1, 0.01, 1, 5, true, "area-test-v1"));
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId, double minimum, double maximum)
    {
        Vec2[] vertices =
        [
            new(minimum, minimum), new(maximum, minimum),
            new(maximum, maximum), new(minimum, maximum)
        ];
        return new BoundaryLoop(
            BoundaryLoopId.New(),
            vertices.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], faceId)),
            isHole: false);
    }

    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec2 point) => new(
        faceId,
        point,
        point,
        new Vec3(point.X, point.Y, 0));

    private sealed record Fixture(
        DesignProject Project,
        HeatingRegion Region,
        GeometrySnapshot Geometry,
        SurfaceElementSet SurfaceElements,
        IReadOnlyList<Circuit> Circuits,
        FaceRefId FaceId);

    private sealed class GeometryQueryStub : IGeometryQueries
    {
        private readonly Func<HeatingRegion, GeometryToleranceProfile, CancellationToken, GeometryQueryResult<SurfaceElementSet>> _surfaceQuery;
        private readonly Queue<SurfaceElementSet> _queuedSurfaceElements = new();

        public int CallCount { get; private set; }
        public List<GeometryToleranceProfile> RequestedTolerances { get; } = [];

        public GeometryQueryStub(SurfaceElementSet surfaceElements)
            : this((_, _, _) => GeometryQueryResult<SurfaceElementSet>.Succeeded(surfaceElements))
        {
        }

        public GeometryQueryStub(
            Func<HeatingRegion, GeometryToleranceProfile, CancellationToken, GeometryQueryResult<SurfaceElementSet>> surfaceQuery)
        {
            _surfaceQuery = surfaceQuery;
        }

        public void Enqueue(SurfaceElementSet surfaceElements) => _queuedSurfaceElements.Enqueue(surfaceElements);

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(
            HeatingRegion region,
            GeometryToleranceProfile tolerances,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            RequestedTolerances.Add(tolerances);
            if (_queuedSurfaceElements.Count > 0)
                return GeometryQueryResult<SurfaceElementSet>.Succeeded(_queuedSurfaceElements.Dequeue());
            return _surfaceQuery(region, tolerances, cancellationToken);
        }

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

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(HeatingMold.Core.Circuits.ConnectionPath connectionPath, WorkingCoordinateSystem coordinateSystem, CancellationToken cancellationToken = default) =>
            new(GeometryQueryStatus.Failure, null);
    }
}
