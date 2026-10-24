using netDxf.Tables;
using netDxf;

namespace HeatingMold.Infrastructure.Export;

/// <summary>DXF 도면에서 경계, 발열선, 연결선과 사양을 구분하는 레이어 이름입니다.</summary>
public static class DxfLayerCatalog
{
    /// <summary>금형의 선택 영역 경계 레이어입니다.</summary>
    public const string MoldOutline = "MOLD_OUTLINE";

    /// <summary>발열 경로 레이어입니다.</summary>
    public const string HeatingWire = "HEAT_WIRE";

    /// <summary>회로 연결 경로 레이어입니다.</summary>
    public const string ConnectionWire = "CONNECTION_WIRE";

    /// <summary>실제 외부 연결점 표식과 회로 끝 이름 레이어입니다.</summary>
    public const string ConnectionPoint = "HEAT_CONNECTION_POINT";

    /// <summary>도면에서 편집할 수 있는 사양 표 레이어입니다.</summary>
    public const string Specification = "SPECIFICATION";

    /// <summary>표준 색상으로 DXF 레이어를 생성합니다.</summary>
    public static IReadOnlyList<Layer> CreateLayers() =>
    [
        new Layer(MoldOutline) { Color = AciColor.Cyan },
        new Layer(HeatingWire) { Color = AciColor.Red },
        new Layer(ConnectionWire) { Color = AciColor.Green },
        new Layer(ConnectionPoint) { Color = AciColor.Magenta },
        new Layer(Specification) { Color = AciColor.Yellow }
    ];
}
