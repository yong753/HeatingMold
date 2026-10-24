using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>OCCT 표면 요소 질의 상태입니다.</summary>
public enum OcctSurfaceElementQueryStatus
{
    /// <summary>요청한 실제 표면 영역을 삼각 요소로 나눴습니다.</summary>
    Success,

    /// <summary>작업 좌표계, 경계 또는 공차 입력이 유효하지 않습니다.</summary>
    InvalidInput,

    /// <summary>요청한 Native Face 번호가 현재 모델에 없습니다.</summary>
    InvalidFace,

    /// <summary>작업 좌표계가 현재 모델 revision과 다릅니다.</summary>
    StaleContext,

    /// <summary>유효 영역과 선택 표면이 겹치지 않거나 경계를 만들 수 없습니다.</summary>
    InvalidRegion,

    /// <summary>표면 분할이 설정된 요소 수 한도를 넘었습니다.</summary>
    ElementBudgetExceeded,

    /// <summary>OCCT 절단 또는 메시 계산이 완료되지 않았습니다.</summary>
    GeometryFailure,

    /// <summary>현재 어댑터가 모르는 상태를 반환했습니다.</summary>
    Unknown
}

/// <summary>작업 평면의 유효 영역을 이루는 외곽 또는 홀 경계입니다.</summary>
public sealed class OcctRegionLoop
{
    /// <summary>닫힌 경계의 중복 끝점을 제외한 작업 평면 정점입니다.</summary>
    public IReadOnlyList<Vec2> Vertices { get; }

    /// <summary>영역에서 제거할 홀인지 나타냅니다.</summary>
    public bool IsHole { get; }

    /// <summary>유한한 세 점 이상의 정점으로 영역 경계를 만듭니다.</summary>
    public OcctRegionLoop(IEnumerable<Vec2> vertices, bool isHole)
    {
        Vec2[] copiedVertices = vertices?.ToArray()
            ?? throw new ArgumentNullException(nameof(vertices));
        if (copiedVertices.Length < 3 || copiedVertices.Distinct().Count() < 3)
            throw new ArgumentException("영역 경계에는 서로 다른 정점이 세 개 이상 필요합니다.", nameof(vertices));

        Vertices = new ReadOnlyCollection<Vec2>(copiedVertices);
        IsHole = isHole;
    }
}

/// <summary>실제 표면 삼각 요소 하나의 Native 측정 자료입니다.</summary>
public sealed class OcctSurfaceElementData
{
    /// <summary>요소가 속한 현재 문서의 Native Face 번호입니다.</summary>
    public int NativeFaceIndex { get; }

    /// <summary>OCCT 표면 메시에서 계산한 삼각 요소 면적(mm²)입니다.</summary>
    public double SurfaceAreaMm2 { get; }

    /// <summary>삼각 요소의 모델 좌표 중심입니다.</summary>
    public Vec3 ModelCentroid { get; }

    /// <summary>원본 Face 매개변수 좌표의 중심입니다.</summary>
    public Vec2 FaceUvCentroid { get; }

    /// <summary>작업 평면에 투영한 요소 중심입니다.</summary>
    public Vec2 WorkingUvCentroid { get; }

    /// <summary>삼각 요소를 이루는 모델 좌표 정점입니다.</summary>
    public IReadOnlyList<Vec3> ModelVertices { get; }

    /// <summary>세 변의 중점과 중심에서 곡면과 삼각형 사이에 측정한 최대 편차입니다.</summary>
    public double MaximumMeasuredDeviationMm { get; }

    /// <summary>유효 Face, 삼각형 정점과 양의 면적을 확인합니다.</summary>
    public OcctSurfaceElementData(
        int nativeFaceIndex,
        double surfaceAreaMm2,
        Vec3 modelCentroid,
        Vec2 faceUvCentroid,
        Vec2 workingUvCentroid,
        IEnumerable<Vec3> modelVertices,
        double maximumMeasuredDeviationMm)
    {
        if (nativeFaceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(nativeFaceIndex));
        if (!double.IsFinite(surfaceAreaMm2) || surfaceAreaMm2 <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceAreaMm2));
        if (!double.IsFinite(maximumMeasuredDeviationMm) || maximumMeasuredDeviationMm < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMeasuredDeviationMm));

        Vec3[] copiedVertices = modelVertices?.ToArray()
            ?? throw new ArgumentNullException(nameof(modelVertices));
        if (copiedVertices.Length != 3)
            throw new ArgumentException("표면 요소는 삼각형 정점 세 개를 가져야 합니다.", nameof(modelVertices));

        NativeFaceIndex = nativeFaceIndex;
        SurfaceAreaMm2 = surfaceAreaMm2;
        ModelCentroid = modelCentroid;
        FaceUvCentroid = faceUvCentroid;
        WorkingUvCentroid = workingUvCentroid;
        ModelVertices = new ReadOnlyCollection<Vec3>(copiedVertices);
        MaximumMeasuredDeviationMm = maximumMeasuredDeviationMm;
    }
}

/// <summary>OCCT가 분할한 실제 유효 표면과 총 면적 오차 상한입니다.</summary>
public sealed class OcctSurfaceElementQueryResult
{
    /// <summary>Native 분할 질의의 상태입니다.</summary>
    public OcctSurfaceElementQueryStatus Status { get; }

    /// <summary>성공한 요청에서 영역 안에 놓인 삼각 요소입니다.</summary>
    public IReadOnlyList<OcctSurfaceElementData> Elements { get; }

    /// <summary>정확 면적과 메시 면적 합계의 차이 상한(mm²)입니다.</summary>
    public double AreaErrorBoundMm2 { get; }

    /// <summary>상태와 payload를 검사해 Native 표면 분할 결과를 만듭니다.</summary>
    public OcctSurfaceElementQueryResult(
        OcctSurfaceElementQueryStatus status,
        IEnumerable<OcctSurfaceElementData>? elements = null,
        double areaErrorBoundMm2 = 0)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (!double.IsFinite(areaErrorBoundMm2) || areaErrorBoundMm2 < 0)
            throw new ArgumentOutOfRangeException(nameof(areaErrorBoundMm2));

        OcctSurfaceElementData[] copiedElements = elements?.ToArray() ?? [];
        if (copiedElements.Any(element => element is null))
            throw new ArgumentException("표면 요소 목록은 null 항목을 포함할 수 없습니다.", nameof(elements));
        if (status == OcctSurfaceElementQueryStatus.Success && copiedElements.Length == 0)
            throw new ArgumentException("성공한 표면 질의에는 하나 이상의 요소가 필요합니다.", nameof(elements));
        if (status != OcctSurfaceElementQueryStatus.Success && copiedElements.Length != 0)
            throw new ArgumentException("실패한 표면 질의에는 부분 요소를 포함할 수 없습니다.", nameof(elements));

        Status = status;
        Elements = new ReadOnlyCollection<OcctSurfaceElementData>(copiedElements);
        AreaErrorBoundMm2 = areaErrorBoundMm2;
    }
}
