using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 생성된 발열 경로의 제어점(꼭지점)을 3D 뷰포트에서 직접 드래그해 수정한다.
/// 2D 데이텀 좌표가 정본이며, 3D는 항상 실제 모델 표면 매핑으로 다시 계산한다.
/// </summary>
public partial class MainWindow
{
    private sealed record VertexEditSnapshot(
        WorkingArea Area,
        SerpentineResult Pattern,
        List<RoutePieceResult> Projected,
        int[] NativeIds,
        double[] Lengths,
        double[] Leads,
        Vec2? StartAnchor,
        Vec2? EndAnchor,
        bool Edited);

    /// <summary>확정 시 이미 계산한 표본을 재스테이징에서 그대로 쓰기 위한 일회성 캐시.</summary>
    private readonly Dictionary<(WorkingArea Area, int PieceIndex), List<SurfaceMappedSample>> _pendingStageSamples = new();
    /// <summary>변경되지 않은 조각의 표본. 편집 세션 동안 재사용해 확정 시 전체 재매핑을 피한다.</summary>
    private readonly Dictionary<(WorkingArea Area, int PieceIndex), List<SurfaceMappedSample>> _vertexBaselineSamples = new();
    private readonly Dictionary<WorkingArea, Stack<VertexEditSnapshot>> _vertexUndo = new();
    private readonly Dictionary<WorkingArea, Stack<VertexEditSnapshot>> _vertexRedo = new();
    private List<RouteVertexGroup> _vertexGroups = new();
    private int _vertexHover = -1;
    // 표시 인덱스 → _vertexGroups 인덱스. 매핑에 실패한 꼭지점은 핸들을 만들지 않는다.
    private readonly List<int> _vertexHandleGroupMap = new();
    private bool _suppressVertexEditToggle;
    private bool _vertexDragging;
    private int _vertexDragGroup = -1;
    private Vec2 _vertexDragOriginDatum;
    private Vec2 _vertexDragPressUv;
    private int _vertexLastMoveX = int.MinValue, _vertexLastMoveY = int.MinValue;
    private WorkingPresentation _vertexPresentation;
    private bool _vertexPresentationHeld;

    private void VertexEditMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressVertexEditToggle) return;
        if (VertexEditModeBox.IsChecked == true) EnterVertexEdit();
        else ExitVertexEdit();
    }

    private void EnterVertexEdit()
    {
        var area = _activeArea;
        if (area == null) { RejectVertexEdit("작업영역을 먼저 선택하세요"); return; }
        if (area.Pattern == null || area.Pattern.RoutePieces.Count == 0)
        { RejectVertexEdit("먼저 발열 경로를 생성하세요"); return; }
        if (area.Paths.Count > 0)
        { RejectVertexEdit("제외영역 경로는 발열경로 편집을 지원하지 않습니다 — 제외영역을 지우고 다시 생성하세요"); return; }
        if (!WorkingGenerationReady()) { RejectVertexEdit("작업면/모델 상태가 준비되지 않았습니다"); return; }
        var side = _workingSide.Confirmed;
        if (side == null) { RejectVertexEdit("작업면이 확정되지 않았습니다"); return; }
        if (Viewport == null || !Viewport.IsReady) { RejectVertexEdit("뷰포트가 준비되지 않았습니다"); return; }
        if (Viewport.PowerLocationMode || _pendingLeads.Count > 0)
        { RejectVertexEdit("전원 연결 설정을 취소한 뒤 다시 시도하세요"); return; }

        // 다른 편집 모드와 상호 배타.
        Viewport.ConnectorEditMode = false;
        if (ConnectorEditModeBox != null) ConnectorEditModeBox.IsChecked = false;
        CancelExitFacePick();
        ExitExclusionSketch();
        Viewport.RectangleSelectionMode = false;
        Viewport.PowerLocationMode = false;
        Viewport.CircuitHoverMode = false;
        Bridge.ClearHover();

        _vertexGroups = RouteVertexGroups.Build(area.Pattern.RoutePieces).ToList();
        _pendingStageSamples.Clear();
        _vertexBaselineSamples.Clear();
        _vertexHover = -1;

        // 편집 종료 시 원래 카메라로 돌아가기 위해 정면 전환 전에 스냅샷을 잡는다.
        _vertexPresentation = Bridge.CaptureWorkingPresentation();
        _vertexPresentationHeld = _vertexPresentation != null;
        if (!Bridge.ReturnToWorkingFront(side.Id.Value, side.Revision.Value))
        {
            if (_vertexPresentationHeld && _vertexPresentation != null)
            {
                Bridge.RestoreWorkingPresentation(_vertexPresentation.Id);
                Bridge.ReleaseWorkingPresentation(_vertexPresentation.Id);
            }
            _vertexPresentation = null;
            _vertexPresentationHeld = false;
            RejectVertexEdit("작업 정면으로 전환하지 못했습니다");
            return;
        }

        Viewport.VertexEditMode = true;
        RefreshVertexHandles();
        UpdateActionStates();
        StatusText.Text = "발열경로 편집 — 점을 드래그해 경로를 수정합니다 (Esc: 드래그 취소/편집 종료)";
    }

    private void RejectVertexEdit(string reason)
    {
        StatusText.Text = reason;
        _suppressVertexEditToggle = true;
        if (VertexEditModeBox != null) VertexEditModeBox.IsChecked = false;
        _suppressVertexEditToggle = false;
    }

    private void ExitVertexEdit()
    {
        if (Viewport == null || !Viewport.VertexEditMode) return;
        Viewport.VertexEditMode = false;
        _vertexDragging = false;
        _vertexDragGroup = -1;
        Bridge.ClearVertexHandles();
        Bridge.ClearRoutePreview();
        if (_vertexPresentationHeld && _vertexPresentation != null)
        {
            Bridge.RestoreWorkingPresentation(_vertexPresentation.Id);
            Bridge.ReleaseWorkingPresentation(_vertexPresentation.Id);
        }
        _vertexPresentation = null;
        _vertexPresentationHeld = false;
        _vertexGroups.Clear();
        _vertexHandleGroupMap.Clear();
        _pendingStageSamples.Clear();
        _vertexBaselineSamples.Clear();
        _vertexHover = -1;
        _suppressVertexEditToggle = true;
        if (VertexEditModeBox != null) VertexEditModeBox.IsChecked = false;
        _suppressVertexEditToggle = false;
        if (_activeArea?.Pattern != null) Viewport.CircuitHoverMode = true;
        UpdateActionStates();
    }

    private void VertexEscape()
    {
        if (_vertexDragging) return;   // 드래그 취소는 뷰포트가 처리한다
        ExitVertexEdit();
        StatusText.Text = "발열경로 편집을 종료했습니다";
    }

    /// <summary>각 꼭지점 데이텀을 현재 표시 3D 점으로 매핑해 핸들을 다시 그린다.</summary>
    private void RefreshVertexHandles()
    {
        var area = _activeArea;
        var side = _workingSide.Confirmed;
        if (area?.Pattern == null || side == null) { Bridge.ClearVertexHandles(); return; }

        var hit = BuildSurfaceMapDelegate(area, side);
        var xyz = new List<double>();
        _vertexHandleGroupMap.Clear();
        for (int groupIndex = 0; groupIndex < _vertexGroups.Count; groupIndex++)
        {
            RouteVertexGroup group = _vertexGroups[groupIndex];
            Vec3? point = null;
            if (IsAnchorGroup(area, group, out _))
            {
                if (area.HasExitChain3D) point = PointOnExitChain3D(area, group.Datum, out _);
            }
            else
            {
                var sample = hit(group.Datum);
                if (sample.HasValue) point = sample.Value.Point;
            }
            if (!point.HasValue) continue;
            xyz.Add(point.Value.X);
            xyz.Add(point.Value.Y);
            xyz.Add(point.Value.Z);
            _vertexHandleGroupMap.Add(groupIndex);
        }
        Bridge.SetVertexHandles(side.Id.Value, side.Revision.Value, xyz.ToArray());
    }

    /// <summary>경로 시작/종료 역할이고 인출 체인 위에 있는 꼭지점 묶음인지 판정한다.</summary>
    private static bool IsAnchorGroup(WorkingArea area, RouteVertexGroup group, out bool isStart)
    {
        isStart = false;
        if (area.Pattern == null || !area.HasExitFace) return false;
        var pieces = area.Pattern.RoutePieces;
        if (pieces.Count == 0) return false;
        bool atStart = false, atEnd = false;
        foreach (RouteVertexOccurrence occurrence in group.Occurrences)
        {
            if (occurrence.Piece == 0 && occurrence.Point == 0) atStart = true;
            if (occurrence.Piece == pieces.Count - 1 &&
                occurrence.Point == pieces[occurrence.Piece].Points.Count - 1) atEnd = true;
        }
        if (!atStart && !atEnd) return false;
        if (Serpentine.DistanceToChain(area.ExitChain, group.Datum) > 0.05) return false;
        isStart = atStart;
        return true;
    }

    private void OnVertexHovered(int handle)
    {
        if (!Viewport.VertexEditMode) return;
        if (handle == _vertexHover) return;   // 같은 핸들 반복 호버 시 색칠/Redraw 생략
        _vertexHover = handle;
        Bridge.HighlightVertexHandle(handle);
        if (handle < 0 || handle >= _vertexHandleGroupMap.Count)
        {
            HoverText.Text = "";
            return;
        }
        int group = _vertexHandleGroupMap[handle];
        RouteVertexGroup vertex = _vertexGroups[group];
        bool anchor = IsAnchorGroup(_activeArea, vertex, out _);
        HoverText.Text = string.Format("꼭지점 ({0:F2}, {1:F2})mm{2}",
            vertex.Datum.X, vertex.Datum.Y, anchor ? " · 인출 앵커" : "");
    }

    private void OnVertexDragStarted(int handle, int x, int y)
    {
        if (!Viewport.VertexEditMode || handle < 0 || handle >= _vertexHandleGroupMap.Count) return;
        if (!TryViewportPointOnFrame(x, y, out Vec2 pressUv, out _)) return;
        _vertexDragGroup = _vertexHandleGroupMap[handle];
        _vertexDragOriginDatum = _vertexGroups[_vertexDragGroup].Datum;
        _vertexDragPressUv = pressUv;
        _vertexDragging = true;
        _vertexLastMoveX = x;
        _vertexLastMoveY = y;
        Bridge.ClearRoutePreview();
        Bridge.HighlightVertexHandle(handle);
    }

    private void OnVertexDragMoved(int x, int y)
    {
        if (!_vertexDragging || _vertexDragGroup < 0) return;
        if (Math.Abs(x - _vertexLastMoveX) < 2 && Math.Abs(y - _vertexLastMoveY) < 2) return;
        _vertexLastMoveX = x;
        _vertexLastMoveY = y;
        if (!TryViewportPointOnFrame(x, y, out Vec2 uv, out _)) return;
        UpdateVertexPreview(CandidateDatum(uv));
    }

    private void OnVertexDragCompleted(int x, int y)
    {
        if (!_vertexDragging) return;
        _vertexDragging = false;
        int group = _vertexDragGroup;
        _vertexDragGroup = -1;
        Bridge.ClearRoutePreview();
        Bridge.HighlightVertexHandle(-1);
        if (group < 0 || group >= _vertexGroups.Count) return;
        // 이동 없는 클릭은 전체 재생성/이력 기록을 무시한다.
        if (!TryViewportPointOnFrame(x, y, out Vec2 uv, out _))
        {
            RefreshVertexHandles();
            return;
        }
        Vec2 datum = CandidateDatum(uv);
        if ((datum - _vertexDragOriginDatum).Length <= 1e-6)
        {
            StatusText.Text = "꼭지점 위치가 변경되지 않았습니다";
            return;
        }
        _vertexDragGroup = group;
        CommitVertex(datum);
        _vertexDragGroup = -1;
        RefreshVertexHandles();
    }

    private void OnVertexDragCancelled()
    {
        _vertexDragging = false;
        _vertexDragGroup = -1;
        Bridge.ClearRoutePreview();
        Bridge.HighlightVertexHandle(-1);
    }

    private Vec2 CandidateDatum(Vec2 uv) => new(
        _vertexDragOriginDatum.X + (uv.X - _vertexDragPressUv.X),
        _vertexDragOriginDatum.Y + (uv.Y - _vertexDragPressUv.Y));

    private void UpdateVertexPreview(Vec2 datum)
    {
        if (!TryBuildVertexPreview(datum, out var pieces, out var mappedPieces, out Vec2 applied,
                out string failure, out bool anchor, out _))
        {
            Bridge.ClearRoutePreview();
            StatusText.Text = "이동 불가 — " + failure;
            return;
        }
        if (ShowVertexPreview(pieces, mappedPieces))
            StatusText.Text = string.Format("꼭지점 이동 ({0:F2}, {1:F2})mm{2}",
                applied.X, applied.Y, anchor ? " · 인출 앵커" : "");
        else
        {
            Bridge.ClearRoutePreview();
            StatusText.Text = "이동할 수 없습니다 — 확정 구간을 작업면에 매핑하지 못했습니다";
        }
    }

    /// <summary>
    /// 꼭지점 이동 후보를 미리보기용으로 준비한다. 변경된 조각의 점만 복사하고, 조각별로 표면에 매핑한다.
    /// 확정 시에는 <see cref="CommitVertex"/>가 전체 패턴으로 보강 표본화한다.
    /// </summary>
    private bool TryBuildVertexPreview(Vec2 datum,
        out Dictionary<int, IReadOnlyList<Vec2>> pieces,
        out Dictionary<int, List<SurfaceMappedSample>> mappedPieces,
        out Vec2 applied, out string failure, out bool anchor, out bool isStart)
    {
        pieces = new Dictionary<int, IReadOnlyList<Vec2>>();
        mappedPieces = new Dictionary<int, List<SurfaceMappedSample>>();
        applied = datum;
        failure = string.Empty;
        anchor = false;
        isStart = false;
        var area = _activeArea;
        if (area?.Pattern == null || _vertexDragGroup < 0 || _vertexDragGroup >= _vertexGroups.Count)
        {
            failure = "편집할 경로가 없습니다";
            return false;
        }
        RouteVertexGroup group = _vertexGroups[_vertexDragGroup];
        anchor = IsAnchorGroup(area, group, out isStart);
        if (anchor)
        {
            if (area.ExitChain.Count < 2) { failure = "인출면 경계가 없습니다"; return false; }
            applied = Serpentine.SnapOnChain(area.ExitChain, datum);
        }

        var edits = new List<(RouteVertexOccurrence, Vec2)>(group.Occurrences.Count);
        foreach (RouteVertexOccurrence occurrence in group.Occurrences) edits.Add((occurrence, applied));
        if (!SerpentineVertexPreview.TryApplyAffected(area.Pattern, edits, out pieces, out _, out failure))
            return false;

        var side = _workingSide.Confirmed;
        if (side == null) { failure = "작업면이 확정되지 않았습니다"; return false; }
        var hit = BuildSurfaceMapDelegate(area, side, anchor ? applied : (Vec2?)null);
        foreach (KeyValuePair<int, IReadOnlyList<Vec2>> entry in pieces)
        {
            if (entry.Value.Count < 2) continue;
            bool ok = SurfacePathMapper.TryMapRoute(entry.Value, hit, out var samples, out string mapFailure);
            if (!ok) { failure = "모델 표면 매핑 실패 — " + mapFailure; return false; }
            mappedPieces[entry.Key] = samples;
        }
        return true;
    }

    /// <summary>
    /// 확정용 후보 패턴을 만들고 보강 표본(0.1mm 간격)으로 검증한다. 미리보기는
    /// 별도 <see cref="TryBuildVertexPreview"/>가 경량 매핑으로 처리한다. 매핑에 성공한
    /// 조각의 3D 표본은 mappedPieces로 반환해 호출측이 스테이징에 재사용한다.
    /// </summary>
    private bool TryBuildVertexCandidate(Vec2 datum, out SerpentineResult candidate, out Vec2 applied,
        out string failure, out bool anchor, out bool isStart,
        out Dictionary<int, List<SurfaceMappedSample>> mappedPieces)
    {
        candidate = null;
        applied = datum;
        failure = string.Empty;
        anchor = false;
        isStart = false;
        mappedPieces = new Dictionary<int, List<SurfaceMappedSample>>();
        var area = _activeArea;
        if (area?.Pattern == null || _vertexDragGroup < 0 || _vertexDragGroup >= _vertexGroups.Count)
        {
            failure = "편집할 경로가 없습니다";
            return false;
        }
        RouteVertexGroup group = _vertexGroups[_vertexDragGroup];
        anchor = IsAnchorGroup(area, group, out isStart);
        if (anchor)
        {
            if (area.ExitChain.Count < 2) { failure = "인출면 경계가 없습니다"; return false; }
            applied = Serpentine.SnapOnChain(area.ExitChain, datum);
        }

        var edits = new List<(RouteVertexOccurrence, Vec2)>(group.Occurrences.Count);
        foreach (RouteVertexOccurrence occurrence in group.Occurrences) edits.Add((occurrence, applied));
        try { candidate = SerpentineVertexEdit.ApplyVertexEdits(area.Pattern, edits); }
        catch (Exception ex) { failure = ex.Message; return false; }
        var side = _workingSide.Confirmed;

        if (side == null) { failure = "작업면이 확정되지 않았습니다"; return false; }
        var hit = BuildSurfaceMapDelegate(area, side, anchor ? applied : (Vec2?)null);
        foreach (int pieceIndex in group.Occurrences.Select(o => o.Piece).Distinct())
        {
            SerpentineRoutePiece piece = candidate.RoutePieces[pieceIndex];
            if (piece.Points.Count < 2) continue;
            bool ok = SurfacePathMapper.TryMapRouteStrict(piece.Points, hit, out var samples,
                out string mapFailure, 0.1);
            if (!ok) { failure = "모델 표면 매핑 실패 — " + mapFailure; return false; }
            mappedPieces[pieceIndex] = samples;
        }
        return true;
    }

    /// <summary>검증 단계에서 이미 계산한 매핑을 재사용해 미리보기 폴리라인을 그린다.</summary>
    private bool ShowVertexPreview(IReadOnlyDictionary<int, IReadOnlyList<Vec2>> pieces,
        Dictionary<int, List<SurfaceMappedSample>> mappedPieces)
    {
        var area = _activeArea;
        if (area == null) return false;
        foreach (KeyValuePair<int, IReadOnlyList<Vec2>> entry in pieces)
        {
            int pieceIndex = entry.Key;
            IReadOnlyList<Vec2> points = entry.Value;
            if (points.Count < 2) continue;
            if (!mappedPieces.TryGetValue(pieceIndex, out var samples) || samples.Count < 2) return false;
            var xyz = new double[samples.Count * 3];
            for (int i = 0; i < samples.Count; i++)
            {
                xyz[i * 3] = samples[i].Point.X;
                xyz[i * 3 + 1] = samples[i].Point.Y;
                xyz[i * 3 + 2] = samples[i].Point.Z;
            }
            Bridge.ShowRoutePreview(pieceIndex, xyz, false);
        }
        // 조각별 Redraw 대신 이 Tick의 미리보기를 한 번에 반영한다.
        Bridge.RedrawView();
        return true;
    }

    private void CommitVertex(Vec2 datum)
    {
        var area = _activeArea;
        // 확정 시에만 밀집 표본화(0.1mm 간격)로 엄격 검증하고, 그 표본을 스테이징에서 재사용한다.
        if (!TryBuildVertexCandidate(datum, out SerpentineResult candidate, out Vec2 applied,
                out string failure, out bool anchor, out bool isStart,
                out Dictionary<int, List<SurfaceMappedSample>> mappedPieces))
        {
            StatusText.Text = "이동할 수 없습니다 — " + failure;
            return;
        }

        VertexEditSnapshot before = CaptureVertexSnapshot(area);
        area.Pattern = candidate;
        if (anchor)
        {
            if (isStart) area.StartAnchor = applied;
            else area.EndAnchor = applied;
        }
        _pendingStageSamples.Clear();
        foreach (KeyValuePair<int, List<SurfaceMappedSample>> entry in mappedPieces)
            _pendingStageSamples[(area, entry.Key)] = entry.Value;
        bool rebuilt;
        try { rebuilt = RebuildNativePatternGeometry(out _, out _); }
        finally { _pendingStageSamples.Clear(); }
        if (!rebuilt)
        {
            RestoreVertexSnapshot(before);
            return;
        }
        // 방금 스테이징에 쓴 표본을 다음 확정의 기준으로 승격한다.
        foreach (KeyValuePair<int, List<SurfaceMappedSample>> entry in mappedPieces)
            _vertexBaselineSamples[(area, entry.Key)] = entry.Value;
        area.RouteEdited = true;
        UndoStack(area).Push(before);
        RedoStack(area).Clear();
        _vertexGroups = RouteVertexGroups.Build(area.Pattern.RoutePieces).ToList();
        UpdateExitLeadLengths(area);
        UpdateCircuitTable();
        StatusText.Text = string.Format("{0} 영역 · 경로 꼭지점 수정 — 길이 {1:F1}mm",
            area.Name, area.RoutePieceLengths.Sum());
        UpdateActionStates();
    }

    private void VertexUndo_Click(object sender, RoutedEventArgs e) => ApplyVertexHistory(undo: true);

    private void VertexRedo_Click(object sender, RoutedEventArgs e) => ApplyVertexHistory(undo: false);

    private void ApplyVertexHistory(bool undo)
    {
        var area = _activeArea;
        if (area == null) return;
        Stack<VertexEditSnapshot> source = undo ? UndoStack(area) : RedoStack(area);
        Stack<VertexEditSnapshot> target = undo ? RedoStack(area) : UndoStack(area);
        if (source.Count == 0) return;
        VertexEditSnapshot snapshot = source.Pop();
        if (!ReferenceEquals(snapshot.Area, area))
        {
            source.Clear();
            StatusText.Text = "편집 이력은 현재 작업영역의 것만 사용할 수 있습니다";
            return;
        }

        VertexEditSnapshot current = CaptureVertexSnapshot(area);
        // 복원할 패턴은 현재 표본과 다르므로 두 캐시를 모두 비운다. 남겨 두면
        // 되돌린 경로가 아니라 되돌리기 직전 경로의 표본으로 다시 스테이징된다.
        _pendingStageSamples.Clear();
        _vertexBaselineSamples.Clear();
        // 이력의 상태를 실제 영역에 먼저 적용한 뒤 그 형상으로 네이티브 표시를 다시 만든다.
        RestoreVertexSnapshot(snapshot);
        if (!RebuildNativePatternGeometry(out _, out _))
        {
            // 커밋 전 단계 실패는 기존 네이티브 표시를 그대로 남긴다 — 재시도 불필요.
            RestoreVertexSnapshot(current);
            _pendingStageSamples.Clear();
            _vertexBaselineSamples.Clear();
            source.Push(snapshot);
            StatusText.Text = undo ? "Undo 실패 — 경로를 되돌리지 못했습니다" : "Redo 실패 — 경로를 되돌리지 못했습니다";
            return;
        }

        target.Push(current);
        _vertexGroups = area.Pattern != null
            ? RouteVertexGroups.Build(area.Pattern.RoutePieces).ToList()
            : new List<RouteVertexGroup>();
        UpdateExitLeadLengths(area);
        UpdateCircuitTable();
        RefreshVertexHandles();
        StatusText.Text = undo ? "발열경로 편집 Undo" : "발열경로 편집 Redo";
        UpdateActionStates();
    }

    private static VertexEditSnapshot CaptureVertexSnapshot(WorkingArea area) => new(
        area, area.Pattern, area.ProjectedPieces, area.NativePieceIds, area.RoutePieceLengths, area.LeadLengths,
        area.StartAnchor, area.EndAnchor, area.RouteEdited);

    private void RestoreVertexSnapshot(VertexEditSnapshot snapshot)
    {
        WorkingArea area = snapshot.Area;
        area.Pattern = snapshot.Pattern;
        area.ProjectedPieces = snapshot.Projected;
        area.NativePieceIds = snapshot.NativeIds;
        area.RoutePieceLengths = snapshot.Lengths;
        area.LeadLengths = snapshot.Leads;
        area.StartAnchor = snapshot.StartAnchor;
        area.EndAnchor = snapshot.EndAnchor;
        area.RouteEdited = snapshot.Edited;
    }

    /// <summary>편집 내용이 있으면 삭제 동의를 받는다. 여기서는 상태를 지우지 않는다 —
    /// 실제 작업이 성공한 뒤 <see cref="ClearRouteEditState"/>를 호출해야 실패 시 편집이 보존된다.</summary>
    private bool ConfirmDiscardRouteEdits(WorkingArea area, string action)
    {
        if (area == null || !area.RouteEdited) return true;
        MessageBoxResult result = MessageBox.Show(this,
            action + " 작업을 계속하면 " + area.Name + " 영역의 발열경로 편집 내용이 삭제됩니다."
            + Environment.NewLine + "계속할까요?",
            "발열경로 편집 내용 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private void ClearRouteEditState(WorkingArea area)
    {
        if (area == null) return;
        area.RouteEdited = false;
        if (_vertexUndo.TryGetValue(area, out Stack<VertexEditSnapshot> undoStack)) undoStack.Clear();
        if (_vertexRedo.TryGetValue(area, out Stack<VertexEditSnapshot> redoStack)) redoStack.Clear();
    }

    private bool ConfirmDiscardAllRouteEdits(string action)
    {
        var edited = _areas.Where(a => a.RouteEdited).ToList();
        if (edited.Count == 0) return true;
        MessageBoxResult result = MessageBox.Show(this,
            action + " 작업을 계속하면 " + string.Join(", ", edited.Select(a => a.Name))
            + " 영역의 발열경로 편집 내용이 삭제됩니다." + Environment.NewLine + "계속할까요?",
            "발열경로 편집 내용 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private void ClearAllRouteEditStates()
    {
        foreach (WorkingArea area in _areas) area.RouteEdited = false;
        _vertexUndo.Clear();
        _vertexRedo.Clear();
    }

    private Stack<VertexEditSnapshot> UndoStack(WorkingArea area)
    {
        if (!_vertexUndo.TryGetValue(area, out Stack<VertexEditSnapshot> stack))
        {
            stack = new Stack<VertexEditSnapshot>();
            _vertexUndo[area] = stack;
        }
        return stack;
    }

    private Stack<VertexEditSnapshot> RedoStack(WorkingArea area)
    {
        if (!_vertexRedo.TryGetValue(area, out Stack<VertexEditSnapshot> stack))
        {
            stack = new Stack<VertexEditSnapshot>();
            _vertexRedo[area] = stack;
        }
        return stack;
    }

    private int VertexUndoCount(WorkingArea area) =>
        area != null && _vertexUndo.TryGetValue(area, out Stack<VertexEditSnapshot> stack) ? stack.Count : 0;

    private int VertexRedoCount(WorkingArea area) =>
        area != null && _vertexRedo.TryGetValue(area, out Stack<VertexEditSnapshot> stack) ? stack.Count : 0;
}
