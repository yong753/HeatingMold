using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.ConnectionPath;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private CircuitTopologyEditorViewModel CircuitTopologyEditor => _mainWindowViewModel.CircuitTopology;

    /// <summary>회로 topology 모드를 켜거나 현재 미리보기를 취소합니다.</summary>
    private void CircuitSplitMode_Changed(object sender, RoutedEventArgs e)
    {
        if (CircuitTopologyEditor.IsModeChangeSuppressed)
            return;

        if (CircuitSplitModeBox.IsChecked != true)
        {
            Viewport.CircuitSplitMode = false;
            CircuitTurnSegmentsList.IsEnabled = false;
            CircuitTopologyEndpointCombo.IsEnabled = false;
            ResetCircuitTopologyPreview(restoreCommittedPresentation: true);
            RefreshCircuitTurnChoices(_projectSession.Current.CurrentProject);
            return;
        }

        DesignProject project = _projectSession.Current.CurrentProject;
        if (project is null || !project.HeatingPaths.SelectMany(path => path.Segments)
                .Any(segment => segment.Kind == HeatingPathSegmentKind.Turn))
        {
            StatusText.Text = "회전 구간이 있는 확정 발열 경로가 있어야 회로를 편집할 수 있습니다.";
            SetCircuitTopologyMode(false);
            return;
        }

        if (ExcludeModeBox.IsChecked == true || FaceDragSelectModeBox.IsChecked == true)
        {
            ExcludeModeBox.IsChecked = false;
            FaceDragSelectModeBox.IsChecked = false;
        }

        Viewport.CircuitSplitMode = true;
        CircuitTurnSegmentsList.IsEnabled = true;
        CircuitTopologyEndpointCombo.IsEnabled = true;
        RefreshCircuitTopologyPreview();
        StatusText.Text = "회전 구간을 클릭해 임시 회로 구성을 바꾸세요. 적용 전까지 설계에는 반영되지 않습니다.";
    }

    /// <summary>Native pick 런타임 ID를 안정 구간 ID로 바꾸고 topology 미리보기를 다시 계산합니다.</summary>
    private void OnTurnSegmentPicked(int runtimeId)
    {
        if (!Viewport.CircuitSplitMode)
            return;

        SessionSnapshot snapshot = _projectSession.Current;
        DesignProject project = snapshot.CurrentProject;
        if (project is null ||
            !_geometryWorkspace.TryResolveActiveHeatingPathSegmentId(runtimeId, out HeatingPathSegmentId segmentId))
        {
            StatusText.Text = "선택한 Native 구간을 현재 프로젝트의 안정 ID에 연결하지 못했습니다.";
            return;
        }

        HeatingPathSegment segment = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .SingleOrDefault(candidate => candidate.Id == segmentId);
        if (segment is null || segment.Kind != HeatingPathSegmentKind.Turn)
        {
            StatusText.Text = "선택한 항목은 현재 설계의 회전 구간이 아닙니다.";
            return;
        }

        bool currentState = CircuitTopologyEditor.TryGetTurnState(segmentId, out bool previewState)
            ? previewState
            : segment.IsActive;
        bool nextState = !currentState;
        SetCircuitTurnChoice(segmentId, nextState);
        ApplyTurnSegmentDraftState(segmentId, nextState);
    }

    /// <summary>회전 구간 hover ID를 안정 ID에 대응해 상태 표시줄에 표시합니다.</summary>
    private void OnTurnSegmentHovered(int runtimeId)
    {
        if (runtimeId < 0 ||
            !_geometryWorkspace.TryResolveActiveHeatingPathSegmentId(runtimeId, out HeatingPathSegmentId segmentId))
        {
            HoverText.Text = string.Empty;
            return;
        }

        DesignProject project = _projectSession.Current.CurrentProject;
        HeatingPathSegment segment = project?.HeatingPaths
            .SelectMany(path => path.Segments)
            .SingleOrDefault(candidate => candidate.Id == segmentId);
        HoverText.Text = segment is null
            ? string.Empty
            : string.Concat("회전 구간 ID ", ShortId(segmentId.Value));
    }

    /// <summary>확정 회로의 안정 구간 ID를 Native 런타임 ID로 바꿔 hover 강조를 갱신합니다.</summary>
    private void OnHeatingPathSegmentHovered(int runtimeId)
    {
        SessionSnapshot snapshot = _projectSession.Current;
        if (runtimeId >= 0 && snapshot.CurrentProject is { } project &&
            _geometryWorkspace.TryResolveActiveHeatingPathSegmentId(runtimeId, out HeatingPathSegmentId segmentId))
        {
            Circuit circuit = project.Circuits.SingleOrDefault(candidate =>
                candidate.Segments.Any(reference => reference.SegmentId == segmentId));
            if (circuit is not null)
            {
                int[] runtimeIds = circuit.Segments
                    .Select(reference => _geometryWorkspace.TryGetActiveRuntimeId(reference.SegmentId, out int mappedId)
                        ? mappedId
                        : -1)
                    .Where(mappedId => mappedId > 0)
                    .ToArray();
                Bridge.HighlightCircuit(runtimeIds);
                HoverText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "회로 {0} · ID {1}",
                    circuit.Sequence + 1,
                    ShortId(circuit.Id.Value));
                return;
            }
        }

        Bridge.HighlightCircuit(Array.Empty<int>());
        HoverText.Text = string.Empty;
    }

    /// <summary>survivor 선택이 바뀌면 현재 draft 상태로 topology를 다시 분석합니다.</summary>
    private void CircuitSurvivor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CircuitTopologyEditor.SelectSurvivorChoice(
            CircuitSurvivorCombo.SelectedItem as CircuitSurvivorChoice);
        if (Viewport?.CircuitSplitMode == true)
            RefreshCircuitTopologyPreview();
    }

    /// <summary>신규 끝점 선택이 바뀌면 표면·외부 좌표 입력 상태를 표시합니다.</summary>
    private void CircuitTopologyEndpoint_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CircuitTopologyEditor.SelectEndpointChoice(
            CircuitTopologyEndpointCombo.SelectedItem as CircuitTopologyEndpointChoice);
        RefreshCircuitEndpointStatus();
    }

    /// <summary>새 회로 이름을 보존할 기존 회로 ID를 지정해 topology 후보를 적용합니다.</summary>
    private void ApplyCircuitTopology_Click(object sender, RoutedEventArgs e)
    {
        if (CircuitTopologyEditor.Plan is null)
        {
            RefreshCircuitTopologyPreview();
            if (CircuitTopologyEditor.Plan is null)
                return;
        }

        SessionSnapshot snapshot = _projectSession.Current;
        if (snapshot.CurrentProject is not { } project || project.Revision != CircuitTopologyEditor.Plan.BaseRevision)
        {
            CancelCircuitTopologyPreview();
            StatusText.Text = "설계 revision이 바뀌어 topology 미리보기를 취소했습니다. 다시 선택하세요.";
            return;
        }

        if (!CircuitTopologyChangeDetector.HasChanges(project, CircuitTopologyEditor.Plan))
        {
            StatusText.Text = "적용할 회로 topology 변경이 없습니다.";
            return;
        }

        if (!CircuitTopologyEditor.TryBuildCircuitEndpointInputs(
                project,
                CircuitTopologyEditor.Plan,
                out Dictionary<CircuitId, CircuitEndpointPair> endpoints,
                out HashSet<ConnectionPointId> newConnectionPointIds,
                out string inputFailure))
        {
            StatusText.Text = inputFailure;
            return;
        }

        IGeometryQueries geometryQueries = null;
        Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput> connectionPathInputs = new();
        if (newConnectionPointIds.Count > 0)
        {
            if (!WorkingGenerationReady() ||
                WorkingCoordinates.Session.Confirmed is not { } coordinateSystem ||
                _projectSession.Current.Geometry is not { } geometry ||
                coordinateSystem.Revision != geometry.ModelRevision)
            {
                StatusText.Text = "새 연결 경로를 검증할 작업 좌표계와 현재 기하 스냅샷이 필요합니다.";
                return;
            }

            try
            {
                IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices = _faceReferenceIndex.NativeIndicesByReference;
                geometryQueries = AppCompositionRoot.CreateOcctGeometryQueries(
                    coordinateSystem,
                    nativeFaceIndices);
                if (!CircuitTopologyEditor.TryBuildConnectionPathInputs(
                    newConnectionPointIds,
                    coordinateSystem,
                    geometry.ModelRevision,
                    TopologyMoldExitAllowanceBox.Text,
                    TopologyExternalAllowanceBox.Text,
                    out connectionPathInputs,
                    out string connectionInputFailure))
                {
                    StatusText.Text = connectionInputFailure;
                    return;
                }
            }
            catch (ArgumentException exception)
            {
                StatusText.Text = string.Concat("연결 경로 입력이 유효하지 않습니다: ", exception.Message);
                return;
            }
        }

        CandidateOutcome outcome = _changeCircuitTopology.Execute(
            CircuitTopologyEditor.Plan,
            endpoints,
            cancellationToken: default,
            newConnectionPathInputs: connectionPathInputs,
            geometryQueries: geometryQueries);
        if (outcome.IsCommitted)
        {
            StatusText.Text = outcome.Status == CandidateStatus.PostCommitFailure
                ? string.Concat("회로 구성을 확정했지만 화면 복구가 필요합니다: ", FormatIssues(outcome.Issues))
                : "회로 구성을 프로젝트에 확정했습니다.";
            return;
        }

        string diagnostic = FormatIssues(outcome.Issues);
        CancelCircuitTopologyPreview();
        StatusText.Text = string.Concat("회로 구성을 적용하지 않았습니다: ", diagnostic);
    }

    /// <summary>회로 topology 미리보기와 Native 임시 표시를 취소합니다.</summary>
    private void CancelCircuitTopology_Click(object sender, RoutedEventArgs e) =>
        CancelCircuitTopologyPreview();

    /// <summary>현재 확정 회로에서 survivor로 지정할 회로 목록을 안정 ID와 함께 표시합니다.</summary>
    private void RefreshCircuitSurvivorChoices(DesignProject project)
    {
        CircuitTopologyEditor.RefreshSurvivorChoices(project?.Circuits ?? Array.Empty<Circuit>());
        CircuitSurvivorCombo.ItemsSource = CircuitTopologyEditor.SurvivorChoices;
        CircuitSurvivorCombo.SelectedItem = CircuitTopologyEditor.SelectedSurvivorChoice;
    }

    /// <summary>확정 프로젝트의 모든 회전 구간을 안정 ID와 활성 상태로 목록화합니다.</summary>
    private void RefreshCircuitTurnChoices(DesignProject project)
    {
        IEnumerable<HeatingPathSegment> segments = project?.HeatingPaths
            .SelectMany(path => path.Segments) ?? Array.Empty<HeatingPathSegment>();
        CircuitTopologyEditor.RefreshTurnChoices(segments);
        CircuitTurnSegmentsList.ItemsSource = CircuitTopologyEditor.TurnChoices;
    }

    /// <summary>체크 목록에서 회전 구간 활성 초안이 바뀌면 Native와 topology 미리보기를 갱신합니다.</summary>
    private void CircuitTurnChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: CircuitTurnChoice choice } checkBox)
            return;

        ApplyTurnSegmentDraftState(choice.SegmentId, checkBox.IsChecked == true);
    }

    /// <summary>전기·길이 표를 확정 GeometrySnapshot의 회로별 곡선 길이로 갱신합니다.</summary>
    private void RefreshCircuitLengthTable(SessionSnapshot snapshot)
    {
        RefreshElectricalResults(snapshot);
        RefreshElectricalSpecificationEditor(snapshot.CurrentProject);
        DesignProject project = snapshot.CurrentProject;
        if (project is null || snapshot.Geometry is not { } geometry || project.Circuits.Count == 0)
        {
            CircuitLengthGrid.ItemsSource = null;
            return;
        }

        CircuitLengthGrid.ItemsSource = CircuitLengthTablePresentation.BuildRows(project, geometry);
    }

    /// <summary>현재 segment draft를 Application에서 분석해 미리보기 상태를 갱신합니다.</summary>
    private void RefreshCircuitTopologyPreview()
    {
        SessionSnapshot snapshot = _projectSession.Current;
        if (snapshot.CurrentProject is not { } project)
        {
            CircuitTopologyEditor.Reset();
            CircuitTopologyEndpointCombo.ItemsSource = null;
            RefreshCircuitEndpointStatus();
            CircuitTopologyPreviewText.Text = "미리보기: 확정 설계 없음";
            return;
        }

        var result = _changeCircuitTopology.Analyze(
            project.Revision,
            requestedMergeSurvivor: CircuitTopologyEditor.SelectedSurvivorChoice?.CircuitId,
            turnSegmentStates: CircuitTopologyEditor.TurnStates);
        if (!result.IsSuccess)
        {
            CircuitTopologyEditor.SetPlan(null);
            CircuitTopologyEndpointCombo.ItemsSource = null;
            CircuitTopologyEditor.ClearEndpointDrafts();
            RefreshCircuitEndpointStatus();
            string diagnostic = FormatIssues(result.Issues);
            CircuitTopologyPreviewText.Text = string.Concat("미리보기 분석 실패: ", diagnostic);
            StatusText.Text = string.Concat("회로 미리보기를 계산하지 못했습니다: ", diagnostic);
            return;
        }

        CircuitTopologyEditor.SetPlan(result.GetValueOrThrow());
        RefreshCircuitEndpointChoices(project, CircuitTopologyEditor.Plan);
        CircuitTopologyPreviewText.Text = FormatCircuitTopologyPreview(CircuitTopologyEditor.Plan);
        StatusText.Text = CircuitTopologyPreviewText.Text;
    }

    /// <summary>안정 ID의 회전 구간 활성 상태를 검증하고 되돌릴 수 있는 미리보기를 갱신합니다.</summary>
    private void ApplyTurnSegmentDraftState(HeatingPathSegmentId segmentId, bool isActive)
    {
        DesignProject project = _projectSession.Current.CurrentProject;
        HeatingPathSegment segment = project?.HeatingPaths
            .SelectMany(path => path.Segments)
            .SingleOrDefault(candidate => candidate.Id == segmentId);
        if (segment is null || segment.Kind != HeatingPathSegmentKind.Turn)
        {
            StatusText.Text = "현재 프로젝트에서 회전 구간을 찾지 못했습니다.";
            return;
        }

        CircuitTopologyEditor.SetTurnState(
            segmentId,
            isActive == segment.IsActive ? null : isActive);

        if (!TryShowTurnSegmentPreview(segmentId, isActive))
        {
            ResetCircuitTopologyPreview(restoreCommittedPresentation: true);
            RefreshCircuitTurnChoices(project);
            StatusText.Text = "회전 구간 미리보기를 Native 화면에 표시하지 못했습니다.";
            return;
        }

        RefreshCircuitTopologyPreview();
    }

    /// <summary>뷰포트 pick 상태를 같은 안정 ID를 가진 체크 항목에 반영합니다.</summary>
    private void SetCircuitTurnChoice(HeatingPathSegmentId segmentId, bool isActive)
    {
        CircuitTopologyEditor.SetTurnChoiceState(segmentId, isActive);
    }

    /// <summary>확정 Native 장면에서 회전 구간을 프로젝트 활성 상태에 맞춰 표시합니다.</summary>
    private string ApplyCommittedTurnVisibility(DesignProject project)
    {
        int failedCount = 0;
        HeatingPathSegment[] turnSegments = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Where(segment => segment.Kind == HeatingPathSegmentKind.Turn)
            .ToArray();

        foreach (HeatingPathSegment segment in turnSegments)
        {
            if (!_geometryWorkspace.TryGetActiveRuntimeId(segment.Id, out int runtimeId) ||
                !Bridge.SetTurnSegmentEnabled(runtimeId, segment.IsActive, 0.2, 0.8, 0.3))
            {
                failedCount++;
            }
        }

        return failedCount == 0
            ? null
            : $"회전 구간 {failedCount}개의 확정 화면 표시를 갱신하지 못했습니다.";
    }

    /// <summary>Application topology 배정의 회로 ID와 구간 수를 짧은 미리보기로 만듭니다.</summary>
    private static string FormatCircuitTopologyPreview(CircuitTopologyPlan plan)
    {
        string assignments = string.Join(
            " · ",
            plan.Mapping.Assignments.Select(assignment => string.Format(
                CultureInfo.CurrentCulture,
                "{0} ({1}구간)",
                ShortId(assignment.CircuitId.Value),
                assignment.Component.Segments.Count)));
        return string.Format(
            CultureInfo.CurrentCulture,
            "미리보기 · 회로 {0}개 · {1}",
            plan.Mapping.Assignments.Count,
            assignments);
    }

    /// <summary>현재 분석 결과에서 기존 연결부가 없는 끝점만 편집 목록에 표시합니다.</summary>
    private void RefreshCircuitEndpointChoices(DesignProject project, CircuitTopologyPlan plan)
    {
        CircuitTopologyEditor.RefreshEndpointChoices(project, plan);
        CircuitTopologyEndpointChoice selectedChoice = CircuitTopologyEditor.SelectedEndpointChoice;
        CircuitTopologyEndpointCombo.ItemsSource = CircuitTopologyEditor.EndpointChoices;
        CircuitTopologyEndpointCombo.SelectedItem = selectedChoice;
        CircuitTopologyEditor.SelectEndpointChoice(
            CircuitTopologyEndpointCombo.SelectedItem as CircuitTopologyEndpointChoice);
        RefreshCircuitEndpointStatus();
    }

    /// <summary>새 끝점 입력 대상으로 선택된 안정 ID를 반환합니다.</summary>
    private CircuitTopologyEndpointChoice GetSelectedCircuitTopologyEndpoint()
    {
        CircuitTopologyEditor.SelectEndpointChoice(
            CircuitTopologyEndpointCombo.SelectedItem as CircuitTopologyEndpointChoice);
        return CircuitTopologyEditor.SelectedEndpointChoice;
    }

    /// <summary>선택한 끝점의 금형 표면 위치를 뷰포트에서 지정합니다.</summary>
    private void PickCircuitTopologyEndpointAnchor_Click(object sender, RoutedEventArgs e) =>
        BeginSurfaceAnchorPick(SurfaceAnchorTarget.CircuitEndpoint);

    /// <summary>입력한 외부 XYZ 좌표를 선택된 신규 연결부 초안에 저장합니다.</summary>
    private void SaveCircuitTopologyEndpointCoordinates_Click(object sender, RoutedEventArgs e)
    {
        CircuitTopologyEndpointChoice choice = GetSelectedCircuitTopologyEndpoint();
        if (choice is null)
        {
            StatusText.Text = "외부 좌표를 저장할 신규 회로 끝점을 선택하세요.";
            return;
        }

        if (!CircuitTopologyEditor.TryUpdateExternalConnectionPoint(
                choice.Key,
                TopologyEndpointXBox.Text,
                TopologyEndpointYBox.Text,
                TopologyEndpointZBox.Text,
                out string failure))
        {
            StatusText.Text = failure;
            return;
        }

        RefreshCircuitEndpointStatus();
        StatusText.Text = "신규 연결부 외부 좌표를 저장했습니다.";
    }

    /// <summary>선택된 새 끝점의 앵커·외부 좌표 입력 상태를 화면에 표시합니다.</summary>
    private void RefreshCircuitEndpointStatus()
    {
        CircuitTopologyEditor.RefreshSelectedEndpointPresentation();
        CircuitTopologyEndpointStatusText.Text = CircuitTopologyEditor.EndpointStatusText;
        TopologyEndpointXBox.Text = CircuitTopologyEditor.ExternalXCoordinateText;
        TopologyEndpointYBox.Text = CircuitTopologyEditor.ExternalYCoordinateText;
        TopologyEndpointZBox.Text = CircuitTopologyEditor.ExternalZCoordinateText;
    }

    /// <summary>안정 구간 ID를 Native runtime ID로 변환해 회전 구간 표시만 미리 변경합니다.</summary>
    private bool TryShowTurnSegmentPreview(HeatingPathSegmentId segmentId, bool enabled)
    {
        if (!_geometryWorkspace.TryGetActiveRuntimeId(segmentId, out int runtimeId))
            return false;

        return Bridge.SetTurnSegmentEnabled(runtimeId, enabled, 0.2, 0.8, 0.3);
    }

    /// <summary>미리보기 표시를 확정 session 상태로 되돌리고 임시 topology를 버립니다.</summary>
    private void ResetCircuitTopologyPreview(bool restoreCommittedPresentation)
    {
        if (restoreCommittedPresentation && _projectSession?.Current.CurrentProject is { } project)
        {
            Dictionary<HeatingPathSegmentId, bool> committedStates = project.HeatingPaths
                .SelectMany(path => path.Segments)
                .Where(segment => segment.Kind == HeatingPathSegmentKind.Turn)
                .ToDictionary(segment => segment.Id, segment => segment.IsActive);
            foreach (HeatingPathSegmentId segmentId in CircuitTopologyEditor.TurnStates.Keys)
            {
                if (committedStates.TryGetValue(segmentId, out bool enabled))
                    TryShowTurnSegmentPreview(segmentId, enabled);
            }
        }

        CircuitTopologyEditor.Reset();
        _mainWindowViewModel.PathAnchors.CancelPicking();
        if (Viewport is not null)
            Viewport.SurfaceAnchorSelectionMode = false;
        if (CircuitTopologyEndpointCombo is not null)
            CircuitTopologyEndpointCombo.ItemsSource = null;
        if (CircuitTopologyEndpointStatusText is not null)
            CircuitTopologyEndpointStatusText.Text = "신규 연결부 입력: 필요 없음";
        if (CircuitTopologyPreviewText is not null)
            CircuitTopologyPreviewText.Text = "미리보기: 없음";
    }

    /// <summary>미리보기 확정 전 취소 명령을 처리하고 입력 모드를 종료합니다.</summary>
    private void CancelCircuitTopologyPreview() => CancelCircuitTopologyPreview(showStatus: true);

    /// <summary>topology 초안을 지우고 확정 표시를 복구하며 필요하면 상태 문구를 갱신합니다.</summary>
    private void CancelCircuitTopologyPreview(bool showStatus)
    {
        SetCircuitTopologyMode(false);
        Viewport.CircuitSplitMode = false;
        ResetCircuitTopologyPreview(restoreCommittedPresentation: true);
        RefreshCircuitTurnChoices(_projectSession.Current.CurrentProject);
        if (showStatus)
            StatusText.Text = "회로 topology 미리보기를 취소했습니다.";
    }

    /// <summary>체크 상자 변경 이벤트의 재진입을 막으면서 topology 입력 모드를 갱신합니다.</summary>
    private void SetCircuitTopologyMode(bool enabled)
    {
        using (CircuitTopologyEditor.SuppressModeChangedEvents())
        {
            CircuitSplitModeBox.IsChecked = enabled;
            CircuitTurnSegmentsList.IsEnabled = enabled;
            CircuitTopologyEndpointCombo.IsEnabled = enabled;
        }
    }

    /// <summary>영속 ID를 화면에서 비교하기 쉬운 짧은 표기로 만듭니다.</summary>
    private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);

}
