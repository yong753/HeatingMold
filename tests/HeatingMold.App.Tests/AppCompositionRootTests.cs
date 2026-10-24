using System.Collections.Generic;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>실제 CompositionRoot가 후보 session에 맞는 Native query 어댑터를 조립하는지 확인합니다.</summary>
public sealed class AppCompositionRootTests
{
    /// <summary>CompositionRoot가 제어점과 회전 곡선 편집 유스케이스를 화면 서비스에 주입합니다.</summary>
    [Fact]
    public void CreateMainWindowServices_ProvidesControlPointEditUseCase()
    {
        AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices();

        Assert.NotNull(services.MoveControlPoint);
        Assert.NotNull(services.EditTurnSegmentCurve);
        Assert.NotNull(services.ChangeCircuitTopology);
        Assert.NotNull(services.UpdateConnectionPath);
        Assert.NotNull(services.AssessHeatingPathRegeneration);
        Assert.NotNull(services.GeometryWorkspace);
        Assert.NotNull(services.UpdateElectricalSpecification);
        Assert.IsAssignableFrom<IElectricalGeometryWorkspace>(services.GeometryWorkspace);
    }

    /// <summary>CompositionRoot가 화면의 재생성 확인 동작을 서비스 경계에 보존합니다.</summary>
    [Fact]
    public void CreateMainWindowServices_PreservesRegenerationConfirmationHandler()
    {
        System.Func<string, bool> confirm = _ => false;

        AppCompositionRoot.MainWindowServices services = AppCompositionRoot.CreateMainWindowServices(
            confirmHeatingPathRegeneration: confirm);

        Assert.Same(confirm, services.ConfirmHeatingPathRegeneration);
    }

    /// <summary>후보 session에서 만든 query가 같은 후보와 좌표계 ID를 사용합니다.</summary>
    [Fact]
    public void CreatePreparedCandidateQueries_UsesSessionCandidateAndCoordinateSystem()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 901,
            Revision = 23,
            Frame =
            [
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
                0, 0, 1
            ]
        };
        Bridge.PreparedCandidateWorkingSurfaceHit = new WorkingSurfaceHit
        {
            Status = WorkingSurfaceQueryStatus.Hit,
            FaceId = 5,
            Point = [1, 2, 3],
            Normal = [0, 0, 1],
            FaceUv = [.25, .5]
        };

        bool created = PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 82,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic);
        Assert.True(created, diagnostic);

        FaceRefId faceId = FaceRefId.New();
        var faceIndices = new Dictionary<FaceRefId, int> { [faceId] = 5 };
        var geometryQueries = AppCompositionRoot.CreatePreparedCandidateOcctGeometryQueries(session, faceIndices);
        var result = geometryQueries.QueryFirstHit(
            session.CoordinateSystem,
            new Vec2(3, 4),
            faceId);

        Assert.Equal(GeometryQueryStatus.Success, result.Status);
        Assert.Equal(82, Bridge.LastPreparedCandidateWorkingSurfaceCandidateId);
        Assert.Equal(session.CoordinateSystem.Id.Value, Bridge.LastPreparedCandidateWorkingSurfaceContextId);
        Assert.Equal(session.CoordinateSystem.Revision.Value, Bridge.LastPreparedCandidateWorkingSurfaceRevision);
        Assert.Equal(0, Bridge.ActiveWorkingGeometryQueryCount);

        session.Dispose();
    }

    /// <summary>null session은 후보 query 객체를 만들기 전에 거부합니다.</summary>
    [Fact]
    public void CreatePreparedCandidateQueries_NullSessionThrows()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            AppCompositionRoot.CreatePreparedCandidateOcctGeometryQueries(
                null,
                new Dictionary<FaceRefId, int>()));
    }
}
