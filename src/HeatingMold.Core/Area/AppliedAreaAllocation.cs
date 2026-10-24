using System.Collections.ObjectModel;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Area;

/// <summary>표면 분할과 면적 배정의 오차·수렴 근거입니다.</summary>
public sealed record AreaAllocationQuality
{
    /// <summary>분할에 사용한 최대 메시 편차(mm)입니다.</summary>
    public double MeshDeflectionMm { get; }

    /// <summary>분할에 사용한 최대 각도 공차(rad)입니다.</summary>
    public double AngularToleranceRad { get; }

    /// <summary>분할 요소의 최대 작업 면적(mm²)입니다.</summary>
    public double MaximumElementAreaMm2 { get; }

    /// <summary>총 실면적 측정의 상한 오차(mm²)입니다.</summary>
    public double MeasuredAreaErrorBoundMm2 { get; }

    /// <summary>배정 면적 보존을 판단하는 허용 잔차(mm²)입니다.</summary>
    public double ConservationToleranceMm2 { get; }

    /// <summary>분할과 경계 동률 재평가 횟수입니다.</summary>
    public int IterationCount { get; }

    /// <summary>분할 오차와 면적 보존 목표를 만족했는지 나타냅니다.</summary>
    public bool IsConverged { get; }

    /// <summary>면적 분할·배정 규칙의 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>유한한 분할 설정과 수렴 증거를 기록합니다.</summary>
    public AreaAllocationQuality(
        double meshDeflectionMm,
        double angularToleranceRad,
        double maximumElementAreaMm2,
        double measuredAreaErrorBoundMm2,
        double conservationToleranceMm2,
        int iterationCount,
        bool isConverged,
        string ruleVersion)
    {
        MeshDeflectionMm = RequirePositiveFinite(meshDeflectionMm, nameof(meshDeflectionMm));
        AngularToleranceRad = RequirePositiveFinite(angularToleranceRad, nameof(angularToleranceRad));
        MaximumElementAreaMm2 = RequirePositiveFinite(maximumElementAreaMm2, nameof(maximumElementAreaMm2));
        MeasuredAreaErrorBoundMm2 = RequireNonNegativeFinite(measuredAreaErrorBoundMm2, nameof(measuredAreaErrorBoundMm2));
        ConservationToleranceMm2 = RequirePositiveFinite(conservationToleranceMm2, nameof(conservationToleranceMm2));
        if (iterationCount < 0)
            throw new ArgumentOutOfRangeException(nameof(iterationCount));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("면적 규칙 버전이 필요합니다.", nameof(ruleVersion));

        IterationCount = iterationCount;
        IsConverged = isConverged;
        RuleVersion = ruleVersion.Trim();
    }

    private static double RequirePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "면적 분할 설정은 양의 유한값이어야 합니다.");

        return value;
    }

    private static double RequireNonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "면적 오차는 비음수 유한값이어야 합니다.");

        return value;
    }
}

/// <summary>회로별 적용면적과 미배정 면적, 총 실면적 잔차를 보관합니다.</summary>
public sealed class AppliedAreaAllocation
{
    /// <summary>면적 분할에 사용한 설계·모델 입력 지문입니다.</summary>
    public DependencyStamp DependencyStamp { get; }

    /// <summary>대상 Face의 측정된 전체 유효 표면 면적(mm²)입니다.</summary>
    public AreaMm2 TotalEffectiveSurfaceAreaMm2 { get; }

    /// <summary>회로에 배정된 영역과 미배정 영역의 합계(mm²)입니다.</summary>
    public AreaMm2 TotalAppliedAreaMm2 { get; }

    /// <summary>회로별 표면 적용 면적(mm²)입니다.</summary>
    public IReadOnlyDictionary<CircuitId, AreaMm2> CircuitAppliedAreas { get; }

    /// <summary>가장 가까운 활성 회로에 배정되지 않은 유효 면적(mm²)입니다.</summary>
    public AreaMm2 UnassignedAppliedAreaMm2 { get; }

    /// <summary>전체 유효 실면적과 배정 합계의 차이(mm²)입니다.</summary>
    public double AreaResidualMm2 { get; }

    /// <summary>분할과 면적 보존의 품질 증거입니다.</summary>
    public AreaAllocationQuality Quality { get; }

    /// <summary>회로별 면적과 미배정 면적을 합산하고 잔차를 계산합니다.</summary>
    public AppliedAreaAllocation(
        DependencyStamp dependencyStamp,
        AreaMm2 totalEffectiveSurfaceAreaMm2,
        IEnumerable<KeyValuePair<CircuitId, AreaMm2>> circuitAppliedAreas,
        AreaMm2 unassignedAppliedAreaMm2,
        AreaAllocationQuality quality)
    {
        ArgumentNullException.ThrowIfNull(dependencyStamp);
        ArgumentNullException.ThrowIfNull(quality);
        if (totalEffectiveSurfaceAreaMm2.Value < 0 || unassignedAppliedAreaMm2.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(totalEffectiveSurfaceAreaMm2), "면적 값은 음수일 수 없습니다.");

        KeyValuePair<CircuitId, AreaMm2>[] copiedAreas = circuitAppliedAreas?.ToArray()
            ?? throw new ArgumentNullException(nameof(circuitAppliedAreas));
        if (copiedAreas.Any(pair => pair.Key.IsEmpty || pair.Value.Value < 0))
            throw new ArgumentException("회로별 면적에는 유효 ID와 비음수 면적이 필요합니다.", nameof(circuitAppliedAreas));
        if (copiedAreas.Select(pair => pair.Key).Distinct().Count() != copiedAreas.Length)
            throw new ArgumentException("같은 회로 면적을 중복 지정할 수 없습니다.", nameof(circuitAppliedAreas));

        double totalApplied = unassignedAppliedAreaMm2.Value;
        foreach (KeyValuePair<CircuitId, AreaMm2> pair in copiedAreas)
        {
            totalApplied += pair.Value.Value;
            if (!double.IsFinite(totalApplied))
                throw new ArithmeticException("배정 면적 합계가 숫자 표현 범위를 벗어났습니다.");
        }

        double residual = totalEffectiveSurfaceAreaMm2.Value - totalApplied;
        if (!double.IsFinite(residual))
            throw new ArithmeticException("면적 잔차가 숫자 표현 범위를 벗어났습니다.");
        if (quality.IsConverged && Math.Abs(residual) > quality.ConservationToleranceMm2)
            throw new ArgumentException("수렴 완료된 면적 배정은 허용 잔차 안에서 전체 면적을 보존해야 합니다.", nameof(circuitAppliedAreas));

        var sortedAreas = copiedAreas.OrderBy(pair => pair.Key.Value).ToDictionary(pair => pair.Key, pair => pair.Value);
        DependencyStamp = dependencyStamp;
        TotalEffectiveSurfaceAreaMm2 = totalEffectiveSurfaceAreaMm2;
        TotalAppliedAreaMm2 = new AreaMm2(totalApplied);
        CircuitAppliedAreas = new ReadOnlyDictionary<CircuitId, AreaMm2>(sortedAreas);
        UnassignedAppliedAreaMm2 = unassignedAppliedAreaMm2;
        AreaResidualMm2 = residual;
        Quality = quality;
    }
}
