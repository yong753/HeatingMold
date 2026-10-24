using System.Collections.ObjectModel;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Geometry;

/// <summary>Native가 한 곡선을 만들고 측정한 결과를 원 정의와 함께 보관합니다.</summary>
public sealed class CurveGeometrySnapshot
{
    /// <summary>발열 경로 곡선이면 해당 구간 ID입니다.</summary>
    public HeatingPathSegmentId? HeatingPathSegmentId { get; }

    /// <summary>연결 경로 곡선이면 해당 구간 ID입니다.</summary>
    public ConnectionPathSegmentId? ConnectionPathSegmentId { get; }

    /// <summary>재생성할 때 사용할 원본 수학 곡선 정의입니다.</summary>
    public CurveDefinition Definition { get; }

    /// <summary>Native 기하에서 측정한 최종 3차원 길이(mm)입니다.</summary>
    public LengthMm LengthMm { get; }

    /// <summary>최종 곡선 진행 방향의 시작점입니다.</summary>
    public Vec3 Start { get; }

    /// <summary>최종 곡선 진행 방향의 끝점입니다.</summary>
    public Vec3 End { get; }

    /// <summary>표시와 근사 검사에 사용하는 순서 있는 표본입니다.</summary>
    public IReadOnlyList<Vec3> Samples { get; }

    /// <summary>표본의 근사 설정과 실제 최대 편차를 보관합니다.</summary>
    public CurveApproximationInfo Approximation { get; }

    /// <summary>정확히 하나의 구간 ID와 유효한 최종 기하 측정값으로 스냅샷을 만듭니다.</summary>
    public CurveGeometrySnapshot(
        HeatingPathSegmentId? heatingPathSegmentId,
        ConnectionPathSegmentId? connectionPathSegmentId,
        CurveDefinition definition,
        LengthMm lengthMm,
        Vec3 start,
        Vec3 end,
        IEnumerable<Vec3> samples,
        CurveApproximationInfo approximation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (heatingPathSegmentId.HasValue == connectionPathSegmentId.HasValue)
            throw new ArgumentException("곡선 스냅샷에는 발열 구간 또는 연결 구간 ID 하나만 필요합니다.");
        if (heatingPathSegmentId is { IsEmpty: true } || connectionPathSegmentId is { IsEmpty: true })
            throw new ArgumentException("곡선 스냅샷의 구간 ID는 비어 있을 수 없습니다.");
        if (lengthMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(lengthMm), "최종 곡선 길이는 양수여야 합니다.");

        Vec3[] copiedSamples = samples?.ToArray() ?? throw new ArgumentNullException(nameof(samples));
        if (copiedSamples.Length < 2)
            throw new ArgumentException("곡선 스냅샷에는 두 개 이상의 표본이 필요합니다.", nameof(samples));
        if (copiedSamples[0] != start || copiedSamples[^1] != end)
            throw new ArgumentException("곡선 표본의 첫 점과 마지막 점은 측정된 시작점·끝점과 같아야 합니다.", nameof(samples));

        HeatingPathSegmentId = heatingPathSegmentId;
        ConnectionPathSegmentId = connectionPathSegmentId;
        Definition = definition;
        LengthMm = lengthMm;
        Start = start;
        End = end;
        Samples = new ReadOnlyCollection<Vec3>(copiedSamples);
        Approximation = approximation ?? throw new ArgumentNullException(nameof(approximation));
    }
}

/// <summary>곡선 표본의 설정·측정 편차·완전성을 나타냅니다.</summary>
public sealed record CurveApproximationInfo
{
    /// <summary>메시 또는 샘플 생성에 요청한 편차 한도(mm)입니다.</summary>
    public double RequestedDeflectionMm { get; }

    /// <summary>메시 또는 샘플 생성에 요청한 각도 한도(rad)입니다.</summary>
    public double RequestedAngularToleranceRad { get; }

    /// <summary>측정한 최대 현 편차(mm)입니다.</summary>
    public double MaximumMeasuredDeviationMm { get; }

    /// <summary>연속 표본 사이에서 측정한 최대 간격(mm)입니다.</summary>
    public double MaximumSampleSpacingMm { get; }

    /// <summary>질의한 표본 또는 측정 수입니다.</summary>
    public int ProbeCount { get; }

    /// <summary>근사·측정 절차가 모든 작업 한도를 지켰는지 나타냅니다.</summary>
    public bool IsComplete { get; }

    /// <summary>근사 규칙 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>유한한 비음수 오차와 작업 증거를 보관합니다.</summary>
    public CurveApproximationInfo(
        double requestedDeflectionMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double maximumSampleSpacingMm,
        int probeCount,
        bool isComplete,
        string ruleVersion)
    {
        RequestedDeflectionMm = RequireNonNegativeFinite(requestedDeflectionMm, nameof(requestedDeflectionMm));
        RequestedAngularToleranceRad = RequireNonNegativeFinite(requestedAngularToleranceRad, nameof(requestedAngularToleranceRad));
        MaximumMeasuredDeviationMm = RequireNonNegativeFinite(maximumMeasuredDeviationMm, nameof(maximumMeasuredDeviationMm));
        MaximumSampleSpacingMm = RequireNonNegativeFinite(maximumSampleSpacingMm, nameof(maximumSampleSpacingMm));
        if (probeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(probeCount));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("근사 규칙 버전이 필요합니다.", nameof(ruleVersion));

        ProbeCount = probeCount;
        IsComplete = isComplete;
        RuleVersion = ruleVersion.Trim();
    }

    private static double RequireNonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "근사 수치는 유한한 0 이상이어야 합니다.");

        return value;
    }
}

/// <summary>열린 Native 문서에서 만든 곡선 측정값과 의존 지문을 묶습니다.</summary>
public sealed class GeometrySnapshot
{
    /// <summary>이 스냅샷을 생성한 런타임 문서 식별자입니다.</summary>
    public GeometryDocumentId DocumentId { get; }

    /// <summary>스냅샷이 가리키는 네이티브 모델 기하 세대입니다.</summary>
    public ModelRevision ModelRevision { get; }

    /// <summary>곡선 전체가 공유하는 설계 입력과 규칙 지문입니다.</summary>
    public DependencyStamp DependencyStamp { get; }

    /// <summary>경로 구간별 최종 Native 곡선 측정 결과입니다.</summary>
    public IReadOnlyList<CurveGeometrySnapshot> Curves { get; }

    /// <summary>중복 구간이 없는 읽기 전용 곡선 결과 스냅샷을 만듭니다.</summary>
    public GeometrySnapshot(
        GeometryDocumentId documentId,
        ModelRevision modelRevision,
        DependencyStamp dependencyStamp,
        IEnumerable<CurveGeometrySnapshot> curves)
    {
        if (documentId.IsEmpty)
            throw new ArgumentException("기하 스냅샷에는 유효한 문서 ID가 필요합니다.", nameof(documentId));
        ArgumentNullException.ThrowIfNull(dependencyStamp);

        CurveGeometrySnapshot[] copiedCurves = curves?.ToArray()
            ?? throw new ArgumentNullException(nameof(curves));
        HashSet<HeatingPathSegmentId> heatingIds = [];
        HashSet<ConnectionPathSegmentId> connectionIds = [];
        foreach (CurveGeometrySnapshot curve in copiedCurves)
        {
            if (curve.HeatingPathSegmentId is { } heatingId && !heatingIds.Add(heatingId))
                throw new ArgumentException("발열 경로 구간의 곡선 측정 결과가 중복되었습니다.", nameof(curves));
            if (curve.ConnectionPathSegmentId is { } connectionId && !connectionIds.Add(connectionId))
                throw new ArgumentException("연결 경로 구간의 곡선 측정 결과가 중복되었습니다.", nameof(curves));
        }

        DocumentId = documentId;
        ModelRevision = modelRevision;
        DependencyStamp = dependencyStamp;
        Curves = new ReadOnlyCollection<CurveGeometrySnapshot>(copiedCurves);
    }
}
