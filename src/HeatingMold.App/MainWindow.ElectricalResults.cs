using System.Windows;
using System.Windows.Controls;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>회로·면적 패널을 한 확정 세션 스냅샷의 값으로 갱신합니다.</summary>
    private void RefreshElectricalResults(SessionSnapshot snapshot)
    {
        Viewport.ClearQualityIssueLocation();

        ElectricalPanelData panel = ElectricalResultsPresentation.Build(snapshot);
        ElectricalResultsStatusText.Text = panel.ElectricalStatus;
        ElectricalResultsList.ItemsSource = panel.CircuitRows;
        AppliedAreaStatusText.Text = panel.AreaStatus;
        AppliedAreaSummaryText.Text = panel.AreaSummary;
        CircuitAppliedAreasList.ItemsSource = panel.CircuitAreaRows;

        CenterPitchQualityPanelData qualityPanel = CenterPitchQualityPresentation.Build(snapshot);
        CenterPitchQualityStatusText.Text = qualityPanel.Status;
        CenterPitchQualityList.ItemsSource = qualityPanel.Rows;
    }

    /// <summary>최신 확정 품질 표본 위치만 뷰포트에 강조 표시합니다.</summary>
    private void QualityLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CenterPitchQualityLocationSelection selection })
            return;

        SessionSnapshot snapshot = _projectSession.Current;
        if (!CenterPitchQualityPresentation.TryResolveCurrentLocation(snapshot, selection, out Vec3 location))
        {
            Viewport.ClearQualityIssueLocation();
            CenterPitchQualityStatusText.Text = "측정 형상 또는 화면이 변경되어 해당 표본 위치를 표시할 수 없습니다.";
            return;
        }

        if (!Viewport.ShowQualityIssueLocation(location))
            CenterPitchQualityStatusText.Text = "품질 표본 위치를 뷰포트에 표시하지 못했습니다.";
    }
}
