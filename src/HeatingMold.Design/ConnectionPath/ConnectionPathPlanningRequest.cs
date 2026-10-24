using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;

namespace HeatingMold.Design.ConnectionPath;

/// <summary>실제 발열사 끝점부터 표면 전이와 외부 연결부까지의 명시 경로 입력입니다.</summary>
public sealed class ConnectionPathPlanningRequest
{
    /// <summary>경로를 만들 대상 회로입니다.</summary>
    public Circuit Circuit { get; }

    /// <summary>새 연결 경로가 끝나는 회로 연결부입니다.</summary>
    public ConnectionPoint ConnectionPoint { get; }

    /// <summary>연결부가 매달린 발열 경로의 확정 구간입니다.</summary>
    public HeatingPathSegment EndpointSegment { get; }

    /// <summary>삽입 깊이가 적용된 실제 발열 곡선 끝점입니다.</summary>
    public Vec3 HeatingWireEndpoint { get; }

    /// <summary>시작부터 외부 연결점까지 사용자가 지정한 직선 구간입니다.</summary>
    public IReadOnlyList<ConnectionPathLegRequest> Legs { get; }

    /// <summary>경로를 질의할 열린 모델의 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystem CoordinateSystem { get; }

    /// <summary>경로 계산의 기준 모델 revision입니다.</summary>
    public ModelRevision ExpectedModelRevision { get; }

    /// <summary>회로 끝과 연결부 소유권, 작업 좌표계 revision을 검사해 요청을 만듭니다.</summary>
    public ConnectionPathPlanningRequest(
        Circuit circuit,
        ConnectionPoint connectionPoint,
        HeatingPathSegment endpointSegment,
        Vec3 heatingWireEndpoint,
        IEnumerable<ConnectionPathLegRequest> legs,
        WorkingCoordinateSystem coordinateSystem,
        ModelRevision expectedModelRevision)
    {
        Circuit = circuit ?? throw new ArgumentNullException(nameof(circuit));
        ConnectionPoint = connectionPoint ?? throw new ArgumentNullException(nameof(connectionPoint));
        ArgumentNullException.ThrowIfNull(endpointSegment);
        EndpointSegment = endpointSegment;
        CoordinateSystem = coordinateSystem ?? throw new ArgumentNullException(nameof(coordinateSystem));
        if (connectionPoint.CircuitId != circuit.Id || endpointSegment.Id != connectionPoint.HeatingPathSegmentId)
            throw new ArgumentException("연결부와 발열 끝점 구간은 지정한 회로에 속해야 합니다.");
        if (coordinateSystem.Revision != expectedModelRevision)
            throw new ArgumentException("작업 좌표계와 요청 모델 revision이 다릅니다.", nameof(coordinateSystem));

        CircuitSegmentReference[] orderedReferences = circuit.Segments.OrderBy(reference => reference.Sequence).ToArray();
        CircuitSegmentReference endpointReference = connectionPoint.Role == ConnectionPointRole.Start
            ? orderedReferences[0]
            : orderedReferences[^1];
        ControlPointId expectedControlPoint = connectionPoint.Role == ConnectionPointRole.Start
            ? endpointReference.IsReversed ? endpointSegment.EndControlPointId : endpointSegment.StartControlPointId
            : endpointReference.IsReversed ? endpointSegment.StartControlPointId : endpointSegment.EndControlPointId;
        if (endpointReference.SegmentId != endpointSegment.Id || connectionPoint.ControlPointId != expectedControlPoint)
            throw new ArgumentException("연결부가 회로 순서에서 지정한 실제 끝점 구간과 절점을 가리키지 않습니다.");

        ConnectionPathLegRequest[] copiedLegs = legs?.ToArray() ?? throw new ArgumentNullException(nameof(legs));
        if (copiedLegs.Length < 2 || copiedLegs.Any(leg => leg is null))
            throw new ArgumentException("연결 경로에는 OnMold와 External 구간을 포함한 두 개 이상의 구간이 필요합니다.", nameof(legs));
        Legs = new ReadOnlyCollection<ConnectionPathLegRequest>(copiedLegs);
        HeatingWireEndpoint = heatingWireEndpoint;
        ExpectedModelRevision = expectedModelRevision;
    }
}

/// <summary>연결 경로에서 다음 점까지의 도메인·재료 역할과 길이 여유 입력입니다.</summary>
public sealed record ConnectionPathLegRequest
{
    /// <summary>이 구간의 지정 끝점(mm)입니다.</summary>
    public Vec3 EndPoint { get; }

    /// <summary>구간이 금형 위인지 외부인지 나타냅니다.</summary>
    public GeometryDomain GeometryDomain { get; }

    /// <summary>발열사 연장 또는 별도 연결선의 재료 역할입니다.</summary>
    public ConductorRole ConductorRole { get; }

    /// <summary>실제 곡선 길이에 더할 명시 여유(mm)입니다.</summary>
    public LengthMm AdditionalAllowanceMm { get; }

    /// <summary>이 구간 시작에서 금형 표면을 벗어나는 전이 표식입니다.</summary>
    public bool IsMoldExitTransition { get; }

    /// <summary>유효 끝점과 재료·공간 역할, 비음수 길이 여유를 보관합니다.</summary>
    public ConnectionPathLegRequest(
        Vec3 endPoint,
        GeometryDomain geometryDomain,
        ConductorRole conductorRole,
        LengthMm additionalAllowanceMm,
        bool isMoldExitTransition = false)
    {
        if (!Enum.IsDefined(geometryDomain) || !Enum.IsDefined(conductorRole))
            throw new ArgumentOutOfRangeException(nameof(geometryDomain));
        if (!double.IsFinite(additionalAllowanceMm.Value) || additionalAllowanceMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalAllowanceMm));

        EndPoint = endPoint;
        GeometryDomain = geometryDomain;
        ConductorRole = conductorRole;
        AdditionalAllowanceMm = additionalAllowanceMm;
        IsMoldExitTransition = isMoldExitTransition;
    }
}
