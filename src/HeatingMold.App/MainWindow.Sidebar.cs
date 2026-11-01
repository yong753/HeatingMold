using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 사이드바 탭 구성과 탭 전환 정책.
/// 탭 이동은 화면과 일시적 입력 모드만 바꾸고, 확정 영역·패턴·편집 이력 같은 작업 데이터는
/// 절대 초기화하지 않는다. 초기화가 필요한 경로는 기존 영역/모델 수명주기 함수만 사용한다.
/// </summary>
public partial class MainWindow
{
    private const int AreaTabIndex = 0;
    private const int ExcludeTabIndex = 1;
    private const int PatternTabIndex = 2;
    private const int EditTabIndex = 3;
    private const int ResultsTabIndex = 4;

    private bool _sidebarInitialized;
    private int _activeSidebarTabIndex = AreaTabIndex;

    /// <summary>현재 선택된 사이드바 탭. 초기화 전에는 영역 탭으로 본다.</summary>
    private int ActiveSidebarTab =>
        SidebarTabs != null && SidebarTabs.SelectedIndex >= 0 ? SidebarTabs.SelectedIndex : AreaTabIndex;

    /// <summary>생성자에서 호출한다. 테스트가 창을 표시하지 않아도 초기 상태가 확정되도록 Loaded가 아닌 생성자에 둔다.</summary>
    private void InitializeSidebar()
    {
        if (SidebarTabs == null) return;
        _sidebarInitialized = true;
        SidebarTabs.SelectedIndex = AreaTabIndex;
        ApplySidebarTabState(AreaTabIndex, AreaTabIndex);
    }

    private void SidebarTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 내부 ListBox/ComboBox의 SelectionChanged가 버블링된 경우는 무시한다.
        if (!ReferenceEquals(e.OriginalSource, SidebarTabs)) return;
        if (!_sidebarInitialized) return;

        int next = ActiveSidebarTab;
        if (next == _activeSidebarTabIndex) return;
        int previous = _activeSidebarTabIndex;
        // 발열경로 편집은 특정 탭 소유가 아니므로 어느 탭으로 이동하든 종료한다.
        // 진행 중 드래그는 뷰포트가 확정 없이 취소하고, 완료된 편집·이력은 그대로 둔다.
        ExitVertexEdit();
        ExitTransientModeForTab(previous);
        ApplySidebarTabState(next, previous);
    }

    private void ApplySidebarTabState(int tabIndex, int previousTab)
    {
        _activeSidebarTabIndex = tabIndex;

        // Face 선택은 영역 탭에서만 허용한다. 뷰포트가 자체적으로 선택을 막으므로
        // 이벤트가 올라오기 전에 네이티브 선택 자체가 일어나지 않는다.
        if (Viewport != null)
            Viewport.FaceSelectionEnabled = tabIndex == AreaTabIndex;

        UpdateActionStates();
    }

    /// <summary>탭을 떠날 때 그 탭의 일시적 입력 모드만 종료한다.
    /// 확정 데이터(패턴·경로·Undo/Redo·접속 길이)는 건드리지 않는다.</summary>
    private void ExitTransientModeForTab(int tabIndex)
    {
        switch (tabIndex)
        {
            case ExcludeTabIndex:
                // ExcludeMode_Changed는 프레임이 없으면 조기 반환하므로 뷰포트 플래그를 직접 내린다.
                if (Viewport != null) Viewport.ExclusionSketchMode = false;
                Bridge.SetExclusionPreview(null);
                if (ExcludeModeBox != null && ExcludeModeBox.IsChecked == true)
                    ExcludeModeBox.IsChecked = false;
                break;

            case PatternTabIndex:
                // 인출면 후보 강조/선택 모드만 종료한다. 확정된 인출면과 시작점은 유지된다.
                CancelExitFacePick();
                break;

            case EditTabIndex:
                if (Viewport != null) Viewport.ConnectorEditMode = false;
                if (ConnectorEditModeBox != null && ConnectorEditModeBox.IsChecked == true)
                    ConnectorEditModeBox.IsChecked = false;
                break;
        }
    }

    /// <summary>모델을 새로 열어 작업 상태가 실제로 초기화된 뒤 영역 탭으로 되돌린다.</summary>
    private void ReturnToAreaTab()
    {
        if (SidebarTabs != null && SidebarTabs.SelectedIndex != AreaTabIndex)
            SidebarTabs.SelectedIndex = AreaTabIndex;
        if (_activeSidebarTabIndex != AreaTabIndex)
            ApplySidebarTabState(AreaTabIndex, _activeSidebarTabIndex);
    }
}
