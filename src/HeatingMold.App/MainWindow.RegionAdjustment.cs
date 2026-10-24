using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Regions;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>포함 사각형 입력을 켜고 제외 입력과 상호 배타로 관리합니다.</summary>
    private void IncludeMode_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = IncludeModeBox.IsChecked == true;
        if (enabled && !CanSketchRegionAdjustment())
        {
            IncludeModeBox.IsChecked = false;
            StatusText.Text = "포함영역을 그리려면 발열 영역과 작업 좌표계를 먼저 확정하세요.";
            return;
        }

        if (enabled && ExcludeModeBox.IsChecked == true)
            ExcludeModeBox.IsChecked = false;
        if (enabled && FaceDragSelectModeBox.IsChecked == true)
            FaceDragSelectModeBox.IsChecked = false;

        Viewport.SetInputMode(enabled
            ? ViewerInputMode.RegionInclusionSketch
            : ExcludeModeBox.IsChecked == true
                ? ViewerInputMode.RegionExclusionSketch
                : ViewerInputMode.Navigate);
        if (enabled)
            StatusText.Text = "뷰포트에서 포함할 사각형을 그리세요. 영역 변경은 기존 경로를 갱신한 뒤 확정합니다.";
    }

    /// <summary>현재 확정 프로젝트에서 선택한 발열 영역을 찾아 편집 입력을 확인합니다.</summary>
    private bool TryGetCommittedRegionForAdjustment(out DesignProject project, out HeatingRegion region)
    {
        project = _projectSession.Current.CurrentProject;
        region = null;
        if (project is null || _projectSession.Current.Geometry is null ||
            _mainWindowViewModel.PathRegionSelection.SelectedRegionId is not { } regionId)
        {
            return false;
        }

        region = project.HeatingRegions.FirstOrDefault(candidate => candidate.Id == regionId);
        return region is not null;
    }

    /// <summary>확정 영역과 좌표계를 모두 사용할 수 있을 때 사각형 편집 모드를 허용합니다.</summary>
    private bool CanSketchRegionAdjustment() =>
        TryGetCommittedRegionForAdjustment(out _, out _) &&
        (WorkingCoordinates.Session.Confirmed?.Projection ?? RegionSelection.ProjectionFrame) is not null;

    /// <summary>확정된 선택 영역이 있으면 유효 경계를, 없으면 선택 topology를 반환합니다.</summary>
    private IReadOnlyList<BoundaryLoop> GetRegionLoopsForDisplay()
    {
        if (TryGetCommittedRegionForAdjustment(out _, out HeatingRegion region))
            return region.EffectiveBoundaryLoops;

        return RegionSelection.BoundaryLoops;
    }

    /// <summary>선택 영역에 저장된 제외 경계를 현재 작업 좌표계로 뷰포트에 다시 표시합니다.</summary>
    private void RedrawCommittedExclusionOverlays(DesignProject project)
    {
        Bridge.ClearExclusionOverlays();
        if (project is null || _mainWindowViewModel.PathRegionSelection.SelectedRegionId is not { } regionId ||
            WorkingCoordinates.Session.Confirmed is not { } coordinateSystem)
        {
            return;
        }

        HeatingRegion region = project.HeatingRegions.FirstOrDefault(candidate => candidate.Id == regionId);
        if (region is null)
            return;

        ProjectionFrame frame = coordinateSystem.Projection;
        foreach (BoundaryLoop loop in region.ExclusionLoops)
        {
            Vec2[] vertices = loop.Vertices.Append(loop.Vertices[0]).ToArray();
            var modelCoordinates = new double[vertices.Length * 3];
            for (int index = 0; index < vertices.Length; index++)
            {
                Vec3 point = frame.Unproject(vertices[index]);
                modelCoordinates[index * 3] = point.X;
                modelCoordinates[index * 3 + 1] = point.Y;
                modelCoordinates[index * 3 + 2] = point.Z;
            }

            Bridge.AddExclusionOverlay(modelCoordinates);
        }
    }

    /// <summary>현재 영역 조정 결과를 staging·검증·확정 유스케이스에 전달합니다.</summary>
    private CandidateOutcome ApplyRegionAdjustment(
        DesignProject project,
        HeatingRegion region,
        IEnumerable<IReadOnlyList<Vec2>> inclusions,
        IEnumerable<IReadOnlyList<Vec2>> exclusions) =>
        _updateRegion.ApplyAdjustment(
            region.Id,
            inclusions,
            exclusions,
            project.Revision);

    /// <summary>저장된 경계 루프를 평면 Boolean 입력으로 복사합니다.</summary>
    private static IReadOnlyList<Vec2>[] CopyRegionPolygons(IEnumerable<BoundaryLoop> loops) =>
        loops.Select(loop => (IReadOnlyList<Vec2>)loop.Vertices.ToArray()).ToArray();
}
