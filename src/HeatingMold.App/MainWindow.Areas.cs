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
        if (CircuitLengthGrid != null) CircuitLengthGrid.ItemsSource = null;
    }

    private void RefreshAreaList()
    {
        _suppressAreaSelection = true;
        _syncingAreaSelector = true;
        try
        {
            if (ConfirmedAreasList != null)
            {
                ConfirmedAreasList.ItemsSource = null;
                ConfirmedAreasList.ItemsSource = _areas.ToList();
                ConfirmedAreasList.SelectedItem = _activeArea;
            }
            if (ActiveAreaSelectorBox != null)
            {
                ActiveAreaSelectorBox.ItemsSource = null;
                ActiveAreaSelectorBox.ItemsSource = _areas.ToList();
                ActiveAreaSelectorBox.SelectedItem = _activeArea;
                ActiveAreaSelectorBox.IsEnabled = _areas.Count > 0;
            }
        }
        finally
        {
            _syncingAreaSelector = false;
            _suppressAreaSelection = false;
        }
        RefreshElectricalTable();
    }

    private void ActiveAreaSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAreaSelector) return;
        if (ActiveAreaSelectorBox?.SelectedItem is not WorkingArea area) return;
        if (ReferenceEquals(area, _activeArea)) return;
        ClearFaceSelectionOnly();
        ActivateArea(area);
        RefreshAreaList();
        StatusText.Text = string.Format("{0} 작업영역 활성 — Face {1}개", area.Name, area.FaceIdSet.Count);
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
        SyncPatternInputs(area);

        if (area == null)
        {
            if (Viewport != null) Viewport.CircuitHoverMode = false;
        }
        else
        {
            if (Viewport != null)
                Viewport.CircuitHoverMode = area.Pattern != null && area.Pattern.RoutePieces.Count > 0;
            if (area.Pattern != null)
                UpdateCircuitTable();
        }

        UpdateActiveAreaHighlight();
        SyncExitFaceUi(area);
        ApplyActiveExitEdgeHighlight();
        UpdateExportScopeText();
        if (area != null && IsPatternInputStale(area))
            StatusText.Text = string.Format("{0} 영역 — 입력 조건이 변경되었습니다 (이전 결과 표시 중)", area.Name);
        UpdateActionStates();
    }

    /// <summary>활성 영역 전환 시 그 영역의 배치 조건 초안을 화면에 복원한다.</summary>
    private void SyncPatternInputs(WorkingArea area)
    {
        if (area == null) return;
        // 두 값을 먼저 캡처해 두고, 복원 중 발생하는 TextChanged가 상대 필드의
        // 아직 화면에 남은 값을 새 영역 초안에 덮어쓰지 않게 한다.
        string marginText = area.MarginText ?? "";
        string spacingText = area.SpacingText ?? "";
        _suppressPatternInputWriteBack = true;
        try
        {
            if (MarginBox != null && MarginBox.Text != marginText) MarginBox.Text = marginText;
            if (SpacingBox != null && SpacingBox.Text != spacingText) SpacingBox.Text = spacingText;
        }
        finally
        {
            _suppressPatternInputWriteBack = false;
        }
    }

    /// <summary>제외영역 변경이나 재생성 실패 시 복원할 경로 상태를 캡처한다.</summary>
    private static AreaRouteSnapshot CaptureAreaRouteState(WorkingArea area) => new(
        area.Exclusions.ToList(),
        area.Pattern,
        area.Paths,
        area.ExclusionPieces,
        area.NativePieceIds,
        area.RoutePieceLengths,
        area.ProjectedPieces,
        area.TurnEnabled.ToList(),
        area.PatternDisplayed,
        area.LastExitFailure,
        area.StartAnchor,
        area.EndAnchor,
        area.AnchorCandidates,
        area.ExcludedCandidates,
        area.ExcludedPlan,
        area.ExcludedCandidateFailure,
        area.ExcludedPlanKey,
        area.Circuits,
        area.CircuitPieceIndices,
        area.CircuitExitOverrides.ToList(),
        area.ExitEdgeRefs,
        area.AppliedInputs,
        area.CommittedMapping);

    /// <summary>캡처한 경로 상태를 영역에 되돌린다. 제외 목록은 필요할 때만 복원한다.</summary>
    private static void RestoreAreaRouteState(WorkingArea area, AreaRouteSnapshot snapshot, bool restoreExclusions)
    {
        if (restoreExclusions)
        {
            area.Exclusions.Clear();
            area.Exclusions.AddRange(snapshot.Exclusions);
        }
        area.Pattern = snapshot.Pattern;
        area.Paths = snapshot.Paths;
        area.ExclusionPieces = snapshot.ExclusionPieces;
        area.NativePieceIds = snapshot.NativePieceIds;
        area.RoutePieceLengths = snapshot.RoutePieceLengths;
        area.ProjectedPieces = snapshot.ProjectedPieces;
        area.TurnEnabled = snapshot.TurnEnabled;
        area.PatternDisplayed = snapshot.PatternDisplayed;
        area.LastExitFailure = snapshot.LastExitFailure;
        area.StartAnchor = snapshot.StartAnchor;
        area.EndAnchor = snapshot.EndAnchor;
        area.AnchorCandidates = snapshot.AnchorCandidates;
        area.ExcludedCandidates = snapshot.ExcludedCandidates;
        area.ExcludedPlan = snapshot.ExcludedPlan;
        area.ExcludedCandidateFailure = snapshot.ExcludedCandidateFailure;
        area.ExcludedPlanKey = snapshot.ExcludedPlanKey;
        area.Circuits = snapshot.Circuits;
        area.CircuitPieceIndices = snapshot.CircuitPieceIndices;
        area.CircuitExitOverrides = snapshot.CircuitExitOverrides;
        area.ExitEdgeRefs = snapshot.ExitEdgeRefs;
        area.AppliedInputs = snapshot.AppliedInputs;
        area.CommittedMapping = snapshot.CommittedMapping;
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
        bool wasActive = ReferenceEquals(area, _activeArea);
        WorkingArea previousActive = _activeArea;
        var remaining = _areas.Where(a => !ReferenceEquals(a, area)).ToList();
        WorkingArea nextActive = wasActive ? remaining.FirstOrDefault() : previousActive;

        // 후보 목록으로 네이티브 재구성을 먼저 검증한다. 성공 전에는
        // _areas/활성 영역/하이라이트/표를 바꾸지 않으므로 실패 시 복원 코드가 필요 없다.
        if (!RebuildNativePatternGeometry(remaining, null, out _, out _))
        {
            StatusText.Text = string.Format(
                "{0} 영역을 삭제하지 못했습니다 — 기존 영역과 경로를 유지합니다", area.Name);
            UpdateActionStates();
            return;
        }

        if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
        _areas.Clear();
        _areas.AddRange(remaining);
        ApplyAreaColors();
        ActivateArea(nextActive);
        RefreshAreaList();
        UpdateCircuitTable();
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

    /// <summary>
    /// 곡면 영역의 조각 하나를 데이텀 점 단위로 매핑해 3D 폴리라인으로 스테이징한다.
    /// 2D 프레임 → 3D 평면 되돌리기와 BRepProj 재투영을 모두 쓰지 않는다.
    /// </summary>
    /// <param name="allowCachedSamples">false면 편집 세션 표본 캐시(_pendingStageSamples/
    /// _vertexBaselineSamples)를 읽지도 쓰지도 않고 항상 다시 매핑한다.</param>
    /// <param name="exclusionMode">true면 제외영역이 잘라낸 연결선을 "ExcludedBridge" 종류로 넘긴다.</param>
    private RoutePieceResult StageCurvedRoutePiece(WorkingArea area, WorkingSideDefinition side,
        SerpentineRoutePiece piece, int index, int count, int stageId,
        bool allowCachedSamples = true, bool exclusionMode = false,
        CommittedRouteContext mappingContext = null,
        IReadOnlyDictionary<int, SurfaceMappedSample> vertexOverrides = null)
    {
        var hit = BuildSurfaceMapDelegate(area, side, context: mappingContext);
        List<SurfaceMappedSample> samples = null;
        bool cached = allowCachedSamples;
        if (cached && !_pendingStageSamples.Remove((area, index), out samples))
            cached = _vertexBaselineSamples.TryGetValue((area, index), out samples);
        if (!cached)
        {
            if (!SurfacePathMapper.TryMapRoute(piece.Points, hit, vertexOverrides, out samples, out string failure))
                throw new InvalidOperationException(
                    "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 매핑 실패 — "
                    + failure + " [" + FaceTypesText(area) + "]");
            // 편집 세션 중에는 바뀌지 않은 조각의 표본을 유지해 다음 확정에서 재매핑을 생략한다.
            if (allowCachedSamples && Viewport != null && Viewport.VertexEditMode && ReferenceEquals(area, _activeArea))
                _vertexBaselineSamples[(area, index)] = samples;
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

    /// <summary>네이티브가 색/선종류를 고르는 데 쓰는 kind 문자열. 제외 모드의 턴·고정 연결선은
    /// 발열선이 아니므로 전부 ExcludedBridge(연결선 스타일)로 넘긴다.</summary>
    private static string StageKindText(SerpentineRoutePiece piece, bool exclusionMode) =>
        exclusionMode && piece.Kind is SerpentineRoutePieceKind.TurnConnector
            or SerpentineRoutePieceKind.FixedConnector
            ? "ExcludedBridge"
            : piece.Kind.ToString();

    /// <summary>회로 인출점과 일치하는 조각 끝점에만 edge 평가 override를 붙인다.</summary>
    private void AddExitOverride(ref Dictionary<int, SurfaceMappedSample> overrides,
        int pointIndex, Vec2 exit, Vec2 piecePoint, WorkingSideDefinition side,
        CommittedRouteContext context)
    {
        if ((exit - piecePoint).Length > 1e-6) return;
        SurfaceMappedSample? sample = BuildExitVertexOverride(side, context, exit);
        if (!sample.HasValue)
            throw new InvalidOperationException("인출점을 원본 CAD edge 위에서 평가하지 못했습니다");
        overrides ??= new Dictionary<int, SurfaceMappedSample>();
        overrides[pointIndex] = sample.Value with { DatumPoint = piecePoint };
    }

    /// <summary>2D 인출 체인에서 가장 가까운 구간을 찾아 같은 구간 파라미터로 3D 점을 보간한다.</summary>
    private static Vec3 PointOnExitChain3D(WorkingArea area, Vec2 datum, out int segmentIndex) =>
        PointOnExitChain3D(area.ExitChain, area.ExitChain3D, datum, out segmentIndex);

    private static Vec3 PointOnExitChain3D(IReadOnlyList<Vec2> chain, IReadOnlyList<Vec3> chain3D,
        Vec2 datum, out int segmentIndex)
    {
        double bestDistance = double.MaxValue;
        int bestSegment = 1;
        double bestT = 0;
        for (int i = 1; i < chain.Count; i++)
        {
            Vec2 a = chain[i - 1], b = chain[i];
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
        Vec3 p0 = chain3D[bestSegment - 1], p1 = chain3D[bestSegment];
        return new Vec3(p0.X + (p1.X - p0.X) * bestT,
                        p0.Y + (p1.Y - p0.Y) * bestT,
                        p0.Z + (p1.Z - p0.Z) * bestT);
    }

    /// <summary>
    /// 모든 작업영역의 경로를 한 번에 다시 스테이징/커밋한다. 네이티브 커밋은
    /// 기존 커밋 경로를 전부 교체하므로 영역 추가/삭제/재생성 시 이 경로만 사용한다.
    /// 실패하면 기존 네이티브 표시는 그대로 남는다.
    /// </summary>
    private bool RebuildNativePatternGeometry(out double activeLength, out int activeCount) =>
        RebuildNativePatternGeometry(_areas, null, out activeLength, out activeCount);

    /// <summary>
    /// 변경 대상 영역만 현재 조건을 쓰고, 나머지 영역은 마지막 성공 시점의 불변 매핑 조건을 쓴다.
    /// 변경 대상의 초안/인출면 변경이 다른 영역의 기존 경로를 조용히 다시 매핑하지 않게 한다.
    /// </summary>
    private bool RebuildNativePatternGeometry(WorkingArea changedArea,
        out double activeLength, out int activeCount) =>
        RebuildNativePatternGeometry(_areas, changedArea, out activeLength, out activeCount);

    private bool RebuildNativePatternGeometry(IReadOnlyList<WorkingArea> areas, WorkingArea changedArea,
        out double activeLength, out int activeCount)
    {
        activeLength = 0;
        activeCount = 0;
        var side = _workingSide.Confirmed;
        if (side == null) return false;
        // 대상 영역은 호출 시점의 확정 후보 상태를 쓴다. 다른 영역은 아래에서 각자의
        // 마지막 성공 매핑 조건을 사용한다.
        CommittedRouteContext changedContext = changedArea == null
            ? null
            : CommittedRouteContext.Capture(changedArea);

        var stagedRoutes = new List<int>();
        var finalIds = new List<int>();
        var stagedLines = new List<int>();
        var routeResults = new Dictionary<WorkingArea, (List<RoutePieceResult> Pieces, double[] Lengths)>();
        var exclusionResults = new Dictionary<WorkingArea, (List<RoutePieceResult> Pieces, double[] Lengths)>();
        var lineLengths = new Dictionary<WorkingArea, double>();
        bool commitRedrawFailed = false;

        try
        {
            foreach (var area in areas)
            {
                CommittedRouteContext mappingContext = ReferenceEquals(area, changedArea)
                    ? changedContext
                    : (area.CommittedMapping ?? CommittedRouteContext.Capture(area));
                if (area.Pattern != null && area.Pattern.RoutePieces.Count > 0)
                {
                    if (!TryRefreshCircuits(area, out string circuitFailure))
                        throw new InvalidOperationException("영역 " + area.Name + ": " + circuitFailure);
                    int count = area.Pattern.RoutePieces.Count;
                    if (area.NativePieceIds.Length != count)
                        area.NativePieceIds = AllocateNativePieceIds(count);
                    var pieces = new RoutePieceResult[count];
                    var lengths = new double[count];
                    var stagedIndices = new bool[count];
                    // 회로별 완결 경로를 스테이징한다. 분할된 회로의 첫/마지막 직선은
                    // 인출 경계까지 연장된 복사본이며, 같은 원본 조각 ID를 유지한다.
                    for (int c = 0; c < area.Circuits.Count; c++)
                    {
                        CircuitRoute circuit = area.Circuits[c];
                        List<int> indices = area.CircuitPieceIndices[c];
                        RoutePieceResult previous = null;
                        for (int j = 0; j < circuit.Pieces.Count; j++)
                        {
                            int index = indices[j];
                            var piece = circuit.Pieces[j];
                            if (piece.Points.Count < 2) continue;
                            Dictionary<int, SurfaceMappedSample> overrides = null;
                            if (j == 0) AddExitOverride(ref overrides, 0, circuit.StartExit.Point, piece.Points[0], side, mappingContext);
                            if (j == circuit.Pieces.Count - 1)
                                AddExitOverride(ref overrides, piece.Points.Count - 1, circuit.EndExit.Point,
                                    piece.Points[^1], side, mappingContext);
                            int stageId = _nextStageId--;
                            RoutePieceResult shown = StageCurvedRoutePiece(
                                area, side, piece, index, count, stageId,
                                exclusionMode: area.Exclusions.Count > 0,
                                mappingContext: mappingContext,
                                vertexOverrides: overrides);
                            stagedRoutes.Add(stageId);
                            finalIds.Add(area.NativePieceIds[index]);
                            pieces[index] = shown;
                            lengths[index] = shown.Length;
                            stagedIndices[index] = true;
                            if (previous != null && Distance3D(previous.End, shown.Start) > 0.01)
                                throw new InvalidOperationException("영역 " + area.Name + " 회로 " + (c + 1) + " 조각이 연결되지 않습니다");
                            previous = shown;
                        }
                    }
                    // 비활성 턴은 회로 길이/연속성에는 포함하지 않지만, 다시 클릭해
                    // 재연결할 수 있도록 네이티브에는 숨김 상태로 남긴다.
                    for (int i = 0; i < count; i++)
                    {
                        if (stagedIndices[i]) continue;
                        SerpentineRoutePiece piece = area.Pattern.RoutePieces[i];
                        if (piece.Points.Count < 2) continue;
                        int stageId = _nextStageId--;
                        RoutePieceResult shown = StageCurvedRoutePiece(
                            area, side, piece, i, count, stageId,
                            exclusionMode: area.Exclusions.Count > 0,
                            mappingContext: mappingContext);
                        stagedRoutes.Add(stageId);
                        finalIds.Add(area.NativePieceIds[i]);
                        pieces[i] = shown;
                        lengths[i] = shown.Length;
                    }
                    for (int i = 0; i < count; i++)
                        if (pieces[i] == null)
                            throw new InvalidOperationException("영역 " + area.Name + " 경로 조각이 비어 있습니다");
                    routeResults[area] = (pieces.ToList(), lengths);
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
                            allowCachedSamples: false, exclusionMode: true,
                            mappingContext: mappingContext);
                        // 스테이징에 성공한 ID는 후속 검증보다 먼저 정리 목록에 넣는다.
                        // 그래야 연속성 검사가 실패해도 방금 만든 네이티브 객체를 폐기할 수 있다.
                        stagedRoutes.Add(stageId);
                        if (previous != null && Distance3D(previous.End, shown.Start) > 0.01)
                            throw new InvalidOperationException("영역 " + area.Name + " 제외 경로 조각이 연결되지 않습니다");
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
            else
            {
                // 커밋은 성공했다. Redraw 실패는 롤백 사유가 아니라 별도 경고로 노출한다.
                commitRedrawFailed = Bridge.ConsumeCommitRedrawFailure();
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
            foreach (var area in areas)
            {
                // 변경 대상이 아닌 영역의 확정 매핑은 현재 초안으로 덮어쓰지 않는다.
                if (changedArea == null || ReferenceEquals(area, changedArea) || area.CommittedMapping == null)
                    area.CommittedMapping = CommittedRouteContext.Capture(area);
                ApplyAreaConnectorStates(area);
            }

            // 인출점 마커 — 분할된 회로마다 첫 조각 Start와 마지막 조각 End를 표시한다.
            var endpointPoints = new List<double>();
            foreach (var entry in routeResults)
            {
                var pieces = entry.Value.Pieces;
                foreach (List<int> indices in entry.Key.CircuitPieceIndices)
                {
                    if (indices.Count == 0) continue;
                    RoutePieceResult first = pieces[indices[0]];
                    RoutePieceResult last = pieces[indices[^1]];
                    if (first?.Start != null) endpointPoints.AddRange(first.Start);
                    if (last?.End != null) endpointPoints.AddRange(last.End);
                }
            }
            Bridge.SetRouteEndpoints(endpointPoints.ToArray());

            if (_activeArea != null && areas.Contains(_activeArea))
            {
                if (_activeArea.Pattern != null)
                {
                    activeCount = _activeArea.Circuits.Count;
                    activeLength = 0;
                    foreach (List<int> indices in _activeArea.CircuitPieceIndices)
                        foreach (int index in indices)
                            activeLength += _activeArea.RoutePieceLengths.ElementAtOrDefault(index);
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
        if (commitRedrawFailed)
        {
            StatusText.Text = "경로는 적용되었지만 화면 갱신에 실패했습니다 — 뷰를 다시 그려 주세요";
            ErrorReporter.ShowWarning("경로 표시 갱신 실패",
                new InvalidOperationException("네이티브 형상 커밋은 성공했지만 Redraw가 실패했습니다."));
        }
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
            Bridge.SetConnectorEnabled(area.NativePieceIds[i], enabled, 1.0, 0.1, 0.1, false);
            turn++;
        }
    }

    private void ClearAllAreas(bool clearNative)
    {
        ExitExclusionSketch();
        ResetCircuitEditingUi();
        // 외부에서 이전 컬렉션 참조를 들고 있어도 빈 상태가 되도록 in-place로 비운다.
        foreach (WorkingArea area in _areas)
        {
            area.FaceIdSet.Clear();
            area.Region.Clear();
            area.Exclusions.Clear();
            area.TurnEnabled.Clear();
            area.ProjectedPieces.Clear();
        }
        _areas.Clear();
        _activeArea = null;
        _nextNativePieceId = 0;
        _selectedFaceIds.Clear();
        if (SelectedFacesList != null) SelectedFacesList.Items.Clear();
        _leadLengths = Array.Empty<double>();
        if (Viewport != null) Viewport.CircuitHoverMode = false;
        RefreshAreaList();
        SyncExitFaceUi(null);
        if (clearNative)
        {
            Bridge.ClearRoutePieces();
            Bridge.ClearExclusionOverlays();
            Bridge.ClearAllAreaHighlights();
            Bridge.ClearConfirmedFaceHighlights();
            Bridge.ClearExitEdgeHighlight();
        }
    }
}
