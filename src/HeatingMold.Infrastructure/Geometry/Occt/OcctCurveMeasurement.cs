using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>OCCT 후보 곡선 하나의 길이, 끝점과 근사 증거를 보관합니다.</summary>
public sealed class OcctCurveMeasurement
{
    /// <summary>최종 Native 곡선의 측정 길이(mm)입니다.</summary>
    public LengthMm LengthMm { get; }

    /// <summary>최종 Native 곡선의 진행 시작점입니다.</summary>
    public Vec3 Start { get; }

    /// <summary>최종 Native 곡선의 진행 끝점입니다.</summary>
    public Vec3 End { get; }

    /// <summary>최종 곡선을 따라 정렬된 표본입니다.</summary>
    public IReadOnlyList<Vec3> Samples { get; }

    /// <summary>표본 공차, 실제 측정값과 완료 여부입니다.</summary>
    public CurveApproximationInfo Approximation { get; }

    /// <summary>양의 길이와 측정된 끝점·표본을 함께 보관합니다.</summary>
    public OcctCurveMeasurement(
        LengthMm lengthMm,
        Vec3 start,
        Vec3 end,
        IEnumerable<Vec3> samples,
        CurveApproximationInfo approximation)
    {
        if (!double.IsFinite(lengthMm.Value) || lengthMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(lengthMm), "곡선 길이는 양의 유한값이어야 합니다.");

        Vec3[] copiedSamples = samples?.ToArray() ?? throw new ArgumentNullException(nameof(samples));
        if (copiedSamples.Length < 2 || copiedSamples[0] != start || copiedSamples[^1] != end)
            throw new ArgumentException("표본의 첫 점과 마지막 점은 측정된 끝점과 같아야 합니다.", nameof(samples));

        LengthMm = lengthMm;
        Start = start;
        End = end;
        Samples = new ReadOnlyCollection<Vec3>(copiedSamples);
        Approximation = approximation ?? throw new ArgumentNullException(nameof(approximation));
    }
}
