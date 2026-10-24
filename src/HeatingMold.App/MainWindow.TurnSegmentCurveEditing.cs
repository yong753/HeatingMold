using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using HeatingMold.App.Geometry;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Model;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Editing;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private TurnSegmentCurveEditorViewModel TurnSegmentCurveEditor =>
        _mainWindowViewModel.TurnSegmentCurves;

    /// <summary>확정 설계의 편집 가능한 회전 구간을 안정 ID로 선택 목록에 반영합니다.</summary>
    private void RefreshTurnSegmentCurveChoices(DesignProject project)
    {
        CancelTurnSegmentCurvePreview(showStatus: false);
        TurnSegmentCurveEditor.Refresh(project);
    }

    /// <summary>대상 구간을 바꿀 때 이전 형상 미리보기를 지웁니다.</summary>
    private void TurnSegmentCurve_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelTurnSegmentCurvePreview(showStatus: false);
        if (TurnSegmentCurveEditor.SelectedChoice is null)
            TurnSegmentCurveEditorStatusText.Text = "편집할 활성 회전 구간이 없습니다.";
    }

    /// <summary>내부점 입력이 바뀌면 이전에 표시한 후보 곡선을 지웁니다.</summary>
    private void TurnSegmentInteriorPoint_CellEditEnding(
        object sender,
        DataGridCellEditEndingEventArgs e) =>
        CancelTurnSegmentCurvePreview(showStatus: false);

    /// <summary>현재 내부점 입력으로 확정하지 않는 회전 곡선 미리보기를 표시합니다.</summary>
    private void PreviewTurnSegmentCurve_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        CancelActiveControlPointEdit(showStatus: false);
        CancelTurnSegmentCurvePreview(showStatus: false);
        if (!TurnSegmentCurveEditor.TryCreateRequest(out TurnSegmentCurveEditRequest request, out string diagnostic))
        {
            TurnSegmentCurveEditorStatusText.Text = diagnostic;
            return;
        }

        OperationResult<TurnSegmentCurveEditDraft> result = _editTurnSegmentCurve.CreatePreviewDraft(request);
        if (!result.IsSuccess)
        {
            TurnSegmentCurveEditorStatusText.Text = FormatIssues(result.Issues);
            return;
        }

        TurnSegmentCurveEditDraft draft = result.GetValueOrThrow();
        if (_projectSession.Current.CurrentProject?.Revision != request.BaseRevision)
        {
            TurnSegmentCurveEditorStatusText.Text = "설계가 바뀌었습니다. 현재 회전 구간을 다시 선택하세요.";
            RefreshTurnSegmentCurveChoices(_projectSession.Current.CurrentProject);
            return;
        }

        try
        {
            double[][] preview = ControlPointPreviewCurveSampler.SampleAffectedSegments(
                draft.CandidatePath,
                [draft.SegmentId]);
            TurnSegmentCurveEditor.MarkPreviewActive();
            if (!Bridge.SetControlPointEditPreview(preview))
                throw new InvalidOperationException("Native 뷰어가 회전 곡선 미리보기를 표시하지 못했습니다.");

            TurnSegmentCurveEditorStatusText.Text = "미리보기 중 · 확정 설계는 바뀌지 않았습니다.";
        }
        catch (Exception exception)
        {
            CancelTurnSegmentCurvePreview(showStatus: false);
            TurnSegmentCurveEditorStatusText.Text = string.Concat(
                "회전 곡선 미리보기를 표시하지 못했습니다: ",
                exception.Message);
        }
    }

    /// <summary>안정 ID와 revision이 고정된 회전 곡선 요청을 후보 검증기로 전달합니다.</summary>
    private void ApplyTurnSegmentCurve_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!TurnSegmentCurveEditor.TryCreateRequest(out TurnSegmentCurveEditRequest request, out string diagnostic))
        {
            CancelTurnSegmentCurvePreview(showStatus: false);
            TurnSegmentCurveEditorStatusText.Text = diagnostic;
            return;
        }

        CancelTurnSegmentCurvePreview(showStatus: false);
        CandidateOutcome outcome = _editTurnSegmentCurve.Execute(request);
        TurnSegmentCurveEditorStatusText.Text = outcome.Status switch
        {
            CandidateStatus.Committed => "회전 구간 곡선을 확정했습니다.",
            CandidateStatus.PostCommitFailure => string.Concat(
                "회전 곡선은 확정됐지만 화면 복구가 필요합니다: ",
                FormatIssues(outcome.Issues)),
            _ => string.Concat("회전 곡선을 적용하지 않았습니다: ", FormatIssues(outcome.Issues))
        };
    }

    /// <summary>미리보기를 지우고 확정 설계를 그대로 둡니다.</summary>
    private void CancelTurnSegmentCurve_Click(object sender, System.Windows.RoutedEventArgs e) =>
        CancelTurnSegmentCurvePreview(showStatus: true);

    /// <summary>취소·설계 변경·창 종료 때 Native 임시 곡선을 제거합니다.</summary>
    private void CancelTurnSegmentCurvePreview(bool showStatus)
    {
        if (!TurnSegmentCurveEditor.TryClearPreviewState())
            return;

        Bridge.ClearControlPointEditPreview();
        if (showStatus)
            TurnSegmentCurveEditorStatusText.Text = "미리보기를 취소했습니다. 확정 설계는 바뀌지 않았습니다.";
    }
}
