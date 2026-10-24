using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>OCCT 곡선 간격 질의가 종료된 이유입니다.</summary>
public enum OcctCurveClearanceStatus
{
    /// <summary>모든 비국소 곡선 쌍의 최단거리를 계산했습니다.</summary>
    Success,

    /// <summary>곡선 정의나 거리 설정이 유효하지 않습니다.</summary>
    InvalidInput,

    /// <summary>작업 좌표계가 현재 모델 revision에 속하지 않습니다.</summary>
    StaleContext,

    /// <summary>준비 후보가 없거나 이미 폐기되었습니다.</summary>
    CandidateNotFound,

    /// <summary>거리 계산 쌍의 작업 한도를 넘었습니다.</summary>
    PairBudgetExceeded,

    /// <summary>OCCT가 거리 계산을 완료하지 못했습니다.</summary>
    GeometryFailure,

    /// <summary>비국소 곡선 쌍이 없거나 수치 정밀도가 충분하지 않습니다.</summary>
    Unresolved,

    /// <summary>Native 브리지가 이 버전에서 알 수 없는 상태를 반환했습니다.</summary>
    Unknown
}

/// <summary>OCCT가 계산한 곡선 중심선 최단거리와 오차 정보를 보관합니다.</summary>
public sealed record OcctCurveClearanceQueryResult
{
    /// <summary>Native 질의 종료 상태입니다.</summary>
    public OcctCurveClearanceStatus Status { get; }

    /// <summary>모든 비국소 쌍을 완료한 성공 결과입니다.</summary>
    public ClearanceMeasurement? Measurement { get; }

    /// <summary>Native 계산 실패 원인이나 미확정 사유입니다.</summary>
    public string? Diagnostic { get; }

    /// <summary>성공 상태와 측정값 존재 여부를 검증해 질의 결과를 만듭니다.</summary>
    public OcctCurveClearanceQueryResult(
        OcctCurveClearanceStatus status,
        ClearanceMeasurement? measurement = null,
        string? diagnostic = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == OcctCurveClearanceStatus.Success) != (measurement is not null))
            throw new ArgumentException("성공 상태에서만 간격 측정값을 포함할 수 있습니다.", nameof(measurement));
        if (status == OcctCurveClearanceStatus.Success && !measurement!.IsComplete)
            throw new ArgumentException("성공한 Native 간격 측정은 완전해야 합니다.", nameof(measurement));

        Status = status;
        Measurement = measurement;
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic.Trim();
    }
}

/// <summary>정확한 곡선 정의를 현재 OCCT 문서 또는 준비 후보에서 비교합니다.</summary>
public interface IOcctCurveClearanceBridge
{
    /// <summary>모델 revision에 고정한 곡선 정의들의 비국소 최단거리와 오차를 계산합니다.</summary>
    /// <param name="localCurvePairs">이전 곡선 인덱스와 다음 곡선 인덱스로 구성된 방향 있는 연결 쌍입니다.</param>
    OcctCurveClearanceQueryResult MeasureCurveClearance(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<CurveDefinitionBridgeDto> curves,
        IReadOnlyList<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        LengthMm wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks);
}
