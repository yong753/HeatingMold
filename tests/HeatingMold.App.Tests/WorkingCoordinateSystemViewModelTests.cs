using System.Collections.Generic;
using System.ComponentModel;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Geometry;
using Xunit;

namespace HeatingMold.App.Tests;

/// <summary>작업 좌표계 축 선택 ViewModel의 변환과 알림을 검사합니다.</summary>
public sealed class WorkingCoordinateSystemViewModelTests
{
    /// <summary>축 인덱스와 법선 방향이 양방향으로 일치합니다.</summary>
    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, -1, 0, 0)]
    [InlineData(2, 0, 1, 0)]
    [InlineData(3, 0, -1, 0)]
    [InlineData(4, 0, 0, 1)]
    [InlineData(5, 0, 0, -1)]
    public void AxisSelection_MapsToAndFromNormal(int axisIndex, double x, double y, double z)
    {
        var normal = new Vec3(x, y, z);

        Assert.Equal(normal, WorkingCoordinateSystemViewModel.NormalForAxisIndex(axisIndex));
        Assert.Equal(axisIndex, WorkingCoordinateSystemViewModel.AxisIndexForNormal(normal));
    }

    /// <summary>확정 세션의 법선으로 현재 콤보 선택을 복원합니다.</summary>
    [Fact]
    public void ConfirmedAxisIndex_UsesTheSessionNormal()
    {
        var viewModel = new WorkingCoordinateSystemViewModel();
        var normal = new Vec3(0, -1, 0);
        var coordinateSystem = new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(7),
            new ModelRevision(3),
            new ProjectionFrame(default, normal, new Vec3(1, 0, 0)));

        viewModel.Session.SetPreview(coordinateSystem);
        viewModel.Session.Confirm(new ModelRevision(3));

        Assert.Equal(3, viewModel.ConfirmedAxisIndex);
    }

    /// <summary>모델 축으로 선택을 복원하면 바인딩 알림이 한 번 나고 동기화 guard가 해제됩니다.</summary>
    [Fact]
    public void SynchronizeAxisSelection_NotifiesAndRestoresGuard()
    {
        var viewModel = new WorkingCoordinateSystemViewModel();
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.SynchronizeAxisSelection(5);

        Assert.Equal(5, viewModel.SelectedAxisIndex);
        Assert.Equal(new Vec3(0, 0, -1), viewModel.SelectedNormal);
        Assert.Equal(new[] { nameof(viewModel.SelectedAxisIndex) }, changedProperties);
        Assert.False(viewModel.IsSynchronizingAxisSelection);
    }

    /// <summary>좌표계 안내 문구가 바뀌면 WPF 바인딩에 변경을 알립니다.</summary>
    [Fact]
    public void DisplayStatus_NotifiesBindingsWhenUpdated()
    {
        var viewModel = new WorkingCoordinateSystemViewModel();
        string changedProperty = null;
        viewModel.PropertyChanged += (_, eventArgs) => changedProperty = eventArgs.PropertyName;

        viewModel.DisplayStatus = "작업 좌표계 · 모델을 불러오세요";

        Assert.Equal(nameof(viewModel.DisplayStatus), changedProperty);
    }
}
