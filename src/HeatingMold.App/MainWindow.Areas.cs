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
        ExitVertexEdit();
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

        if (!ConfirmDiscardRouteEdits(area, "작업영역 삭제")) return;
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

    /// <summary>곡면이 하나라도 포함된 영역인지. 평면 전용 영역은 기존 투영 경로를 유지한다.</summary>
    private bool IsCurvedArea(WorkingArea area) =>
        area.FaceIdSet.Any(id => !string.Equals(Bridge.GetFaceType(id), "Plane", StringComparison.Ordinal));

    private string FaceTypesText(WorkingArea area) =>
        string.Join(", ", SortedFaceIds(area).Select(id => "Face " + id + "=" + Bridge.GetFaceType(id)));

    /// <summary>위상 인접성 검사가 실패한 전이에서 허용하는 실제 3D 간극 — 게이트 sewing 허용치 0.01mm와 동일.</summary>
    private const double SeamGapTolerance = 0.01;

    /// <summary>
    /// 곡면 영역의 조각 하나를 데이텀 점 단위로 매핑해 3D 폴리라인으로 스테이징한다.
    /// 2D 프레임 → 3D 평면 되돌리기와 BRepProj 재투영을 모두 쓰지 않는다.
    /// </summary>
    private RoutePieceResult StageCurvedRoutePiece(WorkingArea area, WorkingSideDefinition side,
        SerpentineRoutePiece piece, int index, int count, int stageId)
    {
        int[] faces = SortedFaceIds(area);
        Vec3? anchor3D = ResolveExitAnchor3D(area, piece, out Vec2? anchorDatum, out int anchorFaceId);
        var hit = BuildCurvedMapDelegate(side, faces, anchorDatum, anchor3D, anchorFaceId);
        if (!SurfacePathMapper.TryMapRoute(piece.Points, hit, out var samples, out string failure))
            throw new InvalidOperationException(
                "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 매핑 실패 — "
                + failure + " [" + FaceTypesText(area) + "]");

        // 결정적 레이캐스트에서는 같은 데이텀 점이 항상 같은 3D 점을 내므로 인접 조각 끝점은
        // 자동으로 일치한다. 여기서는 비인접 면으로 건너뛴 경우만 잡는다(앞/뒤면 붕괴 방지).
        // 위상적으로 Edge를 공유하지 않아도 두 면이 기하학적으로는 접합(같은 곡면)일 수 있다.
        // 전이 지점의 데이텀 간격을 µm 수준으로 좁혀 실제 3D 간극을 재고, 그 값으로 판정한다.
        for (int i = 1; i < samples.Count; i++)
        {
            int previous = samples[i - 1].FaceId, current = samples[i].FaceId;
            if (previous < 0 || current < 0 || previous == current) continue;
            if (Bridge.AreWorkingFacesAdjacent(side.Id.Value, side.Revision.Value, previous, current)) continue;
            double? gap = MeasureSeamGap(hit, samples[i - 1], samples[i]);
            if (gap.HasValue && gap.Value <= SeamGapTolerance) continue;
            throw new InvalidOperationException(
                "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ")가 인접하지 않은 면으로 건너뜁니다 — Face "
                + previous + " → Face " + current + " (3D 간극 "
                + (gap.HasValue ? gap.Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "측정 실패")
                + " mm) [" + FaceTypesText(area) + "]");
        }

        var xyz = new double[samples.Count * 3];
        for (int i = 0; i < samples.Count; i++)
        {
            xyz[i * 3] = samples[i].Point.X;
            xyz[i * 3 + 1] = samples[i].Point.Y;
            xyz[i * 3 + 2] = samples[i].Point.Z;
        }
        var shown = Bridge.StageSurfaceMappedRoutePiece(
            side.Id.Value, side.Revision.Value, stageId, piece.Kind.ToString(), xyz, faces);
        if (shown == null)
            throw new InvalidOperationException(
                "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 3D 스테이징 실패 ["
                + FaceTypesText(area) + " | " + Bridge.GetWorkingStageDiagnostic() + "]");
        return shown;
    }

    /// <summary>
    /// 두 연속 샘플의 면이 다를 때 그 사이 데이텀 구간을 이분 탐색해 전이 지점을 µm 수준으로 좁히고,
    /// 같은 데이텀에서 양쪽 면의 3D 점 거리(= 실제 간극)를 잰다. 접합면이면 ≈0, 앞/뒤면 붕괴면 벽 두께.
    /// 측정 불가(구간이 영역 밖으로 나감)면 null.
    /// </summary>
    private static double? MeasureSeamGap(Func<Vec2, SurfaceMappedSample?> hit,
        SurfaceMappedSample before, SurfaceMappedSample after)
    {
        Vec2 low = before.DatumPoint, high = after.DatumPoint;
        int lowFace = before.FaceId;
        Vec3 lowPoint = before.Point, highPoint = after.Point;
        for (int step = 0; step < 30; step++)
        {
            var middle = new Vec2((low.X + high.X) * 0.5, (low.Y + high.Y) * 0.5);
            var sample = hit(middle);
            if (!sample.HasValue || sample.Value.FaceId < 0) return null;
            if (sample.Value.FaceId == lowFace) { low = middle; lowPoint = sample.Value.Point; }
            else { high = middle; highPoint = sample.Value.Point; }
        }
        return (lowPoint - highPoint).Length;
    }

    /// <summary>선택 면 집합만 대상으로 하는 매핑 대리자. 실패 시 전역 히트→0.5mm 스냅으로 보정한다.</summary>
    private static Func<Vec2, SurfaceMappedSample?> BuildCurvedMapDelegate(WorkingSideDefinition side,
        int[] faces, Vec2? anchorDatum, Vec3? anchor3D, int anchorFaceId)
    {
        return uv =>
        {
            // 인출 리드의 끝점은 실측 CAD 공유 모서리 위 점을 그대로 쓴다(실루엣 붕괴 예외).
            if (anchorDatum.HasValue && anchor3D.HasValue && uv == anchorDatum.Value)
                return new SurfaceMappedSample(uv, anchor3D.Value, anchorFaceId);
            var direct = Bridge.MapWorkingPointOntoFaces(side.Id.Value, side.Revision.Value, faces, uv.X, uv.Y);
            if (direct != null && direct.Status == WorkingQueryStatus.Hit && direct.Point != null)
                return new SurfaceMappedSample(uv, new Vec3(direct.Point[0], direct.Point[1], direct.Point[2]), direct.FaceId);
            // 폴백: 전역 레이캐스트 히트를 선택 면까지 0.5mm 이내로 스냅한다. 벽면이 얇아도
            // 0.5mm를 넘으면 채택하지 않으므로 반대편 면으로 조용히 넘어가지 않는다.
            var loose = Bridge.QueryWorkingSurface(side.Id.Value, side.Revision.Value, uv.X, uv.Y);
            if (loose != null && loose.Status == WorkingQueryStatus.Hit && loose.Point != null)
            {
                var snapped = Bridge.SnapWorkingPointToFaces(loose.Point[0], loose.Point[1], loose.Point[2], faces, 0.5);
                if (snapped != null && snapped.Status == WorkingQueryStatus.Hit && snapped.Point != null)
                    return new SurfaceMappedSample(uv, new Vec3(snapped.Point[0], snapped.Point[1], snapped.Point[2]), snapped.FaceId);
            }
            return null;
        };
    }

    /// <summary>
    /// 조각의 끝점이 이 영역 경로의 시작/종료 역할이고 인출 체인 위에 있으면
    /// 실제 공유 모서리 3D 점과 그 구간의 인출 Face id를 찾는다.
    /// ExitPiece 종류가 아니라 경로 시작/종료 역할로 판정하므로, 앵커와 레인 끝이
    /// 일치해 ExitPiece가 없는 경우에도 동작한다.
    /// </summary>
    private static Vec3? ResolveExitAnchor3D(WorkingArea area, SerpentineRoutePiece piece,
        out Vec2? datum, out int anchorFaceId)
    {
        datum = null;
        anchorFaceId = area.ExitFaceId;
        if (!area.HasExitChain3D || piece.Points.Count == 0) return null;
        var pieces = area.Pattern?.RoutePieces;
        if (pieces == null || pieces.Count == 0) return null;
        if (pieces[0].Points.Count == 0 || pieces[^1].Points.Count == 0) return null;

        Vec2 start = pieces[0].Points[0];
        Vec2 end = pieces[^1].Points[^1];
        Vec2 first = piece.Points[0], last = piece.Points[^1];
        if (Serpentine.DistanceToChain(area.ExitChain, start) <= 0.05 && (first == start || last == start))
            datum = start;
        else if (Serpentine.DistanceToChain(area.ExitChain, end) <= 0.05 && (first == end || last == end))
            datum = end;
        if (!datum.HasValue) return null;
        Vec3 point = PointOnExitChain3D(area, datum.Value, out int segmentIndex);
        // 병합된 인출면은 구간마다 실제 Face가 다를 수 있으므로 앵커가 놓인 구간의 id를 쓴다.
        if (segmentIndex >= 1 && segmentIndex - 1 < area.ExitChainFaceIds.Count)
            anchorFaceId = area.ExitChainFaceIds[segmentIndex - 1];
        return point;
    }

    /// <summary>2D 인출 체인에서 가장 가까운 구간을 찾아 같은 구간 파라미터로 3D 점을 보간한다.</summary>
    private static Vec3 PointOnExitChain3D(WorkingArea area, Vec2 datum, out int segmentIndex)
    {
        double bestDistance = double.MaxValue;
        int bestSegment = 1;
        double bestT = 0;
        for (int i = 1; i < area.ExitChain.Count; i++)
        {
            Vec2 a = area.ExitChain[i - 1], b = area.ExitChain[i];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared <= 1e-18
                ? 0
                : Math.Clamp(((datum.X - a.X) * dx + (datum.Y - a.Y) * dy) / lengthSquared, 0, 1);
            double px = a.X + dx * t - datum.X, py = a.Y + dy * t - datum.Y;
            double distance = px * px + py * py;
            if (distance < bestDistance) { bestDistance = distance; bestSegment = i; bestT = t; }
        }
        segmentIndex = bestSegment;
        Vec3 p0 = area.ExitChain3D[bestSegment - 1], p1 = area.ExitChain3D[bestSegment];
        return new Vec3(p0.X + (p1.X - p0.X) * bestT,
                        p0.Y + (p1.Y - p0.Y) * bestT,
                        p0.Z + (p1.Z - p0.Z) * bestT);
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
                    // 평면만 있는 영역은 기존 BRepProj 경로를 그대로 유지한다(회귀 없음).
                    // 곡면이 하나라도 섞이면 데이텀 점 → 선택면 전용 레이캐스트 → 3D 폴리라인 스테이징.
                    bool curved = IsCurvedArea(area);
                    for (int i = 0; i < count; i++)
                    {
                        int stageId = _nextStageId--;
                        var piece = area.Pattern.RoutePieces[i];
                        // 인출 리드(ExitPiece)도 다른 조각과 동일하게 작업 방향으로 투영한다.
                        // 2D 프레임 좌표를 3D로 되돌린 점은 평면에서만 정확하므로 절대 근접성 검사로는 판정하지 않는다.
                        RoutePieceResult shown;
                        if (curved)
                        {
                            shown = StageCurvedRoutePiece(area, side, piece, i, count, stageId);
                        }
                        else
                        {
                            shown = Bridge.StageProjectedWorkingRoutePiece(
                                side.Id.Value, side.Revision.Value, stageId, piece.Kind.ToString(),
                                Flatten(area.Frame, piece.Points), SortedFaceIds(area),
                                -area.Frame.Normal.X, -area.Frame.Normal.Y, -area.Frame.Normal.Z);
                        }
                        if (shown == null)
                            throw new InvalidOperationException(
                                "영역 " + area.Name + " 경로 조각 " + (i + 1) + "/" + count + " (" + piece.Kind + ")를 적용면에 투영하지 못했습니다 — 선택면 경계 밖으로 나갔거나 곡면 투영에 실패했습니다 ["
                                + (curved ? FaceTypesText(area) + " | " : "") + Bridge.GetWorkingStageDiagnostic() + "]");
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

        // 네이티브 커밋은 이미 성공했다. 아래 갱신이 실패해도 되돌리지 않고(표시와 상태가
        // 어긋난 채 남는 것보다 낫다) 오류만 보고한다. 되돌리기는 커밋 전 단계에서만 한다.
        try
        {
            foreach (var entry in routeResults)
            {
                entry.Key.ProjectedPieces = entry.Value.Pieces;
                entry.Key.RoutePieceLengths = entry.Value.Lengths;
            }
            foreach (var area in _areas) ApplyAreaConnectorStates(area);

            // 발열 경로 인출점/끝점 마커 — 각 영역 첫 조각의 Start, 마지막 조각의 End.
            var endpointPoints = new List<double>();
            foreach (var entry in routeResults.Values)
            {
                var pieces = entry.Pieces;
                if (pieces.Count == 0) continue;
                var first = pieces[0];
                var last = pieces[^1];
                if (first?.Start != null) endpointPoints.AddRange(first.Start);
                if (last?.End != null) endpointPoints.AddRange(last.End);
            }
            Bridge.SetRouteEndpoints(endpointPoints.ToArray());

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
        }
        catch (Exception ex)
        {
            StatusText.Text = "경로는 적용되었지만 표시 상태 갱신에 실패했습니다: " + ex.Message;
            ErrorReporter.ShowWarning("경로 표시 상태 갱신 실패", ex);
            Bridge.RedrawView();
            return true;
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
