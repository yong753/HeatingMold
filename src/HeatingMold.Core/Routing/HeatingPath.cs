using System.Collections.ObjectModel;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Routing;

/// <summary>공유 절점 하나의 안정 ID와 최종 모델 좌표를 보관합니다.</summary>
public sealed record ControlPoint
{
    /// <summary>절점의 영속 ID입니다.</summary>
    public ControlPointId Id { get; }

    /// <summary>절점의 최종 모델 좌표(mm)입니다.</summary>
    public Vec3 Position { get; }

    /// <summary>유효 ID와 유한 모델 좌표로 절점을 만듭니다.</summary>
    public ControlPoint(ControlPointId id, Vec3 position)
    {
        if (id.IsEmpty)
            throw new ArgumentException("제어점 ID는 비어 있을 수 없습니다.", nameof(id));

        Id = id;
        Position = position;
    }
}

/// <summary>발열 경로를 이루는 구간의 설계 역할입니다.</summary>
public enum HeatingPathSegmentKind
{
    /// <summary>길게 왕복하는 발열 주행 구간입니다.</summary>
    HeatingRun,

    /// <summary>이웃 레인을 연결하는 회전 구간입니다.</summary>
    Turn,

    /// <summary>편집으로 고정하거나 추가한 접합 구간입니다.</summary>
    FixedJoin
}

/// <summary>안정 ID와 공유 절점, 원본 곡선 정의를 가진 경로 조각입니다.</summary>
public sealed record HeatingPathSegment
{
    /// <summary>경로 조각의 영속 ID입니다.</summary>
    public HeatingPathSegmentId Id { get; }

    /// <summary>조각의 설계 역할입니다.</summary>
    public HeatingPathSegmentKind Kind { get; }

    /// <summary>경로 시작 절점 ID입니다.</summary>
    public ControlPointId StartControlPointId { get; }

    /// <summary>경로 끝 절점 ID입니다.</summary>
    public ControlPointId EndControlPointId { get; }

    /// <summary>현재 경로에서의 순서입니다.</summary>
    public int Sequence { get; }

    /// <summary>경로 생성에서 정해진 진단용 레인 인덱스입니다.</summary>
    public int? LaneIndex { get; }

    /// <summary>저장·측정·출력 대상인지 나타냅니다.</summary>
    public bool IsActive { get; }

    /// <summary>조각 중심선을 재생성할 수 있는 수학 곡선입니다.</summary>
    public CurveDefinition Curve { get; }

    /// <summary>안정 참조, 순서, 역할과 유효 곡선으로 경로 조각을 만듭니다.</summary>
    public HeatingPathSegment(
        HeatingPathSegmentId id,
        HeatingPathSegmentKind kind,
        ControlPointId startControlPointId,
        ControlPointId endControlPointId,
        int sequence,
        bool isActive,
        CurveDefinition curve,
        int? laneIndex = null)
    {
        if (id.IsEmpty)
            throw new ArgumentException("경로 조각 ID는 비어 있을 수 없습니다.", nameof(id));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (startControlPointId.IsEmpty || endControlPointId.IsEmpty || startControlPointId == endControlPointId)
            throw new ArgumentException("경로 조각에는 서로 다른 유효 절점 ID가 필요합니다.");
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "경로 순서는 음수일 수 없습니다.");
        if (laneIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(laneIndex), "레인 인덱스는 음수일 수 없습니다.");

        Id = id;
        Kind = kind;
        StartControlPointId = startControlPointId;
        EndControlPointId = endControlPointId;
        Sequence = sequence;
        IsActive = isActive;
        Curve = curve ?? throw new ArgumentNullException(nameof(curve));
        LaneIndex = laneIndex;
    }
}

/// <summary>발열 경로를 만든 영역·사양·제약·규칙을 보존합니다.</summary>
public sealed record HeatingPathGenerationSource
{
    /// <summary>경로가 생성된 영역 ID입니다.</summary>
    public RegionId RegionId { get; }

    /// <summary>생성 시점의 배치 입력입니다.</summary>
    public PlacementConstraints PlacementConstraints { get; }

    /// <summary>생성 시점의 발열 도체 제원입니다.</summary>
    public WireSpecification WireSpecification { get; }

    /// <summary>경로 생성 알고리즘의 규칙 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>재생성 근거를 추적할 초기 입력과 규칙을 보관합니다.</summary>
    public HeatingPathGenerationSource(
        RegionId regionId,
        PlacementConstraints placementConstraints,
        WireSpecification wireSpecification,
        string ruleVersion)
    {
        if (regionId.IsEmpty)
            throw new ArgumentException("경로 생성 근거에는 유효 영역 ID가 필요합니다.", nameof(regionId));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("경로 생성 규칙 버전이 필요합니다.", nameof(ruleVersion));

        RegionId = regionId;
        PlacementConstraints = placementConstraints ?? throw new ArgumentNullException(nameof(placementConstraints));
        WireSpecification = wireSpecification ?? throw new ArgumentNullException(nameof(wireSpecification));
        RuleVersion = ruleVersion.Trim();
    }
}

/// <summary>생성 원본 조각과 현재 편집 조각을 함께 보존하는 발열 경로입니다.</summary>
public sealed class HeatingPath
{
    /// <summary>발열 경로의 영속 ID입니다.</summary>
    public HeatingPathId Id { get; }

    /// <summary>경로 생성 시 사용한 영역·사양·제약입니다.</summary>
    public HeatingPathGenerationSource GenerationSource { get; }

    /// <summary>생성 당시 원본 경로 조각입니다.</summary>
    public IReadOnlyList<HeatingPathSegment> GeneratedSegments { get; }

    /// <summary>성공한 편집을 반영한 현재 경로 조각입니다.</summary>
    public IReadOnlyList<HeatingPathSegment> Segments { get; }

    /// <summary>생성 원본과 현재 경로가 참조하는 공유 절점입니다.</summary>
    public IReadOnlyList<ControlPoint> ControlPoints { get; }

    /// <summary>모든 조각 ID와 순서가 유일하고 절점 참조가 완전한 경로를 만듭니다.</summary>
    public HeatingPath(
        HeatingPathId id,
        HeatingPathGenerationSource generationSource,
        IEnumerable<ControlPoint> controlPoints,
        IEnumerable<HeatingPathSegment> generatedSegments,
        IEnumerable<HeatingPathSegment> segments)
    {
        if (id.IsEmpty)
            throw new ArgumentException("발열 경로 ID는 비어 있을 수 없습니다.", nameof(id));
        ControlPoint[] points = controlPoints?.ToArray() ?? throw new ArgumentNullException(nameof(controlPoints));
        HeatingPathSegment[] generated = generatedSegments?.ToArray()
            ?? throw new ArgumentNullException(nameof(generatedSegments));
        HeatingPathSegment[] current = segments?.ToArray() ?? throw new ArgumentNullException(nameof(segments));
        if (points.Select(point => point.Id).Distinct().Count() != points.Length)
            throw new ArgumentException("발열 경로의 절점 ID는 중복될 수 없습니다.", nameof(controlPoints));
        if (generated.Length == 0 || current.Length == 0)
            throw new ArgumentException("생성 원본과 현재 경로에는 각각 하나 이상의 조각이 필요합니다.");
        if (generated.Select(segment => segment.Id).Distinct().Count() != generated.Length ||
            current.Select(segment => segment.Id).Distinct().Count() != current.Length)
        {
            throw new ArgumentException("한 경로 정의 안에서 조각 ID는 중복될 수 없습니다.");
        }
        if (generated.Select(segment => segment.Sequence).Distinct().Count() != generated.Length ||
            current.Select(segment => segment.Sequence).Distinct().Count() != current.Length)
        {
            throw new ArgumentException("한 경로 정의 안에서 조각 순서는 중복될 수 없습니다.");
        }

        var pointIds = points.Select(point => point.Id).ToHashSet();
        foreach (HeatingPathSegment segment in generated.Concat(current))
        {
            if (!pointIds.Contains(segment.StartControlPointId) || !pointIds.Contains(segment.EndControlPointId))
                throw new ArgumentException("경로 조각이 존재하지 않는 공유 절점을 참조합니다.");
        }

        Id = id;
        GenerationSource = generationSource ?? throw new ArgumentNullException(nameof(generationSource));
        ControlPoints = new ReadOnlyCollection<ControlPoint>(points);
        GeneratedSegments = new ReadOnlyCollection<HeatingPathSegment>(generated.OrderBy(segment => segment.Sequence).ToArray());
        Segments = new ReadOnlyCollection<HeatingPathSegment>(current.OrderBy(segment => segment.Sequence).ToArray());
    }
}
