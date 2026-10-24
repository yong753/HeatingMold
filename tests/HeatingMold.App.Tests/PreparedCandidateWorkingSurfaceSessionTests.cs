using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>후보 작업 좌표계 session이 Native context의 값과 수명을 올바르게 관리하는지 확인합니다.</summary>
public sealed class PreparedCandidateWorkingSurfaceSessionTests
{
    /// <summary>유효한 Native frame을 관리형 좌표계로 변환하고 context를 한 번 해제합니다.</summary>
    [Fact]
    public void TryCreate_MapsFrameAndDisposeReleasesCandidateContextOnce()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 612,
            Revision = 17,
            Frame =
            [
                4, 5, 6,
                1, 0, 0,
                0, 1, 0,
                0, 0, 1
            ]
        };

        bool created = PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 73,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic);

        Assert.True(created, diagnostic);
        Assert.NotNull(session);
        Assert.Null(diagnostic);
        Assert.Equal(73, Bridge.LastPreparedCandidateContextCreationCandidateId);
        Assert.Equal(new[] { 0d, 0, 1, 1, 0, 0 }, Bridge.LastPreparedCandidateContextCreationAxes);
        Assert.Equal(612, session.CoordinateSystem.Id.Value);
        Assert.Equal(17, session.CoordinateSystem.Revision.Value);
        Assert.Equal(new Vec3(4, 5, 6), session.CoordinateSystem.Projection.Origin);
        Assert.Equal(new Vec3(1, 0, 0), session.CoordinateSystem.Projection.AxisX);
        Assert.Equal(new Vec3(0, 1, 0), session.CoordinateSystem.Projection.AxisY);
        Assert.Equal(new Vec3(0, 0, 1), session.CoordinateSystem.Projection.Normal);

        session.Dispose();
        session.Dispose();

        Assert.Equal([(73L, 612L)], Bridge.ReleasedPreparedCandidateContexts);
    }

    /// <summary>잘못된 Native frame을 거부하고 생성된 후보 context를 바로 해제합니다.</summary>
    [Fact]
    public void TryCreate_InvalidFrameReleasesContextAndReturnsDiagnostic()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 613,
            Revision = 18,
            Frame = [0, 0, 0]
        };

        bool created = PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 74,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic);

        Assert.False(created);
        Assert.Null(session);
        Assert.Contains("frame", diagnostic);
        Assert.Equal([(74L, 613L)], Bridge.ReleasedPreparedCandidateContexts);
    }

    /// <summary>단위 길이가 아닌 Native frame 축을 받아들이지 않고 context를 해제합니다.</summary>
    [Fact]
    public void TryCreate_NonUnitFrameAxisReleasesContext()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 614,
            Revision = 19,
            Frame =
            [
                0, 0, 0,
                2, 0, 0,
                0, 1, 0,
                0, 0, 1
            ]
        };

        bool created = PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 75,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic);

        Assert.False(created);
        Assert.Null(session);
        Assert.Contains("단위 벡터", diagnostic);
        Assert.Equal([(75L, 614L)], Bridge.ReleasedPreparedCandidateContexts);
    }

    /// <summary>잘못된 후보 ID는 Native context를 만들기 전에 거부합니다.</summary>
    [Fact]
    public void TryCreate_InvalidCandidateIdDoesNotCallNativeBridge()
    {
        Bridge.Reset();

        bool created = PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 0,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic);

        Assert.False(created);
        Assert.Null(session);
        Assert.Contains("후보 ID", diagnostic);
        Assert.Equal(0, Bridge.LastPreparedCandidateContextCreationCandidateId);
        Assert.Empty(Bridge.ReleasedPreparedCandidateContexts);
    }
}
