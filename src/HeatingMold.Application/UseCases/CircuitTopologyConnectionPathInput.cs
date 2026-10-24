using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.Application.UseCases;

/// <summary>새 회로 끝점의 좌표계와 사용자가 지정한 연결 경로 구간입니다.</summary>
public sealed class CircuitTopologyConnectionPathInput
{
    /// <summary>연결 경로를 지정한 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystem CoordinateSystem { get; }

    /// <summary>경로 질의에 사용해야 하는 Native 모델 revision입니다.</summary>
    public ModelRevision ExpectedModelRevision { get; }

    /// <summary>금형 위 구간부터 외부 연결부까지의 순서 있는 사용자 입력입니다.</summary>
    public IReadOnlyList<ConnectionPathLegRequest> Legs { get; }

    /// <summary>좌표계 revision과 연결 구간 입력을 복사해 보관합니다.</summary>
    public CircuitTopologyConnectionPathInput(
        WorkingCoordinateSystem coordinateSystem,
        ModelRevision expectedModelRevision,
        IEnumerable<ConnectionPathLegRequest> legs)
    {
        CoordinateSystem = coordinateSystem ?? throw new ArgumentNullException(nameof(coordinateSystem));
        if (coordinateSystem.Revision != expectedModelRevision)
            throw new ArgumentException("작업 좌표계와 연결 경로의 모델 revision이 다릅니다.", nameof(expectedModelRevision));

        ConnectionPathLegRequest[] copiedLegs = legs?.ToArray()
            ?? throw new ArgumentNullException(nameof(legs));
        if (copiedLegs.Length < 2 || copiedLegs.Any(leg => leg is null))
            throw new ArgumentException("연결 경로에는 두 개 이상의 유효 구간이 필요합니다.", nameof(legs));

        ExpectedModelRevision = expectedModelRevision;
        Legs = new ReadOnlyCollection<ConnectionPathLegRequest>(copiedLegs);
    }
}
