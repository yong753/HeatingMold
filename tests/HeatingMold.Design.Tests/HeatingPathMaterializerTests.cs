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
using HeatingMold.Design.GeometryMapping;
using HeatingMold.Design.HeatingPath;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Tests;

/// <summary>2차원 초안이 전체 모델 표면 조회를 거쳐 안정적인 3차원 경로가 되는지 검증합니다.</summary>
public sealed class HeatingPathMaterializerTests
{
    /// <summary>평면 경로를 삽입 깊이가 반영된 같은 ID의 3차원 다중선으로 변환합니다.</summary>
    [Fact]
    public void Materialize_PreservesDraftIdsAndBuildsAdaptivePolyline()
    {
        Fixture fixture = Fixture.Create(faceCount: 1);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            fixture.Draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            fixture.CoordinateSystem.Revision,
            new PlanarGeometryQueries());

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        CoreHeatingPath path = result.GetValueOrThrow();
        Assert.Equal(fixture.Draft.Id, path.Id);
        Assert.Equal(
            $"{fixture.Project.Rules.GenerationRuleVersion}+native-axis-ray-bracket-v2",
            path.GenerationSource.RuleVersion);
        Assert.Equal(fixture.Draft.ControlPoints.Select(point => point.Id), path.ControlPoints.Select(point => point.Id));
        HeatingPathSegment segment = Assert.Single(path.Segments);
        Assert.Equal(fixture.Draft.Segments[0].Id, segment.Id);
        PolylineCurveDefinition curve = Assert.IsType<PolylineCurveDefinition>(segment.Curve);
        Assert.True(curve.Points.Count > 2);
        Assert.Equal(new Vec3(2, 10, -0.15), curve.Points[0]);
        Assert.Equal(new Vec3(18, 10, -0.15), curve.Points[^1]);
        Assert.Equal(curve.Points[0], path.ControlPoints[0].Position);
        Assert.Equal(curve.Points[^1], path.ControlPoints[^1].Position);
    }

    /// <summary>경사진 면에서 작업축 광선과 원형 단면의 교점에 맞춰 중심선을 보정합니다.</summary>
    [Fact]
    public void Materialize_AdjustsSlopedSurfaceForWorkingAxisRayIntersection()
    {
        Fixture fixture = Fixture.Create(faceCount: 1, insertionDepthMm: 1, wireDiameterMm: 0.4, surfaceSlopeX: 1);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            fixture.Draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            fixture.CoordinateSystem.Revision,
            new PlanarGeometryQueries(surfaceSlopeX: 1));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        CoreHeatingPath path = result.GetValueOrThrow();
        PolylineCurveDefinition curve = Assert.IsType<PolylineCurveDefinition>(Assert.Single(path.Segments).Curve);
        double expectedOffset = 1 + 0.2 / Math.Sqrt(0.5);
        Assert.InRange(curve.Points[0].Z, 2 - expectedOffset - 1e-9, 2 - expectedOffset + 1e-9);
        Assert.InRange(curve.Points[^1].Z, 18 - expectedOffset - 1e-9, 18 - expectedOffset + 1e-9);
        Assert.Equal(curve.Points[0], path.ControlPoints[0].Position);
        Assert.Equal(curve.Points[^1], path.ControlPoints[^1].Position);
    }

    /// <summary>곡면 표본에서 보정된 접선 수직 단면의 깊이가 경로 전체에서 목표값에 수렴합니다.</summary>
    [Fact]
    public void Materialize_RefinesCurvedSurfaceOffsetsAcrossThePath()
    {
        const double curvature = 0.01;
        const double requiredDepthMm = 1;
        const double wireRadiusMm = 0.2;
        Fixture fixture = Fixture.Create(
            faceCount: 1,
            insertionDepthMm: requiredDepthMm,
            wireDiameterMm: wireRadiusMm * 2,
            surfaceCurvatureX2: curvature);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            fixture.Draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            fixture.CoordinateSystem.Revision,
            new PlanarGeometryQueries(surfaceCurvatureX2: curvature));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        PolylineCurveDefinition curve = Assert.IsType<PolylineCurveDefinition>(Assert.Single(result.GetValueOrThrow().Segments).Curve);
        for (int index = 0; index < curve.Points.Count; index++)
        {
            Vec3 tangentVector = index switch
            {
                0 => curve.Points[1] - curve.Points[0],
                _ when index == curve.Points.Count - 1 => curve.Points[^1] - curve.Points[^2],
                _ => curve.Points[index + 1] - curve.Points[index - 1]
            };
            Vec3 tangent = tangentVector.Normalized();
            Vec3 axisSupport = new Vec3(0, 0, 1) - tangent * Vec3.Dot(tangent, new Vec3(0, 0, 1));
            Vec3 wireOutsidePoint = curve.Points[index] + new Vec3(0, 0, 1) * (wireRadiusMm / axisSupport.Length);
            double surfaceHeight = curvature * curve.Points[index].X * curve.Points[index].X;
            double measuredDepth = Vec3.Dot(new Vec3(0, 0, surfaceHeight) - wireOutsidePoint, new Vec3(0, 0, 1));

            Assert.InRange(measuredDepth, requiredDepthMm - 0.001, requiredDepthMm + 0.001);
        }
    }

    /// <summary>깊이 보정 뒤에도 이웃 구간이 하나의 모델 좌표 절점을 공유합니다.</summary>
    [Fact]
    public void Materialize_PreservesSharedEndpointAfterDepthAdjustment()
    {
        Fixture fixture = Fixture.Create(faceCount: 1);
        HeatingPathDraftControlPoint start = fixture.Draft.ControlPoints[0];
        HeatingPathDraftControlPoint end = fixture.Draft.ControlPoints[^1];
        var joint = new HeatingPathDraftControlPoint(ControlPointId.New(), new Vec2(10, 10));
        var draft = new HeatingPathDraft(
            fixture.Draft.ProjectId,
            fixture.Draft.BaseRevision,
            HeatingPathId.New(),
            fixture.Draft.RegionId,
            laneCount: 1,
            fixture.Draft.Score,
            [start, joint, end],
            [
                new HeatingPathDraftSegment(
                    HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                    start.Id, joint.Id, 0, [start.WorkingUv, joint.WorkingUv], laneIndex: 0),
                new HeatingPathDraftSegment(
                    HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
                    joint.Id, end.Id, 1, [joint.WorkingUv, end.WorkingUv])
            ]);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            fixture.CoordinateSystem.Revision,
            new PlanarGeometryQueries());

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.SelectMany(issue => issue.Evidence)));
        CoreHeatingPath path = result.GetValueOrThrow();
        HeatingPathSegment[] segments = path.Segments.OrderBy(segment => segment.Sequence).ToArray();
        Vec3 previousEnd = Assert.IsType<PolylineCurveDefinition>(segments[0].Curve).Points[^1];
        Vec3 nextStart = Assert.IsType<PolylineCurveDefinition>(segments[1].Curve).Points[0];
        ControlPoint sharedPoint = Assert.Single(path.ControlPoints.Where(point => point.Id == joint.Id));

        Assert.Equal(previousEnd, nextStart);
        Assert.Equal(sharedPoint.Position, previousEnd);
        Assert.Equal(-0.15, previousEnd.Z, precision: 10);
    }

    /// <summary>모서리 양쪽의 요구 깊이가 연결 공차 밖으로 다르면 경로 후보를 거부합니다.</summary>
    [Fact]
    public void Materialize_RejectsSharedEndpointWithIncompatibleDepthOffsets()
    {
        Fixture fixture = Fixture.Create(
            faceCount: 1,
            insertionDepthMm: 1,
            wireDiameterMm: 0.4,
            surfaceSlopeX: 1);
        HeatingPathDraftControlPoint start = fixture.Draft.ControlPoints[0];
        HeatingPathDraftControlPoint end = fixture.Draft.ControlPoints[^1];
        var joint = new HeatingPathDraftControlPoint(ControlPointId.New(), new Vec2(4, 20));
        var draft = new HeatingPathDraft(
            fixture.Draft.ProjectId,
            fixture.Draft.BaseRevision,
            HeatingPathId.New(),
            fixture.Draft.RegionId,
            laneCount: 1,
            fixture.Draft.Score,
            [start, joint, end],
            [
                new HeatingPathDraftSegment(
                    HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                    start.Id, joint.Id, 0, [start.WorkingUv, joint.WorkingUv], laneIndex: 0),
                new HeatingPathDraftSegment(
                    HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
                    joint.Id, end.Id, 1, [joint.WorkingUv, end.WorkingUv])
            ]);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            fixture.CoordinateSystem.Revision,
            new PlanarGeometryQueries(surfaceSlopeX: 1));

        Assert.False(result.IsSuccess);
        Assert.Equal("InsertionDepthSharedEndpointMismatch", Assert.Single(result.Issues).Code);
    }

    /// <summary>여러 Face를 함께 대상으로 하는 first-hit 조회를 지원하지 않으면 부분 경로를 만들지 않습니다.</summary>
    [Fact]
    public void Materialize_RejectsUnresolvedMultiFaceFirstHit()
    {
        Fixture fixture = Fixture.Create(faceCount: 2);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            fixture.Draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            fixture.CoordinateSystem.Revision,
            new PlanarGeometryQueries());

        Assert.False(result.IsSuccess);
        Assert.Equal("SurfaceMappingUnresolved", Assert.Single(result.Issues).Code);
    }

    /// <summary>작업 좌표계가 요청한 Native 기하 revision과 다르면 표면 조회 전에 거부합니다.</summary>
    [Fact]
    public void Materialize_RejectsStaleWorkingCoordinateSystem()
    {
        Fixture fixture = Fixture.Create(faceCount: 1);

        OperationResult<CoreHeatingPath> result = fixture.Materializer.Materialize(
            fixture.Draft,
            fixture.Project,
            fixture.WireSpecification,
            fixture.Constraints,
            fixture.CoordinateSystem,
            new ModelRevision(fixture.CoordinateSystem.Revision.Value + 1),
            new PlanarGeometryQueries());

        Assert.False(result.IsSuccess);
        Assert.Equal("StaleSurfaceMappingContext", Assert.Single(result.Issues).Code);
    }

    private sealed class Fixture
    {
        public HeatingPathMaterializer Materializer { get; } = new();
        public DesignProject Project { get; }
        public WireSpecification WireSpecification { get; }
        public PlacementConstraints Constraints { get; }
        public WorkingCoordinateSystem CoordinateSystem { get; }
        public HeatingPathDraft Draft { get; }

        private Fixture(
            int faceCount,
            double insertionDepthMm,
            double wireDiameterMm,
            double surfaceSlopeX,
            double surfaceCurvatureX2)
        {
            byte[] sourceBytes = [3, 1, 4, 1, 5, 9];
            string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
            var modelIdentity = new ModelIdentity(sourceHash, 1, "Fixture", "1", "face-v1");
            var faces = Enumerable.Range(0, faceCount)
                .Select(index => new FaceReference(
                    FaceRefId.New(), sourceHash, index, FaceGeometryKind.Plane,
                    new AreaMm2(400), default, $"face-{index}", []))
                .ToArray();
            BoundaryLoop original = CreateLoop(faces[0].Id);
            BoundaryLoop effective = CreateLoop(faces[0].Id);
            var exitSide = new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0, 1)]);
            var region = new HeatingRegion(
                RegionId.New(), faces.Select(face => face.Id), [original], [], [], [effective], exitSide);
            WireSpecification = new WireSpecification("Fixture alloy", new LengthMm(wireDiameterMm), new ResistanceOhmsPerMeter(100));
            SurfaceAnchor startAnchor = CreateAnchor(faces[0].Id, new Vec2(2, 10), new Vec3(2, 10, SurfaceHeight(2)));
            SurfaceAnchor endAnchor = CreateAnchor(faces[0].Id, new Vec2(18, 10), new Vec3(18, 10, SurfaceHeight(18)));
            Constraints = new PlacementConstraints(
                new LengthMm(1), new LengthMm(1), new LengthMm(insertionDepthMm),
                PlacementDirection.AlongX, startAnchor, endAnchor, exitSide);
            CoordinateSystem = new WorkingCoordinateSystem(
                new WorkingCoordinateSystemId(2),
                new ModelRevision(7),
                new ProjectionFrame(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0)));

            DateTimeOffset timestamp = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
            Project = new DesignProject(
                ProjectId.New(), 1, new DesignRevision(3), "Materializer fixture",
                timestamp, timestamp, modelIdentity,
                new WorkingCoordinateSystemDefinition(default,
                    new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
                faces, [region], WireSpecification, Constraints,
                [], [], [], []);

            ControlPointId startPointId = ControlPointId.New();
            ControlPointId endPointId = ControlPointId.New();
            HeatingPathSegmentId segmentId = HeatingPathSegmentId.New();
            Draft = new HeatingPathDraft(
                Project.Id,
                Project.Revision,
                HeatingPathId.New(),
                region.Id,
                laneCount: 1,
                new HeatingPathCandidateScore(0, 0, 0, 0),
                [
                    new HeatingPathDraftControlPoint(startPointId, startAnchor.WorkingUv),
                    new HeatingPathDraftControlPoint(endPointId, endAnchor.WorkingUv)
                ],
                [new HeatingPathDraftSegment(
                    segmentId,
                    HeatingPathSegmentKind.HeatingRun,
                    startPointId,
                    endPointId,
                    0,
                    [startAnchor.WorkingUv, endAnchor.WorkingUv],
                    laneIndex: 0)]);

            double SurfaceHeight(double x) => surfaceSlopeX * x + surfaceCurvatureX2 * x * x;
        }

        public static Fixture Create(
            int faceCount,
            double insertionDepthMm = 0,
            double wireDiameterMm = 0.3,
            double surfaceSlopeX = 0,
            double surfaceCurvatureX2 = 0) =>
            new(faceCount, insertionDepthMm, wireDiameterMm, surfaceSlopeX, surfaceCurvatureX2);

        private static BoundaryLoop CreateLoop(FaceRefId faceId)
        {
            Vec2[] points = [new(0, 0), new(20, 0), new(20, 20), new(0, 20)];
            BoundarySegment[] segments = points.Select((start, index) => new BoundarySegment(
                BoundarySegmentId.New(), start, points[(index + 1) % points.Length], faceId)).ToArray();
            return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
        }

        private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec2 uv, Vec3 modelPoint) =>
            new(faceId, uv, uv, modelPoint);
    }

    private sealed class PlanarGeometryQueries : IGeometryQueries
    {
        private readonly double _surfaceSlopeX;
        private readonly double _surfaceCurvatureX2;

        public PlanarGeometryQueries(double surfaceSlopeX = 0, double surfaceCurvatureX2 = 0)
        {
            _surfaceSlopeX = surfaceSlopeX;
            _surfaceCurvatureX2 = surfaceCurvatureX2;
        }

        public GeometryQueryResult<SurfaceHit> QueryFirstHit(
            WorkingCoordinateSystem coordinateSystem,
            Vec2 workingUv,
            FaceRefId selectedFaceReferenceId,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SurfaceHit>.Succeeded(new SurfaceHit(
                selectedFaceReferenceId,
                new Vec3(workingUv.X, workingUv.Y, SurfaceHeight(workingUv.X)),
                new Vec3(-SurfaceSlope(workingUv.X), 0, 1).Normalized(),
                workingUv));

        public GeometryQueryResult<InsertionDepthAdjustedPath> AdjustInsertionDepth(
            IReadOnlyList<Vec2> surfacePathWorkingSamples,
            IReadOnlyList<FaceRefId> selectedSurfaceFaces,
            SurfaceAnchor anchor,
            LengthMm wireDiameterMm,
            LengthMm requiredInsertionDepthMm,
            WorkingCoordinateSystem coordinateSystem,
            GeometryToleranceProfile tolerances,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return GeometryQueryResult<InsertionDepthAdjustedPath>.Unresolved([]);

            Vec3[] surfacePoints = surfacePathWorkingSamples
                .Select(point => new Vec3(point.X, point.Y, SurfaceHeight(point.X)))
                .ToArray();
            Vec3 normal = coordinateSystem.Projection.Normal;
            double radiusMm = wireDiameterMm.Value / 2;
            Vec3[] adjustedPoints = new Vec3[surfacePoints.Length];
            for (int index = 0; index < surfacePoints.Length; index++)
            {
                Vec3 tangentVector = index switch
                {
                    0 => surfacePoints[1] - surfacePoints[0],
                    _ when index == surfacePoints.Length - 1 => surfacePoints[^1] - surfacePoints[^2],
                    _ => surfacePoints[index + 1] - surfacePoints[index - 1]
                };
                Vec3 tangent = tangentVector.Normalized();
                double axialTangent = Math.Clamp(Vec3.Dot(tangent, normal), -1, 1);
                double axisSupport = Math.Sqrt(Math.Max(0, 1 - axialTangent * axialTangent));
                double offsetMm = requiredInsertionDepthMm.Value + radiusMm / axisSupport;
                adjustedPoints[index] = surfacePoints[index] - normal * offsetMm;
            }

            var curve = new PolylineCurveDefinition(adjustedPoints);
            return GeometryQueryResult<InsertionDepthAdjustedPath>.Succeeded(new InsertionDepthAdjustedPath(
                curve,
                new LengthMm(0.0001),
                new LengthMm(0.0002),
                adjustedPoints.Length + 1,
                "native-axis-ray-bracket-v2"));
        }

        private double SurfaceHeight(double x) => _surfaceSlopeX * x + _surfaceCurvatureX2 * x * x;

        private double SurfaceSlope(double x) => _surfaceSlopeX + 2 * _surfaceCurvatureX2 * x;

        public GeometryQueryResult<SelectedTopology> GetSelectedTopology(
            IReadOnlyList<FaceRefId> selectedFaceReferences,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SelectedTopology>.Succeeded(new SelectedTopology([], []));

        public GeometryQueryResult<bool> AreAdjacent(
            FaceRefId first,
            FaceRefId second,
            CancellationToken cancellationToken = default) => GeometryQueryResult<bool>.Succeeded(first == second);

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
            IReadOnlyList<CurveGeometrySnapshot> curves,
            LengthMm wireDiameterMm,
            LengthMm requiredClearGapMm,
            GeometryToleranceProfile tolerances,
            CancellationToken cancellationToken = default) => GeometryQueryResult<ClearanceMeasurement>.Unresolved([]);

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(
            CurveGeometrySnapshot curve,
            LengthMm wireDiameterMm,
            SurfaceAnchor anchor,
            WorkingCoordinateSystem coordinateSystem,
            LengthMm requiredInsertionDepthMm,
            CancellationToken cancellationToken = default) => GeometryQueryResult<DepthMeasurement>.Unresolved([]);

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(
            HeatingRegion region,
            GeometryToleranceProfile tolerances,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SurfaceElementSet>.Succeeded(new SurfaceElementSet([], new AreaMm2(0)));

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(
            CoreConnectionPath connectionPath,
            WorkingCoordinateSystem coordinateSystem,
            CancellationToken cancellationToken = default) =>
            GeometryQueryResult<ConnectionObstacleCheck>.Unresolved([]);
    }
}
