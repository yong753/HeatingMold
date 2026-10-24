using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeatingMold.App.ViewModels;
using HeatingMold.App.Geometry;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Editing;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>제어점 목록과 transient 편집 상태를 소유하는 ViewModel입니다.</summary>
    private ControlPointEditorViewModel ControlPointEditor => _mainWindowViewModel.ControlPoints;

    /// <summary>확정 경로의 활성 절점을 안정 ID와 함께 편집 목록에 표시합니다.</summary>
    private void RefreshControlPointChoices(DesignProject project)
    {
        ControlPointEditor.RefreshChoices(project);
    }

    /// <summary>절점 선택이 바뀌면 이전 선택에 종속된 새 표면 위치를 버립니다.</summary>
    private void ControlPoint_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        HandleControlPointSelectionChanged();

    /// <summary>대상 절점이 바뀌면 기존 앵커와 진행 중인 드래그 후보를 폐기합니다.</summary>
    private void HandleControlPointSelectionChanged()
    {
        CancelTurnSegmentCurvePreview(showStatus: false);
        CancelActiveControlPointEdit(showStatus: false);
        ClearControlPointTargetAnchor();
    }

    /// <summary>선택한 절점이 속한 발열 영역에서 이동 위치를 고르도록 뷰포트를 전환합니다.</summary>
    private void PickControlPointAnchor_Click(object sender, RoutedEventArgs e)
    {
        CancelTurnSegmentCurvePreview(showStatus: false);
        CancelActiveControlPointEdit(showStatus: false);
        BeginSurfaceAnchorPick(SurfaceAnchorTarget.ControlPointTarget);
    }

    /// <summary>선택한 안정 절점을 작업 화면에서 드래그하도록 입력 모드를 시작합니다.</summary>
    private void BeginControlPointDrag_Click(object sender, RoutedEventArgs e)
    {
        CancelTurnSegmentCurvePreview(showStatus: false);
        if (!WorkingGenerationReady())
            return;

        ControlPointChoice choice = GetSelectedControlPointChoice();
        SessionSnapshot snapshot = _projectSession.Current;
        DesignProject project = snapshot.CurrentProject;
        if (choice is null || project is null || project.Revision != choice.Revision ||
            snapshot.Geometry is not { } geometry || snapshot.RuntimeDocument is not { } runtimeDocument)
        {
            StatusText.Text = "현재 확정 설계에서 이동할 제어점을 선택하세요.";
            return;
        }
        if (FindRegion(project, choice.RegionId) is null)
        {
            StatusText.Text = "제어점의 발열 영역을 현재 설계에서 찾을 수 없습니다.";
            return;
        }
        if (!TryReadPositive(ControlPointToleranceBox.Text, out double endpointToleranceMm))
        {
            StatusText.Text = "원호 끝점 허용 오차는 양의 유한 mm 값이어야 합니다.";
            return;
        }

        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        if (coordinateSystem is null ||
            !Bridge.ReturnToWorkingFront(coordinateSystem.Id.Value, coordinateSystem.Revision.Value))
        {
            StatusText.Text = "제어점을 편집할 작업 화면을 준비하지 못했습니다.";
            return;
        }

        CancelActiveControlPointEdit(showStatus: false);
        ControlPointEditor.BeginDrag(new ControlPointDragState(
            choice,
            endpointToleranceMm,
            geometry.DocumentId,
            runtimeDocument.ModelRevision));
        Viewport.ControlPointEditMode = true;
        StatusText.Text = "선택한 제어점을 끌어 새 표면 위치를 지정하세요 · Esc로 취소";
    }

    /// <summary>드래그 위치가 속한 표면을 측정하고 임시 곡선을 표시합니다.</summary>
    private void OnControlPointEditPreviewMoved(int x, int y, long generation)
    {
        ControlPointDragState drag = ControlPointEditor.ActiveDrag;
        if (drag is null || !Viewport.IsCurrentControlPointEditGeneration(generation))
            return;

        if (!TryCreateControlPointAnchor(
                x,
                y,
                drag,
                out DesignProject project,
                out SurfaceAnchor anchor,
                out string diagnostic))
        {
            Bridge.ClearControlPointEditPreview();
            StatusText.Text = string.Concat("제어점 미리보기 불가: ", diagnostic);
            return;
        }

        var request = new ControlPointEditRequest(
            drag.Choice.PathId,
            drag.Choice.ControlPointId,
            drag.Choice.Revision,
            anchor,
            drag.EndpointToleranceMm);
        OperationResult<HeatingPathEditDraft> result = _moveControlPoint.CreatePreviewDraft(request);
        if (!result.IsSuccess)
        {
            Bridge.ClearControlPointEditPreview();
            StatusText.Text = string.Concat("제어점 미리보기 불가: ", FormatIssues(result.Issues));
            return;
        }
        if (!Viewport.IsCurrentControlPointEditGeneration(generation) ||
            _projectSession.Current.CurrentProject?.Revision != project.Revision)
        {
            Bridge.ClearControlPointEditPreview();
            return;
        }

        try
        {
            HeatingPathEditDraft draft = result.GetValueOrThrow();
            double[][] polylines = ControlPointPreviewCurveSampler.SampleAffectedSegments(
                draft.CandidatePath,
                draft.AffectedSegmentIds);
            if (!Bridge.SetControlPointEditPreview(polylines))
                throw new InvalidOperationException("Native 뷰어가 임시 경로를 표시하지 못했습니다.");
            StatusText.Text = "제어점 미리보기 · 드롭하면 전체 기하 검증 후 확정합니다.";
        }
        catch (Exception exception)
        {
            Bridge.ClearControlPointEditPreview();
            StatusText.Text = string.Concat("제어점 미리보기를 표시하지 못했습니다: ", exception.Message);
        }
    }

    /// <summary>드롭 위치를 다시 질의한 뒤 최종 Application 편집을 실행합니다.</summary>
    private void OnControlPointEditCompleted(int x, int y)
    {
        ControlPointDragState drag = ControlPointEditor.ActiveDrag;
        if (drag is null)
            return;

        if (!TryCreateControlPointAnchor(
                x,
                y,
                drag,
                out _,
                out SurfaceAnchor anchor,
                out string diagnostic))
        {
            CancelActiveControlPointEdit(showStatus: false);
            StatusText.Text = string.Concat("제어점 이동을 적용하지 않았습니다: ", diagnostic);
            return;
        }

        var request = new ControlPointEditRequest(
            drag.Choice.PathId,
            drag.Choice.ControlPointId,
            drag.Choice.Revision,
            anchor,
            drag.EndpointToleranceMm);
        CandidateOutcome outcome = _moveControlPoint.Execute(request);
        ControlPointEditor.ClearActiveDrag();
        ClearControlPointTargetAnchor();
        Viewport.ControlPointEditMode = false;
        Bridge.ClearControlPointEditPreview();
        StatusText.Text = outcome.Status switch
        {
            CandidateStatus.Committed => "제어점 이동을 확정했습니다.",
            CandidateStatus.PostCommitFailure => string.Concat(
                "제어점 이동은 확정됐지만 화면 복구가 필요합니다: ",
                FormatIssues(outcome.Issues)),
            _ => string.Concat("제어점 이동을 적용하지 않았습니다: ", FormatIssues(outcome.Issues))
        };
    }

    /// <summary>취소·모드 변경·창 종료 때 드래그 상태와 Native 임시 표시를 지웁니다.</summary>
    private void CancelActiveControlPointEdit(bool showStatus)
    {
        bool hadTarget = ControlPointEditor.ActiveDrag is not null || ControlPointEditor.TargetAnchor is not null;
        bool wasModeActive = Viewport?.ControlPointEditMode == true;
        bool wasActive = hadTarget || wasModeActive;
        if (!wasActive)
            return;

        ControlPointEditor.ClearActiveDrag();
        if (wasModeActive)
            Viewport.ControlPointEditMode = false;
        else
            Bridge.ClearControlPointEditPreview();
        ClearControlPointTargetAnchor();
        if (showStatus && wasActive)
            StatusText.Text = "제어점 드래그를 취소했습니다.";
    }

    /// <summary>선택 영역의 실제 Face를 기준으로 커서 위치를 표면 앵커로 변환합니다.</summary>
    private bool TryCreateControlPointAnchor(
        int x,
        int y,
        ControlPointDragState drag,
        out DesignProject project,
        out SurfaceAnchor anchor,
        out string diagnostic)
    {
        project = null;
        anchor = null;
        diagnostic = string.Empty;
        SessionSnapshot snapshot = _projectSession.Current;
        if (snapshot.CurrentProject is not { } currentProject ||
            currentProject.Revision != drag.Choice.Revision ||
            snapshot.Geometry is not { } geometry ||
            geometry.DocumentId != drag.DocumentId ||
            geometry.ModelRevision != drag.ModelRevision ||
            snapshot.RuntimeDocument?.DocumentId != drag.DocumentId ||
            snapshot.RuntimeDocument?.ModelRevision != drag.ModelRevision)
        {
            diagnostic = "기준 설계가 바뀌었습니다.";
            return false;
        }

        HeatingRegion region = FindRegion(currentProject, drag.Choice.RegionId);
        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        if (region is null || coordinateSystem is null || coordinateSystem.Revision != geometry.ModelRevision)
        {
            diagnostic = "제어점 영역 또는 작업 좌표계가 바뀌었습니다.";
            return false;
        }

        ProjectionFrame projection = coordinateSystem.Projection;
        double[] planePoint = Bridge.ScreenPointOnPlane(
            x,
            y,
            projection.Origin.X,
            projection.Origin.Y,
            projection.Origin.Z,
            projection.Normal.X,
            projection.Normal.Y,
            projection.Normal.Z);
        if (planePoint is null || planePoint.Length < 3 || planePoint.Take(3).Any(value => !double.IsFinite(value)))
        {
            diagnostic = "커서 위치를 작업 평면에 투영하지 못했습니다.";
            return false;
        }

        Vec2 workingUv = projection.Project(new Vec3(planePoint[0], planePoint[1], planePoint[2]));
        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices = _faceReferenceIndex.NativeIndicesByReference;
        if (region.FaceReferences.Any(faceId => !nativeFaceIndices.ContainsKey(faceId)))
        {
            diagnostic = "발열 영역의 Face를 현재 Native 면 번호에 연결하지 못했습니다.";
            return false;
        }

        try
        {
            OcctGeometryQueries queries = AppCompositionRoot.CreateOcctGeometryQueries(
                coordinateSystem,
                nativeFaceIndices);
            GeometryQueryResult<SurfaceHit> query = queries.QueryFirstHit(
                coordinateSystem,
                workingUv,
                region.FaceReferences);
            if (!query.HasValue || query.Value is not { } hit)
            {
                diagnostic = query.Issues.Count == 0
                    ? "커서 아래에 선택 발열 영역 표면이 없습니다."
                    : FormatIssues(query.Issues);
                return false;
            }

            project = currentProject;
            anchor = new SurfaceAnchor(hit.FaceReferenceId, hit.FaceUv, workingUv, hit.ModelPoint);
            return true;
        }
        catch (Exception exception)
        {
            diagnostic = string.Concat("표면 교차 질의에 실패했습니다: ", exception.Message);
            return false;
        }
    }

    /// <summary>Esc 입력으로 절점·곡선·앵커 선택·회로 topology 미리보기를 정리합니다.</summary>
    private void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (_mainWindowViewModel.PathAnchors.PendingSurfaceAnchorTarget is not null)
        {
            CancelSurfaceAnchorPick_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ControlPointEditor.ActiveDrag is not null || TurnSegmentCurveEditor.IsPreviewActive)
        {
            CancelActiveControlPointEdit(showStatus: true);
            CancelTurnSegmentCurvePreview(showStatus: true);
            e.Handled = true;
            return;
        }

        if (Viewport?.CircuitSplitMode == true)
        {
            CancelCircuitTopologyPreview();
            e.Handled = true;
        }
    }

    /// <summary>창이 비활성화되면 포인터 캡처와 미리보기를 해제합니다.</summary>
    private void OnMainWindowDeactivated(object sender, EventArgs e)
    {
        CancelTransientViewerInput();
    }

    /// <summary>창을 닫기 전에 Native 임시 곡선과 입력 상태를 정리합니다.</summary>
    private void OnMainWindowClosed(object sender, EventArgs e)
    {
        CancelTransientViewerInput();
    }

    /// <summary>뷰어 입력 모드별 임시 상태를 정리하고 topology 표시를 확정 상태로 복구합니다.</summary>
    private void CancelTransientViewerInput()
    {
        Viewport?.CancelTransientPointerInput();
        CancelActiveControlPointEdit(showStatus: false);
        CancelTurnSegmentCurvePreview(showStatus: false);
        ClearPendingSurfaceAnchorPick();
        Bridge.SetExclusionPreview(null);

        if (Viewport?.CircuitSplitMode == true || CircuitSplitModeBox?.IsChecked == true)
            CancelCircuitTopologyPreview(showStatus: false);
    }

    /// <summary>선택된 안정 절점 ID와 표면 앵커를 Application 후보 편집으로 전달합니다.</summary>
    private void MoveControlPoint_Click(object sender, RoutedEventArgs e)
    {
        CancelTurnSegmentCurvePreview(showStatus: false);
        Bridge.ClearControlPointEditPreview();
        ControlPointChoice choice = GetSelectedControlPointChoice();
        if (choice is null)
        {
            StatusText.Text = "이동할 활성 제어점을 선택하세요.";
            return;
        }

        SessionSnapshot snapshot = _projectSession.Current;
        DesignProject project = snapshot.CurrentProject;
        if (project is null || project.Revision != choice.Revision)
        {
            if (project is not null)
                RefreshControlPointChoices(project);
            StatusText.Text = "설계가 바뀌었습니다. 현재 제어점을 다시 선택하세요.";
            return;
        }

        if (ControlPointEditor.TargetAnchor is null || ControlPointEditor.TargetRevision != project.Revision)
        {
            StatusText.Text = "먼저 뷰포트에서 새 표면 위치를 선택하세요.";
            return;
        }

        HeatingRegion region = FindRegion(project, choice.RegionId);
        if (region is null || !region.FaceReferences.Contains(ControlPointEditor.TargetAnchor.FaceReferenceId))
        {
            ClearControlPointTargetAnchor();
            StatusText.Text = "새 위치는 선택한 제어점의 발열 영역 안에서 지정해야 합니다.";
            return;
        }

        if (!TryReadPositive(ControlPointToleranceBox.Text, out double endpointToleranceMm))
        {
            StatusText.Text = "원호 끝점 허용 오차는 양의 유한 mm 값이어야 합니다.";
            return;
        }

        var request = new ControlPointEditRequest(
            choice.PathId,
            choice.ControlPointId,
            project.Revision,
            ControlPointEditor.TargetAnchor,
            endpointToleranceMm);
        CandidateOutcome outcome = _moveControlPoint.Execute(request);
        if (outcome.Status == CandidateStatus.Committed)
        {
            StatusText.Text = "제어점 이동을 확정했습니다.";
            return;
        }

        string diagnostic = FormatIssues(outcome.Issues);
        StatusText.Text = outcome.Status == CandidateStatus.PostCommitFailure
            ? string.Concat("제어점 이동은 확정됐지만 화면 복구가 필요합니다: ", diagnostic)
            : string.Concat("제어점 이동을 적용하지 않았습니다: ", diagnostic);
    }

    /// <summary>요청한 발열 영역이 현재 확정 프로젝트에 속하는지 다시 확인합니다.</summary>
    private static HeatingRegion FindRegion(DesignProject project, RegionId regionId) =>
        project?.HeatingRegions.SingleOrDefault(region => region.Id == regionId);

    /// <summary>제어점 편집용 표면 앵커 선택의 영역을 현재 프로젝트에서 조회합니다.</summary>
    private HeatingRegion GetSurfaceAnchorRegion(
        SurfaceAnchorTarget target,
        DesignProject project,
        CircuitTopologyEndpointKey? endpointKey = null)
    {
        if (target == SurfaceAnchorTarget.CircuitEndpoint)
        {
            if (project is null || endpointKey is not { } key)
                return null;

            HeatingPath endpointPath = project.HeatingPaths.SingleOrDefault(path =>
                path.Segments.Any(segment => segment.Id == key.SegmentId));
            return endpointPath is null
                ? null
                : FindRegion(project, endpointPath.GenerationSource.RegionId);
        }

        if (target != SurfaceAnchorTarget.ControlPointTarget)
        {
            HeatingRegion selectedRegion = GetSelectedHeatingRegion();
            return selectedRegion is null ? null : FindRegion(project, selectedRegion.Id);
        }

        ControlPointChoice choice = GetSelectedControlPointChoice();
        if (project is null || choice is null || choice.Revision != project.Revision)
            return null;

        HeatingPath path = project.HeatingPaths.SingleOrDefault(candidate => candidate.Id == choice.PathId);
        return path is null || path.GenerationSource.RegionId != choice.RegionId
            ? null
            : FindRegion(project, choice.RegionId);
    }

    /// <summary>표면에서 고른 제어점 편집 위치와 기준 revision을 화면에 보관합니다.</summary>
    private void StoreControlPointTargetAnchor(SurfaceAnchor anchor, string status)
    {
        ControlPointEditor.SetTargetAnchor(anchor, _projectSession.Current.CurrentProject?.Revision);
        ControlPointAnchorStatusText.Text = string.Concat(status, " · 새 위치로 사용");
    }

    /// <summary>절점 선택이나 설계 변경 뒤 기존 편집 위치를 지웁니다.</summary>
    private void ClearControlPointTargetAnchor()
    {
        ControlPointEditor.ClearTargetAnchor();
        if (ControlPointAnchorStatusText is not null)
            ControlPointAnchorStatusText.Text = "새 위치: 미지정";
    }

    /// <summary>콤보 상자에서 현재 경로와 절점의 안정 ID를 가진 선택 항목을 가져옵니다.</summary>
    private ControlPointChoice GetSelectedControlPointChoice() =>
        ControlPointEditor.SelectedChoice;
}
