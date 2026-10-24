using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Quality;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>인접 레인 간격과 표면 경로 거리를 관측값으로 보고하는지 검사합니다.</summary>
public sealed class CenterPitchQualityAnalyzerTests
{
    /// <summary>두 직선 레인과 표면 표본에서 chord 간격과 가까운 중심선 거리를 반환합니다.</summary>
    [Fact]
    public void Analyze_ReportsMeasuredSpacingWithoutThermalPassJudgment()
    {
        Fixture fixture = CreateFixture();

        var result = new CenterPitchQualityAnalyzer().Analyze(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            fixture.Surface);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CenterPitchQualityReport report = result.GetValueOrThrow();
        LanePitchMeasurement pitch = Assert.Single(report.LanePitchMeasurements);
        Assert.Equal(50, pitch.MinimumChordDistanceMm, 8);
        Assert.Equal(50, pitch.AverageChordDistanceMm, 8);
        Assert.Equal(50, pitch.MaximumChordDistanceMm, 8);
        Assert.Equal(10.3, pitch.TargetCenterPitchMm!.Value.Value, 8);
        Assert.Equal(39.7, pitch.AverageDeviationFromTargetMm!.Value, 8);
        Assert.Equal(10.3, report.TargetCenterPitchMm!.Value.Value, 8);
        Assert.Equal(25, report.MaximumSampledCenterlineDistanceMm, 8);
        Assert.Equal(25.152, report.MaximumSampledCenterlineDistanceUpperBoundMm, 8);
        Assert.Equal(0.152, report.SurfaceSamples[0].ApproximationBoundMm, 8);
        Assert.Equal(new LengthMm(0.3), report.WireDiameterMm);
        Assert.Equal(4, report.SurfaceSamples.Count);
    }

    /// <summary>표본 예산을 넘으면 일부 측정만 담은 성공 보고서를 만들지 않습니다.</summary>
    [Fact]
    public void Analyze_RejectsProbeBudgetInsteadOfReturningPartialMetrics()
    {
        Fixture fixture = CreateFixture();
        var settings = new CenterPitchQualitySettings(2, 8, "quality-budget-test-v1");

        var result = new CenterPitchQualityAnalyzer().Analyze(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            fixture.Surface,
            settings);

        Assert.False(result.IsSuccess);
        Assert.Equal("QualityProbeBudgetExceeded", Assert.Single(result.Issues).Code);
    }

    /// <summary>전체 표본 수와 최댓값은 유지하고 보고서의 위치 목록은 설정 한도에 맞춥니다.</summary>
    [Fact]
    public void Analyze_RetainsOnlyConfiguredNumberOfMostDistantSurfaceSamples()
    {
        Fixture fixture = CreateFixture();
        var settings = new CenterPitchQualitySettings(
            maximumSurfaceProbeCount: 20,
            samplesPerLanePair: 8,
            ruleVersion: "bounded-quality-report-v1",
            maximumReportedSurfaceSamples: 2);

        OperationResult<CenterPitchQualityReport> result = new CenterPitchQualityAnalyzer().Analyze(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            fixture.Surface,
            settings);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        CenterPitchQualityReport report = result.GetValueOrThrow();
        Assert.Equal(4, report.SurfaceProbeCount);
        Assert.Equal(2, report.SurfaceSamples.Count);
        Assert.Equal(25, report.MaximumSampledCenterlineDistanceMm, 8);
        Assert.Equal(25, report.SurfaceSamples[0].CenterlineDistanceMm, 8);
    }

    /// <summary>표면 probe가 없으면 0 mm 거리로 오해될 성공 보고서를 만들지 않습니다.</summary>
    [Fact]
    public void Analyze_RejectsSurfaceWithoutAnyProbeLocations()
    {
        Fixture fixture = CreateFixture();
        var emptySurface = new SurfaceElementSet([], new AreaMm2(0));

        OperationResult<CenterPitchQualityReport> result = new CenterPitchQualityAnalyzer().Analyze(
            fixture.Project,
            fixture.Region,
            fixture.Geometry,
            emptySurface);

        Assert.False(result.IsSuccess);
        Assert.Equal("NoQualitySurfaceSamples", Assert.Single(result.Issues).Code);
    }

    private static Fixture CreateFixture()
    {
        byte[] sourceBytes = [4, 8, 15, 16, 23, 42];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var identity = new ModelIdentity(sourceHash, sourceBytes.Length, "QualityFixture", "1", "face-v1");
        FaceRefId faceId = FaceRefId.New();
        BoundaryLoop original = CreateLoop(faceId);
        BoundaryLoop effective = CreateLoop(faceId);
        var exitSide = new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0.1, 0.9)]);
        var region = new HeatingRegion(RegionId.New(), [faceId], [original], [], [], [effective], exitSide);
        var wire = new WireSpecification("NiCr", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var placement = new PlacementConstraints(
            new LengthMm(10), new LengthMm(2), new LengthMm(0), PlacementDirection.AlongX,
            new SurfaceAnchor(faceId, default, default, new Vec3(0, 0, 0)),
            new SurfaceAnchor(faceId, new Vec2(100, 0), new Vec2(100, 0), new Vec3(100, 0, 0)),
            exitSide);

        var paths = new List<CoreHeatingPath>();
        var geometryCurves = new List<CurveGeometrySnapshot>();
        for (int laneIndex = 0; laneIndex < 2; laneIndex++)
        {
            double y = laneIndex == 0 ? 25 : 75;
            var start = new ControlPoint(ControlPointId.New(), new Vec3(0, y, 0));
            var end = new ControlPoint(ControlPointId.New(), new Vec3(100, y, 0));
            var curve = new LineCurveDefinition(start.Position, end.Position);
            var segment = new HeatingPathSegment(
                HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                start.Id, end.Id, 0, true, curve, laneIndex);
            paths.Add(new CoreHeatingPath(
                HeatingPathId.New(),
                new HeatingPathGenerationSource(region.Id, placement, wire, "quality-fixture-v1"),
                [start, end], [segment], [segment]));
            geometryCurves.Add(new CurveGeometrySnapshot(
                segment.Id,
                null,
                curve,
                new LengthMm(100),
                start.Position,
                end.Position,
                [start.Position, end.Position],
                new CurveApproximationInfo(0.01, 0.01, 0.001, 0.1, 2, true, "quality-fixture-v1")));
        }

        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(7), "Quality fixture",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            identity,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [new FaceReference(faceId, sourceHash, 0, FaceGeometryKind.Plane, new AreaMm2(10_000), default, "quality-face", [])],
            [region], wire, placement, paths, [], [], []);
        var stamp = new DependencyStamp(
            project.Revision, sourceHash, new string('c', 64), new string('d', 64), "quality-fixture-v1");
        var geometry = new GeometrySnapshot(GeometryDocumentId.New(), new ModelRevision(2), stamp, geometryCurves);
        var element = new SurfaceElement(
            0,
            faceId,
            new AreaMm2(20),
            new Vec3(50, 50, 0),
            new Vec2(50, 50),
            new Vec2(50, 50),
            [new Vec3(48, 48, 0), new Vec3(52, 48, 0), new Vec3(50, 52, 0)],
            new CurveApproximationInfo(0.01, 0.01, 0.001, 0.1, 4, true, "quality-fixture-v1"));
        var surface = new SurfaceElementSet([element], new AreaMm2(0));
        return new Fixture(project, region, geometry, surface);
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId)
    {
        Vec2[] points = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        return new BoundaryLoop(
            BoundaryLoopId.New(),
            points.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(), start, points[(index + 1) % points.Length], faceId)),
            isHole: false);
    }

    private sealed record Fixture(
        DesignProject Project,
        HeatingRegion Region,
        GeometrySnapshot Geometry,
        SurfaceElementSet Surface);
}
