using System;
using System.Collections.Generic;
using System.Globalization;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>경로 생성 표면 앵커의 화면 상태 전이를 검증합니다.</summary>
public sealed class HeatingPathAnchorViewModelTests
{
    /// <summary>네 앵커 선택과 초기화가 값·문구·바인딩 알림을 함께 갱신합니다.</summary>
    [Fact]
    public void AnchorSelectionAndClear_UpdateStateAndNotifyBindings()
    {
        var viewModel = new HeatingPathAnchorViewModel();
        viewModel.BeginPicking(SurfaceAnchorTarget.HeatingStart);
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);
        SurfaceAnchor start = CreateAnchor("12345678-0000-0000-0000-000000000001", 1.25, 2.5);
        SurfaceAnchor end = CreateAnchor("22345678-0000-0000-0000-000000000002", 3.5, 4.75);
        SurfaceAnchor startExit = CreateAnchor("32345678-0000-0000-0000-000000000003", 5.0, 6.0);
        SurfaceAnchor endExit = CreateAnchor("42345678-0000-0000-0000-000000000004", 7.0, 8.0);

        viewModel.SetHeatingStartAnchor(start);
        viewModel.SetHeatingEndAnchor(end);
        viewModel.SetStartMoldExitAnchor(startExit);
        viewModel.SetEndMoldExitAnchor(endExit);

        Assert.True(viewModel.HasRequiredAnchors);
        Assert.Same(start, viewModel.HeatingStartAnchor);
        Assert.Same(end, viewModel.HeatingEndAnchor);
        Assert.Same(startExit, viewModel.StartMoldExitAnchor);
        Assert.Same(endExit, viewModel.EndMoldExitAnchor);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, "발열 경로 시작: Face 12345678, U {0:F2}, V {1:F2}", 1.25, 2.5),
            viewModel.HeatingStartStatusText);
        Assert.Contains(nameof(HeatingPathAnchorViewModel.HeatingStartAnchor), changedProperties);
        Assert.Contains(nameof(HeatingPathAnchorViewModel.HeatingStartStatusText), changedProperties);
        Assert.Contains(nameof(HeatingPathAnchorViewModel.HasRequiredAnchors), changedProperties);

        viewModel.Clear();

        Assert.False(viewModel.HasRequiredAnchors);
        Assert.Null(viewModel.HeatingStartAnchor);
        Assert.Null(viewModel.HeatingEndAnchor);
        Assert.Null(viewModel.StartMoldExitAnchor);
        Assert.Null(viewModel.EndMoldExitAnchor);
        Assert.Null(viewModel.PendingSurfaceAnchorTarget);
        Assert.Equal("시작 앵커: 미지정", viewModel.HeatingStartStatusText);
        Assert.Equal("끝 앵커: 미지정", viewModel.HeatingEndStatusText);
        Assert.Equal("시작 인출: 미지정", viewModel.StartMoldExitStatusText);
        Assert.Equal("끝 인출: 미지정", viewModel.EndMoldExitStatusText);
    }

    /// <summary>표면 클릭은 대기 대상을 한 번 소비하고 취소는 입력 대기를 끝냅니다.</summary>
    [Fact]
    public void PendingPick_IsConsumedOnceOrCancelled()
    {
        var viewModel = new HeatingPathAnchorViewModel();

        viewModel.BeginPicking(SurfaceAnchorTarget.CircuitEndpoint);
        Assert.Equal(SurfaceAnchorTarget.CircuitEndpoint, viewModel.PendingSurfaceAnchorTarget);
        Assert.True(viewModel.TryCompletePicking(out SurfaceAnchorTarget completedTarget));
        Assert.Equal(SurfaceAnchorTarget.CircuitEndpoint, completedTarget);
        Assert.Null(viewModel.PendingSurfaceAnchorTarget);
        Assert.False(viewModel.TryCompletePicking(out _));

        viewModel.BeginPicking(SurfaceAnchorTarget.ControlPointTarget);
        viewModel.CancelPicking();
        Assert.Null(viewModel.PendingSurfaceAnchorTarget);
    }

    private static SurfaceAnchor CreateAnchor(string faceId, double u, double v) =>
        new(
            new FaceRefId(Guid.Parse(faceId)),
            new Vec2(u, v),
            new Vec2(u, v),
            new Vec3(u, v, 0));
}
