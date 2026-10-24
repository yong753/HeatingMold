using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Contracts.Geometry;

/// <summary>설계 topology가 같은 연속 경로로 선언한 발열 구간 두 개입니다.</summary>
public sealed record CurveSegmentAdjacency
{
    /// <summary>연속 쌍에서 앞선 발열 구간의 안정 ID입니다.</summary>
    public HeatingPathSegmentId FirstSegmentId { get; }

    /// <summary>연속 쌍에서 뒤따르는 발열 구간의 안정 ID입니다.</summary>
    public HeatingPathSegmentId SecondSegmentId { get; }

    /// <summary>앞선 구간의 끝 절점과 뒤따르는 구간의 시작 절점 연결 관계를 만듭니다.</summary>
    public CurveSegmentAdjacency(
        HeatingPathSegmentId firstSegmentId,
        HeatingPathSegmentId secondSegmentId)
    {
        if (firstSegmentId.IsEmpty || secondSegmentId.IsEmpty || firstSegmentId == secondSegmentId)
            throw new ArgumentException("연결 관계에는 서로 다른 유효 발열 구간 두 개가 필요합니다.");

        FirstSegmentId = firstSegmentId;
        SecondSegmentId = secondSegmentId;
    }

    /// <summary>주어진 두 구간이 이 연결 관계에 포함되는지 순서와 무관하게 확인합니다.</summary>
    public bool Connects(HeatingPathSegmentId firstSegmentId, HeatingPathSegmentId secondSegmentId) =>
        (FirstSegmentId == firstSegmentId && SecondSegmentId == secondSegmentId) ||
        (FirstSegmentId == secondSegmentId && SecondSegmentId == firstSegmentId);
}
