using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.Ports.Export;

/// <summary>정사영 DXF에서 출력할 2차원 다중선입니다.</summary>
public sealed record OrthographicDxfPolyline
{
    /// <summary>순서가 유지되는 투영 평면 좌표입니다.</summary>
    public IReadOnlyList<Vec2> Points { get; }

    /// <summary>마지막 점에서 첫 점으로 닫을 다중선인지 나타냅니다.</summary>
    public bool IsClosed { get; }

    /// <summary>두 점 이상의 좌표와 닫힘 여부를 복사해 보관합니다.</summary>
    public OrthographicDxfPolyline(IEnumerable<Vec2> points, bool isClosed = false)
    {
        Vec2[] copiedPoints = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
        if (copiedPoints.Length < 2)
            throw new ArgumentException("DXF 다중선에는 두 점 이상이 필요합니다.", nameof(points));

        Points = new ReadOnlyCollection<Vec2>(copiedPoints);
        IsClosed = isClosed;
    }
}

/// <summary>보이는 선, 은선과 발열선으로 구성된 정사영 도면 한 장입니다.</summary>
public sealed class OrthographicDxfView
{
    /// <summary>금형 외곽으로 출력할 가시선입니다.</summary>
    public IReadOnlyList<OrthographicDxfPolyline> VisibleEdges { get; }

    /// <summary>은선 레이어로 출력할 가려진 선입니다.</summary>
    public IReadOnlyList<OrthographicDxfPolyline> HiddenEdges { get; }

    /// <summary>발열 밴드 외곽으로 출력할 다중선입니다.</summary>
    public IReadOnlyList<OrthographicDxfPolyline> HeatBands { get; }

    /// <summary>발열 경로로 출력할 다중선입니다.</summary>
    public IReadOnlyList<OrthographicDxfPolyline> HeatLines { get; }

    /// <summary>도면별 선 목록을 방어 복사해 정사영 결과를 만듭니다.</summary>
    public OrthographicDxfView(
        IEnumerable<OrthographicDxfPolyline> visibleEdges,
        IEnumerable<OrthographicDxfPolyline> hiddenEdges,
        IEnumerable<OrthographicDxfPolyline> heatBands,
        IEnumerable<OrthographicDxfPolyline> heatLines)
    {
        VisibleEdges = Copy(visibleEdges, nameof(visibleEdges));
        HiddenEdges = Copy(hiddenEdges, nameof(hiddenEdges));
        HeatBands = Copy(heatBands, nameof(heatBands));
        HeatLines = Copy(heatLines, nameof(heatLines));
    }

    /// <summary>모든 선 종류를 하나의 순서 있는 목록으로 열거합니다.</summary>
    public IEnumerable<OrthographicDxfPolyline> AllPolylines() =>
        VisibleEdges.Concat(HiddenEdges).Concat(HeatBands).Concat(HeatLines);

    private static IReadOnlyList<OrthographicDxfPolyline> Copy(
        IEnumerable<OrthographicDxfPolyline> source,
        string parameterName)
    {
        OrthographicDxfPolyline[] copied = source?.ToArray()
            ?? throw new ArgumentNullException(parameterName);
        if (copied.Any(polyline => polyline is null))
            throw new ArgumentException("정사영 도면은 null 다중선을 포함할 수 없습니다.", parameterName);

        return new ReadOnlyCollection<OrthographicDxfPolyline>(copied);
    }
}

/// <summary>기존 HLR 정사영 도면을 DXF 파일로 기록하는 출력 포트입니다.</summary>
public interface IOrthographicDxfExporter
{
    /// <summary>정사영 목록을 간격을 두고 배치해 DXF로 저장합니다.</summary>
    OperationResult<ExportReceipt> ExportViews(
        string path,
        IReadOnlyList<OrthographicDxfView> views,
        CancellationToken cancellationToken = default);
}
