using System;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.Circuits;

namespace HeatingMold.Application.Tests;

/// <summary>회로 topology 후보의 변경 여부가 확정 프로젝트 구조를 기준으로 판정되는지 확인합니다.</summary>
public sealed class CircuitTopologyChangeDetectorTests
{
    /// <summary>빈 회로 계획은 변경이 아니며 새 회로 배정은 변경으로 판정합니다.</summary>
    [Fact]
    public void HasChanges_DetectsNewCircuitAssignmentWithoutTurnToggle()
    {
        DesignProject project = CreateEmptyProject();
        var unchangedPlan = new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            new CircuitPathTopologyAnalysis([], []),
            new CircuitIdentityMapping([]));
        var newComponent = new CircuitPathComponent(
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), 0, isReversed: false)],
            ControlPointId.New(),
            ControlPointId.New());
        var changedPlan = new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            new CircuitPathTopologyAnalysis([], []),
            new CircuitIdentityMapping(
            [
                new CircuitIdentityAssignment(
                    CircuitId.New(),
                    parentCircuitId: null,
                    preservesExistingIdentity: false,
                    component: newComponent,
                    startConnectionPointId: null,
                    endConnectionPointId: null,
                    preservedSupply: null,
                    mergedCircuitIds: [])
            ]));

        Assert.False(CircuitTopologyChangeDetector.HasChanges(project, unchangedPlan));
        Assert.True(CircuitTopologyChangeDetector.HasChanges(project, changedPlan));
    }

    /// <summary>완전히 일치하는 기존 회로는 무변경으로 보고 구간·전원·끝점 변경은 감지합니다.</summary>
    [Fact]
    public void HasChanges_RecognizesMatchingCircuitAndDetectsChangedAssignments()
    {
        CircuitTopologyFixture fixture = CreateExistingCircuitFixture();

        Assert.False(CircuitTopologyChangeDetector.HasChanges(fixture.Project, fixture.Plan));

        CircuitPathComponent reversedComponent = new(
            [new CircuitSegmentReference(
                fixture.SegmentId,
                sequence: 0,
                isReversed: true)],
            fixture.StartControlPointId,
            fixture.EndControlPointId);
        CircuitTopologyPlan changedSegments = CreatePlan(
            fixture,
            reversedComponent,
            fixture.StartConnectionPointId,
            fixture.EndConnectionPointId,
            fixture.Supply);
        CircuitTopologyPlan changedSupply = CreatePlan(
            fixture,
            fixture.Component,
            fixture.StartConnectionPointId,
            fixture.EndConnectionPointId,
            new Supply(new VoltageVrms(230), null, null));
        CircuitTopologyPlan changedEndpoint = CreatePlan(
            fixture,
            fixture.Component,
            ConnectionPointId.New(),
            fixture.EndConnectionPointId,
            fixture.Supply);

        Assert.True(CircuitTopologyChangeDetector.HasChanges(fixture.Project, changedSegments));
        Assert.True(CircuitTopologyChangeDetector.HasChanges(fixture.Project, changedSupply));
        Assert.True(CircuitTopologyChangeDetector.HasChanges(fixture.Project, changedEndpoint));
    }

    /// <summary>테스트 프로젝트에 유효한 모델 identity와 작업 좌표계를 제공합니다.</summary>
    private static DesignProject CreateEmptyProject()
    {
        var modelIdentity = new ModelIdentity(new string('A', 64), 1, "Fixture", "1", "face-v1");
        var coordinateSystem = new WorkingCoordinateSystemDefinition(
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1));
        return DesignProject.CreateEmpty(ProjectId.New(), modelIdentity, coordinateSystem);
    }

    /// <summary>기존 회로·끝점과 같은 안정 ID를 사용하는 topology 기준 fixture를 만듭니다.</summary>
    private static CircuitTopologyFixture CreateExistingCircuitFixture()
    {
        DesignProject emptyProject = CreateEmptyProject();
        CircuitId circuitId = CircuitId.New();
        HeatingPathSegmentId segmentId = HeatingPathSegmentId.New();
        ControlPointId startControlPointId = ControlPointId.New();
        ControlPointId endControlPointId = ControlPointId.New();
        ConnectionPointId startConnectionPointId = ConnectionPointId.New();
        ConnectionPointId endConnectionPointId = ConnectionPointId.New();
        CircuitPathComponent component = new(
            [new CircuitSegmentReference(segmentId, sequence: 0, isReversed: false)],
            startControlPointId,
            endControlPointId);
        var supply = new Supply(new VoltageVrms(120), null, null);
        var circuit = new Circuit(
            circuitId,
            sequence: 0,
            component.Segments,
            startConnectionPointId,
            endConnectionPointId,
            supply);
        var startAnchor = new SurfaceAnchor(
            FaceRefId.New(),
            new Vec2(1, 2),
            new Vec2(1, 2),
            new Vec3(1, 2, 3));
        var endAnchor = new SurfaceAnchor(
            FaceRefId.New(),
            new Vec2(4, 5),
            new Vec2(4, 5),
            new Vec3(4, 5, 6));
        ConnectionPoint[] connectionPoints =
        [
            new ConnectionPoint(
                startConnectionPointId,
                circuitId,
                ConnectionPointRole.Start,
                segmentId,
                startControlPointId,
                startAnchor,
                new Vec3(7, 8, 9)),
            new ConnectionPoint(
                endConnectionPointId,
                circuitId,
                ConnectionPointRole.End,
                segmentId,
                endControlPointId,
                endAnchor,
                new Vec3(10, 11, 12))
        ];
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
            connectionPoints,
            []);
        CircuitTopologyPlan plan = CreatePlan(
            new CircuitTopologyFixture(
                project,
                component,
                circuitId,
                segmentId,
                startControlPointId,
                endControlPointId,
                startConnectionPointId,
                endConnectionPointId,
                supply),
            component,
            startConnectionPointId,
            endConnectionPointId,
            supply);

        return new CircuitTopologyFixture(
            project,
            component,
            circuitId,
            segmentId,
            startControlPointId,
            endControlPointId,
            startConnectionPointId,
            endConnectionPointId,
            supply,
            plan);
    }

    /// <summary>fixture의 회로 안정 ID와 지정한 구성으로 분석 계획을 만듭니다.</summary>
    private static CircuitTopologyPlan CreatePlan(
        CircuitTopologyFixture fixture,
        CircuitPathComponent component,
        ConnectionPointId? startConnectionPointId,
        ConnectionPointId? endConnectionPointId,
        Supply supply)
    {
        var assignment = new CircuitIdentityAssignment(
            fixture.CircuitId,
            parentCircuitId: null,
            preservesExistingIdentity: true,
            component,
            startConnectionPointId,
            endConnectionPointId,
            supply,
            mergedCircuitIds: []);
        return new CircuitTopologyPlan(
            fixture.Project.Id,
            fixture.Project.Revision,
            new CircuitPathTopologyAnalysis([component], []),
            new CircuitIdentityMapping([assignment]));
    }

    private sealed record CircuitTopologyFixture(
        DesignProject Project,
        CircuitPathComponent Component,
        CircuitId CircuitId,
        HeatingPathSegmentId SegmentId,
        ControlPointId StartControlPointId,
        ControlPointId EndControlPointId,
        ConnectionPointId StartConnectionPointId,
        ConnectionPointId EndConnectionPointId,
        Supply Supply,
        CircuitTopologyPlan Plan = null);
}
