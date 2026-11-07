using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.App.VisualTesting;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 생성된 발열 경로의 제어점(꼭지점)을 3D 뷰포트에서 직접 드래그해 수정한다.
/// 2D 데이텀 좌표가 정본이며, 3D는 항상 실제 모델 표면 매핑으로 다시 계산한다.
/// </summary>
public partial class MainWindow
{
    private List<RouteVertexGroup> _vertexGroups = new();
    /// <summary>분할 회로의 파생 인출점 핸들 → 원본 Pattern 조각 인덱스/역할.</summary>
    private readonly Dictionary<int, (int PieceIndex, CircuitExitRole Role)> _vertexCircuitAnchors = new();
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
        if (!area.HasExitFace)
        { RejectVertexEdit("인출면을 먼저 지정하세요"); return; }
        if (area.Paths.Count > 0)
        { RejectVertexEdit("제외영역 경로는 발열경로 편집을 지원하지 않습니다 — 제외영역을 지우고 다시 생성하세요"); return; }
        if (IsPatternInputStale(area))
        { RejectVertexEdit("입력 조건이 변경되었습니다 — 패턴을 다시 생성한 뒤 편집하세요"); return; }
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

        _vertexGroups = BuildVertexGroups(area);
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
        _vertexCircuitAnchors.Clear();
        _vertexHandleGroupMap.Clear();
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
            if (IsAnchorGroup(area, group, out bool startRole))
            {
                var context = CommittedRouteContext.Capture(area);
                SurfaceMappedSample? sample = MapExitDatum(side, context,
                    startRole ? area.Pattern.StartExit : area.Pattern.EndExit);
                if (sample.HasValue) point = sample.Value.Point;
            }
            else if (_vertexCircuitAnchors.TryGetValue(groupIndex, out var key))
            {
                int circuitIndex = FindCircuitEndpoint(area, key);
                if (circuitIndex < 0) continue;
                CircuitRoute circuit = area.Circuits[circuitIndex];
                SurfaceMappedSample? sample = MapExitDatum(side, CommittedRouteContext.Capture(area),
                    key.Role == CircuitExitRole.Start ? circuit.StartExit : circuit.EndExit);
                if (sample.HasValue) point = sample.Value.Point;
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

    /// <summary>경로 정본에 기록된 시작/종료 인출점 역할과 정확히 일치하는 꼭지점 묶음인지 판정한다.</summary>
    private static bool IsAnchorGroup(WorkingArea area, RouteVertexGroup group, out bool isStart)
    {
        isStart = false;
        if (area.Pattern == null || !area.HasExitFace) return false;
        var pattern = area.Pattern;
        IReadOnlyList<SerpentineRoutePiece> pieces = pattern.RoutePieces;
        if (pieces.Count == 0) return false;

        CircuitExitPoint startExit = pattern.StartExit;
        CircuitExitPoint endExit = pattern.EndExit;
        bool startRole = startExit != null &&
            group.Datum.DistanceTo(startExit.Point) <= 1e-6 &&
            group.Occurrences.Any(o => o.Piece == 0 && o.Point == 0);
        int lastPiece = pieces.Count - 1;
        int lastPoint = pieces[lastPiece].Points.Count - 1;
        bool endRole = endExit != null &&
            group.Datum.DistanceTo(endExit.Point) <= 1e-6 &&
            lastPoint >= 0 &&
            group.Occurrences.Any(o => o.Piece == lastPiece && o.Point == lastPoint);

        if (startRole == endRole) return false;   // 둘 다/둘 다 아님은 모호하므로 앵커로 보지 않는다.
        isStart = startRole;
        return true;
    }

    /// <summary>표시된 확정 회로의 꼭지점을 원본 조각 occurrence에 연결한다.</summary>
    private List<RouteVertexGroup> BuildVertexGroups(WorkingArea area)
    {
        var groups = new List<RouteVertexGroup>();
        _vertexCircuitAnchors.Clear();
        if (area.Circuits.Count == 0 || area.Circuits.Count != area.CircuitPieceIndices.Count)
            return RouteVertexGroups.Build(area.Pattern.RoutePieces).ToList();
        for (int i = 0; i < area.Circuits.Count; i++)
        {
            List<int> indices = area.CircuitPieceIndices[i];
            if (indices.Count == 0) continue;
            CircuitRoute route = area.Circuits[i];
            foreach (RouteVertexGroup local in RouteVertexGroups.Build(route.Pieces))
            {
                var occurrences = local.Occurrences.Select(o =>
                    new RouteVertexOccurrence(indices[o.Piece], o.Point)).ToArray();
                groups.Add(new RouteVertexGroup(occurrences, local.Datum));
                if (indices[0] != 0 && local.Occurrences.Any(o => o.Piece == 0 && o.Point == 0))
                    _vertexCircuitAnchors[groups.Count - 1] = (indices[0], CircuitExitRole.Start);
                int last = route.Pieces.Count - 1;
                if (indices[^1] != area.Pattern.RoutePieces.Count - 1 &&
                    local.Occurrences.Any(o => o.Piece == last && o.Point == route.Pieces[last].Points.Count - 1))
                    _vertexCircuitAnchors[groups.Count - 1] = (indices[^1], CircuitExitRole.End);
            }
        }
        return groups;
    }

    private static int FindCircuitEndpoint(WorkingArea area, (int PieceIndex, CircuitExitRole Role) key) =>
        area.CircuitPieceIndices.FindIndex(g => g.Count > 0 &&
            (key.Role == CircuitExitRole.Start ? g[0] == key.PieceIndex : g[^1] == key.PieceIndex));

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
        bool anchor = IsAnchorGroup(_activeArea, vertex, out _) || _vertexCircuitAnchors.ContainsKey(group);
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
        // 이동 없는 클릭은 전체 재생성을 무시한다.
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
        if (_vertexCircuitAnchors.TryGetValue(_vertexDragGroup, out var circuitKey))
        {
            anchor = true;
            isStart = circuitKey.Role == CircuitExitRole.Start;
            return TryBuildCircuitAnchor(circuitKey, datum, strict: false,
                out pieces, out mappedPieces, out applied, out failure, out _);
        }
        anchor = IsAnchorGroup(area, group, out isStart);
        var side = _workingSide.Confirmed;
        if (side == null) { failure = "작업면이 확정되지 않았습니다"; return false; }
        var context = CommittedRouteContext.Capture(area);
        SurfaceMappedSample? anchorSample = null;
        if (anchor)
        {
            CircuitExitPoint boundExit = isStart ? area.Pattern.StartExit : area.Pattern.EndExit;
            if (!TryProjectExitDatum(side, context, boundExit?.Binding, datum, 5.0,
                    out ExitBoundaryPoint projected, out SurfaceMappedSample projectedSample, out failure))
            {
                return false;
            }
            applied = projected.Point;
            if (applied.DistanceTo(group.Datum) <= 1e-6)
            {
                failure = "인출점 위치가 변경되지 않았습니다";
                return false;
            }
            anchorSample = projectedSample;
        }

        var edits = new List<(RouteVertexOccurrence, Vec2)>(group.Occurrences.Count);
        foreach (RouteVertexOccurrence occurrence in group.Occurrences) edits.Add((occurrence, applied));
        if (!SerpentineVertexPreview.TryApplyAffected(area.Pattern, edits, out pieces, out _, out failure))
            return false;

        var hit = BuildSurfaceMapDelegate(area, side, context: context);
        foreach (KeyValuePair<int, IReadOnlyList<Vec2>> entry in pieces.ToArray())
        {
            if (entry.Value.Count < 2) continue;
            if (!PrepareDisplayedEditPiece(area, side, context, group, entry.Key, entry.Value, anchorSample,
                    out IReadOnlyList<Vec2> displayPoints, out var overrides, out failure)) return false;
            pieces[entry.Key] = displayPoints;
            bool ok = SurfacePathMapper.TryMapRoute(
                displayPoints, hit, overrides, out var samples, out string mapFailure);
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
        out Dictionary<int, List<SurfaceMappedSample>> mappedPieces, out ExitEdgeBinding? movedBinding)
    {
        candidate = null;
        applied = datum;
        failure = string.Empty;
        anchor = false;
        isStart = false;
        mappedPieces = new Dictionary<int, List<SurfaceMappedSample>>();
        movedBinding = null;
        var area = _activeArea;
        if (area?.Pattern == null || _vertexDragGroup < 0 || _vertexDragGroup >= _vertexGroups.Count)
        {
            failure = "편집할 경로가 없습니다";
            return false;
        }
        RouteVertexGroup group = _vertexGroups[_vertexDragGroup];
        if (_vertexCircuitAnchors.TryGetValue(_vertexDragGroup, out var circuitKey))
        {
            anchor = true;
            isStart = circuitKey.Role == CircuitExitRole.Start;
            if (!TryBuildCircuitAnchor(circuitKey, datum, strict: true,
                    out _, out mappedPieces, out applied, out failure, out CircuitExitPoint moved))
                return false;
            movedBinding = moved.Binding;
            candidate = area.Pattern;
            return true;
        }
        anchor = IsAnchorGroup(area, group, out isStart);
        var side = _workingSide.Confirmed;
        if (side == null) { failure = "작업면이 확정되지 않았습니다"; return false; }
        var context = CommittedRouteContext.Capture(area);
        SurfaceMappedSample? anchorSample = null;
        if (anchor)
        {
            CircuitExitPoint boundExit = isStart ? area.Pattern.StartExit : area.Pattern.EndExit;
            if (!TryProjectExitDatum(side, context, boundExit?.Binding, datum, 5.0,
                    out ExitBoundaryPoint projected, out SurfaceMappedSample projectedSample, out failure))
            {
                return false;
            }
            applied = projected.Point;
            movedBinding = projected.Binding;
            if (applied.DistanceTo(group.Datum) <= 1e-6)
            {
                failure = "인출점 위치가 변경되지 않았습니다";
                return false;
            }
            anchorSample = projectedSample;
        }

        var edits = new List<(RouteVertexOccurrence, Vec2)>(group.Occurrences.Count);
        foreach (RouteVertexOccurrence occurrence in group.Occurrences) edits.Add((occurrence, applied));
        try { candidate = SerpentineVertexEdit.ApplyVertexEdits(area.Pattern, edits); }
        catch (Exception ex) { failure = ex.Message; return false; }
        if (anchor)
        {
            CircuitRoute route = CircuitRoute.FromResult(candidate);
            route = isStart
                ? route with { StartExit = route.StartExit with { Binding = movedBinding } }
                : route with { EndExit = route.EndExit with { Binding = movedBinding } };
            candidate = route.ToResult();
        }

        var hit = BuildSurfaceMapDelegate(area, side, context: context);
        foreach (int pieceIndex in group.Occurrences.Select(o => o.Piece).Distinct())
        {
            SerpentineRoutePiece piece = candidate.RoutePieces[pieceIndex];
            if (piece.Points.Count < 2) continue;
            if (!PrepareDisplayedEditPiece(area, side, context, group, pieceIndex, piece.Points, anchorSample,
                    out IReadOnlyList<Vec2> displayPoints, out var overrides, out failure)) return false;
            bool ok = SurfacePathMapper.TryMapRouteStrict(displayPoints, hit, overrides, out var samples,
                out string mapFailure, 0.1);
            if (!ok) { failure = "모델 표면 매핑 실패 — " + mapFailure; return false; }
            mappedPieces[pieceIndex] = samples;
        }
        return true;
    }

    /// <summary>분할 회로의 파생 인출점 하나를 이동한 후보를 만든다.</summary>
    private bool TryBuildCircuitAnchor((int PieceIndex, CircuitExitRole Role) key, Vec2 datum, bool strict,
        out Dictionary<int, IReadOnlyList<Vec2>> pieces,
        out Dictionary<int, List<SurfaceMappedSample>> mappedPieces,
        out Vec2 applied, out string failure, out CircuitExitPoint movedExit)
    {
        pieces = new Dictionary<int, IReadOnlyList<Vec2>>();
        mappedPieces = new Dictionary<int, List<SurfaceMappedSample>>();
        applied = datum;
        failure = string.Empty;
        movedExit = null;
        var area = _activeArea;
        var side = _workingSide.Confirmed;
        if (area?.Pattern == null || side == null) { failure = "편집할 경로가 없습니다"; return false; }
        int circuitIndex = FindCircuitEndpoint(area, key);
        if (circuitIndex < 0 || circuitIndex >= area.Circuits.Count)
        { failure = "분할 회로의 인출점을 찾을 수 없습니다"; return false; }
        CircuitRoute route = area.Circuits[circuitIndex];
        var context = CommittedRouteContext.Capture(area);
        CircuitExitPoint boundExit = key.Role == CircuitExitRole.Start ? route.StartExit : route.EndExit;
        if (!TryProjectExitDatum(side, context, boundExit.Binding, datum, 5.0,
                out ExitBoundaryPoint projected, out SurfaceMappedSample anchorSample, out failure)) return false;
        applied = projected.Point;
        movedExit = boundExit with { Point = applied, Binding = projected.Binding,
            BoundarySegmentIndex = projected.SegmentIndex, BoundaryParameter = projected.SegmentParameter };
        if (applied.DistanceTo(route.StartExit.Point) <= 1e-6 && key.Role == CircuitExitRole.Start ||
            applied.DistanceTo(route.EndExit.Point) <= 1e-6 && key.Role == CircuitExitRole.End)
        { failure = "인출점 위치가 변경되지 않았습니다"; return false; }

        CircuitExitPoint start = key.Role == CircuitExitRole.Start
            ? movedExit
            : route.StartExit;
        CircuitExitPoint end = key.Role == CircuitExitRole.End
            ? movedExit
            : route.EndExit;
        if (!CircuitRouteBuilder.TryTrimToExit(route.Pieces, ToBoundary(start), ToBoundary(end),
                route.AngleDeg, route.LaneCount, route.NominalLaneCount,
                out CircuitRoute candidate, out failure))
            return false;
        IReadOnlyList<Vec2> points = key.Role == CircuitExitRole.Start
            ? candidate.Pieces[0].Points
            : candidate.Pieces[^1].Points;
        if (points.Count < 2) { failure = "인출점 주변 구간이 퇴화합니다"; return false; }
        var overrides = new Dictionary<int, SurfaceMappedSample>
        {
            [key.Role == CircuitExitRole.Start ? 0 : points.Count - 1] =
                anchorSample
        };
        if (candidate.Pieces.Count == 1)
        {
            CircuitExitPoint other = key.Role == CircuitExitRole.Start ? end : start;
            if (!TryEvaluateExitSample(side, context, other.Binding, other.Point, out var otherSample, out failure))
                return false;
            overrides[key.Role == CircuitExitRole.Start ? points.Count - 1 : 0] = otherSample;
        }
        var hit = BuildSurfaceMapDelegate(area, side, context: context);
        bool ok = strict
            ? SurfacePathMapper.TryMapRouteStrict(points, hit, overrides, out var samples, out failure, 0.1)
            : SurfacePathMapper.TryMapRoute(points, hit, overrides, out samples, out failure);
        if (!ok) { failure = "모델 표면 매핑 실패 — " + failure; return false; }
        pieces[key.PieceIndex] = points;
        mappedPieces[key.PieceIndex] = samples;
        return true;
    }

    /// <summary>변경 조각에 실제 회로의 양끝을 적용하고, 해당 occurrence에만 원본 평가 표본을 준다.</summary>
    private static bool PrepareDisplayedEditPiece(WorkingArea area, WorkingSideDefinition side,
        CommittedRouteContext context, RouteVertexGroup movedGroup, int pieceIndex,
        IReadOnlyList<Vec2> points, SurfaceMappedSample? movedAnchorSample,
        out IReadOnlyList<Vec2> displayedPoints, out Dictionary<int, SurfaceMappedSample> overrides,
        out string failure)
    {
        displayedPoints = points;
        overrides = null;
        failure = null;
        List<Vec2> adjusted = null;
        for (int i = 0; i < area.Circuits.Count && i < area.CircuitPieceIndices.Count; i++)
        {
            List<int> indices = area.CircuitPieceIndices[i];
            if (indices.Count == 0) continue;
            for (int role = 0; role < 2; role++)
            {
                if ((role == 0 ? indices[0] : indices[^1]) != pieceIndex) continue;
                int pointIndex = role == 0 ? 0 : points.Count - 1;
                if (pointIndex < 0) { failure = "인출점이 있는 조각에 꼭지점이 없습니다"; return false; }
                SurfaceMappedSample sample;
                bool moved = movedAnchorSample.HasValue && movedGroup.Occurrences.Any(o =>
                    o.Piece == pieceIndex && o.Point == pointIndex);
                if (moved) sample = movedAnchorSample.Value;
                else
                {
                    CircuitExitPoint exit = role == 0 ? area.Circuits[i].StartExit : area.Circuits[i].EndExit;
                    if (!TryEvaluateExitSample(side, context, exit?.Binding, exit?.Point, out sample, out failure))
                        return false;
                }
                adjusted ??= points.ToList();
                adjusted[pointIndex] = sample.DatumPoint;
                overrides ??= new Dictionary<int, SurfaceMappedSample>();
                overrides[pointIndex] = sample;
            }
        }
        displayedPoints = adjusted ?? points;
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
        if (area?.Pattern == null) return;
        if (!area.HasExitFace)
        {
            ExitVertexEdit();
            StatusText.Text = "인출면이 지정되지 않아 편집을 적용할 수 없습니다";
            return;
        }
        // 미리보기 중 초안/인출면/앵커가 바뀌어 표시 결과의 원본 조건이 달라졌다면
        // 이전 결과를 새 조건에 덮어쓰지 않고 편집을 거부한다.
        if (IsPatternInputStale(area))
        {
            ExitVertexEdit();
            StatusText.Text = "입력 조건이 변경되어 편집을 적용하지 않았습니다 — 패턴을 다시 생성한 뒤 편집하세요";
            return;
        }
        // 후보를 엄격 검증한다. 스테이징의 캐시는 전체 형상·binding·검증 정책이 같을 때만 재사용한다.
        if (!TryBuildVertexCandidate(datum, out SerpentineResult candidate, out Vec2 applied,
                out string failure, out bool anchor, out bool isStart,
                out _, out ExitEdgeBinding? movedBinding))
        {
            StatusText.Text = "이동할 수 없습니다 — " + failure;
            return;
        }

        WorkingArea edited = ForkAreaRoute(area);
        bool circuitAnchor = _vertexCircuitAnchors.TryGetValue(_vertexDragGroup, out var circuitKey);
        if (circuitAnchor)
        {
            edited.CircuitExitOverrides.RemoveAll(o =>
                o.PieceIndex == circuitKey.PieceIndex && o.Role == circuitKey.Role);
            edited.CircuitExitOverrides.Add(new CircuitExitOverride(
                circuitKey.PieceIndex, circuitKey.Role, applied) { Binding = movedBinding });
        }
        else
        {
            edited.Pattern = candidate;
            if (anchor)
            {
                if (isStart) edited.StartAnchor = applied;
                else edited.EndAnchor = applied;
                if (edited.SelectedAnchorCandidate != null)
                    edited.SelectedAnchorCandidate = isStart
                        ? edited.SelectedAnchorCandidate with { Start = applied, StartBinding = movedBinding }
                        : edited.SelectedAnchorCandidate with { End = applied, EndBinding = movedBinding };
            }
        }
        edited.RouteEdited = true;
        if (!RebuildNativePatternGeometry(area, edited, out _, out _)) return;
        try
        {
            _vertexGroups = BuildVertexGroups(area);
            UpdateCircuitTable();
            StatusText.Text = string.Format("{0} 영역 · 경로 꼭지점 수정 — 길이 {1:F1}mm",
                area.Name, GetCircuitLengths(area).Sum(c => c.HeatingLengthMm));
            UpdateActionStates();
        }
        catch (Exception ex) { ErrorReporter.ShowWarning("편집은 확정되었으나 화면 갱신에 실패했습니다", ex); }
    }

    /// <summary>편집 내용이 있으면 삭제 동의를 받는다. 여기서는 상태를 지우지 않는다 —
    /// 실제 작업이 성공한 뒤 <see cref="ClearRouteEditState"/>를 호출해야 실패 시 편집이 보존된다.</summary>
    private bool ConfirmDiscardRouteEdits(WorkingArea area, string action)
    {
        if (area == null || !area.RouteEdited) return true;
        if (VisualTestSession.Current != null) return ResolveVisualTestConfirm(action);
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
    }

    private bool ConfirmDiscardAllRouteEdits(string action)
    {
        var edited = _areas.Where(a => a.RouteEdited).ToList();
        if (edited.Count == 0) return true;
        if (VisualTestSession.Current != null) return ResolveVisualTestConfirm(action);
        MessageBoxResult result = MessageBox.Show(this,
            action + " 작업을 계속하면 " + string.Join(", ", edited.Select(a => a.Name))
            + " 영역의 발열경로 편집 내용이 삭제됩니다." + Environment.NewLine + "계속할까요?",
            "발열경로 편집 내용 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>모델 교체·앱 종료 전에 저장되지 않는 작업 세션 전체의 손실을 확인한다.</summary>
    private bool ConfirmDiscardSession(string action)
    {
        if (_areas.Count == 0) return true;
        if (VisualTestSession.Current != null) return ResolveVisualTestConfirm(action);
        MessageBoxResult result = MessageBox.Show(this,
            action + " 작업을 계속하면 현재 작업 세션(영역 " + _areas.Count
            + "개, 생성 경로, 입력 조건)이 사라집니다." + Environment.NewLine
            + "이 앱은 작업 세션 저장을 지원하지 않으며 DXF 내보내기는 세션 저장이 아닙니다."
            + Environment.NewLine + "계속할까요?",
            "작업 세션 손실", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private void ClearAllRouteEditStates()
    {
        foreach (WorkingArea area in _areas) area.RouteEdited = false;
    }
}
