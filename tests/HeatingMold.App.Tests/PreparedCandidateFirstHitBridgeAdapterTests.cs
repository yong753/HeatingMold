using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>후보 first-hit 어댑터가 Native 질의 범위를 유지하는지 확인합니다.</summary>
public sealed class PreparedCandidateFirstHitBridgeAdapterTests
{
    /// <summary>후보 ID, context, revision과 좌표를 후보 전용 Native API에 전달합니다.</summary>
    [Fact]
    public void QueryPreparedCandidateFirstHit_ForwardsCandidateScopeAndMapsHit()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceHit = new WorkingSurfaceHit
        {
            Status = WorkingSurfaceQueryStatus.Hit,
            FaceId = 4,
            Point = [10, 20, 30],
            Normal = [0, 0, 1],
            FaceUv = [.25, .75]
        };

        var adapter = new PreparedCandidateFirstHitBridgeAdapter();
        OcctSurfaceQueryResult result = adapter.QueryPreparedCandidateFirstHit(
            candidateId: 73,
            new WorkingCoordinateSystemId(501),
            new ModelRevision(12),
            new Vec2(2.5, -4));

        Assert.Equal(OcctSurfaceQueryStatus.Hit, result.Status);
        Assert.Equal(4, result.NativeFaceIndex);
        Assert.Equal(new Vec3(10, 20, 30), result.ModelPoint);
        Assert.Equal(new Vec2(.25, .75), result.FaceUv);
        Assert.Equal(73, Bridge.LastPreparedCandidateWorkingSurfaceCandidateId);
        Assert.Equal(501, Bridge.LastPreparedCandidateWorkingSurfaceContextId);
        Assert.Equal(12, Bridge.LastPreparedCandidateWorkingSurfaceRevision);
        Assert.Equal(new[] { 2.5, -4d }, Bridge.LastPreparedCandidateWorkingSurfaceUv);
    }

    /// <summary>후보 context가 사라진 상태를 활성 모델 조회로 바꾸지 않습니다.</summary>
    [Fact]
    public void QueryPreparedCandidateFirstHit_PreservesStaleContextStatus()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceHit = new WorkingSurfaceHit
        {
            Status = WorkingSurfaceQueryStatus.StaleContext,
            FaceId = -1
        };

        var adapter = new PreparedCandidateFirstHitBridgeAdapter();
        OcctSurfaceQueryResult result = adapter.QueryPreparedCandidateFirstHit(
            88,
            new WorkingCoordinateSystemId(602),
            new ModelRevision(19),
            new Vec2(0, 0));

        Assert.Equal(OcctSurfaceQueryStatus.StaleContext, result.Status);
        Assert.Null(result.NativeFaceIndex);
        Assert.Equal(88, Bridge.LastPreparedCandidateWorkingSurfaceCandidateId);
        Assert.Equal(0, Bridge.ActiveFaceFeatureQueryCount);
    }
}
