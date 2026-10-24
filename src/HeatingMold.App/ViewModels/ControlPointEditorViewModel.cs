using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;

namespace HeatingMold.App.ViewModels;

/// <summary>편집 가능한 경로 절점의 안정 식별자와 설계 revision입니다.</summary>
internal sealed record ControlPointChoice(
    HeatingPathId PathId,
    ControlPointId ControlPointId,
    RegionId RegionId,
    DesignRevision Revision,
    string DisplayName);

/// <summary>드래그 시작 시 고정할 절점과 기하 문서 세대입니다.</summary>
internal sealed record ControlPointDragState(
    ControlPointChoice Choice,
    double EndpointToleranceMm,
    GeometryDocumentId DocumentId,
    ModelRevision ModelRevision);

/// <summary>제어점 선택, 표면 위치 입력과 드래그 후보의 화면 상태를 소유합니다.</summary>
internal sealed class ControlPointEditorViewModel : INotifyPropertyChanged
{
    private ControlPointChoice _selectedChoice;

    /// <summary>현재 확정 경로에서 편집할 수 있는 절점 목록입니다.</summary>
    public ObservableCollection<ControlPointChoice> Choices { get; } = new();

    /// <summary>현재 이동 대상으로 선택한 절점입니다.</summary>
    public ControlPointChoice SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (Equals(_selectedChoice, value))
                return;

            _selectedChoice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
        }
    }

    /// <summary>화면에서 지정한 표면 앵커입니다.</summary>
    public SurfaceAnchor TargetAnchor { get; private set; }

    /// <summary>표면 앵커를 지정한 확정 설계 revision입니다.</summary>
    public DesignRevision? TargetRevision { get; private set; }

    /// <summary>포인터 드래그 중 검증에 사용할 고정 입력입니다.</summary>
    public ControlPointDragState ActiveDrag { get; private set; }

    /// <summary>선택 절점 속성 변경을 WPF binding에 알립니다.</summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>새 설계에 맞춰 목록을 갱신하고 같은 안정 ID 선택을 우선 유지합니다.</summary>
    public void RefreshChoices(DesignProject project)
    {
        HeatingPathId? previousPathId = _selectedChoice?.PathId;
        ControlPointId? previousControlPointId = _selectedChoice?.ControlPointId;
        HashSet<ControlPointId> connectionEndpointIds = project?.ConnectionPoints
            .Select(point => point.ControlPointId)
            .ToHashSet() ?? [];
        ControlPointChoice[] choices = project?.HeatingPaths
            .SelectMany((path, pathIndex) => CreateChoices(
                path,
                pathIndex,
                project.Revision,
                connectionEndpointIds))
            .ToArray() ?? [];

        Choices.Clear();
        foreach (ControlPointChoice choice in choices)
            Choices.Add(choice);

        SelectedChoice = previousPathId is { } pathId && previousControlPointId is { } controlPointId
            ? choices.FirstOrDefault(choice =>
                choice.PathId == pathId && choice.ControlPointId == controlPointId)
                ?? choices.FirstOrDefault()
            : choices.FirstOrDefault();

        ClearTargetAnchor();
        ClearActiveDrag();
    }

    /// <summary>경로에서 편집 가능한 연결 절점만 안정 ID 선택 항목으로 만듭니다.</summary>
    public static IReadOnlyList<ControlPointChoice> CreateChoices(
        HeatingPath path,
        int pathIndex,
        DesignRevision revision,
        IReadOnlySet<ControlPointId> connectionEndpointIds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(connectionEndpointIds);

        return path.ControlPoints
            .Select((point, pointIndex) => (point, pointIndex))
            .Where(item => IsEditableControlPoint(item.point.Id, path.Segments, connectionEndpointIds))
            .Select(item => new ControlPointChoice(
                path.Id,
                item.point.Id,
                path.GenerationSource.RegionId,
                revision,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "경로 {0} · 절점 {1} · ID {2}",
                    pathIndex + 1,
                    item.pointIndex + 1,
                    item.point.Id.Value.ToString("N"))))
            .ToArray();
    }

    /// <summary>표면 위치와 그 위치를 선택한 설계 revision을 함께 보관합니다.</summary>
    public void SetTargetAnchor(SurfaceAnchor anchor, DesignRevision? revision)
    {
        TargetAnchor = anchor ?? throw new System.ArgumentNullException(nameof(anchor));
        TargetRevision = revision;
    }

    /// <summary>대상 절점이 바뀌거나 설계를 갱신할 때 표면 위치 초안을 지웁니다.</summary>
    public void ClearTargetAnchor()
    {
        TargetAnchor = null;
        TargetRevision = null;
    }

    /// <summary>현재 절점 편집에 고정된 드래그 입력을 보관합니다.</summary>
    public void BeginDrag(ControlPointDragState drag)
    {
        ActiveDrag = drag ?? throw new System.ArgumentNullException(nameof(drag));
    }

    /// <summary>드래그 취소나 완료 뒤 편집 후보를 해제합니다.</summary>
    public void ClearActiveDrag() => ActiveDrag = null;

    /// <summary>회로 끝점과 미지원 곡선에 연결되지 않은 활성 절점인지 판정합니다.</summary>
    private static bool IsEditableControlPoint(
        ControlPointId pointId,
        IReadOnlyList<HeatingPathSegment> segments,
        IReadOnlySet<ControlPointId> connectionEndpointIds)
    {
        if (connectionEndpointIds.Contains(pointId))
            return false;

        HeatingPathSegment[] attachedSegments = segments
            .Where(segment => segment.StartControlPointId == pointId || segment.EndControlPointId == pointId)
            .ToArray();
        return attachedSegments.Any(segment => segment.IsActive) &&
            attachedSegments.All(segment => segment.Curve is
                LineCurveDefinition or PolylineCurveDefinition or ArcCurveDefinition);
    }
}
