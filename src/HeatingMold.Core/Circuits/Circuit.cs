using System.Collections.ObjectModel;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Circuits;

/// <summary>발열 경로 조각의 회로 순서와 진행 방향 참조입니다.</summary>
public readonly record struct CircuitSegmentReference
{
    /// <summary>소속된 발열 경로 조각 ID입니다.</summary>
    public HeatingPathSegmentId SegmentId { get; }

    /// <summary>회로가 해당 조각의 원 정의를 역방향으로 따르는지 나타냅니다.</summary>
    public bool IsReversed { get; }

    /// <summary>회로 내 순서입니다.</summary>
    public int Sequence { get; }

    /// <summary>유효 조각 ID와 순서로 회로의 경로 참조를 만듭니다.</summary>
    public CircuitSegmentReference(HeatingPathSegmentId segmentId, int sequence, bool isReversed)
    {
        if (segmentId.IsEmpty)
            throw new ArgumentException("회로 조각 참조에는 유효 ID가 필요합니다.", nameof(segmentId));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        SegmentId = segmentId;
        Sequence = sequence;
        IsReversed = isReversed;
    }
}

/// <summary>양 끝 연결부와 순서가 지정된 발열 조각을 묶는 전기 회로입니다.</summary>
public sealed class Circuit
{
    /// <summary>회로의 영속 ID입니다.</summary>
    public CircuitId Id { get; }

    /// <summary>프로젝트 내 정렬 순서입니다.</summary>
    public int Sequence { get; }

    /// <summary>회로가 소유하는 발열 경로 조각과 진행 방향입니다.</summary>
    public IReadOnlyList<CircuitSegmentReference> Segments { get; }

    /// <summary>회로 시작 쪽 연결부 ID입니다.</summary>
    public ConnectionPointId StartConnectionPointId { get; }

    /// <summary>회로 끝 쪽 연결부 ID입니다.</summary>
    public ConnectionPointId EndConnectionPointId { get; }

    /// <summary>회로만 사용하는 실제 전압과 최대 한도입니다.</summary>
    public Supply Supply { get; }

    /// <summary>서로 다른 두 연결부와 중복 없는 순서 경로 조각으로 회로를 만듭니다.</summary>
    public Circuit(
        CircuitId id,
        int sequence,
        IEnumerable<CircuitSegmentReference> segments,
        ConnectionPointId startConnectionPointId,
        ConnectionPointId endConnectionPointId,
        Supply supply)
    {
        if (id.IsEmpty)
            throw new ArgumentException("회로 ID는 비어 있을 수 없습니다.", nameof(id));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (startConnectionPointId.IsEmpty || endConnectionPointId.IsEmpty ||
            startConnectionPointId == endConnectionPointId)
        {
            throw new ArgumentException("회로에는 서로 다른 시작·끝 연결부가 필요합니다.");
        }

        CircuitSegmentReference[] copiedSegments = segments?.ToArray()
            ?? throw new ArgumentNullException(nameof(segments));
        if (copiedSegments.Length == 0)
            throw new ArgumentException("회로에는 발열 경로 조각이 하나 이상 필요합니다.", nameof(segments));
        if (copiedSegments.Any(segment => segment.SegmentId.IsEmpty || segment.Sequence < 0))
            throw new ArgumentException("회로 조각에는 유효한 ID와 0 이상의 순서가 필요합니다.", nameof(segments));
        if (copiedSegments.Select(segment => segment.SegmentId).Distinct().Count() != copiedSegments.Length ||
            copiedSegments.Select(segment => segment.Sequence).Distinct().Count() != copiedSegments.Length)
        {
            throw new ArgumentException("회로 내 경로 조각 ID와 순서는 중복될 수 없습니다.", nameof(segments));
        }

        Id = id;
        Sequence = sequence;
        Segments = new ReadOnlyCollection<CircuitSegmentReference>(copiedSegments.OrderBy(segment => segment.Sequence).ToArray());
        StartConnectionPointId = startConnectionPointId;
        EndConnectionPointId = endConnectionPointId;
        Supply = supply ?? throw new ArgumentNullException(nameof(supply));
    }
}

/// <summary>회로 끝의 시작·종료 역할입니다.</summary>
public enum ConnectionPointRole
{
    /// <summary>회로 경로의 시작 쪽 연결부입니다.</summary>
    Start,

    /// <summary>회로 경로의 끝 쪽 연결부입니다.</summary>
    End
}

/// <summary>전도체 재료 종류를 나타냅니다.</summary>
public enum ConductorRole
{
    /// <summary>저항과 발열 길이에 포함하는 동일 발열사입니다.</summary>
    HeatingWireExtension,

    /// <summary>저항과 발열 길이에서 제외하는 별도 연결선입니다.</summary>
    SeparateConnectionWire
}

/// <summary>경로 구간이 금형 표면을 따라가는지 외부에 있는지 나타냅니다.</summary>
public enum GeometryDomain
{
    /// <summary>금형 표면과 내부 검사를 받는 구간입니다.</summary>
    OnMold,

    /// <summary>금형 표면 밖에 놓이는 구간입니다.</summary>
    External
}

/// <summary>회로 끝, 발열사 절점, 인출 앵커와 실제 연결부를 결합합니다.</summary>
public sealed record ConnectionPoint
{
    /// <summary>회로 연결부의 영속 ID입니다.</summary>
    public ConnectionPointId Id { get; }

    /// <summary>이 연결부를 소유하는 회로 ID입니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>회로에서 시작과 종료 중 어느 쪽인지 나타냅니다.</summary>
    public ConnectionPointRole Role { get; }

    /// <summary>끝점이 붙는 발열 경로 조각 ID입니다.</summary>
    public HeatingPathSegmentId HeatingPathSegmentId { get; }

    /// <summary>끝점이 공유하는 발열 경로 절점 ID입니다.</summary>
    public ControlPointId ControlPointId { get; }

    /// <summary>금형 표면을 빠져나가는 안정 위치입니다.</summary>
    public Geometry.SurfaceAnchor SurfaceAnchor { get; }

    /// <summary>외부 설비와 만나는 최종 연결 좌표(mm)입니다.</summary>
    public Geometry.Vec3 ExternalConnectionPoint { get; }

    /// <summary>끝 역할, 소유 회로, 발열 절점과 표면 인출 위치를 연결합니다.</summary>
    public ConnectionPoint(
        ConnectionPointId id,
        CircuitId circuitId,
        ConnectionPointRole role,
        HeatingPathSegmentId heatingPathSegmentId,
        ControlPointId controlPointId,
        Geometry.SurfaceAnchor surfaceAnchor,
        Geometry.Vec3 externalConnectionPoint)
    {
        if (id.IsEmpty || circuitId.IsEmpty || heatingPathSegmentId.IsEmpty || controlPointId.IsEmpty)
            throw new ArgumentException("연결부 참조 ID는 비어 있을 수 없습니다.");
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));

        Id = id;
        CircuitId = circuitId;
        Role = role;
        HeatingPathSegmentId = heatingPathSegmentId;
        ControlPointId = controlPointId;
        SurfaceAnchor = surfaceAnchor ?? throw new ArgumentNullException(nameof(surfaceAnchor));
        ExternalConnectionPoint = externalConnectionPoint;
    }
}

/// <summary>표면 전이와 재료 역할을 갖는 하나의 연결 경로 구간입니다.</summary>
public sealed record ConnectionPathSegment
{
    /// <summary>연결 경로 구간의 영속 ID입니다.</summary>
    public ConnectionPathSegmentId Id { get; }

    /// <summary>연결 경로 내 순서입니다.</summary>
    public int Sequence { get; }

    /// <summary>구간의 도체 재료 역할입니다.</summary>
    public ConductorRole ConductorRole { get; }

    /// <summary>금형 위 또는 외부라는 구간 공간입니다.</summary>
    public GeometryDomain GeometryDomain { get; }

    /// <summary>실제 형상 길이에 추가할 재료별 여유입니다.</summary>
    public LengthMm AdditionalAllowanceMm { get; }

    /// <summary>이 구간 시작점이 금형 위에서 외부로 나가는 경계인지 나타냅니다.</summary>
    public bool IsMoldExitTransition { get; }

    /// <summary>구간 중심선의 수학적 곡선 정의입니다.</summary>
    public Geometry.CurveDefinition Curve { get; }

    /// <summary>경로 순서, 재료·공간 역할과 비음수 여유로 구간을 만듭니다.</summary>
    public ConnectionPathSegment(
        ConnectionPathSegmentId id,
        int sequence,
        ConductorRole conductorRole,
        GeometryDomain geometryDomain,
        LengthMm additionalAllowanceMm,
        Geometry.CurveDefinition curve,
        bool isMoldExitTransition = false)
    {
        if (id.IsEmpty)
            throw new ArgumentException("연결 구간 ID는 비어 있을 수 없습니다.", nameof(id));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(conductorRole) || !Enum.IsDefined(geometryDomain))
            throw new ArgumentOutOfRangeException(nameof(conductorRole));
        if (additionalAllowanceMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalAllowanceMm));

        Id = id;
        Sequence = sequence;
        ConductorRole = conductorRole;
        GeometryDomain = geometryDomain;
        AdditionalAllowanceMm = additionalAllowanceMm;
        Curve = curve ?? throw new ArgumentNullException(nameof(curve));
        IsMoldExitTransition = isMoldExitTransition;
    }
}

/// <summary>하나의 연결부까지 이어지는 재료·공간별 최종 경로입니다.</summary>
public sealed class ConnectionPath
{
    /// <summary>연결 경로의 안정 ID입니다.</summary>
    public ConnectionPathId Id { get; }

    /// <summary>경로가 끝나는 회로 연결부 ID입니다.</summary>
    public ConnectionPointId ConnectionPointId { get; }

    /// <summary>실제 진행 순서의 연결 경로 구간입니다.</summary>
    public IReadOnlyList<ConnectionPathSegment> Segments { get; }

    /// <summary>한 명시 외부 전이 뒤 금형 위로 다시 진입하지 않는 연결 경로를 만듭니다.</summary>
    public ConnectionPath(ConnectionPathId id, ConnectionPointId connectionPointId, IEnumerable<ConnectionPathSegment> segments)
    {
        if (id.IsEmpty || connectionPointId.IsEmpty)
            throw new ArgumentException("연결 경로와 연결부 ID는 비어 있을 수 없습니다.");

        ConnectionPathSegment[] copiedSegments = segments?.OrderBy(segment => segment.Sequence).ToArray()
            ?? throw new ArgumentNullException(nameof(segments));
        if (copiedSegments.Length == 0)
            throw new ArgumentException("연결 경로에는 구간이 하나 이상 필요합니다.", nameof(segments));
        if (copiedSegments.Select(segment => segment.Id).Distinct().Count() != copiedSegments.Length ||
            copiedSegments.Select(segment => segment.Sequence).Distinct().Count() != copiedSegments.Length)
        {
            throw new ArgumentException("연결 경로 구간 ID와 순서는 중복될 수 없습니다.", nameof(segments));
        }

        int transitionCount = copiedSegments.Count(segment => segment.IsMoldExitTransition);
        if (transitionCount != 1)
            throw new ArgumentException("연결 경로에는 금형 위에서 외부로 나가는 전이가 정확히 하나 필요합니다.", nameof(segments));

        bool hasExitedMold = false;
        for (int index = 0; index < copiedSegments.Length; index++)
        {
            ConnectionPathSegment segment = copiedSegments[index];
            bool isExternalTransition = segment.IsMoldExitTransition &&
                segment.GeometryDomain == GeometryDomain.External && index > 0 &&
                copiedSegments[index - 1].GeometryDomain == GeometryDomain.OnMold;
            if (segment.IsMoldExitTransition && !isExternalTransition)
                throw new ArgumentException("금형 이탈 표식은 OnMold 다음의 External 구간에만 둘 수 있습니다.", nameof(segments));
            if (index > 0 && copiedSegments[index - 1].GeometryDomain == GeometryDomain.OnMold &&
                segment.GeometryDomain == GeometryDomain.External && !segment.IsMoldExitTransition)
            {
                throw new ArgumentException("금형 위에서 외부로 바뀌는 구간에는 이탈 표식이 필요합니다.", nameof(segments));
            }

            if (segment.GeometryDomain == GeometryDomain.External)
                hasExitedMold = true;
            else if (hasExitedMold)
                throw new ArgumentException("금형 밖으로 나간 연결 경로는 다시 금형 위로 진입할 수 없습니다.", nameof(segments));
        }

        Id = id;
        ConnectionPointId = connectionPointId;
        Segments = new ReadOnlyCollection<ConnectionPathSegment>(copiedSegments);
    }
}
