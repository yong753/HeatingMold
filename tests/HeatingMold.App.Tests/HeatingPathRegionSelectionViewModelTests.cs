using HeatingMold.App.ViewModels;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>경로 생성 영역 선택의 복원·갱신 상태를 검사합니다.</summary>
public sealed class HeatingPathRegionSelectionViewModelTests
{
    /// <summary>화면 목록을 다시 채울 때 이전에 확정한 선택 ID를 우선 복원합니다.</summary>
    [Fact]
    public void GetPreferredSelection_PreservesSelectedRegionBeforeRefresh()
    {
        RegionId selectedRegion = RegionId.New();
        RegionId currentComboSelection = RegionId.New();
        var viewModel = new HeatingPathRegionSelectionViewModel();
        viewModel.SelectRegion(selectedRegion);

        Assert.Equal(selectedRegion, viewModel.GetPreferredSelection(currentComboSelection));
    }

    /// <summary>목록 갱신 플래그의 수명과 선택 변경 이벤트를 구분합니다.</summary>
    [Fact]
    public void RefreshGuardAndSelectionChange_AreTrackedSeparately()
    {
        RegionId firstRegion = RegionId.New();
        RegionId secondRegion = RegionId.New();
        var viewModel = new HeatingPathRegionSelectionViewModel();

        Assert.True(viewModel.SelectRegion(firstRegion));
        Assert.False(viewModel.SelectRegion(firstRegion));
        viewModel.BeginRefreshingChoices();
        Assert.True(viewModel.IsRefreshingChoices);
        Assert.True(viewModel.SelectRegion(secondRegion));
        viewModel.EndRefreshingChoices();

        Assert.False(viewModel.IsRefreshingChoices);
        Assert.Equal(secondRegion, viewModel.SelectedRegionId);
        Assert.Equal(secondRegion, viewModel.GetPreferredSelection(null));
    }
}
