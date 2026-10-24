using System;
using HeatingMold.Core.Geometry;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>준비 후보 전용 OccBridge first-hit API를 Infrastructure 결과로 변환합니다.</summary>
public sealed class PreparedCandidateFirstHitBridgeAdapter : IOcctPreparedCandidateFirstHitBridge
{
    /// <summary>후보 ID와 후보 작업 context를 함께 전달해 실제 표면 교점을 조회합니다.</summary>
    public OcctSurfaceQueryResult QueryPreparedCandidateFirstHit(
        long candidateId,
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        Vec2 workingUv)
    {
        WorkingSurfaceHit nativeHit = Bridge.QueryPreparedCandidateWorkingSurface(
            candidateId,
            coordinateSystemId.Value,
            revision.Value,
            workingUv.X,
            workingUv.Y);
        if (nativeHit is null)
            return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure);

        switch (nativeHit.Status)
        {
            case WorkingSurfaceQueryStatus.Hit:
                return CreateHit(nativeHit);
            case WorkingSurfaceQueryStatus.Miss:
                return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Miss);
            case WorkingSurfaceQueryStatus.Tangent:
                return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Tangent);
            case WorkingSurfaceQueryStatus.InvalidInput:
                return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.InvalidInput);
            case WorkingSurfaceQueryStatus.StaleContext:
                return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.StaleContext);
            case WorkingSurfaceQueryStatus.GeometryFailure:
                return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure);
            default:
                return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Unknown);
        }
    }

    private static OcctSurfaceQueryResult CreateHit(WorkingSurfaceHit hit)
    {
        if (hit.Point is not { Length: 3 } || hit.Normal is not { Length: 3 } || hit.FaceUv is not { Length: 2 })
            return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure);

        try
        {
            return new OcctSurfaceQueryResult(
                OcctSurfaceQueryStatus.Hit,
                hit.FaceId,
                new Vec3(hit.Point[0], hit.Point[1], hit.Point[2]),
                new Vec3(hit.Normal[0], hit.Normal[1], hit.Normal[2]),
                new Vec2(hit.FaceUv[0], hit.FaceUv[1]));
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException)
        {
            return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure);
        }
    }
}
