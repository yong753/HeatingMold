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
        RefreshElectricalTable();
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
        // 영역이 바뀌면 이전 영역의 2D 제외 도구를 새 영역에 이어 쓰지 않는다.
        ExitExclusionSketch();
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

        UpdateActiveAreaHighlight();
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
        ExitVertexEdit();
        // 영역을 지우기 전에 채움 메시 표시를 먼저 제거한다(유령 하이라이트 방지).
        if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
        bool wasActive = ReferenceEquals(area, _activeArea);
        _areas.Remove(area);
        // 삭제된 영역을 참조하는 편집 이력은 남겨두지 않는다.
        _vertexUndo.Remove(area);
        _vertexRedo.Remove(area);
        if (wasActive) ActivateArea(_areas.FirstOrDefault());

        ApplyAreaColors();
        if (!RebuildNativePatternGeometry(out _, out _)) return;

        if (!wasActive) UpdateActiveAreaHighlight();
        RefreshAreaList();
        ClearFaceSelectionOnly();
        StatusText.Text = string.Format("{0} 작업영역을 삭제했습니다 — 남은 영역 {1}개", area.Name, _areas.Count);
        UpdateActionStates();
    }

    /// <summary>
    /// 모든 영역의 발열면 표시를 다시 적용한다. 제외가 없는 영역은 기존처럼 Face 색칠을
    /// 쓰고, 제외가 있는 영역은 클리핑된 채움 메시만 표시한다(통짜 Face 색칠 금지).
    /// </summary>
    private void ApplyAreaColors()
    {
        var faceIds = new List<int>();
        var rgb = new List<double>();
        var clipped = new List<WorkingArea>();
        foreach (var area in _areas)
        {
            if (area.Exclusions.Count > 0) { clipped.Add(area); continue; }
            double r = area.Color.R / 255.0, g = area.Color.G / 255.0, b = area.Color.B / 255.0;
            foreach (int faceId in area.FaceIdSet)
            {
                faceIds.Add(faceId);
                rgb.Add(r); rgb.Add(g); rgb.Add(b);
            }
        }

        // 메시를 먼저 확정해 두면 Face 색칠 갱신이 그 영역의 색을 덮어쓰지 않는다.
        foreach (var area in clipped)
            if (!ApplyAreaHighlightMesh(area))
                StatusText.Text = string.Format("{0} 영역의 제외영역 표시를 갱신하지 못했습니다 — 이전 표시를 유지합니다", area.Name);

        if (faceIds.Count == 0)
        {
            Bridge.ClearConfirmedFaceHighlights();
            return;
        }
        Bridge.SetConfirmedFaceHighlightColors(faceIds.ToArray(), rgb.ToArray());
    }

    /// <summary>
    /// 활성 영역의 발열면 표시를 현재 제외 상태에 맞춘다.
    /// 제외 없음 → Face 색칠, 제외 있음 → 클리핑된 채움 메시. 실패 시 이전 표시를 유지한다.
    /// </summary>
    private void UpdateActiveAreaHighlight()
    {
        var area = _activeArea;
        if (area == null) return;
        try
        {
            if (area.Frame == null || area.FaceIdSet == null || area.FaceIdSet.Count == 0)
            {
                if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
                return;
            }
            if (area.Exclusions.Count == 0)
            {
                // Face 색칠로 되돌린다. 여러 영역이 공존하므로 전체 색칠을 다시 적용한다.
                if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
                ApplyAreaColors();
                return;
            }
            // Stage가 실패하면 이전 표시(이전 커밋 메시 또는 Face 색칠)가 그대로 남는다.
            if (!ApplyAreaHighlightMesh(area))
            {
                StatusText.Text = "제외영역 표시를 갱신하지 못했습니다 — 이전 표시를 유지합니다";
                return;
            }
            Bridge.RedrawView();
        }
        catch (Exception ex)
        {
            StatusText.Text = "제외영역 표시 갱신 실패: " + ex.Message;
        }
    }

    /// <summary>영역의 2D 제외 사각형을 4개 double(minX,minY,maxX,maxY)씩 펼친다.</summary>
    private static double[] ExclusionRectValues(WorkingArea area)
    {
        var rects = new double[area.Exclusions.Count * 4];
        for (int i = 0; i < area.Exclusions.Count; i++)
        {
            var rect = area.Exclusions[i];
            rects[i * 4] = rect.MinX;
            rects[i * 4 + 1] = rect.MinY;
            rects[i * 4 + 2] = rect.MaxX;
            rects[i * 4 + 3] = rect.MaxY;
        }
        return rects;
    }

    /// <summary>StageAreaHighlightMesh에 넘길 프레임 값 9개(origin, axisX, axisY).</summary>
    private static double[] FrameValues(WorkingArea area)
    {
        ProjectionFrame frame = area.Frame;
        return new[]
        {
            frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
            frame.AxisX.X, frame.AxisX.Y, frame.AxisX.Z,
            frame.AxisY.X, frame.AxisY.Y, frame.AxisY.Z
        };
    }

    /// <summary>제외 사각형을 뺀 채움 메시를 스테이징한다(표시는 아직 바뀌지 않는다).</summary>
    private bool StageAreaHighlightMesh(WorkingArea area)
    {
        var side = _workingSide?.Confirmed;
        if (area == null || area.AreaId <= 0 || area.Frame == null || area.FaceIdSet == null ||
            area.FaceIdSet.Count == 0 || area.Exclusions.Count == 0 ||
            side == null) return false;
        try
        {
            int[] faceIds = SortedFaceIds(area);
            return Bridge.StageAreaHighlightMesh(area.AreaId, side.Revision.Value, side.Id.Value,
                faceIds, ExclusionRectValues(area), FrameValues(area),
                new[] { area.Color.R / 255.0, area.Color.G / 255.0, area.Color.B / 255.0 });
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>스테이징된 채움 메시를 확정 표시로 바꾼다.</summary>
    private bool CommitAreaHighlight(WorkingArea area)
    {
        if (area == null || area.AreaId <= 0) return false;
        try { return Bridge.CommitAreaHighlight(area.AreaId); }
        catch (Exception) { return false; }
    }

    private bool ApplyAreaHighlightMesh(WorkingArea area) =>
        StageAreaHighlightMesh(area) && CommitAreaHighlight(area);

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

    private string FaceTypesText(WorkingArea area) =>
        string.Join(", ", SortedFaceIds(area).Select(id => "Face " + id + "=" + Bridge.GetFaceType(id)));

    /// <summary>위상 인접성 검사가 실패한 전이에서 허용하는 실제 3D 간극 — 게이트 sewing 허용치 0.01mm와 동일.</summary>
    private const double SeamGapTolerance = 0.01;

    /// <summary>
    /// 곡면 영역의 조각 하나를 데이텀 점 단위로 매핑해 3D 폴리라인으로 스테이징한다.
    /// 2D 프레임 → 3D 평면 되돌리기와 BRepProj 재투영을 모두 쓰지 않는다.
    /// </summary>
    /// <param name="allowCachedSamples">false면 편집 세션 표본 캐시(_pendingStageSamples/
    /// _vertexBaselineSamples)를 읽지도 쓰지도 않고 항상 다시 매핑한다.</param>
    /// <param name="exclusionMode">true면 제외영역이 잘라낸 연결선을 "ExcludedBridge" 종류로 넘긴다.</param>
    private RoutePieceResult StageCurvedRoutePiece(WorkingArea area, WorkingSideDefinition side,
        SerpentineRoutePiece piece, int index, int count, int stageId,
        bool allowCachedSamples = true, bool exclusionMode = false)
    {
        var hit = BuildSurfaceMapDelegate(area, side);
        List<SurfaceMappedSample> samples = null;
        bool cached = allowCachedSamples;
        if (cached && !_pendingStageSamples.Remove((area, index), out samples))
            cached = _vertexBaselineSamples.TryGetValue((area, index), out samples);
        if (!cached)
        {
            if (!SurfacePathMapper.TryMapRoute(piece.Points, hit, out samples, out string failure))
                throw new InvalidOperationException(
                    "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 매핑 실패 — "
                    + failure + " [" + FaceTypesText(area) + "]");
            // 편집 세션 중에는 바뀌지 않은 조각의 표본을 유지해 다음 확정에서 재매핑을 생략한다.
            if (allowCachedSamples && Viewport != null && Viewport.VertexEditMode && ReferenceEquals(area, _activeArea))
                _vertexBaselineSamples[(area, index)] = samples;
        }

        // 결정적 레이캐스트에서는 같은 데이텀 점이 항상 같은 3D 점을 내므로 인접 조각 끝점은
        // 자동으로 일치한다. 여기서는 비인접 면으로 건너뛴 경우만 잡는다(앞/뒤면 붕괴 방지).
        // 위상적으로 Edge를 공유하지 않아도 두 면이 기하학적으로는 접합(같은 곡면)일 수 있다.
        // 전이 지점의 데이텀 간격을 µm 수준으로 좁혀 실제 3D 간극을 재고, 그 값으로 판정한다.
        for (int i = 1; i < samples.Count; i++)
        {
            int previous = samples[i - 1].FaceId, current = samples[i].FaceId;
            if (previous < 0 || current < 0 || previous == current) continue;
            double? gap = MeasureSeamGap(hit, samples[i - 1], samples[i]);
            if (gap.HasValue && gap.Value <= SeamGapTolerance) continue;
            throw new InvalidOperationException(
                "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ")가 인접하지 않은 면으로 건너뜁니다 — Face "
                + previous + " → Face " + current + " (3D 간극 "
                + (gap.HasValue ? gap.Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "측정 실패")
                + " mm) [" + FaceTypesText(area) + "]");
        }

        var xyz = new double[samples.Count * 3];
        var supportFaces = new int[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            xyz[i * 3] = samples[i].Point.X;
            xyz[i * 3 + 1] = samples[i].Point.Y;
            xyz[i * 3 + 2] = samples[i].Point.Z;
            supportFaces[i] = samples[i].FaceId;
        }
        var shown = Bridge.StageSurfaceMappedRoutePieceWithFaces(
            side.Id.Value, side.Revision.Value, stageId, StageKindText(piece, exclusionMode), xyz, supportFaces);
        if (shown == null)
            throw new InvalidOperationException(
                "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 3D 스테이징 실패 ["
                + FaceTypesText(area) + " | " + Bridge.GetWorkingStageDiagnostic() + "]");
        return shown;
    }

    /// <summary>네이티브가 색/선종류를 고르는 데 쓰는 kind 문자열. 제외 모드의 턴·고정 연결선과
    /// 인출 리드는 발열선이 아니므로 전부 ExcludedBridge(연결선 스타일)로 넘긴다.</summary>
    private static string StageKindText(SerpentineRoutePiece piece, bool exclusionMode) =>
        exclusionMode && piece.Kind is SerpentineRoutePieceKind.TurnConnector
            or SerpentineRoutePieceKind.FixedConnector or SerpentineRoutePieceKind.ExitPiece
            ? "ExcludedBridge"
            : piece.Kind.ToString();

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
        var exclusionResults = new Dictionary<WorkingArea, (List<RoutePieceResult> Pieces, double[] Lengths)>();
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
                    // 모든 영역(평면 포함)이 같은 규칙을 쓴다: 데이텀 점 → 우선 선택면/전역 표면
                    // 레이캐스트 → 3D 폴리라인 스테이징. 평면 전용 BRepProj 분기는 선택면 밖으로
                    // 이동한 꼭지점을 거부하므로 더 이상 쓰지 않는다.
                    for (int i = 0; i < count; i++)
                    {
                        int stageId = _nextStageId--;
                        var piece = area.Pattern.RoutePieces[i];
                        RoutePieceResult shown = StageCurvedRoutePiece(
                            area, side, piece, i, count, stageId,
                            exclusionMode: area.Exclusions.Count > 0);
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
                else if (area.ExclusionPieces is { Count: > 0 })
                {
                    // 제외 모드도 일반 경로와 같은 표면 매핑을 쓴다. 제외영역을 지나는 연결선은 실제
                    // 모델 표면 위에 있을 때만 표시되고, 표면이 끊기면 이번 생성 전체가 거부된다.
                    var flat = new List<SerpentineRoutePiece>();
                    var componentOf = new List<int>();
                    for (int c = 0; c < area.ExclusionPieces.Count; c++)
                        foreach (var piece in area.ExclusionPieces[c])
                        {
                            flat.Add(piece);
                            componentOf.Add(c);
                        }
                    if (area.NativePieceIds.Length != flat.Count)
                        area.NativePieceIds = AllocateNativePieceIds(flat.Count);
                    var pieces = new List<RoutePieceResult>();
                    var lengths = new List<double>();
                    double sum = 0;
                    RoutePieceResult previous = null;
                    int previousComponent = -1;
                    for (int i = 0; i < flat.Count; i++)
                    {
                        if (componentOf[i] != previousComponent)
                        {
                            previous = null;
                            previousComponent = componentOf[i];
                        }
                        SerpentineRoutePiece piece = flat[i];
                        if (piece.Points.Count < 2) continue;
                        int stageId = _nextStageId--;
                        RoutePieceResult shown = StageCurvedRoutePiece(
                            area, side, piece, i, flat.Count, stageId,
                            allowCachedSamples: false, exclusionMode: true);
                        if (previous != null && Distance3D(previous.End, shown.Start) > 0.01)
                            throw new InvalidOperationException("영역 " + area.Name + " 제외 경로 조각이 연결되지 않습니다");
                        stagedRoutes.Add(stageId);
                        finalIds.Add(area.NativePieceIds[i]);
                        pieces.Add(shown);
                        lengths.Add(shown.Length);
                        sum += shown.Length;
                        previous = shown;
                    }
                    exclusionResults[area] = (pieces, lengths.ToArray());
                    lineLengths[area] = sum;
                }
                else if (area.Paths.Count > 0)
                {
                    double sum = 0;
                    foreach (var path in area.Paths)
                    {
                        if (path.Count < 2) continue;
                        int stageId = _nextStageId--;
                        var projected = Bridge.StageProjectedWorkingPolyline(
                            side.Id.Value, side.Revision.Value, stageId, "HeatingRun",
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
            foreach (var entry in exclusionResults)
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
            // Rebuild 끝의 RedrawView가 일괄 반영한다 — 커넥터마다 Redraw하지 않는다.
            Bridge.SetConnectorEnabled(area.NativePieceIds[i], enabled, 0.2, 0.8, 0.3, false);
            turn++;
        }
    }

    private void ClearAllAreas(bool clearNative)
    {
        ExitExclusionSketch();
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
        SyncExitFaceUi(null);
        if (clearNative)
        {
            Bridge.ClearRoutePieces();
            Bridge.ClearExclusionOverlays();
            Bridge.ClearAllAreaHighlights();
            Bridge.ClearConfirmedFaceHighlights();
        }
    }
}
