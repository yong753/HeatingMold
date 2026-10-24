using System.Collections.ObjectModel;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Regions;

/// <summary>선택 Face, 영역 도형, 최종 경계와 공통 출구 측면을 묶습니다.</summary>
public sealed class HeatingRegion
{
    /// <summary>발열 영역의 영속 ID입니다.</summary>
    public RegionId Id { get; }

    /// <summary>영역 형상에 포함되는 원본 Face 참조입니다.</summary>
    public IReadOnlyList<FaceRefId> FaceReferences { get; }

    /// <summary>선택 Face에서 가져온 원래 경계 루프입니다.</summary>
    public IReadOnlyList<BoundaryLoop> OriginalBoundaryLoops { get; }

    /// <summary>원래 영역에 더하는 포함 도형입니다.</summary>
    public IReadOnlyList<BoundaryLoop> InclusionLoops { get; }

    /// <summary>원래 영역에서 제거하는 제외 도형입니다.</summary>
    public IReadOnlyList<BoundaryLoop> ExclusionLoops { get; }

    /// <summary>포함·제외 연산 후 실제 경로 배치에 쓰는 경계입니다.</summary>
    public IReadOnlyList<BoundaryLoop> EffectiveBoundaryLoops { get; }

    /// <summary>회로 두 끝의 인출 앵커가 공유할 경계 측면입니다.</summary>
    public ExitSideReference ExitSide { get; }

    /// <summary>모든 원본 Face·경계와 출구 참조가 영역 내에서 일치하는 값을 만듭니다.</summary>
    public HeatingRegion(
        RegionId id,
        IEnumerable<FaceRefId> faceReferences,
        IEnumerable<BoundaryLoop> originalBoundaryLoops,
        IEnumerable<BoundaryLoop> inclusionLoops,
        IEnumerable<BoundaryLoop> exclusionLoops,
        IEnumerable<BoundaryLoop> effectiveBoundaryLoops,
        ExitSideReference exitSide)
    {
        if (id.IsEmpty)
            throw new ArgumentException("발열 영역 ID는 비어 있을 수 없습니다.", nameof(id));

        FaceRefId[] faces = faceReferences?.ToArray() ?? throw new ArgumentNullException(nameof(faceReferences));
        if (faces.Length == 0 || faces.Any(face => face.IsEmpty) || faces.Distinct().Count() != faces.Length)
            throw new ArgumentException("발열 영역에는 중복 없이 유효한 Face 참조가 필요합니다.", nameof(faceReferences));

        BoundaryLoop[] original = CopyLoops(originalBoundaryLoops, nameof(originalBoundaryLoops));
        BoundaryLoop[] included = CopyLoops(inclusionLoops, nameof(inclusionLoops));
        BoundaryLoop[] excluded = CopyLoops(exclusionLoops, nameof(exclusionLoops));
        BoundaryLoop[] effective = CopyLoops(effectiveBoundaryLoops, nameof(effectiveBoundaryLoops));
        if (original.Length == 0 || effective.Length == 0)
            throw new ArgumentException("원래 경계와 유효 경계를 각각 하나 이상 지정해야 합니다.");

        BoundaryLoop[] allLoops = original.Concat(included).Concat(excluded).Concat(effective).ToArray();
        if (allLoops.Select(loop => loop.Id).Distinct().Count() != allLoops.Length)
            throw new ArgumentException("영역 내 각 경계 루프에는 고유한 ID가 필요합니다.");

        var segmentIds = effective.SelectMany(loop => loop.Segments).Select(segment => segment.Id).ToHashSet();
        if (exitSide is null)
            throw new ArgumentNullException(nameof(exitSide));
        if (exitSide.Ranges.Any(range => !segmentIds.Contains(range.BoundarySegmentId)))
            throw new ArgumentException("출구 측면은 유효 경계의 구간만 참조할 수 있습니다.", nameof(exitSide));

        Id = id;
        FaceReferences = new ReadOnlyCollection<FaceRefId>(faces);
        OriginalBoundaryLoops = new ReadOnlyCollection<BoundaryLoop>(original);
        InclusionLoops = new ReadOnlyCollection<BoundaryLoop>(included);
        ExclusionLoops = new ReadOnlyCollection<BoundaryLoop>(excluded);
        EffectiveBoundaryLoops = new ReadOnlyCollection<BoundaryLoop>(effective);
        ExitSide = exitSide;
    }

    private static BoundaryLoop[] CopyLoops(IEnumerable<BoundaryLoop> loops, string parameterName)
    {
        BoundaryLoop[] copied = loops?.ToArray() ?? throw new ArgumentNullException(parameterName);
        if (copied.Any(loop => loop is null))
            throw new ArgumentException("경계 루프 목록은 null 항목을 포함할 수 없습니다.", parameterName);

        return copied;
    }
}

/// <summary>경계 구간의 일부를 정규화된 매개변수 범위로 지정합니다.</summary>
public readonly record struct BoundarySegmentRange
{
    /// <summary>범위를 포함하는 안정 경계 구간 ID입니다.</summary>
    public BoundarySegmentId BoundarySegmentId { get; }

    /// <summary>포함 시작 매개변수이며 0부터 1 사이입니다.</summary>
    public double StartParameter { get; }

    /// <summary>포함 끝 매개변수이며 시작보다 크고 1 이하입니다.</summary>
    public double EndParameter { get; }

    /// <summary>유효 ID와 증가하는 정규화 구간으로 측면 일부를 만듭니다.</summary>
    public BoundarySegmentRange(BoundarySegmentId boundarySegmentId, double startParameter, double endParameter)
    {
        if (boundarySegmentId.IsEmpty)
            throw new ArgumentException("출구 구간에는 안정 경계 ID가 필요합니다.", nameof(boundarySegmentId));
        if (!double.IsFinite(startParameter) || !double.IsFinite(endParameter) ||
            startParameter < 0 || endParameter > 1 || startParameter >= endParameter)
        {
            throw new ArgumentOutOfRangeException(nameof(startParameter), "측면 범위는 0 이상 1 이하에서 증가해야 합니다.");
        }

        BoundarySegmentId = boundarySegmentId;
        StartParameter = startParameter;
        EndParameter = endParameter;
    }
}

/// <summary>화면 방향 이름 대신 경계 구간 ID와 범위로 연결 측면을 식별합니다.</summary>
public sealed class ExitSideReference
{
    /// <summary>한 측면에 속하는 연결 순서의 경계 범위입니다.</summary>
    public IReadOnlyList<BoundarySegmentRange> Ranges { get; }

    /// <summary>한 측면을 구성하는 중복 없는 정규화 경계 범위를 보관합니다.</summary>
    public ExitSideReference(IEnumerable<BoundarySegmentRange> ranges)
    {
        BoundarySegmentRange[] copiedRanges = ranges?.ToArray()
            ?? throw new ArgumentNullException(nameof(ranges));
        if (copiedRanges.Length == 0 || copiedRanges.Any(range => range.BoundarySegmentId.IsEmpty))
            throw new ArgumentException("출구 측면에는 유효한 경계 범위가 하나 이상 필요합니다.", nameof(ranges));
        if (copiedRanges.Select(range => range.BoundarySegmentId).Distinct().Count() != copiedRanges.Length)
            throw new ArgumentException("한 출구 측면은 같은 경계 구간을 두 번 참조할 수 없습니다.", nameof(ranges));

        Ranges = new ReadOnlyCollection<BoundarySegmentRange>(copiedRanges);
    }
}
