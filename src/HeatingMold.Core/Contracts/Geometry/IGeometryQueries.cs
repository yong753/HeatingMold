using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Core.Contracts.Geometry;

/// <summary>Native 기하 질의의 종료 상태를 구분합니다.</summary>
public enum GeometryQueryStatus
{
    /// <summary>요청한 유효 기하 결과를 얻었습니다.</summary>
    Success,

    /// <summary>질의 광선에 표면이 없습니다.</summary>
    Miss,

    /// <summary>광선이 표면에 접하여 깊이 방향을 정할 수 없습니다.</summary>
    Tangent,

    /// <summary>열린 모델 또는 좌표계 revision이 바뀌었습니다.</summary>
    StaleGeometryContext,

    /// <summary>지원하지 않는 기하나 질의 처리 실패입니다.</summary>
    Failure,

    /// <summary>근사 오차를 줄였지만 최종 판정에 필요한 정확도가 없습니다.</summary>
    Unresolved
}

/// <summary>값이 없을 수 있는 Native 질의 결과와 표준 진단입니다.</summary>
public sealed class GeometryQueryResult<T>
{
    /// <summary>질의의 성공·실패·미확정 상태입니다.</summary>
    public GeometryQueryStatus Status { get; }

    /// <summary>성공한 질의에서만 제공하는 값입니다.</summary>
    public T? Value { get; }

    /// <summary>상태가 Success여서 Value가 실제 결과값임을 나타냅니다.</summary>
    public bool HasValue => Status == GeometryQueryStatus.Success;

    /// <summary>질의 상태를 설명하는 진단 목록입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>허용 상태와 값 존재 여부를 확인해 질의 결과를 만듭니다.</summary>
    public GeometryQueryResult(
        GeometryQueryStatus status,
        T? value,
        IEnumerable<ValidationIssue>? issues = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        ValidationIssue[] copiedIssues = issues?.ToArray() ?? Array.Empty<ValidationIssue>();
        if (status == GeometryQueryStatus.Success && value is null)
            throw new ArgumentException("성공한 기하 질의에는 결과값이 필요합니다.", nameof(value));
        if (status != GeometryQueryStatus.Success &&
            !EqualityComparer<T?>.Default.Equals(value, default))
        {
            throw new ArgumentException("실패·미확정 기하 질의는 성공값을 포함할 수 없습니다.", nameof(value));
        }
        if (copiedIssues.Any(issue => issue is null))
            throw new ArgumentException("질의 진단은 null 항목을 포함할 수 없습니다.", nameof(issues));

        Status = status;
        Value = value;
        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
    }

    /// <summary>유효한 결과값을 가진 질의 성공을 만듭니다.</summary>
    public static GeometryQueryResult<T> Succeeded(T value) => new(GeometryQueryStatus.Success, value);

    /// <summary>정상적인 기하 miss를 빈 값과 함께 돌려줍니다.</summary>
    public static GeometryQueryResult<T> Miss() => new(GeometryQueryStatus.Miss, default);

    /// <summary>지원·기하 한도 문제로 결정하지 못한 결과를 만듭니다.</summary>
    public static GeometryQueryResult<T> Unresolved(IEnumerable<ValidationIssue> issues) =>
        new(GeometryQueryStatus.Unresolved, default, issues);
}

/// <summary>선택 Face와 실제 first-hit 표면의 점·법선·UV입니다.</summary>
public sealed record SurfaceHit
{
    /// <summary>실제로 first-hit한 안정 Face 참조 ID입니다.</summary>
    public FaceRefId FaceReferenceId { get; }

    /// <summary>실제 표면 교차 모델 좌표(mm)입니다.</summary>
    public Vec3 ModelPoint { get; }

    /// <summary>교차점의 단위 표면 법선입니다.</summary>
    public Vec3 SurfaceNormal { get; }

    /// <summary>교차점의 Face 고유 매개변수 좌표입니다.</summary>
    public Vec2 FaceUv { get; }

    /// <summary>실제 표면에서 first-hit 결과를 만듭니다.</summary>
    public SurfaceHit(FaceRefId faceReferenceId, Vec3 modelPoint, Vec3 surfaceNormal, Vec2 faceUv)
    {
        if (faceReferenceId.IsEmpty)
            throw new ArgumentException("표면 hit에 안정 Face 참조가 필요합니다.", nameof(faceReferenceId));
        if (Math.Abs(surfaceNormal.Length - 1) > 1e-9)
            throw new ArgumentException("표면 법선은 단위 벡터여야 합니다.", nameof(surfaceNormal));

        FaceReferenceId = faceReferenceId;
        ModelPoint = modelPoint;
        SurfaceNormal = surfaceNormal;
        FaceUv = faceUv;
    }
}

/// <summary>선택한 Face 집합의 연결 성분과 실제 경계 루프입니다.</summary>
public sealed class SelectedTopology
{
    /// <summary>연결 성분별 Face와 경계입니다.</summary>
    public IReadOnlyList<TopologyComponent> Components { get; }

    /// <summary>선택 topology에서 확인한 공유 Edge 기반 인접 쌍입니다.</summary>
    public IReadOnlyList<FaceAdjacency> Adjacencies { get; }

    /// <summary>이 topology 질의를 수행한 열린 모델 revision입니다.</summary>
    public ModelRevision? ModelRevision { get; }

    /// <summary>성분·인접 정보와 선택적으로 열린 모델 revision을 보관합니다.</summary>
    public SelectedTopology(
        IEnumerable<TopologyComponent> components,
        IEnumerable<FaceAdjacency> adjacencies,
        ModelRevision? modelRevision = null)
    {
        Components = Copy(components, nameof(components));
        Adjacencies = Copy(adjacencies, nameof(adjacencies));
        ModelRevision = modelRevision;
    }

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, string parameterName)
    {
        T[] copied = values?.ToArray() ?? throw new ArgumentNullException(parameterName);
        return new ReadOnlyCollection<T>(copied);
    }
}

/// <summary>연결된 선택 면 그룹의 영속 Face와 바깥·홀 루프입니다.</summary>
public sealed class TopologyComponent
{
    /// <summary>연결 성분을 이루는 원본 면 참조입니다.</summary>
    public IReadOnlyList<FaceRefId> FaceReferences { get; }

    /// <summary>연결 성분의 외곽·홀 경계 루프입니다.</summary>
    public IReadOnlyList<BoundaryLoop> BoundaryLoops { get; }

    /// <summary>중복 없는 Face와 해당 성분 경계를 방어 복사합니다.</summary>
    public TopologyComponent(IEnumerable<FaceRefId> faceReferences, IEnumerable<BoundaryLoop> boundaryLoops)
    {
        FaceRefId[] faces = faceReferences?.ToArray() ?? throw new ArgumentNullException(nameof(faceReferences));
        BoundaryLoop[] loops = boundaryLoops?.ToArray() ?? throw new ArgumentNullException(nameof(boundaryLoops));
        if (faces.Length == 0 || faces.Any(face => face.IsEmpty) || faces.Distinct().Count() != faces.Length)
            throw new ArgumentException("연결 성분에는 유효한 Face 참조가 하나 이상 필요합니다.", nameof(faceReferences));

        FaceReferences = new ReadOnlyCollection<FaceRefId>(faces);
        BoundaryLoops = new ReadOnlyCollection<BoundaryLoop>(loops);
    }
}

/// <summary>실제 공유 경계 Edge로 인접하다고 확인한 면 쌍입니다.</summary>
public readonly record struct FaceAdjacency
{
    /// <summary>공유 Edge의 첫 번째 Face입니다.</summary>
    public FaceRefId First { get; }

    /// <summary>공유 Edge의 두 번째 Face입니다.</summary>
    public FaceRefId Second { get; }

    /// <summary>서로 다른 두 유효 Face의 인접 관계를 만듭니다.</summary>
    public FaceAdjacency(FaceRefId first, FaceRefId second)
    {
        if (first.IsEmpty || second.IsEmpty || first == second)
            throw new ArgumentException("인접 관계에는 서로 다른 유효 Face 참조가 필요합니다.");

        First = first;
        Second = second;
    }
}

/// <summary>최단 중심선 거리, 실제 간격, 위치와 오차 범위입니다.</summary>
public sealed record ClearanceMeasurement
{
    /// <summary>도체 중심선 사이 최소 거리(mm)입니다.</summary>
    public LengthMm MinimumCenterlineDistanceMm { get; }

    /// <summary>두 도체 반지름을 뺀 최소 외곽 간격(mm)입니다.</summary>
    public LengthMm MinimumClearGapMm { get; }

    /// <summary>최단거리가 계산된 첫 곡선의 위치입니다.</summary>
    public Vec3 FirstLocation { get; }

    /// <summary>최단거리가 계산된 두 번째 곡선의 위치입니다.</summary>
    public Vec3 SecondLocation { get; }

    /// <summary>최단거리 결과의 최대 오차 추정치(mm)입니다.</summary>
    public LengthMm ErrorBoundMm { get; }

    /// <summary>모든 비국소 곡선 구간을 정밀 판정했는지 나타냅니다.</summary>
    public bool IsComplete { get; }

    /// <summary>중심선·외곽 거리 관계, 측정 위치와 정밀도 증거를 보관합니다.</summary>
    public ClearanceMeasurement(
        LengthMm minimumCenterlineDistanceMm,
        LengthMm minimumClearGapMm,
        Vec3 firstLocation,
        Vec3 secondLocation,
        LengthMm errorBoundMm,
        bool isComplete)
    {
        if (minimumCenterlineDistanceMm.Value < 0 || errorBoundMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumCenterlineDistanceMm));
        if (minimumClearGapMm.Value > minimumCenterlineDistanceMm.Value)
            throw new ArgumentOutOfRangeException(
                nameof(minimumClearGapMm),
                "도체 외곽 간격은 중심선 거리보다 클 수 없습니다.");

        MinimumCenterlineDistanceMm = minimumCenterlineDistanceMm;
        MinimumClearGapMm = minimumClearGapMm;
        FirstLocation = firstLocation;
        SecondLocation = secondLocation;
        ErrorBoundMm = errorBoundMm;
        IsComplete = isComplete;
    }
}

/// <summary>실제 표면과 와이어 바깥면 사이 축방향 깊이 측정입니다.</summary>
public sealed record DepthMeasurement
{
    /// <summary>요청한 축방향 삽입 깊이(mm)입니다.</summary>
    public LengthMm RequiredInsertionDepthMm { get; }

    /// <summary>단면 깊이 또는 전체 envelope의 중간 대표값(mm)입니다.</summary>
    public LengthMm MeasuredInsertionDepthMm { get; }

    /// <summary>측정 결과의 최대 오차 추정치(mm)입니다.</summary>
    public LengthMm ErrorBoundMm { get; }

    /// <summary>측정 기준 표면 점입니다.</summary>
    public Vec3 SurfacePoint { get; }

    /// <summary>측정 기준 도체 바깥면 점입니다.</summary>
    public Vec3 WireOutsidePoint { get; }

    /// <summary>곡선 전체에서 측정한 최소 삽입 깊이(mm)입니다.</summary>
    public LengthMm MinimumMeasuredInsertionDepthMm { get; }

    /// <summary>곡선 전체에서 측정한 최대 삽입 깊이(mm)입니다.</summary>
    public LengthMm MaximumMeasuredInsertionDepthMm { get; }

    /// <summary>최소 깊이가 측정된 도체 바깥면 위치입니다.</summary>
    public Vec3 MinimumDepthWireOutsidePoint { get; }

    /// <summary>최대 깊이가 측정된 도체 바깥면 위치입니다.</summary>
    public Vec3 MaximumDepthWireOutsidePoint { get; }

    /// <summary>최소 깊이 측정의 표면 교점입니다.</summary>
    public Vec3 MinimumDepthSurfacePoint { get; }

    /// <summary>최대 깊이 측정의 표면 교점입니다.</summary>
    public Vec3 MaximumDepthSurfacePoint { get; }

    /// <summary>깊이 envelope를 확인한 표본 수입니다.</summary>
    public int ProbeCount { get; }

    /// <summary>측정 절차가 정해진 작업 한도 안에서 끝났는지 나타냅니다.</summary>
    public bool IsComplete { get; }

    /// <summary>최소·최대값이 곡선 전체 표본을 측정한 envelope인지 나타냅니다.</summary>
    public bool IsCurveEnvelope { get; }

    /// <summary>깊이 envelope 측정 규칙 버전입니다.</summary>
    public string MeasurementRuleVersion { get; }

    /// <summary>지원 범위와 허용 오차 안에서 측정했는지 나타냅니다.</summary>
    public bool IsResolved { get; }

    /// <summary>비음수 깊이와 오차, 표면 교점 정보를 보관합니다.</summary>
    public DepthMeasurement(
        LengthMm requiredInsertionDepthMm,
        LengthMm measuredInsertionDepthMm,
        LengthMm errorBoundMm,
        Vec3 surfacePoint,
        Vec3 wireOutsidePoint,
        bool isResolved)
        : this(
            requiredInsertionDepthMm,
            measuredInsertionDepthMm,
            measuredInsertionDepthMm,
            measuredInsertionDepthMm,
            errorBoundMm,
            surfacePoint,
            surfacePoint,
            surfacePoint,
            wireOutsidePoint,
            wireOutsidePoint,
            probeCount: 1,
            isResolved,
            isComplete: true,
            measurementRuleVersion: "single-section-depth-v1",
            isCurveEnvelope: false)
    {
    }

    /// <summary>곡선 전체 깊이 범위와 최소·최대 측정 위치를 함께 보관합니다.</summary>
    public DepthMeasurement(
        LengthMm requiredInsertionDepthMm,
        LengthMm measuredInsertionDepthMm,
        LengthMm minimumMeasuredInsertionDepthMm,
        LengthMm maximumMeasuredInsertionDepthMm,
        LengthMm errorBoundMm,
        Vec3 surfacePoint,
        Vec3 minimumDepthSurfacePoint,
        Vec3 maximumDepthSurfacePoint,
        Vec3 minimumDepthWireOutsidePoint,
        Vec3 maximumDepthWireOutsidePoint,
        int probeCount,
        bool isResolved,
        bool isComplete,
        string measurementRuleVersion,
        bool isCurveEnvelope = true)
    {
        if (requiredInsertionDepthMm.Value < 0 || measuredInsertionDepthMm.Value < 0 ||
            minimumMeasuredInsertionDepthMm.Value < 0 || maximumMeasuredInsertionDepthMm.Value < 0 ||
            errorBoundMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredInsertionDepthMm));
        if (minimumMeasuredInsertionDepthMm.Value > maximumMeasuredInsertionDepthMm.Value ||
            measuredInsertionDepthMm.Value < minimumMeasuredInsertionDepthMm.Value ||
            measuredInsertionDepthMm.Value > maximumMeasuredInsertionDepthMm.Value)
        {
            throw new ArgumentException("측정 깊이는 최소·최대 범위 안에 있어야 합니다.", nameof(measuredInsertionDepthMm));
        }
        if (probeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(probeCount));
        if (string.IsNullOrWhiteSpace(measurementRuleVersion))
            throw new ArgumentException("깊이 측정 규칙 버전이 필요합니다.", nameof(measurementRuleVersion));

        RequiredInsertionDepthMm = requiredInsertionDepthMm;
        MeasuredInsertionDepthMm = measuredInsertionDepthMm;
        MinimumMeasuredInsertionDepthMm = minimumMeasuredInsertionDepthMm;
        MaximumMeasuredInsertionDepthMm = maximumMeasuredInsertionDepthMm;
        ErrorBoundMm = errorBoundMm;
        SurfacePoint = surfacePoint;
        MinimumDepthSurfacePoint = minimumDepthSurfacePoint;
        MaximumDepthSurfacePoint = maximumDepthSurfacePoint;
        MinimumDepthWireOutsidePoint = minimumDepthWireOutsidePoint;
        MaximumDepthWireOutsidePoint = maximumDepthWireOutsidePoint;
        WireOutsidePoint = Math.Abs(measuredInsertionDepthMm.Value - minimumMeasuredInsertionDepthMm.Value) >=
                           Math.Abs(maximumMeasuredInsertionDepthMm.Value - measuredInsertionDepthMm.Value)
            ? minimumDepthWireOutsidePoint
            : maximumDepthWireOutsidePoint;
        ProbeCount = probeCount;
        IsResolved = isResolved;
        IsComplete = isComplete;
        IsCurveEnvelope = isCurveEnvelope;
        MeasurementRuleVersion = measurementRuleVersion.Trim();
    }
}

/// <summary>실제 trimmed 표면을 분할한 면적 요소 하나입니다.</summary>
public sealed class SurfaceElement
{
    /// <summary>분할 결과 안에서만 쓰는 요소 번호입니다.</summary>
    public int ElementIndex { get; }

    /// <summary>요소가 위치한 안정 Face 참조 ID입니다.</summary>
    public FaceRefId FaceReferenceId { get; }

    /// <summary>실제 표면에서 측정한 유한 요소 면적(mm²)입니다.</summary>
    public AreaMm2 SurfaceAreaMm2 { get; }

    /// <summary>요소의 모델 좌표 중심점입니다.</summary>
    public Vec3 ModelCentroid { get; }

    /// <summary>원본 Face의 매개변수 중심점입니다.</summary>
    public Vec2 FaceUvCentroid { get; }

    /// <summary>확정 작업 평면의 투영 중심점입니다.</summary>
    public Vec2 WorkingUvCentroid { get; }

    /// <summary>실제 표면 요소를 근사한 모델 좌표 정점입니다.</summary>
    public IReadOnlyList<Vec3> ModelVertices { get; }

    /// <summary>요소 분할의 오차와 완전성 증거입니다.</summary>
    public CurveApproximationInfo Approximation { get; }

    /// <summary>Face와 표면 정점, 양의 실면적을 확인해 요소를 만듭니다.</summary>
    public SurfaceElement(
        int elementIndex,
        FaceRefId faceReferenceId,
        AreaMm2 surfaceAreaMm2,
        Vec3 modelCentroid,
        Vec2 faceUvCentroid,
        Vec2 workingUvCentroid,
        IEnumerable<Vec3> modelVertices,
        CurveApproximationInfo approximation)
    {
        if (elementIndex < 0 || faceReferenceId.IsEmpty || surfaceAreaMm2.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(elementIndex), "표면 요소 ID와 면적은 유효해야 합니다.");
        Vec3[] copiedVertices = modelVertices?.ToArray() ?? throw new ArgumentNullException(nameof(modelVertices));
        if (copiedVertices.Length < 3)
            throw new ArgumentException("표면 요소에는 세 개 이상의 모델 정점이 필요합니다.", nameof(modelVertices));

        ElementIndex = elementIndex;
        FaceReferenceId = faceReferenceId;
        SurfaceAreaMm2 = surfaceAreaMm2;
        ModelCentroid = modelCentroid;
        FaceUvCentroid = faceUvCentroid;
        WorkingUvCentroid = workingUvCentroid;
        ModelVertices = new ReadOnlyCollection<Vec3>(copiedVertices);
        Approximation = approximation ?? throw new ArgumentNullException(nameof(approximation));
    }
}

/// <summary>실제 표면 요소와 실면적 합계를 보관합니다.</summary>
public sealed class SurfaceElementSet
{
    /// <summary>실제 trimmed Face에서 만든 읽기 전용 요소 목록입니다.</summary>
    public IReadOnlyList<SurfaceElement> Elements { get; }

    /// <summary>요소 합계로 계산한 유효 표면적(mm²)입니다.</summary>
    public AreaMm2 TotalSurfaceAreaMm2 { get; }

    /// <summary>실제 trimmed 면적 합계의 최대 측정 오차(mm²)입니다.</summary>
    public AreaMm2 AreaErrorBoundMm2 { get; }

    /// <summary>중복 없는 요소 인덱스와 측정 면적·명시된 오차 상한을 보관합니다.</summary>
    public SurfaceElementSet(IEnumerable<SurfaceElement> elements, AreaMm2 areaErrorBoundMm2)
    {
        SurfaceElement[] copied = elements?.ToArray() ?? throw new ArgumentNullException(nameof(elements));
        if (copied.Select(element => element.ElementIndex).Distinct().Count() != copied.Length)
            throw new ArgumentException("표면 요소 인덱스는 중복될 수 없습니다.", nameof(elements));
        if (!double.IsFinite(areaErrorBoundMm2.Value) || areaErrorBoundMm2.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(areaErrorBoundMm2));

        double total = copied.Sum(element => element.SurfaceAreaMm2.Value);
        if (!double.IsFinite(total))
            throw new ArithmeticException("실제 표면적 합계가 숫자 표현 범위를 벗어났습니다.");

        Elements = new ReadOnlyCollection<SurfaceElement>(copied);
        TotalSurfaceAreaMm2 = new AreaMm2(total);
        AreaErrorBoundMm2 = areaErrorBoundMm2;
    }
}

/// <summary>연결 경로가 금형을 재관통하는지 확인한 결과입니다.</summary>
public sealed record ConnectionObstacleCheck
{
    /// <summary>경로가 금형 충돌 없이 외부 연결점까지 이어지는지 나타냅니다.</summary>
    public bool IsClear { get; }

    /// <summary>충돌이 있으면 처음 확인한 모델 좌표입니다.</summary>
    public Vec3? FirstObstruction { get; }

    /// <summary>경로 시작점부터 충돌점까지의 거리입니다.</summary>
    public LengthMm? DistanceToObstruction { get; }

    /// <summary>금형 위에서 외부로 나가는 명시 전이를 확인했는지 나타냅니다.</summary>
    public bool MoldExitTransitionFound { get; }

    /// <summary>충돌과 금형 이탈 전이 결과를 모순 없이 기록합니다.</summary>
    public ConnectionObstacleCheck(
        bool isClear,
        Vec3? firstObstruction,
        LengthMm? distanceToObstruction,
        bool moldExitTransitionFound)
    {
        if (distanceToObstruction.HasValue && distanceToObstruction.Value.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(distanceToObstruction));
        if (isClear && (firstObstruction.HasValue || distanceToObstruction.HasValue || !moldExitTransitionFound))
            throw new ArgumentException("통과한 연결 경로에는 충돌이 없고 금형 이탈 전이가 있어야 합니다.");
        if (!isClear && firstObstruction.HasValue != distanceToObstruction.HasValue)
            throw new ArgumentException("충돌 결과에는 위치와 시작점부터 거리를 함께 기록해야 합니다.");

        IsClear = isClear;
        FirstObstruction = firstObstruction;
        DistanceToObstruction = distanceToObstruction;
        MoldExitTransitionFound = moldExitTransitionFound;
    }
}

/// <summary>모델 세션에 바인딩된 읽기 전용 Native 기하 질의 계약입니다.</summary>
public interface IGeometryQueries
{
    /// <summary>Native가 전체 모델 first-hit을 사용해 요구 삽입 깊이의 중심선을 계산합니다.</summary>
    GeometryQueryResult<InsertionDepthAdjustedPath> AdjustInsertionDepth(
        IReadOnlyList<Vec2> surfacePathWorkingSamples,
        IReadOnlyList<FaceRefId> selectedSurfaceFaces,
        SurfaceAnchor anchor,
        LengthMm wireDiameterMm,
        LengthMm requiredInsertionDepthMm,
        WorkingCoordinateSystem coordinateSystem,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surfacePathWorkingSamples);
        ArgumentNullException.ThrowIfNull(selectedSurfaceFaces);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(tolerances);

        Guid[] entityIds = selectedSurfaceFaces.Select(face => face.Value).ToArray();
        if (cancellationToken.IsCancellationRequested)
        {
            return GeometryQueryResult<InsertionDepthAdjustedPath>.Unresolved(
            [new ValidationIssue(
                "OperationCancelled",
                ValidationSeverity.Error,
                "Geometry.OperationCancelled",
                entityIds,
                "insertion-depth-adjustment-v1",
                evidence: ["삽입 깊이 보정 질의가 취소되었습니다."])]);
        }

        return GeometryQueryResult<InsertionDepthAdjustedPath>.Unresolved(
        [new ValidationIssue(
            "InsertionDepthAdjustmentUnsupported",
            ValidationSeverity.Error,
            "Geometry.InsertionDepthAdjustmentUnsupported",
            entityIds,
            "insertion-depth-adjustment-v1",
            evidence: ["이 기하 어댑터는 Native 삽입 깊이 경로 보정을 구현하지 않았습니다."])]);
    }

    /// <summary>전체 모델 first-hit가 선택 Face인지 판정합니다.</summary>
    GeometryQueryResult<SurfaceHit> QueryFirstHit(
        WorkingCoordinateSystem coordinateSystem,
        Vec2 workingUv,
        FaceRefId selectedFaceReferenceId,
        CancellationToken cancellationToken = default);

    /// <summary>선택 Face 집합을 허용 대상으로 삼아 전체 모델의 실제 first-hit을 한 번 질의합니다.</summary>
    GeometryQueryResult<SurfaceHit> QueryFirstHit(
        WorkingCoordinateSystem coordinateSystem,
        Vec2 workingUv,
        IReadOnlyList<FaceRefId> selectedFaceReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(selectedFaceReferences);
        FaceRefId[] faceIds = selectedFaceReferences.ToArray();
        if (faceIds.Length == 1)
            return QueryFirstHit(coordinateSystem, workingUv, faceIds[0], cancellationToken);

        return GeometryQueryResult<SurfaceHit>.Unresolved(
        [new ValidationIssue(
            "MultiFaceFirstHitNotSupported",
            ValidationSeverity.Error,
            "Geometry.MultiFaceFirstHitNotSupported",
            faceIds.Select(face => face.Value),
            "geometry-query-v1",
            datumUv: workingUv,
            evidence: ["이 기하 어댑터는 선택 Face 집합의 first-hit 질의를 구현하지 않았습니다."]) ]);
    }

    /// <summary>선택 Face 집합의 실제 공유 topology와 경계 성분을 구합니다.</summary>
    GeometryQueryResult<SelectedTopology> GetSelectedTopology(
        IReadOnlyList<FaceRefId> selectedFaceReferences,
        CancellationToken cancellationToken = default);

    /// <summary>두 Face가 실제 공유 Edge를 갖는지 질의합니다.</summary>
    GeometryQueryResult<bool> AreAdjacent(
        FaceRefId first,
        FaceRefId second,
        CancellationToken cancellationToken = default);

    /// <summary>모든 곡선 구간의 3차원 외곽 간격을 검사합니다.</summary>
    GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default);

    /// <summary>설계 topology가 명시한 연속 구간만 국소 접점으로 제외해 최소 간격을 검사합니다.</summary>
    GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        IReadOnlyCollection<CurveSegmentAdjacency> localAdjacencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localAdjacencies);
        if (localAdjacencies.Count == 0)
            return MeasureClearance(curves, wireDiameterMm, requiredClearGapMm, tolerances, cancellationToken);

        CurveSegmentAdjacency firstAdjacency = localAdjacencies.First();
        return GeometryQueryResult<ClearanceMeasurement>.Unresolved(
        [new ValidationIssue(
            "ClearanceAdjacencyUnsupported",
            ValidationSeverity.Error,
            "Geometry.ClearanceAdjacencyUnsupported",
            [firstAdjacency.FirstSegmentId.Value, firstAdjacency.SecondSegmentId.Value],
            "geometry-query-v1",
            evidence: ["이 기하 어댑터는 설계 구간 연결 관계를 반영한 간격 질의를 구현하지 않았습니다."]) ]);
    }

    /// <summary>최종 중심 곡선의 축방향 바깥면 삽입 깊이를 측정합니다.</summary>
    GeometryQueryResult<DepthMeasurement> MeasureDepth(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        CancellationToken cancellationToken = default);

    /// <summary>선택 Face 집합을 기준으로 최종 곡선 전체의 깊이 envelope를 측정합니다.</summary>
    GeometryQueryResult<DepthMeasurement> MeasureDepth(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        IReadOnlyList<FaceRefId> selectedSurfaceFaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(selectedSurfaceFaces);
        if (selectedSurfaceFaces.Count == 0 || !selectedSurfaceFaces.Contains(anchor.FaceReferenceId))
        {
            return GeometryQueryResult<DepthMeasurement>.Unresolved(
            [new ValidationIssue(
                "InvalidDepthSurfaceFaces",
                ValidationSeverity.Error,
                "Geometry.InvalidDepthSurfaceFaces",
                [anchor.FaceReferenceId.Value],
                "geometry-query-v1",
                evidence: ["깊이 질의 Face 집합은 앵커 Face를 포함해야 합니다."]) ]);
        }

        return GeometryQueryResult<DepthMeasurement>.Unresolved(
        [new ValidationIssue(
            "DepthEnvelopeUnsupported",
            ValidationSeverity.Error,
            "Geometry.DepthEnvelopeUnsupported",
            [anchor.FaceReferenceId.Value],
            "geometry-query-v1",
            evidence: ["이 기하 어댑터는 최종 곡선 전체의 삽입 깊이 envelope를 측정하지 않습니다."]) ]);
    }

    /// <summary>영역의 실제 trimmed 표면을 면적 요소로 분할합니다.</summary>
    GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(
        HeatingRegion region,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default);

    /// <summary>연결 경로 전체에서 금형 충돌과 외부 전이를 확인합니다.</summary>
    GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(
        ConnectionPath connectionPath,
        WorkingCoordinateSystem coordinateSystem,
        CancellationToken cancellationToken = default);
}
