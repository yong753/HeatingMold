using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>Native 삽입 깊이 보정에 필요한 표면 경로와 제한값입니다.</summary>
public sealed record OcctCurveDepthAdjustmentRequest
{
    /// <summary>질의에 사용할 작업 좌표계 context입니다.</summary>
    public WorkingCoordinateSystemId CoordinateSystemId { get; }

    /// <summary>작업 좌표계의 기준 모델 revision입니다.</summary>
    public ModelRevision Revision { get; }

    /// <summary>작업 평면에서 순서가 보존된 표면 경로 표본입니다.</summary>
    public IReadOnlyList<Vec2> SurfacePathWorkingSamples { get; }

    /// <summary>전체 모델 first-hit에 허용하는 선택 Face 번호입니다.</summary>
    public IReadOnlyList<int> SelectedNativeFaceIndices { get; }

    /// <summary>경로 깊이를 확인할 표면 앵커의 Native Face 번호입니다.</summary>
    public int AnchorNativeFaceIndex { get; }

    /// <summary>표면 앵커의 작업 평면 좌표입니다.</summary>
    public Vec2 AnchorWorkingUv { get; }

    /// <summary>표면 앵커의 저장된 모델 좌표입니다.</summary>
    public Vec3 AnchorModelPoint { get; }

    /// <summary>원형 도체의 전체 지름(mm)입니다.</summary>
    public LengthMm WireDiameterMm { get; }

    /// <summary>도체 바깥면까지 요구하는 축방향 깊이(mm)입니다.</summary>
    public LengthMm RequiredInsertionDepthMm { get; }

    /// <summary>작업축 경로 표본 사이의 최대 허용 간격(mm)입니다.</summary>
    public double MaximumSampleSpacingMm { get; }

    /// <summary>축방향 ray 깊이 오차를 계산할 접선 각도 허용오차(rad)입니다.</summary>
    public double RequestedAngularToleranceRad { get; }

    /// <summary>표면 경로 매핑의 최대 오차(mm)입니다.</summary>
    public double MaximumSurfaceDeviationMm { get; }

    /// <summary>앵커·표면 검증과 결과 연결에 적용하는 거리 공차(mm)입니다.</summary>
    public double ConnectionToleranceMm { get; }

    /// <summary>앵커와 경로 표면 first-hit 질의의 최대 횟수입니다.</summary>
    public int MaximumProbeCount { get; }

    /// <summary>표면 경로 요청을 복사하고 수치·Face·작업 한도를 확인합니다.</summary>
    public OcctCurveDepthAdjustmentRequest(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IEnumerable<Vec2> surfacePathWorkingSamples,
        IEnumerable<int> selectedNativeFaceIndices,
        int anchorNativeFaceIndex,
        Vec2 anchorWorkingUv,
        Vec3 anchorModelPoint,
        LengthMm wireDiameterMm,
        LengthMm requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumSurfaceDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        ArgumentNullException.ThrowIfNull(surfacePathWorkingSamples);
        ArgumentNullException.ThrowIfNull(selectedNativeFaceIndices);

        Vec2[] samples = surfacePathWorkingSamples.ToArray();
        int[] faces = selectedNativeFaceIndices.ToArray();
        if (samples.Length < 2 || samples.Any(point => !IsFinite(point)) ||
            samples.Zip(samples.Skip(1), (first, second) => (second - first).Length)
                .Any(length => !double.IsFinite(length) || length <= 0))
        {
            throw new ArgumentException("깊이 보정에는 연속된 작업 평면 표본 두 개 이상이 필요합니다.", nameof(surfacePathWorkingSamples));
        }
        if (faces.Length == 0 || faces.Any(index => index < 0) ||
            faces.Distinct().Count() != faces.Length || !faces.Contains(anchorNativeFaceIndex))
        {
            throw new ArgumentException("선택 Native Face는 중복 없이 유효해야 하며 앵커 Face를 포함해야 합니다.", nameof(selectedNativeFaceIndices));
        }
        if (!IsFinite(anchorWorkingUv) || !IsFinite(anchorModelPoint) ||
            !double.IsFinite(wireDiameterMm.Value) || wireDiameterMm.Value <= 0 ||
            !double.IsFinite(requiredInsertionDepthMm.Value) || requiredInsertionDepthMm.Value < 0 ||
            !double.IsFinite(maximumSampleSpacingMm) || maximumSampleSpacingMm <= 0 ||
            !double.IsFinite(requestedAngularToleranceRad) || requestedAngularToleranceRad <= 0 ||
            !double.IsFinite(maximumSurfaceDeviationMm) || maximumSurfaceDeviationMm < 0 ||
            !double.IsFinite(connectionToleranceMm) || connectionToleranceMm <= 0 || maximumProbeCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(wireDiameterMm), "깊이 보정 치수·좌표·공차·작업 한도가 유효하지 않습니다.");
        }

        CoordinateSystemId = coordinateSystemId;
        Revision = revision;
        SurfacePathWorkingSamples = Array.AsReadOnly(samples);
        SelectedNativeFaceIndices = Array.AsReadOnly(faces);
        AnchorNativeFaceIndex = anchorNativeFaceIndex;
        AnchorWorkingUv = anchorWorkingUv;
        AnchorModelPoint = anchorModelPoint;
        WireDiameterMm = wireDiameterMm;
        RequiredInsertionDepthMm = requiredInsertionDepthMm;
        MaximumSampleSpacingMm = maximumSampleSpacingMm;
        RequestedAngularToleranceRad = requestedAngularToleranceRad;
        MaximumSurfaceDeviationMm = maximumSurfaceDeviationMm;
        ConnectionToleranceMm = connectionToleranceMm;
        MaximumProbeCount = maximumProbeCount;
    }

    private static bool IsFinite(Vec2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsFinite(Vec3 point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
}

/// <summary>Native 깊이 보정의 최종 경로 또는 실패 진단입니다.</summary>
public sealed record OcctCurveDepthAdjustmentQueryResult
{
    /// <summary>Native 깊이 보정이 끝난 상태입니다.</summary>
    public OcctCurveDepthEnvelopeStatus Status { get; }

    /// <summary>성공한 경우에만 포함되는 최종 중심선과 계산 근거입니다.</summary>
    public InsertionDepthAdjustedPath? Adjustment { get; }

    /// <summary>실패 또는 미확정 이유입니다.</summary>
    public string? Diagnostic { get; }

    /// <summary>성공 상태와 보정 경로의 일관성을 확인해 결과를 만듭니다.</summary>
    public OcctCurveDepthAdjustmentQueryResult(
        OcctCurveDepthEnvelopeStatus status,
        InsertionDepthAdjustedPath? adjustment = null,
        string? diagnostic = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == OcctCurveDepthEnvelopeStatus.Success) != (adjustment is not null))
            throw new ArgumentException("성공 상태에서만 깊이 보정 경로를 포함할 수 있습니다.", nameof(adjustment));

        Status = status;
        Adjustment = adjustment;
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic.Trim();
    }
}

/// <summary>활성 또는 후보 OCCT 문서에서 최종 깊이 경로를 계산합니다.</summary>
public interface IOcctCurveDepthAdjustmentBridge
{
    /// <summary>표면 경로, 앵커와 선택 Face를 확인해 보정된 중심선을 반환합니다.</summary>
    OcctCurveDepthAdjustmentQueryResult AdjustInsertionDepth(OcctCurveDepthAdjustmentRequest request);
}
