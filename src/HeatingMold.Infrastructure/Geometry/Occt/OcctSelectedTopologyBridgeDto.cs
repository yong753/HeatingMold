using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>선택 Face topology 질의의 Native 종료 상태입니다.</summary>
public enum OcctSelectedTopologyStatus
{
    /// <summary>연결 성분과 경계 루프를 얻었습니다.</summary>
    Success = 0,

    /// <summary>선택 목록이나 수치 설정이 유효하지 않습니다.</summary>
    InvalidInput = 1,

    /// <summary>선택 목록에 현재 문서에 없는 Face 번호가 있습니다.</summary>
    InvalidFace = 2,

    /// <summary>요청한 작업 좌표계가 현재 모델 revision과 다릅니다.</summary>
    StaleContext = 3,

    /// <summary>경계가 열려 있거나 분기되어 닫힌 topology를 만들 수 없습니다.</summary>
    InvalidTopology = 4,

    /// <summary>OCCT 위상 처리에서 기하 오류가 발생했습니다.</summary>
    GeometryFailure = 5,

    /// <summary>현재 브리지가 알지 못하는 상태 값입니다.</summary>
    Unknown = 6
}

/// <summary>Native topology에서 추출한 닫힌 경계 루프의 모델 좌표입니다.</summary>
public sealed class OcctTopologyBoundaryLoop
{
    /// <summary>중복 닫힘 점을 제외한 순서 있는 모델 좌표 정점입니다.</summary>
    public IReadOnlyList<Vec3> ModelVertices { get; }

    /// <summary>각 정점에서 다음 정점으로 이어지는 구간의 원본 Native Face 번호입니다.</summary>
    public IReadOnlyList<int> NativeSegmentFaceIndices { get; }

    /// <summary>OCCT가 연결된 닫힌 루프로 확인했는지 나타냅니다.</summary>
    public bool IsClosed { get; }

    /// <summary>정점과 구간 Face 대응을 방어 복사한 Native 경계 루프를 만듭니다.</summary>
    public OcctTopologyBoundaryLoop(
        IEnumerable<Vec3> modelVertices,
        IEnumerable<int> nativeSegmentFaceIndices,
        bool isClosed)
    {
        Vec3[] vertices = modelVertices?.ToArray()
            ?? throw new ArgumentNullException(nameof(modelVertices));
        int[] faceIndices = nativeSegmentFaceIndices?.ToArray()
            ?? throw new ArgumentNullException(nameof(nativeSegmentFaceIndices));
        if (vertices.Length < 3)
            throw new ArgumentException("Native 경계에는 정점이 세 개 이상 필요합니다.", nameof(modelVertices));
        if (faceIndices.Length != vertices.Length || faceIndices.Any(index => index < 0))
            throw new ArgumentException("각 경계 구간에는 유효한 원본 Face 번호가 하나씩 필요합니다.", nameof(nativeSegmentFaceIndices));

        ModelVertices = new ReadOnlyCollection<Vec3>(vertices);
        NativeSegmentFaceIndices = new ReadOnlyCollection<int>(faceIndices);
        IsClosed = isClosed;
    }
}

/// <summary>Native Face 번호와 해당 연결 성분의 실제 경계 루프입니다.</summary>
public sealed class OcctTopologyComponent
{
    /// <summary>연결 성분에 포함된 원본 Native Face 번호입니다.</summary>
    public IReadOnlyList<int> NativeFaceIndices { get; }

    /// <summary>공유 내부 Edge를 제외한 닫힌 경계 루프입니다.</summary>
    public IReadOnlyList<OcctTopologyBoundaryLoop> BoundaryLoops { get; }

    /// <summary>Face와 경계 루프를 방어 복사한 연결 성분을 만듭니다.</summary>
    public OcctTopologyComponent(
        IEnumerable<int> nativeFaceIndices,
        IEnumerable<OcctTopologyBoundaryLoop> boundaryLoops)
    {
        int[] faces = nativeFaceIndices?.ToArray()
            ?? throw new ArgumentNullException(nameof(nativeFaceIndices));
        OcctTopologyBoundaryLoop[] loops = boundaryLoops?.ToArray()
            ?? throw new ArgumentNullException(nameof(boundaryLoops));
        if (faces.Length == 0 || faces.Any(index => index < 0) || faces.Distinct().Count() != faces.Length)
            throw new ArgumentException("연결 성분에는 중복 없는 Native Face 번호가 필요합니다.", nameof(nativeFaceIndices));
        if (loops.Any(loop => loop is null))
            throw new ArgumentException("경계 루프는 null일 수 없습니다.", nameof(boundaryLoops));

        NativeFaceIndices = new ReadOnlyCollection<int>(faces);
        BoundaryLoops = new ReadOnlyCollection<OcctTopologyBoundaryLoop>(loops);
    }
}

/// <summary>선택된 두 Native Face 사이의 공유 Edge 인접 관계입니다.</summary>
public readonly record struct OcctTopologyAdjacency(int FirstNativeFaceIndex, int SecondNativeFaceIndex);

/// <summary>선택 Face topology 질의의 상태·성분·인접 결과입니다.</summary>
public sealed class OcctSelectedTopologyResult
{
    /// <summary>Native topology 질의 상태입니다.</summary>
    public OcctSelectedTopologyStatus Status { get; }

    /// <summary>성공한 질의에서 반환된 연결 성분입니다.</summary>
    public IReadOnlyList<OcctTopologyComponent> Components { get; }

    /// <summary>실제 공유 Edge로 확인한 선택 Face 인접 관계입니다.</summary>
    public IReadOnlyList<OcctTopologyAdjacency> Adjacencies { get; }

    /// <summary>상태와 payload의 일관성을 확인해 Native topology 결과를 만듭니다.</summary>
    public OcctSelectedTopologyResult(
        OcctSelectedTopologyStatus status,
        IEnumerable<OcctTopologyComponent>? components = null,
        IEnumerable<OcctTopologyAdjacency>? adjacencies = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        OcctTopologyComponent[] copiedComponents = components?.ToArray() ?? [];
        OcctTopologyAdjacency[] copiedAdjacencies = adjacencies?.ToArray() ?? [];
        if (copiedComponents.Any(component => component is null))
            throw new ArgumentException("연결 성분은 null일 수 없습니다.", nameof(components));
        if (status != OcctSelectedTopologyStatus.Success &&
            (copiedComponents.Length > 0 || copiedAdjacencies.Length > 0))
        {
            throw new ArgumentException("실패한 Native topology 결과는 부분 payload를 포함할 수 없습니다.");
        }

        Status = status;
        Components = new ReadOnlyCollection<OcctTopologyComponent>(copiedComponents);
        Adjacencies = new ReadOnlyCollection<OcctTopologyAdjacency>(copiedAdjacencies);
    }
}
