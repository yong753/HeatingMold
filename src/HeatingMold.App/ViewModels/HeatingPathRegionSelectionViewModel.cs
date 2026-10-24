using HeatingMold.Core.Primitives;

namespace HeatingMold.App.ViewModels;

/// <summary>경로 생성에 사용할 확정 영역 선택과 목록 갱신 상태를 관리합니다.</summary>
internal sealed class HeatingPathRegionSelectionViewModel
{
    /// <summary>경로 생성 화면에서 현재 선택한 확정 영역 ID입니다.</summary>
    public RegionId? SelectedRegionId { get; private set; }

    /// <summary>영역 선택 목록을 다시 채우는 동안 선택 변경 이벤트를 무시할지 나타냅니다.</summary>
    public bool IsRefreshingChoices { get; private set; }

    /// <summary>현재 콤보 선택과 저장된 영역 ID 중 복원 우선순위가 높은 ID를 반환합니다.</summary>
    public RegionId? GetPreferredSelection(RegionId? currentSelection) =>
        SelectedRegionId ?? currentSelection;

    /// <summary>영역 선택 목록 갱신 중 발생하는 선택 변경 이벤트를 억제합니다.</summary>
    public void BeginRefreshingChoices() => IsRefreshingChoices = true;

    /// <summary>영역 선택 목록 갱신을 끝냅니다.</summary>
    public void EndRefreshingChoices() => IsRefreshingChoices = false;

    /// <summary>선택한 확정 영역 ID를 저장하고 이전 선택과 달라졌는지 반환합니다.</summary>
    public bool SelectRegion(RegionId? regionId)
    {
        bool changed = SelectedRegionId != regionId;
        SelectedRegionId = regionId;
        return changed;
    }
}
