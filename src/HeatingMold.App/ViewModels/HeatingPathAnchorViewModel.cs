#nullable enable annotations

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.ViewModels;

/// <summary>경로 생성에 필요한 표면 앵커와 화면 표시 상태를 관리합니다.</summary>
internal sealed class HeatingPathAnchorViewModel : INotifyPropertyChanged
{
    private SurfaceAnchor? _heatingStartAnchor;
    private SurfaceAnchor? _heatingEndAnchor;
    private SurfaceAnchor? _startMoldExitAnchor;
    private SurfaceAnchor? _endMoldExitAnchor;
    private string _heatingStartStatusText = "시작 앵커: 미지정";
    private string _heatingEndStatusText = "끝 앵커: 미지정";
    private string _startMoldExitStatusText = "시작 인출: 미지정";
    private string _endMoldExitStatusText = "끝 인출: 미지정";
    private SurfaceAnchorTarget? _pendingSurfaceAnchorTarget;

    /// <summary>발열 경로 시작 위치를 고정한 표면 앵커입니다.</summary>
    public SurfaceAnchor? HeatingStartAnchor => _heatingStartAnchor;

    /// <summary>발열 경로 끝 위치를 고정한 표면 앵커입니다.</summary>
    public SurfaceAnchor? HeatingEndAnchor => _heatingEndAnchor;

    /// <summary>시작부 금형 인출 위치를 고정한 표면 앵커입니다.</summary>
    public SurfaceAnchor? StartMoldExitAnchor => _startMoldExitAnchor;

    /// <summary>끝부 금형 인출 위치를 고정한 표면 앵커입니다.</summary>
    public SurfaceAnchor? EndMoldExitAnchor => _endMoldExitAnchor;

    /// <summary>시작 앵커 선택 상태를 화면에 표시합니다.</summary>
    public string HeatingStartStatusText => _heatingStartStatusText;

    /// <summary>끝 앵커 선택 상태를 화면에 표시합니다.</summary>
    public string HeatingEndStatusText => _heatingEndStatusText;

    /// <summary>시작부 인출 앵커 선택 상태를 화면에 표시합니다.</summary>
    public string StartMoldExitStatusText => _startMoldExitStatusText;

    /// <summary>끝부 인출 앵커 선택 상태를 화면에 표시합니다.</summary>
    public string EndMoldExitStatusText => _endMoldExitStatusText;

    /// <summary>뷰포트에서 다음으로 확정할 표면 앵커의 대상입니다.</summary>
    public SurfaceAnchorTarget? PendingSurfaceAnchorTarget => _pendingSurfaceAnchorTarget;

    /// <summary>경로 생성에 필요한 네 앵커가 모두 있는지 나타냅니다.</summary>
    public bool HasRequiredAnchors =>
        HeatingStartAnchor is not null &&
        HeatingEndAnchor is not null &&
        StartMoldExitAnchor is not null &&
        EndMoldExitAnchor is not null;

    /// <summary>화면 바인딩에 상태가 바뀌었음을 알립니다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>발열 경로 시작 표면 앵커를 지정하고 화면 설명을 갱신합니다.</summary>
    public void SetHeatingStartAnchor(SurfaceAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        SetAnchor(ref _heatingStartAnchor, anchor, "발열 경로 시작", nameof(HeatingStartAnchor),
            ref _heatingStartStatusText, "HeatingStartStatusText", "시작 앵커: 미지정");
    }

    /// <summary>발열 경로 끝 표면 앵커를 지정하고 화면 설명을 갱신합니다.</summary>
    public void SetHeatingEndAnchor(SurfaceAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        SetAnchor(ref _heatingEndAnchor, anchor, "발열 경로 끝", nameof(HeatingEndAnchor),
            ref _heatingEndStatusText, "HeatingEndStatusText", "끝 앵커: 미지정");
    }

    /// <summary>시작부 인출 표면 앵커를 지정하고 화면 설명을 갱신합니다.</summary>
    public void SetStartMoldExitAnchor(SurfaceAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        SetAnchor(ref _startMoldExitAnchor, anchor, "시작부 금형 인출", nameof(StartMoldExitAnchor),
            ref _startMoldExitStatusText, "StartMoldExitStatusText", "시작 인출: 미지정");
    }

    /// <summary>끝부 인출 표면 앵커를 지정하고 화면 설명을 갱신합니다.</summary>
    public void SetEndMoldExitAnchor(SurfaceAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        SetAnchor(ref _endMoldExitAnchor, anchor, "끝부 금형 인출", nameof(EndMoldExitAnchor),
            ref _endMoldExitStatusText, "EndMoldExitStatusText", "끝 인출: 미지정");
    }

    /// <summary>지정한 대상을 다음 표면 앵커 입력으로 설정합니다.</summary>
    public void BeginPicking(SurfaceAnchorTarget target)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));

        _pendingSurfaceAnchorTarget = target;
    }

    /// <summary>대기 중인 앵커 입력 대상을 가져오고 대기 상태를 끝냅니다.</summary>
    public bool TryCompletePicking(out SurfaceAnchorTarget target)
    {
        if (_pendingSurfaceAnchorTarget is not { } pendingTarget)
        {
            target = default;
            return false;
        }

        target = pendingTarget;
        _pendingSurfaceAnchorTarget = null;
        return true;
    }

    /// <summary>진행 중인 표면 앵커 입력 대기를 취소합니다.</summary>
    public void CancelPicking() => _pendingSurfaceAnchorTarget = null;

    /// <summary>영역이 바뀌었을 때 네 앵커와 표시 상태를 함께 초기화합니다.</summary>
    public void Clear()
    {
        CancelPicking();
        SetAnchor(ref _heatingStartAnchor, null, "", nameof(HeatingStartAnchor),
            ref _heatingStartStatusText, "HeatingStartStatusText", "시작 앵커: 미지정");
        SetAnchor(ref _heatingEndAnchor, null, "", nameof(HeatingEndAnchor),
            ref _heatingEndStatusText, "HeatingEndStatusText", "끝 앵커: 미지정");
        SetAnchor(ref _startMoldExitAnchor, null, "", nameof(StartMoldExitAnchor),
            ref _startMoldExitStatusText, "StartMoldExitStatusText", "시작 인출: 미지정");
        SetAnchor(ref _endMoldExitAnchor, null, "", nameof(EndMoldExitAnchor),
            ref _endMoldExitStatusText, "EndMoldExitStatusText", "끝 인출: 미지정");
    }

    private void SetAnchor(
        ref SurfaceAnchor? anchorField,
        SurfaceAnchor? anchor,
        string label,
        string anchorPropertyName,
        ref string statusField,
        string statusPropertyName,
        string emptyStatus)
    {
        bool anchorChanged = !EqualityComparer<SurfaceAnchor?>.Default.Equals(anchorField, anchor);
        string status = anchor is null ? emptyStatus : FormatStatus(label, anchor);
        bool statusChanged = statusField != status;
        if (!anchorChanged && !statusChanged)
            return;

        anchorField = anchor;
        statusField = status;
        if (anchorChanged)
            OnPropertyChanged(anchorPropertyName);
        if (statusChanged)
            OnPropertyChanged(statusPropertyName);
        OnPropertyChanged(nameof(HasRequiredAnchors));
    }

    private static string FormatStatus(string label, SurfaceAnchor anchor)
    {
        string faceId = anchor.FaceReferenceId.Value.ToString("N", CultureInfo.CurrentCulture);
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}: Face {1}, U {2:F2}, V {3:F2}",
            label,
            faceId.Substring(0, 8),
            anchor.WorkingUv.X,
            anchor.WorkingUv.Y);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>표면 앵커 입력 중 사용자가 지정하려는 위치의 종류입니다.</summary>
internal enum SurfaceAnchorTarget
{
    /// <summary>발열 경로의 시작 위치입니다.</summary>
    HeatingStart,

    /// <summary>발열 경로의 끝 위치입니다.</summary>
    HeatingEnd,

    /// <summary>시작부의 금형 표면 인출 위치입니다.</summary>
    StartMoldExit,

    /// <summary>끝부의 금형 표면 인출 위치입니다.</summary>
    EndMoldExit,

    /// <summary>편집 중인 제어점의 새 표면 위치입니다.</summary>
    ControlPointTarget,

    /// <summary>신규 회로 연결부 끝점의 표면 위치입니다.</summary>
    CircuitEndpoint
}
