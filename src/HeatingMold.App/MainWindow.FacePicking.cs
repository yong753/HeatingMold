using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Polyline = System.Windows.Shapes.Polyline;
using HeatingMold.App.Geometry;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.App.Presentation;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private void OnFaceHovered(int faceIndex)
    {
        if (faceIndex < 0)
        {
            HoverText.Text = "";
            return;
        }
        string type = Bridge.GetFaceType(faceIndex);
        HoverText.Text = string.Format("호버 #{0} ({1})", faceIndex, type);
    }

    private void FaceDragSelectMode_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = FaceDragSelectModeBox.IsChecked == true;
        if (enabled && IncludeModeBox.IsChecked == true) IncludeModeBox.IsChecked = false;
        if (enabled && ExcludeModeBox.IsChecked == true) ExcludeModeBox.IsChecked = false;
        Viewport.RectangleSelectionMode = enabled;
        if (enabled) StatusText.Text = "Face 드래그 선택 모드 — 드래그 박스에 걸친 Face 선택, Ctrl로 추가/토글";
    }

    private void ExcludeMode_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = ExcludeModeBox.IsChecked == true;
        if (enabled && !CanSketchRegionAdjustment())
        {
            ExcludeModeBox.IsChecked = false;
            StatusText.Text = "제외영역을 그리려면 발열 영역과 작업 좌표계를 먼저 확정하세요.";
            return;
        }
        if (enabled && IncludeModeBox.IsChecked == true) IncludeModeBox.IsChecked = false;
        if (enabled && FaceDragSelectModeBox.IsChecked == true) FaceDragSelectModeBox.IsChecked = false;
        Viewport.SetInputMode(enabled
            ? ViewerInputMode.RegionExclusionSketch
            : IncludeModeBox.IsChecked == true
                ? ViewerInputMode.RegionInclusionSketch
                : ViewerInputMode.Navigate);
        if (!enabled) Bridge.SetExclusionPreview(null);
        if (enabled) StatusText.Text = "뷰포트에서 제외할 사각형을 그리세요. 영역 변경은 기존 경로를 갱신한 뒤 확정합니다.";
    }

    private void OnFacesRectanglePicked(int[] faceIds, bool toggle)
    {
        if (faceIds == null) return;
        RegionSelection.Clear();
        SelectedFacesList.Items.Clear();
        Bridge.ClearExclusionOverlays();
        foreach (int faceIndex in faceIds)
        {
            if (!_faceReferenceIndex.TryGetReferenceId(faceIndex, out FaceRefId faceReferenceId))
                continue;

            string type = Bridge.GetFaceType(faceIndex);
            FaceItem item = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(f => f.Index == faceIndex);
            if (item == null)
            {
                item = new FaceItem
                {
                    Index = faceIndex,
                    ReferenceId = faceReferenceId,
                    Label = string.Format("#{0} {1} (picked)", faceIndex, type)
                };
                FaceCombo.Items.Add(item);
            }
            RegionSelection.Add(faceReferenceId);
            SelectedFacesList.Items.Add(string.Format("#{0} {1}", faceIndex, type));
        }
        FaceRefId active = RegionSelection.SelectedFaceReferences.FirstOrDefault();
        if (!active.IsEmpty)
            FaceCombo.SelectedItem = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(item => item.ReferenceId == active);
        RefreshSelectedRegion();
        StatusText.Text = string.Format("드래그 선택 — {0}개 Face", RegionSelection.Count);
    }

    private bool TryViewportPointOnFrame(int x, int y, out Vec2 point, out Vec3 world)
    {
        point = default;
        world = default;
        ProjectionFrame projectionFrame = WorkingCoordinates.Session.Confirmed?.Projection ?? RegionSelection.ProjectionFrame;
        if (projectionFrame == null) return false;
        Vec3 o = projectionFrame.Origin, n = projectionFrame.Normal;
        double[] hit = Bridge.ScreenPointOnPlane(x, y, o.X, o.Y, o.Z, n.X, n.Y, n.Z);
        if (hit == null || hit.Length < 3 || hit.Any(v => !double.IsFinite(v))) return false;
        world = new Vec3(hit[0], hit[1], hit[2]);
        point = projectionFrame.Project(world);
        return true;
    }

    private double[] ExclusionRectangleWire(Vec2 a, Vec2 b)
    {
        ProjectionFrame projectionFrame = WorkingCoordinates.Session.Confirmed?.Projection ?? RegionSelection.ProjectionFrame;
        if (projectionFrame is null)
            return Array.Empty<double>();

        double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
        Vec2[] corners = { new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY), new(minX, minY) };
        var xyz = new double[corners.Length * 3];
        for (int i = 0; i < corners.Length; i++)
        {
            Vec3 p = projectionFrame.Unproject(corners[i]);
            xyz[i * 3] = p.X; xyz[i * 3 + 1] = p.Y; xyz[i * 3 + 2] = p.Z;
        }
        return xyz;
    }

    private void OnExclusionSketchMoved(int x0, int y0, int x1, int y1)
    {
        if (!TryViewportPointOnFrame(x0, y0, out Vec2 a, out _) ||
            !TryViewportPointOnFrame(x1, y1, out Vec2 b, out _) ||
            Math.Abs(a.X - b.X) < 1e-6 || Math.Abs(a.Y - b.Y) < 1e-6)
        {
            Bridge.SetExclusionPreview(null);
            return;
        }
        Bridge.SetExclusionPreview(ExclusionRectangleWire(a, b));
    }

    private void OnExclusionSketchCompleted(int x0, int y0, int x1, int y1)
    {
        Bridge.SetExclusionPreview(null);
        bool inclusionMode = Viewport.InputMode == ViewerInputMode.RegionInclusionSketch;
        IncludeModeBox.IsChecked = false;
        ExcludeModeBox.IsChecked = false;
        if (!TryViewportPointOnFrame(x0, y0, out Vec2 a, out _) ||
            !TryViewportPointOnFrame(x1, y1, out Vec2 b, out _))
        {
            StatusText.Text = "사각형을 작업 평면에 투영할 수 없습니다.";
            return;
        }
        double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
        if (maxX - minX < 0.5 || maxY - minY < 0.5)
        {
            StatusText.Text = "사각형이 너무 작습니다.";
            return;
        }

        if (!TryGetCommittedRegionForAdjustment(out DesignProject project, out HeatingRegion region))
        {
            StatusText.Text = "확정한 발열 영역을 찾지 못해 사각형을 저장하지 않았습니다.";
            return;
        }

        Vec2[] rectangle =
        [
            new(minX, minY),
            new(maxX, minY),
            new(maxX, maxY),
            new(minX, maxY)
        ];
        IReadOnlyList<Vec2>[] inclusions = CopyRegionPolygons(region.InclusionLoops);
        IReadOnlyList<Vec2>[] exclusions = CopyRegionPolygons(region.ExclusionLoops);
        IReadOnlyList<Vec2>[] updatedInclusions = inclusionMode
            ? inclusions.Append((IReadOnlyList<Vec2>)rectangle).ToArray()
            : inclusions;
        IReadOnlyList<Vec2>[] updatedExclusions = inclusionMode
            ? exclusions
            : exclusions.Append((IReadOnlyList<Vec2>)rectangle).ToArray();

        CandidateOutcome outcome = ApplyRegionAdjustment(
            project,
            region,
            updatedInclusions,
            updatedExclusions);
        if (!outcome.IsCommitted)
        {
            StatusText.Text = string.Concat("발열 영역 조정 실패: ", FormatIssues(outcome.Issues));
            return;
        }

        DrawRegionCanvas();
        StatusText.Text = outcome.Status == CandidateStatus.PostCommitFailure
            ? string.Concat("영역은 확정됐지만 화면 동기화가 필요합니다: ", FormatIssues(outcome.Issues))
            : inclusionMode ? "포함 사각형과 파생 기하를 프로젝트에 확정했습니다."
            : "제외 사각형과 파생 기하를 프로젝트에 확정했습니다.";
    }

    private void OnFacePicked(int faceIndex, bool toggle)
    {
        if (faceIndex < 0)
        {
            if (!toggle)
            {
                RegionSelection.Clear();
                SelectedFacesList.Items.Clear();
                Bridge.ClearExclusionOverlays();
                RefreshSelectedRegion();
            }
            StatusText.Text = "면을 찾지 못했습니다";
            return;
        }
        if (!_faceReferenceIndex.TryGetReferenceId(faceIndex, out FaceRefId faceReferenceId))
        {
            StatusText.Text = "현재 모델의 안정 Face 참조를 찾지 못했습니다.";
            return;
        }
        string type = Bridge.GetFaceType(faceIndex);
        FaceItem match = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(f => f.Index == faceIndex);
        if (match == null)
        {
            // Face missing from combo (filtered by UV size) -> add it so the click selection is honored
            match = new FaceItem
            {
                Index = faceIndex,
                ReferenceId = faceReferenceId,
                Label = string.Format("#{0} {1} (picked)", faceIndex, type)
            };
            FaceCombo.Items.Add(match);
        }
        if (!toggle)
        {
            RegionSelection.Clear();
            SelectedFacesList.Items.Clear();
        }
        if (toggle && RegionSelection.Contains(faceReferenceId))
        {
            RegionSelection.Remove(faceReferenceId);
            for (int i = SelectedFacesList.Items.Count - 1; i >= 0; i--)
                if (SelectedFacesList.Items[i].ToString().StartsWith("#" + faceIndex + " "))
                    SelectedFacesList.Items.RemoveAt(i);
            StatusText.Text = string.Format("적용면에서 제외 — #{0} ({1})", faceIndex, type);
            Bridge.ClearExclusionOverlays();
        }
        else
        {
            RegionSelection.Add(faceReferenceId);
            SelectedFacesList.Items.Add(string.Format("#{0} {1}", faceIndex, type));
            StatusText.Text = string.Format("적용면에 추가 — #{0} ({1}) · 총 {2}개", faceIndex, type, RegionSelection.Count);
            Bridge.ClearExclusionOverlays();
        }
        FaceRefId activeFaceReference = RegionSelection.Contains(faceReferenceId)
            ? faceReferenceId
            : RegionSelection.SelectedFaceReferences.FirstOrDefault();
        if (!activeFaceReference.IsEmpty)
            FaceCombo.SelectedItem = FaceCombo.Items.OfType<FaceItem>()
                .FirstOrDefault(item => item.ReferenceId == activeFaceReference);
        RefreshSelectedRegion();
    }

    private void RefreshSelectedRegion(bool preserveCommittedGeometry = false)
    {
        ResetCircuitEditState(
            clearNative: !preserveCommittedGeometry,
            preserveCommittedGeometry: preserveCommittedGeometry);
        if (!preserveCommittedGeometry)
            Bridge.ClearOverlays();
        RegionSelection.ClearTopology();
        RefreshExitSideChoices();
        DrawRegionCanvas();
        if (RegionSelection.Count == 0)
        {
            return;
        }

        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        if (coordinateSystem == null)
        {
            RegionSelection.FailureMessage = "작업 좌표계를 먼저 확정하세요.";
            StatusText.Text = RegionSelection.FailureMessage;
            return;
        }

        FaceRefId[] selectedFaceReferences = RegionSelection.SelectedFaceReferences
            .OrderBy(faceReferenceId => _faceReferenceIndex.TryGetNativeIndex(faceReferenceId, out int nativeIndex)
                ? nativeIndex
                : int.MaxValue)
            .ToArray();
        if (selectedFaceReferences.Any(faceReferenceId => !_faceReferenceIndex.TryGetNativeIndex(faceReferenceId, out _)))
        {
            RegionSelection.FailureMessage = "선택 Face를 현재 모델의 안정 참조로 연결하지 못했습니다.";
            StatusText.Text = RegionSelection.FailureMessage;
            return;
        }

        try
        {
            OcctGeometryQueries geometryQueries = AppCompositionRoot.CreateOcctGeometryQueries(
                coordinateSystem,
                _faceReferenceIndex.NativeIndicesByReference);
            var topologyResult = geometryQueries.GetSelectedTopology(selectedFaceReferences);
            if (!topologyResult.HasValue || topologyResult.Value is null)
            {
                RegionSelection.FailureMessage = string.Format(
                    "선택 Face의 공유 Edge 경계를 확인하지 못했습니다 ({0}).",
                    topologyResult.Status);
                StatusText.Text = RegionSelection.FailureMessage;
                return;
            }

            RegionSelection.SetTopology(topologyResult, coordinateSystem.Projection);
            RefreshExitSideChoices();
            if (RegionSelection.BoundaryLoops.Count == 0)
            {
                RegionSelection.FailureMessage = "선택 Face에서 닫힌 경계 루프를 찾지 못했습니다.";
                StatusText.Text = RegionSelection.FailureMessage;
                return;
            }

            bool hasMultipleComponents = topologyResult.Value.Components.Count != 1;
            bool hasUnsupportedLoops = RegionSelection.BoundaryLoops.Count != 1 ||
                RegionSelection.BoundaryLoops[0].IsHole;
            if (hasMultipleComponents)
            {
                RegionSelection.FailureMessage = "선택한 Face가 분리된 여러 영역에 속해 하나의 연속 발열 경로를 만들 수 없습니다. 연결 성분별로 따로 선택하세요. 모든 경계를 표시했습니다.";
            }
            else if (hasUnsupportedLoops)
            {
                RegionSelection.FailureMessage = "홀 또는 복수 경계가 있는 영역은 현재 단일 구간 레인 생성기에서 안전한 우회 경로를 만들 수 없습니다. 경계가 단순한 영역으로 나누거나 조정하세요. 모든 경계를 표시했습니다.";
            }
        }
        catch (Exception exception)
        {
            RegionSelection.ClearTopology();
            RegionSelection.FailureMessage = "선택 topology 변환에 실패했습니다: " + exception.Message;
            StatusText.Text = RegionSelection.FailureMessage;
            return;
        }

        DrawRegionCanvas();
        StatusText.Text = RegionSelection.FailureMessage ?? string.Format(
            "확정 작업 좌표계 기준 발열 영역 — {0}개 Face · 경로 생성 준비",
            RegionSelection.Count);
    }

    /// <summary>선택한 영역 경계와 제외부를 작업 평면 투영으로 Canvas에 표시합니다.</summary>
    private void DrawRegionCanvas()
    {
        if (RegionCanvas == null) return;
        RegionCanvas.Children.Clear();
        IReadOnlyList<BoundaryLoop> displayLoops = GetRegionLoopsForDisplay();
        if (displayLoops.Count == 0)
            return;

        if (!RegionCanvasProjection.TryCreate(
                displayLoops.SelectMany(loop => loop.Vertices),
                RegionCanvas.ActualWidth,
                RegionCanvas.ActualHeight,
                padding: 8,
                out RegionCanvasProjection projection))
        {
            return;
        }

        foreach (BoundaryLoop ring in displayLoops)
        {
            var poly = new Polyline
            {
                Stroke = ring.IsHole ? Brushes.OrangeRed : Brushes.SteelBlue,
                StrokeThickness = 2,
                StrokeDashArray = ring.IsHole ? new DoubleCollection { 2, 2 } : null
            };
            foreach (Vec2 point in ring.Vertices)
            {
                RegionCanvasPoint canvasPoint = projection.Project(point);
                poly.Points.Add(new Point(canvasPoint.X, canvasPoint.Y));
            }
            if (poly.Points.Count > 0) poly.Points.Add(poly.Points[0]);
            RegionCanvas.Children.Add(poly);
        }
    }

    private void ClearExclusions_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCommittedRegionForAdjustment(out DesignProject project, out HeatingRegion region))
        {
            StatusText.Text = "제외부를 초기화할 확정 발열 영역이 없습니다.";
            return;
        }
        if (region.ExclusionLoops.Count == 0)
        {
            StatusText.Text = "확정 영역에 제외부가 없습니다.";
            return;
        }

        CandidateOutcome outcome = ApplyRegionAdjustment(
            project,
            region,
            CopyRegionPolygons(region.InclusionLoops),
            Array.Empty<IReadOnlyList<Vec2>>());
        if (!outcome.IsCommitted)
        {
            StatusText.Text = string.Concat("제외부 초기화 실패: ", FormatIssues(outcome.Issues));
            return;
        }

        DrawRegionCanvas();
        StatusText.Text = outcome.Status == CandidateStatus.PostCommitFailure
            ? string.Concat("제외부는 초기화됐지만 화면 동기화가 필요합니다: ", FormatIssues(outcome.Issues))
            : "확정 영역의 제외부를 초기화하고 파생 기하를 갱신했습니다.";
    }

    private void RegionCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRegionCanvas();

}
