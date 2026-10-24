namespace HeatingMold.Core.Geometry;

/// <summary>기하 연산의 목적별 허용오차와 작업 한도를 보관합니다.</summary>
public sealed record GeometryToleranceProfile
{
    /// <summary>인접 연결 판정에 사용하는 거리 공차(mm)입니다.</summary>
    public double ConnectionToleranceMm { get; }

    /// <summary>표면 메시 생성 목표 편차(mm)입니다.</summary>
    public double MeshDeflectionMm { get; }

    /// <summary>표면 메시 생성 목표 각도(rad)입니다.</summary>
    public double MeshAngularToleranceRad { get; }

    /// <summary>평면 경로 매핑의 최대 leaf 길이(mm)입니다.</summary>
    public double MappingLeafLengthMm { get; }

    /// <summary>경로 매핑에서 허용하는 현 편차(mm)입니다.</summary>
    public double MappingChordDeviationMm { get; }

    /// <summary>경로 매핑의 최대 재귀 깊이입니다.</summary>
    public int MappingMaximumDepth { get; }

    /// <summary>경로 매핑 한 번에 허용하는 최대 질의 수입니다.</summary>
    public int MappingMaximumProbes { get; }

    /// <summary>연속 경로 샘플 사이에 허용하는 최대 간격(mm)입니다.</summary>
    public double MaximumContinuousSampleSpacingMm { get; }

    /// <summary>모듈 설계에서 합의한 초기 기하 설정입니다.</summary>
    public static GeometryToleranceProfile Initial { get; } = new(
        connectionToleranceMm: 0.01,
        meshDeflectionMm: 0.05,
        meshAngularToleranceRad: 0.1,
        mappingLeafLengthMm: 0.1,
        mappingChordDeviationMm: 0.05,
        mappingMaximumDepth: 18,
        mappingMaximumProbes: 100_000,
        maximumContinuousSampleSpacingMm: 2.0);

    /// <summary>모든 거리 공차는 양수이고 작업 한도도 유효한 설정을 만듭니다.</summary>
    public GeometryToleranceProfile(
        double connectionToleranceMm,
        double meshDeflectionMm,
        double meshAngularToleranceRad,
        double mappingLeafLengthMm,
        double mappingChordDeviationMm,
        int mappingMaximumDepth,
        int mappingMaximumProbes,
        double maximumContinuousSampleSpacingMm)
    {
        ConnectionToleranceMm = RequirePositiveFinite(connectionToleranceMm, nameof(connectionToleranceMm));
        MeshDeflectionMm = RequirePositiveFinite(meshDeflectionMm, nameof(meshDeflectionMm));
        MeshAngularToleranceRad = RequirePositiveFinite(meshAngularToleranceRad, nameof(meshAngularToleranceRad));
        MappingLeafLengthMm = RequirePositiveFinite(mappingLeafLengthMm, nameof(mappingLeafLengthMm));
        MappingChordDeviationMm = RequirePositiveFinite(mappingChordDeviationMm, nameof(mappingChordDeviationMm));
        MaximumContinuousSampleSpacingMm = RequirePositiveFinite(maximumContinuousSampleSpacingMm, nameof(maximumContinuousSampleSpacingMm));

        if (mappingMaximumDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(mappingMaximumDepth), "최대 재귀 깊이는 1 이상이어야 합니다.");
        if (mappingMaximumProbes < 2)
            throw new ArgumentOutOfRangeException(nameof(mappingMaximumProbes), "질의 한도는 경로의 최소 두 점 이상이어야 합니다.");

        MappingMaximumDepth = mappingMaximumDepth;
        MappingMaximumProbes = mappingMaximumProbes;
    }

    private static double RequirePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "기하 공차는 양의 유한값이어야 합니다.");

        return value;
    }
}
