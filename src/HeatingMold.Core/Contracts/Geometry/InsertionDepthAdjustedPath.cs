using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Contracts.Geometry;

/// <summary>Native가 계산한 최종 삽입 깊이 경로와 수치 근거입니다.</summary>
public sealed record InsertionDepthAdjustedPath
{
    /// <summary>요구 삽입 깊이가 반영된 최종 중심선 다중선입니다.</summary>
    public PolylineCurveDefinition Curve { get; }

    /// <summary>깊이 root 해의 최대 잔차(mm)입니다.</summary>
    public LengthMm MaximumDepthResidualMm { get; }

    /// <summary>표면·접선 근사와 root 잔차를 포함한 최대 오차 추정값(mm)입니다.</summary>
    public LengthMm ErrorBoundMm { get; }

    /// <summary>앵커와 보정 과정에서 수행한 Native 표면 probe 수입니다.</summary>
    public int ProbeCount { get; }

    /// <summary>깊이 보정 알고리즘의 안정 버전 표식입니다.</summary>
    public string AlgorithmVersion { get; }

    /// <summary>유효한 최종 곡선과 Native 수치 근거를 보관합니다.</summary>
    public InsertionDepthAdjustedPath(
        PolylineCurveDefinition curve,
        LengthMm maximumDepthResidualMm,
        LengthMm errorBoundMm,
        int probeCount,
        string algorithmVersion)
    {
        Curve = curve ?? throw new ArgumentNullException(nameof(curve));
        if (!double.IsFinite(maximumDepthResidualMm.Value) || maximumDepthResidualMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDepthResidualMm));
        if (!double.IsFinite(errorBoundMm.Value) || errorBoundMm.Value < maximumDepthResidualMm.Value)
            throw new ArgumentOutOfRangeException(nameof(errorBoundMm));
        if (probeCount < curve.Points.Count)
            throw new ArgumentOutOfRangeException(nameof(probeCount));
        if (string.IsNullOrWhiteSpace(algorithmVersion))
            throw new ArgumentException("깊이 보정 알고리즘 버전이 필요합니다.", nameof(algorithmVersion));

        MaximumDepthResidualMm = maximumDepthResidualMm;
        ErrorBoundMm = errorBoundMm;
        ProbeCount = probeCount;
        AlgorithmVersion = algorithmVersion.Trim();
    }
}
