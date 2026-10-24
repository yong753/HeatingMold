using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Validation;
using Microsoft.Win32;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>프로젝트 패키지를 읽고 검증된 복원 후보를 현재 세션에 적용합니다.</summary>
    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "HeatingMold 프로젝트 열기",
            Filter = "HeatingMold 프로젝트 (*.hmproj)|*.hmproj",
            DefaultExt = ".hmproj",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            OpenProjectFromPath(dialog.FileName);
    }

    /// <summary>확정 프로젝트 snapshot을 원자적으로 저장할 경로를 선택합니다.</summary>
    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectSession.Current.CurrentProject is null)
        {
            StatusText.Text = "저장할 프로젝트가 없습니다. STEP 모델을 먼저 여세요.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "HeatingMold 프로젝트 저장",
            Filter = "HeatingMold 프로젝트 (*.hmproj)|*.hmproj",
            DefaultExt = ".hmproj",
            AddExtension = true,
            FileName = "HeatingMold.hmproj",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true)
            SaveProjectToPath(dialog.FileName);
    }

    /// <summary>패키지를 staging 복원하고 성공한 경우에만 기존 문서 화면을 교체합니다.</summary>
    private void OpenProjectFromPath(string path)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SessionPresentation.RepairMessage = null;
        CandidateOutcome outcome = ExecuteWithoutSessionChangedRefresh(() => _openProject.Execute(path));
        if (!outcome.IsCommitted)
        {
            StatusText.Text = $"프로젝트 열기 실패: {FormatIssues(outcome.Issues)} · {path}";
            return;
        }

        stopwatch.Stop();
        DesignProject project = _projectSession.Current.CurrentProject
            ?? throw new InvalidOperationException("프로젝트 복원 뒤 ProjectSession이 비어 있습니다.");
        CompleteCommittedModelLoad(
            project.Name,
            outcome,
            project.WorkingCoordinateSystem.Normal,
            stopwatch.ElapsedMilliseconds,
            preserveCommittedGeometry: true);
    }

    /// <summary>현재 확정 revision을 저장하고 결과를 화면에 표시합니다.</summary>
    private void SaveProjectToPath(string path)
    {
        OperationResult<SaveProjectOutcome> result = _saveProject.Execute(path);
        if (!result.IsSuccess)
        {
            StatusText.Text = $"프로젝트 저장 실패: {FormatIssues(result.Issues)}";
            return;
        }

        SaveProjectOutcome outcome = result.GetValueOrThrow();
        StatusText.Text = outcome.MarkedCurrentRevisionClean
            ? $"프로젝트 저장 — {outcome.Receipt.Path}"
            : $"프로젝트 revision {outcome.Receipt.SavedRevision.Value} 저장 완료 · 저장 중 변경이 있어 현재 revision은 미저장 상태입니다.";
    }
}
