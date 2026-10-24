using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>Native 곡선 깊이 envelope 질의가 종료된 이유입니다.</summary>
public enum OcctCurveDepthEnvelopeStatus
{
    /// <summary>모든 표본의 깊이 범위를 계산했습니다.</summary>
    Success,

    /// <summary>곡선·면·깊이 설정이 유효하지 않습니다.</summary>
    InvalidInput,

    /// <summary>선택한 Native Face 번호가 현재 문서에 없습니다.</summary>
    InvalidFace,

    /// <summary>준비 후보가 없거나 이미 폐기되었습니다.</summary>
    CandidateNotFound,

    /// <summary>작업 좌표계가 현재 모델 revision에 속하지 않습니다.</summary>
    StaleContext,

    /// <summary>곡선 표본의 작업 좌표에 표면 교점이 없습니다.</summary>
    Miss,

    /// <summary>표면 광선이 접해 깊이를 안정적으로 정할 수 없습니다.</summary>
    Tangent,

    /// <summary>Native 깊이 측정 작업 한도를 넘었습니다.</summary>
    ProbeBudgetExceeded,

    /// <summary>OCCT 교차 또는 깊이 계산에서 기하 오류가 발생했습니다.</summary>
    GeometryFailure,

    /// <summary>표본 또는 오차 근거로 전체 깊이 범위를 확정하지 못했습니다.</summary>
    Unresolved,

    /// <summary>현재 브리지가 알지 못하는 상태입니다.</summary>
    Unknown
}

/// <summary>Native 깊이 질의에 필요한 곡선·앵커·허용오차를 보관합니다.</summary>
public sealed record OcctCurveDepthEnvelopeRequest
{
    /// <summary>깊이 측정에 사용할 Native 작업 좌표계 식별자입니다.</summary>
    public WorkingCoordinateSystemId CoordinateSystemId { get; }

    /// <summary>작업 좌표계가 참조하는 모델 revision입니다.</summary>
    public ModelRevision Revision { get; }

    /// <summary>최종 곡선 중심선의 순서 있는 모델 좌표 표본입니다.</summary>
    public IReadOnlyList<Vec3> CenterlineSamples { get; }

    /// <summary>현재 문서에서 선택된 Native Face 번호입니다.</summary>
    public IReadOnlyList<int> SelectedNativeFaceIndices { get; }

    /// <summary>깊이 앵커가 속한 Native Face 번호입니다.</summary>
    public int AnchorNativeFaceIndex { get; }

    /// <summary>깊이 앵커의 작업 평면 좌표입니다.</summary>
    public Vec2 AnchorWorkingUv { get; }

    /// <summary>저장된 깊이 앵커의 모델 좌표입니다.</summary>
    public Vec3 AnchorModelPoint { get; }

    /// <summary>원형 도체의 전체 지름입니다.</summary>
    public LengthMm WireDiameterMm { get; }

    /// <summary>요청한 축방향 삽입 깊이입니다.</summary>
    public LengthMm RequiredInsertionDepthMm { get; }

    /// <summary>곡선 표본을 세분할 때 허용하는 최대 간격입니다.</summary>
    public double MaximumSampleSpacingMm { get; }

    /// <summary>축방향 ray 깊이 오차를 계산할 접선 각도 허용오차입니다.</summary>
    public double RequestedAngularToleranceRad { get; }

    /// <summary>최종 Native 곡선 근사의 최대 측정 편차입니다.</summary>
    public double MaximumMeasuredDeviationMm { get; }

    /// <summary>앵커와 연속 표본을 비교할 연결 공차입니다.</summary>
    public double ConnectionToleranceMm { get; }

    /// <summary>앵커와 정밀화 표본을 포함한 최대 표면 질의 수입니다.</summary>
    public int MaximumProbeCount { get; }

    /// <summary>Native 표면 질의에 필요한 입력을 복사하고 범위를 검증합니다.</summary>
    public OcctCurveDepthEnvelopeRequest(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IEnumerable<Vec3> centerlineSamples,
        IEnumerable<int> selectedNativeFaceIndices,
        int anchorNativeFaceIndex,
        Vec2 anchorWorkingUv,
        Vec3 anchorModelPoint,
        LengthMm wireDiameterMm,
        LengthMm requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        ArgumentNullException.ThrowIfNull(centerlineSamples);
        ArgumentNullException.ThrowIfNull(selectedNativeFaceIndices);

        Vec3[] samples = centerlineSamples.ToArray();
        int[] faces = selectedNativeFaceIndices.ToArray();
        if (samples.Length < 2 || samples.Any(point => !IsFinite(point)) ||
            samples.Zip(samples.Skip(1), (first, second) => (second - first).Length)
                .Any(length => !double.IsFinite(length) || length <= 0))
        {
            throw new ArgumentException("깊이 곡선에는 유효한 연속 표본이 두 개 이상 필요합니다.", nameof(centerlineSamples));
        }
        if (faces.Length == 0 || faces.Any(index => index < 0) || faces.Distinct().Count() != faces.Length ||
            !faces.Contains(anchorNativeFaceIndex))
        {
            throw new ArgumentException("선택 Native Face 집합은 중복 없이 유효해야 하며 앵커 Face를 포함해야 합니다.", nameof(selectedNativeFaceIndices));
        }
        if (!IsFinite(anchorWorkingUv) || !IsFinite(anchorModelPoint) ||
            wireDiameterMm.Value <= 0 || requiredInsertionDepthMm.Value < 0 ||
            !double.IsFinite(maximumSampleSpacingMm) || maximumSampleSpacingMm <= 0 ||
            !double.IsFinite(requestedAngularToleranceRad) || requestedAngularToleranceRad <= 0 ||
            !double.IsFinite(maximumMeasuredDeviationMm) || maximumMeasuredDeviationMm < 0 ||
            !double.IsFinite(connectionToleranceMm) || connectionToleranceMm <= 0 || maximumProbeCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(wireDiameterMm), "깊이 질의의 직경·좌표·공차·작업 한도가 유효하지 않습니다.");
        }

        CoordinateSystemId = coordinateSystemId;
        Revision = revision;
        CenterlineSamples = Array.AsReadOnly(samples);
        SelectedNativeFaceIndices = Array.AsReadOnly(faces);
        AnchorNativeFaceIndex = anchorNativeFaceIndex;
        AnchorWorkingUv = anchorWorkingUv;
        AnchorModelPoint = anchorModelPoint;
        WireDiameterMm = wireDiameterMm;
        RequiredInsertionDepthMm = requiredInsertionDepthMm;
        MaximumSampleSpacingMm = maximumSampleSpacingMm;
        RequestedAngularToleranceRad = requestedAngularToleranceRad;
        MaximumMeasuredDeviationMm = maximumMeasuredDeviationMm;
        ConnectionToleranceMm = connectionToleranceMm;
        MaximumProbeCount = maximumProbeCount;
    }

    private static bool IsFinite(Vec2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsFinite(Vec3 point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
}

/// <summary>Native 깊이 envelope 질의의 상태와 성공 측정값을 보관합니다.</summary>
public sealed record OcctCurveDepthEnvelopeQueryResult
{
    /// <summary>Native 질의 종료 상태입니다.</summary>
    public OcctCurveDepthEnvelopeStatus Status { get; }

    /// <summary>성공했을 때 전체 곡선 깊이 측정값입니다.</summary>
    public DepthMeasurement? Measurement { get; }

    /// <summary>실패 또는 미확정 사유입니다.</summary>
    public string? Diagnostic { get; }

    /// <summary>성공 상태와 완전한 측정값이 함께 제공되는지 확인합니다.</summary>
    public OcctCurveDepthEnvelopeQueryResult(
        OcctCurveDepthEnvelopeStatus status,
        DepthMeasurement? measurement = null,
        string? diagnostic = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == OcctCurveDepthEnvelopeStatus.Success) != (measurement is not null))
            throw new ArgumentException("성공 상태에서만 깊이 측정값을 포함할 수 있습니다.", nameof(measurement));
        if (status == OcctCurveDepthEnvelopeStatus.Success && (!measurement!.IsComplete || !measurement.IsCurveEnvelope))
            throw new ArgumentException("Native 깊이 envelope 성공 결과는 완전한 곡선 측정이어야 합니다.", nameof(measurement));

        Status = status;
        Measurement = measurement;
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic.Trim();
    }
}

/// <summary>활성 또는 후보 OCCT 문서에서 곡선 깊이 envelope를 측정합니다.</summary>
public interface IOcctCurveDepthBridge
{
    /// <summary>작업 좌표계와 선택 Face 집합을 고정해 곡선 전체 깊이를 계산합니다.</summary>
    OcctCurveDepthEnvelopeQueryResult MeasureDepthEnvelope(OcctCurveDepthEnvelopeRequest request);
}
