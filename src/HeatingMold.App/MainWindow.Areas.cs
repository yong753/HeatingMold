using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private WorkingArea ActiveArea => _activeArea;

    private void ResetCircuitEditingUi()
    {
        CancelPowerSetup();
        CancelExitFacePick();
        if (Viewport != null)
        {
            Viewport.ConnectorEditMode = false;
            Viewport.PowerLocationMode = false;
        }
        if (ConnectorEditModeBox != null) ConnectorEditModeBox.IsChecked = false;
        _connectorUndo.Clear();
        _connectorRedo.Clear();
        _hoverConnector = -1;
        if (CircuitLengthGrid != null) CircuitLengthGrid.ItemsSource = null;
    }

    private void RefreshAreaList()
    {
        if (ConfirmedAreasList == null) return;
        _suppressAreaSelection = true;
        ConfirmedAreasList.ItemsSource = null;
        ConfirmedAreasList.ItemsSource = _areas.ToList();
        ConfirmedAreasList.SelectedItem = _activeArea;
        _suppressAreaSelection = false;
    }

    private void ConfirmedAreasList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAreaSelection) return;
        if (ConfirmedAreasList.SelectedItem is not WorkingArea area) return;
        if (ReferenceEquals(area, _activeArea)) return;
        ClearFaceSelectionOnly();
        ActivateArea(area);
        RefreshAreaList();
        StatusText.Text = string.Format("{0} 작업영역 활성 — Face {1}개", area.Name, area.FaceIdSet.Count);
    }

    private void ActivateArea(WorkingArea area)
    {
        if (ReferenceEquals(_activeArea, area)) return;
        ResetCircuitEditingUi();
        _activeArea = area;

        if (area == null)
        {
            _confirmedFaceIds = new HashSet<int>();
            _selectedRegion = new List<List<Vec2>>();
            _multiFaceFrame = null;
            _exclusions = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
            _pattern = null;
            _turnEnabled = new List<bool>();
            _routePieceLengths = Array.Empty<double>();
            _projectedPieces = new List<RoutePieceResult>();
            _leadLengths = Array.Empty<double>();
            _multiFacePatternDisplayed = false;
            if (Viewport != null) Viewport.CircuitHoverMode = false;
        }
        else
        {
            ActivateAreaStateFields(area);
            if (Viewport != null)
                Viewport.CircuitHoverMode = area.Pattern != null && area.Pattern.RoutePieces.Count > 0;
            if (area.Pattern != null)
                UpdateCircuitTable(SerpentineTopology.Compute(area.Pattern.RoutePieces, area.TurnEnabled));
        }

        DrawRegionCanvas();
        ShowActiveExclusionOverlays();
        SyncExitFaceUi(area);
        UpdateActionStates();
    }

    private void ActivateAreaStateFields(WorkingArea area)
    {
        _confirmedFaceIds = area.FaceIdSet;
        _selectedRegion = area.Region;
        _multiFaceFrame = area.Frame;
        _exclusions = area.Exclusions;
        _pattern = area.Pattern;
        _turnEnabled = area.TurnEnabled;
        _routePieceLengths = area.RoutePieceLengths;
        _projectedPieces = area.ProjectedPieces;
        _leadLengths = area.LeadLengths;
        _multiFacePatternDisplayed = area.PatternDisplayed;
    }

    private void ClearFaceSelectionOnly()
    {
        _selectedFaceIds.Clear();
        if (SelectedFacesList != null) SelectedFacesList.Items.Clear();
        Bridge.ClearFaceSelection();
        Bridge.ClearHover();
        if (HoverText != null) HoverText.Text = "";
    }

    private void DeleteSelectedArea_Click(object sender, RoutedEventArgs e)
    {
        var area = ConfirmedAreasList?.SelectedItem as WorkingArea ?? _activeArea;
        if (area == null)
        {
            StatusText.Text = "삭제할 작업영역이 없습니다";
            return;
        }

        bool wasActive = ReferenceEquals(area, _activeArea);
        _areas.Remove(area);
        if (wasActive) ActivateArea(_areas.FirstOrDefault());

        ApplyAreaColors();
        if (!RebuildNativePatternGeometry(out _, out _)) return;

        if (!wasActive) ShowActiveExclusionOverlays();
        RefreshAreaList();
        ClearFaceSelectionOnly();
        StatusText.Text = string.Format("{0} 작업영역을 삭제했습니다 — 남은 영역 {1}개", area.Name, _areas.Count);
        UpdateActionStates();
    }

    private void ApplyAreaColors()
    {
        var faceIds = new List<int>();
        var rgb = new List<double>();
        foreach (var area in _areas)
        {
            double r = area.Color.R / 255.0, g = area.Color.G / 255.0, b = area.Color.B / 255.0;
            foreach (int faceId in area.FaceIdSet)
            {
                faceIds.Add(faceId);
                rgb.Add(r); rgb.Add(g); rgb.Add(b);
            }
        }
        if (faceIds.Count == 0)
        {
            Bridge.ClearConfirmedFaceHighlights();
            return;
        }
        Bridge.SetConfirmedFaceHighlightColors(faceIds.ToArray(), rgb.ToArray());
    }

    private string NextAreaName()
    {
        for (int i = 0; i < 26; i++)
        {
            string name = ((char)('A' + i)).ToString();
            if (_areas.All(a => a.Name != name)) return name;
        }
        return "A" + (_areas.Count + 1);
    }

    private Color NextAreaColor()
    {
        foreach (var color in AreaColors)
            if (_areas.All(a => a.Color != color)) return color;
        return AreaColors[_areas.Count % AreaColors.Length];
    }

    private int[] AllocateNativePieceIds(int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = _nextNativePieceId++;
        return ids;
    }

    private int[] SortedFaceIds(WorkingArea area) => area.FaceIdSet.OrderBy(id => id).ToArray();

    private string AreaNameForFace(int faceId) =>
        _areas.FirstOrDefault(area => area.FaceIdSet.Contains(faceId))?.Name;

    private static double[] Flatten(ProjectionFrame frame, IReadOnlyList<Vec2> points)
    {
        var xyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            Vec3 p = frame.Unproject(points[i]);
            xyz[i * 3] = p.X; xyz[i * 3 + 1] = p.Y; xyz[i * 3 + 2] = p.Z;
        }
        return xyz;
    }

    /// <summary>
    /// 모든 작업영역의 경로를 한 번에 다시 스테이징/커밋한다. 네이티브 커밋은
    /// 기존 커밋 경로를 전부 교체하므로 영역 추가/삭제/재생성 시 이 경로만 사용한다.
    /// 실패하면 기존 네이티브 표시는 그대로 남는다.
    /// </summary>
    private bool RebuildNativePatternGeometry(out double activeLength, out int activeCount)
    {
        activeLength = 0;
        activeCount = 0;
        var side = _workingSide.Confirmed;
        if (side == null) return false;

        var stagedRoutes = new List<int>();
        var finalIds = new List<int>();
        var stagedLines = new List<int>();
        var routeResults = new Dictionary<WorkingArea, (List<RoutePieceResult> Pieces, double[] Lengths)>();
        var lineLengths = new Dictionary<WorkingArea, double>();

        try
        {
            foreach (var area in _areas)
            {
                if (area.Pattern != null && area.Pattern.RoutePieces.Count > 0)
                {
                    int count = area.Pattern.RoutePieces.Count;
                    if (area.NativePieceIds.Length != count)
                        area.NativePieceIds = AllocateNativePieceIds(count);
                    var pieces = new List<RoutePieceResult>();
                    var lengths = new List<double>();
                    for (int i = 0; i < count; i++)
                    {
                        int stageId = _nextStageId--;
                        var piece = area.Pattern.RoutePieces[i];
                        // 인출 리드(ExitPiece)도 다른 조각과 동일하게 작업 방향으로 투영한다.
                        // 2D 프레임 좌표를 3D로 되돌린 점은 평면에서만 정확하므로 절대 근접성 검사로는 판정하지 않는다.
                        var shown = Bridge.StageProjectedWorkingRoutePiece(
                            side.Id.Value, side.Revision.Value, stageId, piece.Kind.ToString(),
                            Flatten(area.Frame, piece.Points), SortedFaceIds(area),
                            -area.Frame.Normal.X, -area.Frame.Normal.Y, -area.Frame.Normal.Z);
                        if (shown == null)
                            throw new InvalidOperationException(
                                "영역 " + area.Name + " 경로 조각 " + (i + 1) + "/" + count + " (" + piece.Kind + ")를 적용면에 투영하지 못했습니다 — 선택면 경계 밖으로 나갔거나 곡면 투영에 실패했습니다 [" + Bridge.GetWorkingStageDiagnostic() + "]");
                        stagedRoutes.Add(stageId);
                        finalIds.Add(area.NativePieceIds[i]);
                        pieces.Add(shown);
                        lengths.Add(shown.Length);
                    }
                    for (int i = 1; i < pieces.Count; i++)
                        if (Distance3D(pieces[i - 1].End, pieces[i].Start) > 0.01)
                            throw new InvalidOperationException("영역 " + area.Name + " 경로 조각이 연결되지 않습니다");
                    routeResults[area] = (pieces, lengths.ToArray());
                }
                else if (area.Paths.Count > 0)
                {
                    double sum = 0;
                    foreach (var path in area.Paths)
                    {
                        if (path.Count < 2) continue;
                        int stageId = _nextStageId--;
                        var projected = Bridge.StageProjectedWorkingPolyline(
                            side.Id.Value, side.Revision.Value, stageId,
                            Flatten(area.Frame, path), SortedFaceIds(area),
                            -area.Frame.Normal.X, -area.Frame.Normal.Y, -area.Frame.Normal.Z);
                        if (projected == null)
                            throw new InvalidOperationException("영역 " + area.Name + " 제외 경로 투영 실패");
                        stagedLines.Add(stageId);
                        sum += projected.Length;
                    }
                    lineLengths[area] = sum;
                }
            }

            if (stagedRoutes.Count == 0 && stagedLines.Count == 0)
            {
                Bridge.ClearOverlays();
            }
            else if (!Bridge.CommitWorkingRouteGeometry(
                         side.Id.Value, side.Revision.Value,
                         stagedRoutes.ToArray(), finalIds.ToArray(), stagedLines.ToArray()))
            {
                throw new InvalidOperationException("네이티브 경로 커밋 실패");
            }
        }
        catch (Exception ex)
        {
            Bridge.DiscardWorkingRouteGeometry(
                side.Id.Value, side.Revision.Value,
                stagedRoutes.ToArray(), stagedLines.ToArray());
            StatusText.Text = "표시 생성 실패 — 기존 영역/경로를 유지했습니다: " + ex.Message;
            ErrorReporter.ShowError("경로 표시 생성 실패 — 기존 영역/경로를 유지했습니다", ex);
            return false;
        }

        foreach (var entry in routeResults)
        {
            entry.Key.ProjectedPieces = entry.Value.Pieces;
            entry.Key.RoutePieceLengths = entry.Value.Lengths;
        }
        foreach (var area in _areas) ApplyAreaConnectorStates(area);

        if (_activeArea != null)
        {
            _pattern = _activeArea.Pattern;
            _turnEnabled = _activeArea.TurnEnabled;
            _routePieceLengths = _activeArea.RoutePieceLengths;
            _projectedPieces = _activeArea.ProjectedPieces;
            _leadLengths = _activeArea.LeadLengths;
            if (_activeArea.Pattern != null)
            {
                activeCount = _activeArea.Pattern.RoutePieces.Count;
                activeLength = _activeArea.RoutePieceLengths.Sum();
            }
            else if (lineLengths.TryGetValue(_activeArea, out double lineLength))
            {
                activeLength = lineLength;
            }
        }

        Bridge.RedrawView();
        return true;
    }

    private void ApplyAreaConnectorStates(WorkingArea area)
    {
        if (area.Pattern == null) return;
        int turn = 0;
        for (int i = 0; i < area.Pattern.RoutePieces.Count; i++)
        {
            if (area.Pattern.RoutePieces[i].Kind != SerpentineRoutePieceKind.TurnConnector) continue;
            bool enabled = turn < area.TurnEnabled.Count ? area.TurnEnabled[turn] : true;
            Bridge.SetConnectorEnabled(area.NativePieceIds[i], enabled, 0.2, 0.8, 0.3);
            turn++;
        }
    }

    private void ClearAllAreas(bool clearNative)
    {
        ResetCircuitEditingUi();
        _areas.Clear();
        _activeArea = null;
        _nextNativePieceId = 0;
        _selectedFaceIds.Clear();
        if (SelectedFacesList != null) SelectedFacesList.Items.Clear();
        // 외부에서 이전 컬렉션 참조를 들고 있어도 빈 상태가 되도록 in-place로도 비운다.
        _confirmedFaceIds.Clear();
        _selectedRegion.Clear();
        _exclusions.Clear();
        _turnEnabled.Clear();
        _projectedPieces.Clear();
        _confirmedFaceIds = new HashSet<int>();
        _selectedRegion = new List<List<Vec2>>();
        _multiFaceFrame = null;
        _exclusions = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        _pattern = null;
        _turnEnabled = new List<bool>();
        _routePieceLengths = Array.Empty<double>();
        _projectedPieces = new List<RoutePieceResult>();
        _leadLengths = Array.Empty<double>();
        _multiFacePatternDisplayed = false;
        if (Viewport != null) Viewport.CircuitHoverMode = false;
        RefreshAreaList();
        DrawRegionCanvas();
        SyncExitFaceUi(null);
        if (clearNative)
        {
            Bridge.ClearRoutePieces();
            Bridge.ClearExclusionOverlays();
            Bridge.ClearConfirmedFaceHighlights();
        }
    }

    private void ShowActiveExclusionOverlays()
    {
        Bridge.ClearExclusionOverlays();
        if (_activeArea == null) return;
        foreach (var rect in _activeArea.Exclusions)
            Bridge.AddExclusionOverlay(ExclusionRectangleWire(_activeArea.Frame, rect));
    }

    private double[] ExclusionRectangleWire(ProjectionFrame frame, (double MinX, double MinY, double MaxX, double MaxY) r)
    {
        Vec2[] corners =
        {
            new(r.MinX, r.MinY), new(r.MaxX, r.MinY), new(r.MaxX, r.MaxY), new(r.MinX, r.MaxY), new(r.MinX, r.MinY)
        };
        var xyz = new double[corners.Length * 3];
        for (int i = 0; i < corners.Length; i++)
        {
            Vec3 p = frame.Unproject(corners[i]);
            xyz[i * 3] = p.X; xyz[i * 3 + 1] = p.Y; xyz[i * 3 + 2] = p.Z;
        }
        return xyz;
    }
}
