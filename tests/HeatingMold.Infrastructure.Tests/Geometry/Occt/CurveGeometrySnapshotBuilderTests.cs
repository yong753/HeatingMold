using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>Native 측정값이 설계 ID와 의존 지문을 가진 snapshot으로 묶이는지 검증합니다.</summary>
public sealed class CurveGeometrySnapshotBuilderTests
{
    private readonly CurveGeometrySnapshotBuilder _builder = new();

    /// <summary>활성 발열 구간과 모든 연결 구간의 최종 측정값을 ID에 맞춰 보관합니다.</summary>
    [Fact]
    public void Build_IncludesActiveHeatingAndAllConnectionMeasurements()
    {
        Fixture fixture = CreateFixture();

        GeometrySnapshot snapshot = _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(12),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements);

        Assert.Equal(new ModelRevision(12), snapshot.ModelRevision);
        Assert.Equal(3, snapshot.Curves.Count);

        CurveGeometrySnapshot heating = Assert.Single(
            snapshot.Curves.Where(curve => curve.HeatingPathSegmentId == fixture.ActiveHeatingSegment.Id));
        Assert.Null(heating.ConnectionPathSegmentId);
        Assert.Equal(fixture.ActiveHeatingSegment.Curve, heating.Definition);
        Assert.Equal(fixture.ActiveMeasurement.LengthMm, heating.LengthMm);
        Assert.Equal(fixture.ActiveMeasurement.Samples, heating.Samples);
        Assert.Equal(fixture.ActiveMeasurement.Approximation, heating.Approximation);

        Assert.Equal(
            fixture.ConnectionSegments.Select(segment => segment.Id.Value).OrderBy(id => id),
            snapshot.Curves.Where(curve => curve.ConnectionPathSegmentId.HasValue)
                .Select(curve => curve.ConnectionPathSegmentId!.Value.Value).OrderBy(id => id));
        Assert.DoesNotContain(snapshot.Curves, curve =>
            curve.HeatingPathSegmentId == fixture.InactiveHeatingSegment.Id);
    }

    /// <summary>측정 ID가 활성 경로 구간 집합과 다르면 snapshot 생성을 중단합니다.</summary>
    [Fact]
    public void Build_RejectsMissingAndInactiveHeatingMeasurements()
    {
        Fixture fixture = CreateFixture();

        Assert.Throws<InvalidOperationException>(() => _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(0),
            new Dictionary<HeatingPathSegmentId, OcctCurveMeasurement>(),
            fixture.ConnectionMeasurements));

        var measurementsWithInactiveSegment = new Dictionary<HeatingPathSegmentId, OcctCurveMeasurement>(
            fixture.HeatingMeasurements)
        {
            [fixture.InactiveHeatingSegment.Id] = CreateMeasurement(
                new Vec3(2, 0, 0), new Vec3(3, 0, 0))
        };

        Assert.Throws<InvalidOperationException>(() => _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(0),
            measurementsWithInactiveSegment,
            fixture.ConnectionMeasurements));
    }

    /// <summary>비활성 구간까지 포함해 설계 전체에서 중복된 안정 ID를 거부합니다.</summary>
    [Fact]
    public void Build_RejectsDuplicateHeatingSegmentIdsAcrossPaths()
    {
        Fixture fixture = CreateFixture();
        DesignProject duplicateSegments = CopyWithHeatingPaths(
            fixture.Project,
            fixture.Project.HeatingPaths.Concat(fixture.Project.HeatingPaths));

        Assert.Throws<InvalidOperationException>(() => _builder.Build(
            duplicateSegments,
            GeometryDocumentId.New(),
            new ModelRevision(0),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements));
    }

    /// <summary>런타임 문서 revision과 무관하게 같은 설계 입력은 같은 지문을 만들고 입력 변경은 새 지문을 만듭니다.</summary>
    [Fact]
    public void Build_StampsDesignInputsButNotRuntimeDocumentIdentity()
    {
        Fixture fixture = CreateFixture();

        GeometrySnapshot first = _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(2),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements);
        GeometrySnapshot sameInputs = _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(8),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements);
        GeometrySnapshot changedInputs = _builder.Build(
            CopyWithName(fixture.Project, "Changed design name"),
            GeometryDocumentId.New(),
            new ModelRevision(2),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements);

        Assert.Equal(first.DependencyStamp, sameInputs.DependencyStamp);
        Assert.NotEqual(first.DependencyStamp.InputFingerprint, changedInputs.DependencyStamp.InputFingerprint);
        Assert.Equal(fixture.Project.SourceModel.SourceSha256, first.DependencyStamp.SourceModelSha256);
    }

    /// <summary>기존 곡선 측정값은 유지하고 다음 설계 revision의 의존 지문을 새로 만듭니다.</summary>
    [Fact]
    public void Rebind_PreservesNativeMeasurementsAndRestampsCandidateRevision()
    {
        Fixture fixture = CreateFixture();
        GeometrySnapshot current = _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(12),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements);
        DesignProject candidate = CopyWithRevision(fixture.Project, fixture.Project.Revision.Next());

        GeometrySnapshot rebound = _builder.Rebind(candidate, current);

        Assert.Equal(current.DocumentId, rebound.DocumentId);
        Assert.Equal(current.ModelRevision, rebound.ModelRevision);
        Assert.Equal(candidate.Revision, rebound.DependencyStamp.DesignRevision);
        Assert.NotEqual(current.DependencyStamp, rebound.DependencyStamp);
        Assert.Equal(current.Curves.Count, rebound.Curves.Count);
        for (int index = 0; index < current.Curves.Count; index++)
        {
            CurveGeometrySnapshot oldCurve = current.Curves[index];
            CurveGeometrySnapshot newCurve = rebound.Curves[index];
            Assert.Equal(oldCurve.HeatingPathSegmentId, newCurve.HeatingPathSegmentId);
            Assert.Equal(oldCurve.ConnectionPathSegmentId, newCurve.ConnectionPathSegmentId);
            Assert.Equal(oldCurve.Definition, newCurve.Definition);
            Assert.Equal(oldCurve.LengthMm, newCurve.LengthMm);
            Assert.Equal(oldCurve.Start, newCurve.Start);
            Assert.Equal(oldCurve.End, newCurve.End);
            Assert.Equal(oldCurve.Samples, newCurve.Samples);
            Assert.Same(oldCurve.Approximation, newCurve.Approximation);
        }
    }

    /// <summary>후보 경로 곡선 정의가 바뀌면 기존 Native 측정값을 재사용하지 않습니다.</summary>
    [Fact]
    public void Rebind_RejectsChangedCurveDefinition()
    {
        Fixture fixture = CreateFixture();
        GeometrySnapshot current = _builder.Build(
            fixture.Project,
            GeometryDocumentId.New(),
            new ModelRevision(12),
            fixture.HeatingMeasurements,
            fixture.ConnectionMeasurements);
        HeatingPath path = fixture.Project.HeatingPaths.Single();
        HeatingPathSegment changedSegment = new(
            fixture.ActiveHeatingSegment.Id,
            fixture.ActiveHeatingSegment.Kind,
            fixture.ActiveHeatingSegment.StartControlPointId,
            fixture.ActiveHeatingSegment.EndControlPointId,
            fixture.ActiveHeatingSegment.Sequence,
            fixture.ActiveHeatingSegment.IsActive,
            new LineCurveDefinition(new Vec3(0, 0, 1), new Vec3(1, 0, 1)));
        HeatingPath changedPath = new(
            path.Id,
            path.GenerationSource,
            path.ControlPoints,
            [changedSegment, fixture.InactiveHeatingSegment],
            [changedSegment, fixture.InactiveHeatingSegment]);
        DesignProject candidate = CopyWithHeatingPaths(
            CopyWithRevision(fixture.Project, fixture.Project.Revision.Next()),
            [changedPath]);

        Assert.Throws<InvalidOperationException>(() => _builder.Rebind(candidate, current));
    }

    /// <summary>표본 끝점이 측정 끝점과 다르면 잘못된 Native 결과를 거부합니다.</summary>
    [Fact]
    public void Measurement_RejectsSamplesThatDoNotMatchMeasuredEndpoints()
    {
        Vec3 start = new(0, 0, 0);
        Vec3 end = new(1, 0, 0);

        Assert.Throws<ArgumentException>(() => new OcctCurveMeasurement(
            new LengthMm(1),
            start,
            end,
            [start, new Vec3(2, 0, 0)],
            CreateApproximation()));
    }

    /// <summary>최소 경로·모델 fixture와 Native 측정값을 구성합니다.</summary>
    private static Fixture CreateFixture()
    {
        byte[] sourceBytes = [0x10, 0x20, 0x30];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes))
            .ToLowerInvariant();
        var model = new ModelIdentity(sourceHash, 1, "FixtureImporter", "1", "face-v1");
        var baseProject = DesignProject.CreateEmpty(
            ProjectId.New(),
            model,
            new WorkingCoordinateSystemDefinition(
                new Vec3(0, 0, 0),
                new Vec3(1, 0, 0),
                new Vec3(0, 1, 0),
                new Vec3(0, 0, 1)),
            name: "Snapshot fixture",
            createdUtc: new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));

        FaceRefId faceId = FaceRefId.New();
        var startAnchor = new SurfaceAnchor(faceId, new Vec2(0, 0), new Vec2(0, 0), new Vec3(0, 0, 0));
        var endAnchor = new SurfaceAnchor(faceId, new Vec2(1, 0), new Vec2(1, 0), new Vec3(1, 0, 0));
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(BoundarySegmentId.New(), 0, 1)]);
        var placement = new PlacementConstraints(
            new LengthMm(1),
            new LengthMm(0),
            new LengthMm(0.5),
            PlacementDirection.AlongX,
            startAnchor,
            endAnchor,
            exitSide);
        var wire = new WireSpecification("Fixture wire", new LengthMm(1), new ResistanceOhmsPerMeter(1));

        var firstPoint = new ControlPoint(ControlPointId.New(), new Vec3(0, 0, 0));
        var secondPoint = new ControlPoint(ControlPointId.New(), new Vec3(1, 0, 0));
        var thirdPoint = new ControlPoint(ControlPointId.New(), new Vec3(2, 0, 0));
        var activeSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            firstPoint.Id,
            secondPoint.Id,
            sequence: 0,
            isActive: true,
            curve: new LineCurveDefinition(firstPoint.Position, secondPoint.Position));
        var inactiveSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            secondPoint.Id,
            thirdPoint.Id,
            sequence: 1,
            isActive: false,
            curve: new LineCurveDefinition(secondPoint.Position, thirdPoint.Position));
        var path = new HeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(RegionId.New(), placement, wire, "fixture-v1"),
            [firstPoint, secondPoint, thirdPoint],
            [activeSegment, inactiveSegment],
            [activeSegment, inactiveSegment]);

        var onMoldSegment = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(),
            sequence: 0,
            conductorRole: ConductorRole.HeatingWireExtension,
            geometryDomain: GeometryDomain.OnMold,
            additionalAllowanceMm: new LengthMm(0),
            curve: new LineCurveDefinition(new Vec3(0, 1, 0), new Vec3(1, 1, 0)));
        var externalSegment = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(),
            sequence: 1,
            conductorRole: ConductorRole.SeparateConnectionWire,
            geometryDomain: GeometryDomain.External,
            additionalAllowanceMm: new LengthMm(0),
            curve: new LineCurveDefinition(new Vec3(1, 1, 0), new Vec3(2, 1, 0)),
            isMoldExitTransition: true);
        ConnectionPointId connectionPointId = ConnectionPointId.New();
        var connectionPath = new ConnectionPath(
            ConnectionPathId.New(),
            connectionPointId,
            [onMoldSegment, externalSegment]);
        var project = new DesignProject(
            baseProject.Id,
            baseProject.SchemaVersion,
            baseProject.Revision,
            baseProject.Name,
            baseProject.CreatedUtc,
            baseProject.ModifiedUtc,
            baseProject.SourceModel,
            baseProject.WorkingCoordinateSystem,
            baseProject.FaceReferences,
            baseProject.HeatingRegions,
            wireSpecification: null,
            placementConstraints: null,
            heatingPaths: [path],
            circuits: [],
            connectionPoints: [],
            connectionPaths: [connectionPath],
            rules: baseProject.Rules);

        OcctCurveMeasurement activeMeasurement = CreateMeasurement(firstPoint.Position, secondPoint.Position);
        var heatingMeasurements = new Dictionary<HeatingPathSegmentId, OcctCurveMeasurement>
        {
            [activeSegment.Id] = activeMeasurement
        };
        var connectionMeasurements = new Dictionary<ConnectionPathSegmentId, OcctCurveMeasurement>
        {
            [onMoldSegment.Id] = CreateMeasurement(new Vec3(0, 1, 0), new Vec3(1, 1, 0)),
            [externalSegment.Id] = CreateMeasurement(new Vec3(1, 1, 0), new Vec3(2, 1, 0))
        };

        return new Fixture(
            project,
            activeSegment,
            inactiveSegment,
            [onMoldSegment, externalSegment],
            activeMeasurement,
            heatingMeasurements,
            connectionMeasurements);
    }

    /// <summary>모델 단위와 project ID를 보존한 채 일반 입력 하나만 수정합니다.</summary>
    private static DesignProject CopyWithName(DesignProject project, string name) => new(
        project.Id,
        project.SchemaVersion,
        project.Revision,
        name,
        project.CreatedUtc,
        project.ModifiedUtc,
        project.SourceModel,
        project.WorkingCoordinateSystem,
        project.FaceReferences,
        project.HeatingRegions,
        project.WireSpecification,
        project.PlacementConstraints,
        project.HeatingPaths,
        project.Circuits,
        project.ConnectionPoints,
        project.ConnectionPaths,
        project.Rules);

    /// <summary>변경하지 않은 설계 입력을 보존하고 revision만 진행합니다.</summary>
    private static DesignProject CopyWithRevision(DesignProject project, DesignRevision revision) => new(
        project.Id,
        project.SchemaVersion,
        revision,
        project.Name,
        project.CreatedUtc,
        project.ModifiedUtc,
        project.SourceModel,
        project.WorkingCoordinateSystem,
        project.FaceReferences,
        project.HeatingRegions,
        project.WireSpecification,
        project.PlacementConstraints,
        project.HeatingPaths,
        project.Circuits,
        project.ConnectionPoints,
        project.ConnectionPaths,
        project.Rules);

    /// <summary>지정한 발열 경로 목록 외의 영속 설계 입력을 보존합니다.</summary>
    private static DesignProject CopyWithHeatingPaths(
        DesignProject project,
        IEnumerable<HeatingPath> heatingPaths) => new(
        project.Id,
        project.SchemaVersion,
        project.Revision,
        project.Name,
        project.CreatedUtc,
        project.ModifiedUtc,
        project.SourceModel,
        project.WorkingCoordinateSystem,
        project.FaceReferences,
        project.HeatingRegions,
        project.WireSpecification,
        project.PlacementConstraints,
        heatingPaths,
        project.Circuits,
        project.ConnectionPoints,
        project.ConnectionPaths,
        project.Rules);

    /// <summary>직선 native 측정값에 쓸 완전한 근사 증거를 만듭니다.</summary>
    private static OcctCurveMeasurement CreateMeasurement(Vec3 start, Vec3 end) => new(
        new LengthMm((end - start).Length),
        start,
        end,
        [start, end],
        CreateApproximation());

    /// <summary>테스트에 필요한 정상 근사 메타데이터를 만듭니다.</summary>
    private static CurveApproximationInfo CreateApproximation() => new(
        requestedDeflectionMm: 0.1,
        requestedAngularToleranceRad: 0.1,
        maximumMeasuredDeviationMm: 0,
        maximumSampleSpacingMm: 1,
        probeCount: 2,
        isComplete: true,
        ruleVersion: "fixture-v1");

    private sealed record Fixture(
        DesignProject Project,
        HeatingPathSegment ActiveHeatingSegment,
        HeatingPathSegment InactiveHeatingSegment,
        IReadOnlyList<ConnectionPathSegment> ConnectionSegments,
        OcctCurveMeasurement ActiveMeasurement,
        IReadOnlyDictionary<HeatingPathSegmentId, OcctCurveMeasurement> HeatingMeasurements,
        IReadOnlyDictionary<ConnectionPathSegmentId, OcctCurveMeasurement> ConnectionMeasurements);
}
