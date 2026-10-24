using System;

namespace HeatingMold.App.ViewModels;

/// <summary>세션 변경에 따른 화면 갱신과 복구 진단 상태를 관리합니다.</summary>
internal sealed class MainWindowSessionPresentationViewModel
{
    private int _refreshSuppressionCount;

    /// <summary>Native 표시 복구가 필요할 때 화면에 전달할 진단입니다.</summary>
    public string RepairMessage { get; set; }

    /// <summary>세션 변경 이벤트에 대한 화면 갱신이 보류 중인지 나타냅니다.</summary>
    public bool IsRefreshSuppressed => _refreshSuppressionCount > 0;

    /// <summary>호출자가 직접 화면 갱신을 마칠 동안 세션 이벤트 반영을 보류합니다.</summary>
    public IDisposable SuppressSessionChangedRefresh()
    {
        _refreshSuppressionCount++;
        return new RefreshSuppression(this);
    }

    private void ResumeSessionChangedRefresh()
    {
        if (_refreshSuppressionCount <= 0)
            throw new InvalidOperationException("화면 갱신 보류 상태가 올바르지 않습니다.");

        _refreshSuppressionCount--;
    }

    private sealed class RefreshSuppression : IDisposable
    {
        private MainWindowSessionPresentationViewModel _owner;

        public RefreshSuppression(MainWindowSessionPresentationViewModel owner) => _owner = owner;

        public void Dispose()
        {
            MainWindowSessionPresentationViewModel owner = _owner;
            if (owner is null)
                return;

            _owner = null;
            owner.ResumeSessionChangedRefresh();
        }
    }
}
