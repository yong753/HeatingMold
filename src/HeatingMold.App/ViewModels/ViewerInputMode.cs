namespace HeatingMold.App.ViewModels;

/// <summary>뷰포트에서 한 번에 활성화할 수 있는 설계 입력 모드입니다.</summary>
internal enum ViewerInputMode
{
    /// <summary>회전과 기본 Face 선택을 허용합니다.</summary>
    Navigate,

    /// <summary>드래그한 사각형 안의 Face를 선택합니다.</summary>
    FaceRectangleSelection,

    /// <summary>발열 영역에 포함할 사각형을 그립니다.</summary>
    RegionInclusionSketch,

    /// <summary>발열 영역에서 제외할 사각형을 그립니다.</summary>
    RegionExclusionSketch,

    /// <summary>작업 평면 위의 금형 표면 앵커를 지정합니다.</summary>
    SurfaceAnchorPicking,

    /// <summary>선택한 제어점을 드래그해 이동합니다.</summary>
    ControlPointEditing,

    /// <summary>발열 경로의 회전 구간을 선택합니다.</summary>
    CircuitTopologyEditing
}

/// <summary>한 입력 모드가 뷰포트에 적용할 동작을 보관합니다.</summary>
internal readonly record struct ViewerInputModeConfiguration(
    bool UsesFaceRectangle,
    bool UsesRegionSketch,
    bool PicksSurfaceAnchor,
    bool EditsControlPoint,
    bool EditsCircuitTopology,
    int DragMode);

/// <summary>입력 모드별 뷰포트 동작을 한곳에서 정의합니다.</summary>
internal static class ViewerInputModePolicy
{
    /// <summary>선택한 모드에 필요한 동작 플래그와 드래그 유형을 반환합니다.</summary>
    public static ViewerInputModeConfiguration GetConfiguration(ViewerInputMode mode) => mode switch
    {
        ViewerInputMode.Navigate => new(false, false, false, false, false, 0),
        ViewerInputMode.FaceRectangleSelection => new(true, false, false, false, false, 1),
        ViewerInputMode.RegionInclusionSketch => new(false, true, false, false, false, 2),
        ViewerInputMode.RegionExclusionSketch => new(false, true, false, false, false, 2),
        ViewerInputMode.SurfaceAnchorPicking => new(false, false, true, false, false, 0),
        ViewerInputMode.ControlPointEditing => new(false, false, false, true, false, 3),
        ViewerInputMode.CircuitTopologyEditing => new(false, false, false, false, true, 0),
        _ => throw new System.ArgumentOutOfRangeException(nameof(mode), mode, "지원하지 않는 뷰포트 입력 모드입니다.")
    };
}

/// <summary>현재 입력 모드와 그에 해당하는 동작 설정을 함께 유지합니다.</summary>
internal sealed class ViewerInputModeState
{
    /// <summary>새 상태는 기본 화면 조작 모드에서 시작합니다.</summary>
    public ViewerInputModeState()
    {
        Mode = ViewerInputMode.Navigate;
        Configuration = ViewerInputModePolicy.GetConfiguration(Mode);
    }

    /// <summary>현재 활성화된 입력 모드입니다.</summary>
    public ViewerInputMode Mode { get; private set; }

    /// <summary>현재 모드가 사용할 뷰포트 동작 설정입니다.</summary>
    public ViewerInputModeConfiguration Configuration { get; private set; }

    /// <summary>입력 모드와 동작 설정을 하나의 전이로 바꿉니다.</summary>
    public void Activate(ViewerInputMode mode)
    {
        ViewerInputModeConfiguration configuration = ViewerInputModePolicy.GetConfiguration(mode);
        Mode = mode;
        Configuration = configuration;
    }
}
