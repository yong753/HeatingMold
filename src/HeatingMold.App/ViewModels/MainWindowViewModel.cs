namespace HeatingMold.App.ViewModels;

/// <summary>MainWindow가 바인딩할 화면별 상태 모델을 보관합니다.</summary>
internal sealed class MainWindowViewModel
{
    /// <summary>경로 생성과 전기 편집에서 공유하는 발열선 사양 입력입니다.</summary>
    public WireSpecificationInputsViewModel WireSpecification { get; }

    /// <summary>경계 여유, 최소 간격과 선경 입력을 제공합니다.</summary>
    public HeatingPathPlacementInputViewModel PlacementParameters { get; }

    /// <summary>회로별 전기 입력과 초안 명령을 제공합니다.</summary>
    public ElectricalSpecificationViewModel ElectricalSpecification { get; }

    /// <summary>경로 생성에 사용하는 표면 앵커와 선택 상태를 제공합니다.</summary>
    public HeatingPathAnchorViewModel PathAnchors { get; }

    /// <summary>경로 생성의 외부 좌표, 연결 여유와 전원 입력을 제공합니다.</summary>
    public HeatingPathCircuitInputViewModel HeatingPathCircuitInputs { get; }

    /// <summary>회로 topology 편집의 임시 계획과 사용자 입력을 소유합니다.</summary>
    public CircuitTopologyEditorViewModel CircuitTopology { get; }

    /// <summary>연결 경로 편집 표의 선택 상태와 행을 소유합니다.</summary>
    public ConnectionPathEditorViewModel ConnectionPaths { get; }

    /// <summary>공유 제어점 선택과 표면·드래그 입력 상태를 소유합니다.</summary>
    public ControlPointEditorViewModel ControlPoints { get; }

    /// <summary>회전 구간 다중선의 내부점 입력과 선택 상태를 소유합니다.</summary>
    public TurnSegmentCurveEditorViewModel TurnSegmentCurves { get; }

    /// <summary>발열 영역 선택을 안정 Face 참조 ID로 관리합니다.</summary>
    public RegionSelectionViewModel RegionSelection { get; }

    /// <summary>경로 생성에 사용할 확정 발열 영역의 선택 상태를 관리합니다.</summary>
    public HeatingPathRegionSelectionViewModel PathRegionSelection { get; }

    /// <summary>작업 좌표계 세션과 축 선택 상태를 제공합니다.</summary>
    public WorkingCoordinateSystemViewModel WorkingCoordinates { get; }

    /// <summary>세션 변경 알림과 화면 복구 상태를 관리합니다.</summary>
    public MainWindowSessionPresentationViewModel SessionPresentation { get; }

    /// <summary>창에서 사용하는 기능별 화면 상태 모델을 조립합니다.</summary>
    public MainWindowViewModel(
        WireSpecificationInputsViewModel wireSpecification,
        HeatingPathPlacementInputViewModel placementParameters,
        ElectricalSpecificationViewModel electricalSpecification)
    {
        WireSpecification = wireSpecification
            ?? throw new System.ArgumentNullException(nameof(wireSpecification));
        PlacementParameters = placementParameters
            ?? throw new System.ArgumentNullException(nameof(placementParameters));
        ElectricalSpecification = electricalSpecification
            ?? throw new System.ArgumentNullException(nameof(electricalSpecification));
        PathAnchors = new HeatingPathAnchorViewModel();
        HeatingPathCircuitInputs = new HeatingPathCircuitInputViewModel();
        CircuitTopology = new CircuitTopologyEditorViewModel();
        ConnectionPaths = new ConnectionPathEditorViewModel();
        ControlPoints = new ControlPointEditorViewModel();
        TurnSegmentCurves = new TurnSegmentCurveEditorViewModel();
        RegionSelection = new RegionSelectionViewModel();
        PathRegionSelection = new HeatingPathRegionSelectionViewModel();
        WorkingCoordinates = new WorkingCoordinateSystemViewModel();
        SessionPresentation = new MainWindowSessionPresentationViewModel();
    }
}
