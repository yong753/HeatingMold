using System;
using HeatingMold.App.ViewModels;
using Xunit;

namespace HeatingMold.App.Tests;

/// <summary>MainWindow 세션 화면 상태의 보류 수명과 복구 진단을 검사합니다.</summary>
public sealed class MainWindowSessionPresentationViewModelTests
{
    /// <summary>중첩 보류는 한 단계씩 해제되며 같은 보류 핸들의 중복 해제는 안전합니다.</summary>
    [Fact]
    public void SuppressSessionChangedRefresh_NestsAndDisposesIdempotently()
    {
        var viewModel = new MainWindowSessionPresentationViewModel();
        IDisposable outer = viewModel.SuppressSessionChangedRefresh();
        IDisposable inner = viewModel.SuppressSessionChangedRefresh();

        Assert.True(viewModel.IsRefreshSuppressed);

        outer.Dispose();
        outer.Dispose();
        Assert.True(viewModel.IsRefreshSuppressed);

        inner.Dispose();
        Assert.False(viewModel.IsRefreshSuppressed);
    }

    /// <summary>예외가 나도 using 범위가 세션 화면 갱신 보류를 해제합니다.</summary>
    [Fact]
    public void SuppressSessionChangedRefresh_ReleasesAfterException()
    {
        var viewModel = new MainWindowSessionPresentationViewModel();

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (viewModel.SuppressSessionChangedRefresh())
            {
                throw new InvalidOperationException("시험용 예외");
            }
        }));

        Assert.False(viewModel.IsRefreshSuppressed);
    }
}
