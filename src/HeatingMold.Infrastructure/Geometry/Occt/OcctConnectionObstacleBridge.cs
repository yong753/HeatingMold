using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>Native 연결 경로 충돌 질의의 종료 상태입니다.</summary>
public enum OcctConnectionObstacleStatus
{
    /// <summary>모든 외부 구간에 금형 충돌이 없습니다.</summary>
    Clear,

    /// <summary>외부 구간이 금형과 다시 만나는 위치를 찾았습니다.</summary>
    Obstructed,

    /// <summary>경로 정의나 연결 설정이 유효하지 않습니다.</summary>
    InvalidInput,

    /// <summary>질의 작업 좌표계가 현재 모델 revision과 다릅니다.</summary>
    StaleContext,

    /// <summary>질의 대상 준비 후보가 존재하지 않습니다.</summary>
    CandidateNotFound,

    /// <summary>OCCT가 충돌 연산을 완료하지 못했습니다.</summary>
    GeometryFailure,

    /// <summary>허용 접점과 충돌 또는 전이 위치를 확정할 수 없습니다.</summary>
    Unresolved,

    /// <summary>현재 브리지에서 알 수 없는 상태입니다.</summary>
    Unknown
}

/// <summary>Native 연결 경로 충돌 위치와 경로 시작점부터의 거리를 보관합니다.</summary>
public sealed record OcctConnectionObstacleQueryResult
{
    /// <summary>Native 질의 종료 상태입니다.</summary>
    public OcctConnectionObstacleStatus Status { get; }

    /// <summary>금형 이탈 뒤 처음 만난 충돌 위치입니다.</summary>
    public Vec3? FirstObstruction { get; }

    /// <summary>연결 경로 시작점부터 충돌 위치까지의 길이입니다.</summary>
    public LengthMm? DistanceToObstruction { get; }

    /// <summary>Native 기하 연산의 실패 또는 미확정 사유입니다.</summary>
    public string? Diagnostic { get; }

    /// <summary>충돌 상태와 위치 자료의 조합이 모순되지 않는 결과를 만듭니다.</summary>
    public OcctConnectionObstacleQueryResult(
        OcctConnectionObstacleStatus status,
        Vec3? firstObstruction = null,
        LengthMm? distanceToObstruction = null,
        string? diagnostic = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status != OcctConnectionObstacleStatus.Obstructed &&
            (firstObstruction.HasValue || distanceToObstruction.HasValue))
        {
            throw new ArgumentException("장애물 결과에서만 충돌 위치와 거리를 둘 수 있습니다.", nameof(firstObstruction));
        }
        if (status == OcctConnectionObstacleStatus.Obstructed &&
            (!firstObstruction.HasValue || !distanceToObstruction.HasValue))
        {
            throw new ArgumentException("충돌 결과에는 위치와 경로 거리가 모두 필요합니다.", nameof(firstObstruction));
        }
        if (distanceToObstruction.HasValue &&
            (!double.IsFinite(distanceToObstruction.Value.Value) || distanceToObstruction.Value.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(distanceToObstruction));
        }

        Status = status;
        FirstObstruction = firstObstruction;
        DistanceToObstruction = distanceToObstruction;
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic.Trim();
    }
}

/// <summary>확정 Native 문서 또는 준비 후보에서 연결 경로 충돌을 질의하는 포트입니다.</summary>
public interface IOcctConnectionObstacleBridge
{
    /// <summary>경로의 명시적 금형 이탈 뒤 모든 외부 곡선을 전체 금형과 지정 공차로 검사합니다.</summary>
    OcctConnectionObstacleQueryResult CheckConnectionPathObstacles(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<CurveDefinitionBridgeDto> curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm);
}
