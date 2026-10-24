using System;
using HeatingMold.App;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>회로 길이 표시가 확정 측정값과 각 회로의 양 끝 연결 경로를 집계하는지 확인합니다.</summary>
public sealed class CircuitLengthTablePresentationTests
{
    /// <summary>미측정 구간은 제외하고 대상 회로의 끝점 경로만 회로 순서로 합산합니다.</summary>
    [Fact]
    public void BuildRows_UsesMeasuredLengthsAndOnlyCircuitEndpointPaths()
    {
        ProjectFixture fixture = CreateProjectFixture();
        GeometrySnapshot geometry = CreateGeometry(fixture);

        CircuitLengthRow row = Assert.Single(CircuitLengthTablePresentation.BuildRows(fixture.Project, geometry));

        Assert.StartsWith("회로 1 · ", row.Circuit);
        Assert.Equal(17, row.HeatingLength);
        Assert.Equal(10, row.ConnectionLength);
        Assert.Equal(27, row.TotalLength);
    }

    /// <summary>회로가 없는 프로젝트에는 길이 표시 행을 만들지 않습니다.</summary>
    [Fact]
    public void BuildRows_ProjectWithoutCircuits_ReturnsNoRows()
    {
        DesignProject project = CreateEmptyProject();
        var geometry = new GeometrySnapshot(
            GeometryDocumentId.New(),
            new ModelRevision(1),
            CreateDependencyStamp(),
            []);

        Assert.Empty(CircuitLengthTablePresentation.BuildRows(project, geometry));
    }

    /// <summary>발열·연결 구간 ID에 대응하는 테스트용 프로젝트와 화면 측정 ID를 만듭니다.</summary>
    private static ProjectFixture CreateProjectFixture()
    {
        DesignProject emptyProject = CreateEmptyProject();
        HeatingPathSegmentId measuredHeatingId = HeatingPathSegmentId.New();
        HeatingPathSegmentId secondMeasuredHeatingId = HeatingPathSegmentId.New();
        HeatingPathSegmentId unmeasuredHeatingId = HeatingPathSegmentId.New();
        ConnectionPointId startPointId = ConnectionPointId.New();
        ConnectionPointId endPointId = ConnectionPointId.New();
        ConnectionPointId unrelatedPointId = ConnectionPointId.New();
        var circuit = new Circuit(
            CircuitId.New(),
            sequence: 0,
            [
                new CircuitSegmentReference(measuredHeatingId, 0, isReversed: false),
                new CircuitSegmentReference(secondMeasuredHeatingId, 1, isReversed: false),
                new CircuitSegmentReference(unmeasuredHeatingId, 2, isReversed: false)
            ],
            startPointId,
            endPointId,
            new Supply(null, null, null));
        ConnectionPathFixture startPath = CreateConnectionPath(startPointId);
        ConnectionPathFixture endPath = CreateConnectionPath(endPointId);
        ConnectionPathFixture unrelatedPath = CreateConnectionPath(unrelatedPointId);
        var project = new DesignProject(
            emptyProject.Id,
            emptyProject.SchemaVersion,
            emptyProject.Revision,
            emptyProject.Name,
            emptyProject.CreatedUtc,
            emptyProject.ModifiedUtc,
            emptyProject.SourceModel,
            emptyProject.WorkingCoordinateSystem,
            [],
            [],
            null,
            null,
            [],
            [circuit],
            [],
            [startPath.Path, endPath.Path, unrelatedPath.Path],
            emptyProject.Rules);

        return new ProjectFixture(
            project,
            measuredHeatingId,
            secondMeasuredHeatingId,
            startPath,
            endPath,
            unrelatedPath);
    }

    /// <summary>구간별 확정 길이를 담은 기하 스냅샷을 만듭니다.</summary>
    private static GeometrySnapshot CreateGeometry(ProjectFixture fixture)
    {
        CurveGeometrySnapshot[] curves =
        [
            CreateHeatingCurve(fixture.MeasuredHeatingId, 8, 0),
            CreateHeatingCurve(fixture.SecondMeasuredHeatingId, 9, 1),
            CreateConnectionCurve(fixture.StartPath.FirstSegmentId, 1, 2),
            CreateConnectionCurve(fixture.StartPath.SecondSegmentId, 2, 3),
            CreateConnectionCurve(fixture.EndPath.FirstSegmentId, 3, 4),
            CreateConnectionCurve(fixture.EndPath.SecondSegmentId, 4, 5),
            CreateConnectionCurve(fixture.UnrelatedPath.FirstSegmentId, 100, 6),
            CreateConnectionCurve(fixture.UnrelatedPath.SecondSegmentId, 200, 7)
        ];
        return new GeometrySnapshot(
            GeometryDocumentId.New(),
            new ModelRevision(1),
            CreateDependencyStamp(),
            curves);
    }

    /// <summary>빈 프로젝트에 필요한 안정 모델 식별자와 작업 좌표계를 채웁니다.</summary>
    private static DesignProject CreateEmptyProject()
    {
        var modelIdentity = new ModelIdentity(new string('A', 64), 1, "Fixture", "1", "face-v1");
        var workingCoordinateSystem = new WorkingCoordinateSystemDefinition(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        return DesignProject.CreateEmpty(ProjectId.New(), modelIdentity, workingCoordinateSystem, "길이 표 fixture");
    }

    /// <summary>금형 위 구간과 외부 전이 구간을 가진 연결 경로를 만듭니다.</summary>
    private static ConnectionPathFixture CreateConnectionPath(ConnectionPointId connectionPointId)
    {
        ConnectionPathSegmentId firstSegmentId = ConnectionPathSegmentId.New();
        ConnectionPathSegmentId secondSegmentId = ConnectionPathSegmentId.New();
        Vec3 surfacePoint = new(0, 0, 0);
        Vec3 exitPoint = new(0, 0, 1);
        Vec3 externalPoint = new(0, 0, 2);
        var firstSegment = new ConnectionPathSegment(
            firstSegmentId,
            0,
            ConductorRole.HeatingWireExtension,
            GeometryDomain.OnMold,
            new LengthMm(0),
            new LineCurveDefinition(surfacePoint, exitPoint));
        var secondSegment = new ConnectionPathSegment(
            secondSegmentId,
            1,
            ConductorRole.SeparateConnectionWire,
            GeometryDomain.External,
            new LengthMm(0),
            new LineCurveDefinition(exitPoint, externalPoint),
            isMoldExitTransition: true);
        return new ConnectionPathFixture(
            new ConnectionPath(ConnectionPathId.New(), connectionPointId, [firstSegment, secondSegment]),
            firstSegmentId,
            secondSegmentId);
    }

    /// <summary>발열 경로 구간에 대응하는 테스트 측정값을 만듭니다.</summary>
    private static CurveGeometrySnapshot CreateHeatingCurve(
        HeatingPathSegmentId segmentId,
        double lengthMm,
        int position) =>
        CreateCurveSnapshot(segmentId, null, lengthMm, position);

    /// <summary>연결 경로 구간에 대응하는 테스트 측정값을 만듭니다.</summary>
    private static CurveGeometrySnapshot CreateConnectionCurve(
        ConnectionPathSegmentId segmentId,
        double lengthMm,
        int position) =>
        CreateCurveSnapshot(null, segmentId, lengthMm, position);

    /// <summary>구간 안정 ID와 양수 길이를 가진 테스트 곡선 측정값을 만듭니다.</summary>
    private static CurveGeometrySnapshot CreateCurveSnapshot(
        HeatingPathSegmentId? heatingSegmentId,
        ConnectionPathSegmentId? connectionSegmentId,
        double lengthMm,
        int position)
    {
        Vec3 start = new(position, 0, 0);
        Vec3 end = new(position + 1, 0, 0);
        return new CurveGeometrySnapshot(
            heatingSegmentId,
            connectionSegmentId,
            new LineCurveDefinition(start, end),
            new LengthMm(lengthMm),
            start,
            end,
            [start, end],
            new CurveApproximationInfo(0, 0, 0, 0, 0, isComplete: true, "circuit-length-test-v1"));
    }

    /// <summary>Managed 회귀용 계산 의존 지문을 만듭니다.</summary>
    private static DependencyStamp CreateDependencyStamp() => new(
        new DesignRevision(0),
        new string('A', 64),
        new string('B', 64),
        new string('C', 64),
        "circuit-length-test-v1");

    private sealed record ConnectionPathFixture(
        ConnectionPath Path,
        ConnectionPathSegmentId FirstSegmentId,
        ConnectionPathSegmentId SecondSegmentId);

    private sealed record ProjectFixture(
        DesignProject Project,
        HeatingPathSegmentId MeasuredHeatingId,
        HeatingPathSegmentId SecondMeasuredHeatingId,
        ConnectionPathFixture StartPath,
        ConnectionPathFixture EndPath,
        ConnectionPathFixture UnrelatedPath);
}
