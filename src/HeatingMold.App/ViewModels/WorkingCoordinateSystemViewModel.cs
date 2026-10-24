using System.ComponentModel;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.ViewModels;

/// <summary>작업 좌표계 세션과 축 선택 화면 상태를 함께 관리합니다.</summary>
internal sealed class WorkingCoordinateSystemViewModel : INotifyPropertyChanged
{
    private int _selectedAxisIndex = 4;
    private string _displayStatus = "작업 좌표계 미확정 · 발열 경로 생성 보류";

    /// <summary>확정·미리보기 좌표계를 보관하는 응용 세션입니다.</summary>
    public WorkingCoordinateSystemSession Session { get; } = new();

    /// <summary>축 콤보 상자에서 선택한 방향의 인덱스입니다.</summary>
    public int SelectedAxisIndex
    {
        get => _selectedAxisIndex;
        set
        {
            if (_selectedAxisIndex == value)
                return;

            _selectedAxisIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAxisIndex)));
        }
    }

    /// <summary>현재 좌표계와 화면 상태를 설명하는 안내 문구입니다.</summary>
    public string DisplayStatus
    {
        get => _displayStatus;
        set
        {
            if (_displayStatus == value)
                return;

            _displayStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayStatus)));
        }
    }

    /// <summary>프로젝트의 현재 작업 좌표계에 해당하는 콤보 상자 인덱스입니다.</summary>
    public int ConfirmedAxisIndex => Session.Confirmed is { } coordinateSystem
        ? AxisIndexForNormal(coordinateSystem.Projection.Normal)
        : 4;

    /// <summary>축 선택 UI를 코드에서 갱신 중인지 나타냅니다.</summary>
    public bool IsSynchronizingAxisSelection { get; private set; }

    /// <summary>선택 인덱스를 법선으로 바꾸며, 미선택 값은 기본 +Z 방향으로 처리합니다.</summary>
    public Vec3 SelectedNormal => NormalForAxisIndex(SelectedAxisIndex);

    /// <summary>축 선택을 화면과 맞추고 그에 따른 선택 변경 이벤트를 억제합니다.</summary>
    public void SynchronizeAxisSelection(int axisIndex)
    {
        IsSynchronizingAxisSelection = true;
        try
        {
            SelectedAxisIndex = axisIndex;
        }
        finally
        {
            IsSynchronizingAxisSelection = false;
        }
    }

    /// <summary>축 콤보 상자의 인덱스를 작업 평면 바깥 방향 법선으로 변환합니다.</summary>
    public static Vec3 NormalForAxisIndex(int axisIndex) => axisIndex switch
    {
        0 => new Vec3(1, 0, 0),
        1 => new Vec3(-1, 0, 0),
        2 => new Vec3(0, 1, 0),
        3 => new Vec3(0, -1, 0),
        5 => new Vec3(0, 0, -1),
        _ => new Vec3(0, 0, 1)
    };

    /// <summary>축 법선에 대응하는 콤보 상자 인덱스를 반환합니다.</summary>
    public static int AxisIndexForNormal(Vec3 normal) => normal switch
    {
        { X: 1, Y: 0, Z: 0 } => 0,
        { X: -1, Y: 0, Z: 0 } => 1,
        { X: 0, Y: 1, Z: 0 } => 2,
        { X: 0, Y: -1, Z: 0 } => 3,
        { X: 0, Y: 0, Z: -1 } => 5,
        _ => 4
    };

    /// <summary>ViewModel 속성 변경을 WPF 바인딩에 알립니다.</summary>
    public event PropertyChangedEventHandler PropertyChanged;
}
