using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 인출면/시작·종료점 지정. 시작점과 종료점은 반드시 같은 인출면 위에 있어야 하며,
/// 사행 경로의 양 끝(최저/최고 레인 끝점)을 인출면 위로 투영한 지점만 후보가 된다.
/// 후보가 없으면 임의로 보정하지 않고 사유만 표시한다.
/// </summary>
public partial class MainWindow
{
    private bool _suppressDirectionChange;
    private readonly List<ExitSideCandidate> _exitCandidates = new();
    private double[][] _exitCandidatePolylines = Array.Empty<double[]>();
    private int _exitCandidateHover = -1;

    /// <summary>현재 인출점 픽 역할. 0=시작, 1=종료, -1=없음.</summary>
    private int _exitPointPickRole = -1;
    /// <summary>인출점 픽 중 작업 정면으로 전환하기 전의 카메라 스냅샷.</summary>
    private WorkingPresentation _exitPointPresentation;
    private bool _exitPointPresentationHeld;
    /// <summary>커서 픽 허용 거리(mm, 작업 평면). 정확 CAD 판정 tolerance와 별개다.</summary>
    private const double ExitPointPickTolerance = 5.0;

    /// <summary>표시 좌표가 같아도 원본 edge/경로가 다를 수 있으므로 후보 DTO 자체를 보관한다.</summary>
    private sealed record AnchorCandidateRow(int Index, string Label, AnchoredCandidate Candidate);
    private bool _suppressAnchorSelection;

    private static double Dist(Vec2 a, Vec2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void SyncExitFaceUi(WorkingArea area)
    {
        if (DirectionBox != null)
        {
            _suppressDirectionChange = true;
            DirectionBox.SelectedIndex = area == null ? 0 : IndexFromDirection(area.Direction);
            DirectionBox.IsEnabled = area != null;
            _suppressDirectionChange = false;
        }
        bool boundaryReady = area != null && area.Region.Count == 1 && area.RegionExitBoundary != null;
        if (PickStartExitButton != null)
        {
            PickStartExitButton.IsEnabled = boundaryReady;
            PickStartExitButton.Content = area?.SelectedStartExit.HasValue == true ? "시작점 다시 선택" : "시작점 선택";
        }
        if (PickEndExitButton != null)
        {
            PickEndExitButton.IsEnabled = boundaryReady;
            PickEndExitButton.Content = area?.SelectedEndExit.HasValue == true ? "종료점 다시 선택" : "종료점 선택";
        }
        if (ClearExitFaceButton != null)
            ClearExitFaceButton.IsEnabled = area != null
                && (area.SelectedStartExit.HasValue || area.SelectedEndExit.HasValue);
        if (ExitFaceStatusText != null) ExitFaceStatusText.Text = DescribeExitState(area);
        SyncAnchorCandidateList(area);
        if (_exitPointPickRole < 0) UpdateSelectedExitMarkers(area);
    }

    /// <summary>
    /// 활성 영역의 인출 경계 edge를 영역 원색(무광)의 폴리라인으로 계속 강조한다.
    /// 영역을 전환하면 이전 edge 강조는 교체되고, 인출면이 없으면 해제된다.
    /// </summary>
    private void ApplyActiveExitEdgeHighlight()
    {
        var area = _activeArea;
        if (area == null)
        {
            Bridge.ClearExitEdgeHighlight();
            return;
        }
        IReadOnlyList<Vec3> source = null;
        // 인출면 방식(legacy)은 그 면의 경계를, 선택 인출점 방식은 적용영역 전체
        // 경계를 보여 준다. 선택점이 하나라도 있거나 선택 중이면 영역 경계가 정본이다.
        bool showRegionOutline = area.ExitFaceId < 0 || _exitPointPickRole >= 0 ||
            area.SelectedStartExit.HasValue || area.SelectedEndExit.HasValue;
        if (showRegionOutline && area.RegionExitBoundary is SplitRegionBoundary2D split &&
            split.Segments.Count > 0 && area.FaceIdSet.Count > 0)
            source = BuildSplitOutline3D(area, split);
        else if (showRegionOutline && area.RegionExitBoundary is EffectiveRegionBoundary2D effective &&
            effective.Segments.Count > 0 && area.FaceIdSet.Count > 0)
            source = BuildEffectiveOutline3D(area, effective);
        else if (area.ExitBoundary != null && area.ExitChain3D != null && area.ExitChain3D.Count >= 2)
            source = area.ExitChain3D;
        if (source == null || source.Count < 2)
        {
            Bridge.ClearExitEdgeHighlight();
            return;
        }
        // 정본 체인은 native 점 인덱스를 그대로 보존한다. 렌더링 배열에서만 실제로
        // 같은 3D 점을 제거하며, 같은 XY/다른 깊이의 점은 합치지 않는다.
        var displayPoints = new List<Vec3>();
        foreach (Vec3 point in source)
            if (displayPoints.Count == 0 || point != displayPoints[^1]) displayPoints.Add(point);
        if (displayPoints.Count < 2)
        {
            Bridge.ClearExitEdgeHighlight();
            return;
        }
        var points = new double[displayPoints.Count * 3];
        for (int i = 0; i < displayPoints.Count; i++)
        {
            Vec3 p = displayPoints[i];
            points[i * 3] = p.X;
            points[i * 3 + 1] = p.Y;
            points[i * 3 + 2] = p.Z;
        }
        Color c = area.Color;
        Bridge.SetExitEdgeHighlight(points, c.R / 255.0, c.G / 255.0, c.B / 255.0);
    }

    /// <summary>
    /// 제외영역이 외곽을 깎은 경우의 표시용 단일 링. CAD 구간은 원본 체인을 보간하고,
    /// 제외로 생긴 인공 직선은 작업면 → 선택 면 표면 매핑으로 3D 점을 만든다.
    /// 이 표시는 설계 구속이 아니며, 선택/검증은 EffectiveRegionBoundary2D가 담당한다.
    /// </summary>
    private static List<Vec3> BuildEffectiveOutline3D(
        WorkingArea area, EffectiveRegionBoundary2D effective)
    {
        IReadOnlyList<Vec2> outline = effective.Outline;
        if (outline.Count < 3) return null;
        var result = new List<Vec3>();
        for (int i = 0; i < outline.Count; i++)
        {
            Vec2 a = outline[i];
            Vec2 b = outline[(i + 1) % outline.Count];
            if (a.DistanceTo(b) <= 1e-9) continue;
            if (IsArtificialOutlineEdge(effective, a, b))
            {
                int steps = Math.Clamp((int)Math.Ceiling(a.DistanceTo(b) / 2.0), 1, 512);
                for (int step = 0; step <= steps; step++)
                    AddMappedOutlinePoint(result, area, effective.CadBoundary,
                        new Vec2(a.X + (b.X - a.X) * step / steps,
                            a.Y + (b.Y - a.Y) * step / steps));
            }
            else
            {
                AddCadOutlinePoints(result, area, a, b);
            }
        }
        if (result.Count >= 2 && result[0] != result[^1]) result.Add(result[0]);
        return result.Count >= 2 ? result : null;
    }

    /// <summary>
    /// 분할 영역의 표시용 외곽. CAD 구간은 원본 체인을 보간하고, 새 절단선은
    /// 작업면 → 선택 면 표면 매핑으로 3D 점을 만든다. 절단선은 설계 구속으로는
    /// SplitRegionBoundary2D의 독립 artificial provenance로 유지된다.
    /// </summary>
    private static List<Vec3> BuildSplitOutline3D(WorkingArea area, SplitRegionBoundary2D split)
    {
        IReadOnlyList<Vec2> outline = split.Ring;
        if (outline.Count < 3) return null;
        var result = new List<Vec3>();
        for (int i = 0; i < outline.Count; i++)
        {
            Vec2 a = outline[i];
            Vec2 b = outline[(i + 1) % outline.Count];
            if (a.DistanceTo(b) <= 1e-9) continue;
            if (IsSplitOutlineEdge(split, a, b))
            {
                int steps = Math.Clamp((int)Math.Ceiling(a.DistanceTo(b) / 2.0), 1, 512);
                for (int step = 0; step <= steps; step++)
                    AddMappedOutlinePoint(result, area, split.CadBoundary,
                        new Vec2(a.X + (b.X - a.X) * step / steps,
                            a.Y + (b.Y - a.Y) * step / steps));
            }
            else
            {
                AddCadOutlinePoints(result, area, a, b);
            }
        }
        if (result.Count >= 2 && result[0] != result[^1]) result.Add(result[0]);
        return result.Count >= 2 ? result : null;
    }

    private static bool IsSplitOutlineEdge(SplitRegionBoundary2D split, Vec2 a, Vec2 b)
    {
        Vec2 midpoint = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        foreach (SplitRegionBoundary2D.SplitSegment segment in split.Segments)
        {
            if (DistanceToSegment(segment.A, segment.B, midpoint) <= 0.05 &&
                DistanceToSegment(segment.A, segment.B, a) <= 0.05 &&
                DistanceToSegment(segment.A, segment.B, b) <= 0.05)
                return true;
        }
        return false;
    }

    private static bool IsArtificialOutlineEdge(
        EffectiveRegionBoundary2D effective, Vec2 a, Vec2 b)
    {
        Vec2 midpoint = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        foreach (EffectiveRegionBoundary2D.ArtificialSegment segment in effective.Segments)
        {
            if (DistanceToSegment(segment.A, segment.B, midpoint) <= 0.05 &&
                DistanceToSegment(segment.A, segment.B, a) <= 0.05 &&
                DistanceToSegment(segment.A, segment.B, b) <= 0.05)
                return true;
        }
        return false;
    }

    /// <summary>
    /// CAD 구간은 원본 3D 체인의 중간점도 함께 보간한다. 2D에서 직선이어도
    /// 경사/곡면 위에서는 3D가 휘 수 있으므로 끝점 코드만으로 대체하지 않는다.
    /// </summary>
    private static void AddCadOutlinePoints(
        List<Vec3> points, WorkingArea area, Vec2 a, Vec2 b)
    {
        AddUniqueOutlinePoint(points, PointOnExitChain3D(area, a, out _));
        Vec2 ab = b - a;
        double lengthSq = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSq > 1e-18 && area.ExitChain != null && area.ExitChain3D != null)
        {
            int count = Math.Min(area.ExitChain.Count, area.ExitChain3D.Count);
            var interior = new List<(double T, Vec3 Point)>();
            for (int i = 0; i < count; i++)
            {
                Vec2 point = area.ExitChain[i];
                if (DistanceToSegment(a, b, point) > 0.02) continue;
                double t = ((point.X - a.X) * ab.X + (point.Y - a.Y) * ab.Y) / lengthSq;
                if (t <= 1e-9 || t >= 1 - 1e-9) continue;
                interior.Add((t, area.ExitChain3D[i]));
            }
            interior.Sort((left, right) => left.T.CompareTo(right.T));
            foreach ((double _, Vec3 point) in interior)
                AddUniqueOutlinePoint(points, point);
        }
        AddUniqueOutlinePoint(points, PointOnExitChain3D(area, b, out _));
    }

    private static void AddMappedOutlinePoint(
        List<Vec3> points, WorkingArea area, CadExitBoundary2D cad, Vec2 datum)
    {
        int[] faces = area.FaceIdSet.OrderBy(id => id).ToArray();
        var direct = Bridge.MapWorkingPointOntoFaces(
            cad.ContextId, cad.Revision, faces, datum.X, datum.Y);
        Vec3 value;
        if (direct != null && direct.Status == WorkingQueryStatus.Hit &&
            direct.Point is { Length: >= 3 })
        {
            value = new Vec3(direct.Point[0], direct.Point[1], direct.Point[2]);
        }
        else
        {
            var global = Bridge.QueryWorkingSurface(cad.ContextId, cad.Revision, datum.X, datum.Y);
            value = global != null &&
                (global.Status == WorkingQueryStatus.Hit || global.Status == WorkingQueryStatus.Tangent) &&
                global.Point is { Length: >= 3 }
                ? new Vec3(global.Point[0], global.Point[1], global.Point[2])
                : area.Frame.Unproject(datum);
        }
        AddUniqueOutlinePoint(points, value);
    }

    private static void AddUniqueOutlinePoint(List<Vec3> points, Vec3 point)
    {
        if (points.Count == 0 || points[^1] != point) points.Add(point);
    }

    private static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 point)
    {
        Vec2 ab = b - a;
        double lengthSq = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSq <= 1e-18) return point.DistanceTo(a);
        double t = Math.Clamp(
            ((point.X - a.X) * ab.X + (point.Y - a.Y) * ab.Y) / lengthSq, 0, 1);
        return point.DistanceTo(a + ab * t);
    }

    /// <summary>시작/종료점 후보 목록을 현재 영역 후보와 선택 상태에 맞춰 다시 만든다.</summary>
    private void SyncAnchorCandidateList(WorkingArea area)
    {
        if (AnchorCandidateList == null) return;
        _suppressAnchorSelection = true;
        try
        {
            AnchorCandidateList.Items.Clear();
            AnchorCandidateList.IsEnabled = area != null && area.AnchorCandidates.Count > 0;
            if (area == null) return;
            for (int i = 0; i < area.AnchorCandidates.Count; i++)
            {
                var c = area.AnchorCandidates[i];
                AnchorCandidateList.Items.Add(new AnchorCandidateRow(i,
                    string.Format("후보 {0}: ({1:F1}, {2:F1}) → ({3:F1}, {4:F1}) · 각도 {5:F0}°",
                        i + 1, c.Start.X, c.Start.Y, c.End.X, c.End.Y, c.AngleDeg),
                    c));
            }
            string selectedId = area.SelectedAnchorCandidate?.CandidateId;
            if (string.IsNullOrEmpty(selectedId)) return;
            var selected = AnchorCandidateList.Items.Cast<AnchorCandidateRow>()
                .Where(row => string.Equals(row.Candidate.CandidateId, selectedId, StringComparison.Ordinal)).ToList();
            if (selected.Count == 1) AnchorCandidateList.SelectedItem = selected[0];
        }
        finally { _suppressAnchorSelection = false; }
    }

    private void AnchorCandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAnchorSelection) return;
        if (AnchorCandidateList?.SelectedItem is not AnchorCandidateRow row) return;
        if (!ApplyAnchorCandidate(row.Candidate, out string error))
            StatusText.Text = error;
    }

    private string DescribeExitState(WorkingArea area)
    {
        if (area == null) return "작업영역을 먼저 선택하세요";
        if (area.RegionExitBoundary == null)
            return "적용영역 외곽 경계가 없습니다 — 영역을 다시 확정하세요";
        if (!string.IsNullOrEmpty(area.LastExitFailure) && area.HasSelectedExits)
            return area.LastExitFailure;
        bool hasStart = area.SelectedStartExit.HasValue;
        bool hasEnd = area.SelectedEndExit.HasValue;
        if (!hasStart && !hasEnd)
            return "시작점과 종료점을 선택하세요 — 적용영역 외곽 Edge 위 점을 클릭합니다";
        if (!hasStart) return "시작점을 선택하세요 — 외곽 Edge 위 점을 클릭합니다";
        if (!hasEnd) return "종료점을 선택하세요 — 외곽 Edge 위 점을 클릭합니다";
        Vec2 s = area.SelectedStartExit.Value.Point, t = area.SelectedEndExit.Value.Point;
        return string.Format("인출점 — 시작 ({0:F1}, {1:F1}) → 종료 ({2:F1}, {3:F1}) · 외곽 Edge 기준",
            s.X, s.Y, t.X, t.Y);
    }

    private void DirectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDirectionChange) return;
        var area = _activeArea;
        if (area == null) return;
        var direction = DirectionFromIndex(DirectionBox.SelectedIndex);
        if (area.Direction == direction) return;
        if (!ConfirmDiscardRouteEdits(area, "배치 방향 변경"))
        {
            // 취소하면 콤보 표시를 실제 영역 방향으로 되돌린다.
            _suppressDirectionChange = true;
            DirectionBox.SelectedIndex = IndexFromDirection(area.Direction);
            _suppressDirectionChange = false;
            return;
        }
        if (area.Direction == direction) return;

        ExitVertexEdit();
        CancelExitFacePick();
        area.Direction = direction;
        // 방향은 몸통 경로만 바꾼다. 사용자가 고른 두 인출점은 유지하고,
        // 그 점을 전제로 만든 후보/계획만 무효화한다.
        area.AnchorCandidates.Clear();
        area.SelectedAnchorCandidate = null;
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        area.LastExitFailure = null;
        // 기존 결과는 아직 화면에 남아 있다. 수동 편집 표시도 실제 재생성이
        // 성공할 때까지 유지해 '입력 조건 변경'과 '편집됨'을 함께 보여준다.
        SyncExitFaceUi(area);
        UpdateActionStates();
        if (area.Pattern != null && GenerateHintText != null)
            GenerateHintText.Text = "배치 방향이 바뀌었습니다 — 패턴을 다시 생성해야 반영됩니다.";
        StatusText.Text = string.Format("{0} 영역 배치 방향: {1}", area.Name, DirectionLabel(direction));
    }

    private static string DirectionLabel(PatternDirection direction) => direction switch
    {
        PatternDirection.FrameX => "가로 (Frame X)",
        PatternDirection.FrameY => "세로 (Frame Y)",
        _ => "가로 (Frame X)"
    };

    /// <summary>자동(장축) 선택이 제거되어 저장된 Auto는 가로로 해석한다.</summary>
    private static PatternDirection EffectiveDirection(PatternDirection direction) =>
        direction == PatternDirection.FrameY ? PatternDirection.FrameY : PatternDirection.FrameX;

    /// <summary>콤보 인덱스 → 배치 방향. 0=가로, 1=세로.</summary>
    private static PatternDirection DirectionFromIndex(int index) =>
        index == 1 ? PatternDirection.FrameY : PatternDirection.FrameX;

    /// <summary>배치 방향 → 콤보 인덱스. Auto는 가로로 표시한다.</summary>
    private static int IndexFromDirection(PatternDirection direction) =>
        EffectiveDirection(direction) == PatternDirection.FrameY ? 1 : 0;

    private void PickStartExit_Click(object sender, RoutedEventArgs e) => BeginExitPointPick(0);

    private void PickEndExit_Click(object sender, RoutedEventArgs e) => BeginExitPointPick(1);

    /// <summary>적용영역 외곽 원본 Edge 위에서 시작/종료 인출점을 직접 고른다.</summary>
    private void BeginExitPointPick(int role)
    {
        var area = _activeArea;
        if (area == null) { StatusText.Text = "작업영역을 먼저 선택하세요"; return; }
        if (area.Region.Count != 1 || area.RegionExitBoundary == null || area.ExitEdgeRefs.Count == 0)
        {
            StatusText.Text = "하나로 연결된 적용영역의 외곽 경계에서만 인출점을 지정할 수 있습니다";
            return;
        }
        if (!ConfirmDiscardRouteEdits(area, role == 0 ? "시작 인출점 변경" : "종료 인출점 변경")) return;
        ExitVertexEdit();
        if (Viewport == null || !Viewport.IsReady) return;

        // 다른 뷰포트 편집 모드와 상호 배타.
        CancelPowerSetup();
        ExitExclusionSketch();
        Viewport.RectangleSelectionMode = false;
        Viewport.PowerLocationMode = false;
        Viewport.CircuitHoverMode = false;
        Bridge.ClearHover();
        CancelExitFacePick();

        var side = _workingSide?.Confirmed;
        if (side == null) { StatusText.Text = "작업면이 확정되지 않았습니다"; return; }

        _exitPointPickRole = role;
        _exitCandidates.Clear();
        _exitCandidatePolylines = Array.Empty<double[]>();
        _exitCandidateHover = -1;

        // 화면 픽은 현재 카메라에서 작업 평면으로 투영한다. 정면으로 전환하면
        // 경사진 작업면에서도 커서와 원본 Edge의 대응이 안정적이다.
        _exitPointPresentation = Bridge.CaptureWorkingPresentation();
        _exitPointPresentationHeld = _exitPointPresentation != null;
        if (!Bridge.ReturnToWorkingFront(side.Id.Value, side.Revision.Value))
        {
            CancelExitFacePick();
            StatusText.Text = "작업 정면으로 전환하지 못했습니다 — 인출점을 선택할 수 없습니다";
            return;
        }

        Viewport.ExitFacePickMode = true;
        Viewport.ViewLocked = true;
        ApplyActiveExitEdgeHighlight();
        UpdateSelectedExitMarkers(area);
        StatusText.Text = role == 0
            ? "적용영역 외곽 Edge에서 시작 인출점을 클릭하세요 (Esc 취소)"
            : "적용영역 외곽 Edge에서 종료 인출점을 클릭하세요 (Esc 취소)";
    }

    /// <summary>작업평면 2D 좌표를 적용영역 외곽의 원본 Edge로 투영한다. 화면 픽과 시각테스트가
    /// 같은 경계 판정을 쓰도록 공통 경로로 분리했다.</summary>
    private static bool TryResolveExitPoint(WorkingArea area, Vec2 datum,
        out ExitBoundaryPoint point, out string error)
    {
        point = default;
        error = null;
        if (area?.RegionExitBoundary == null)
        {
            error = "적용영역 외곽 경계가 없습니다";
            return false;
        }
        ExitBoundaryResolution resolution = area.RegionExitBoundary.Project(datum, ExitPointPickTolerance);
        if (!resolution.Succeeded)
        {
            error = ExplainExitBoundaryFailure(resolution.Failure);
            return false;
        }
        point = resolution.Point!.Value;
        return true;
    }

    private bool TryProjectExitPoint(WorkingArea area, int x, int y,
        out ExitBoundaryPoint point, out string error)
    {
        point = default;
        error = null;
        if (!TryViewportPointOnFrame(x, y, out Vec2 datum, out _))
        {
            error = "작업 평면 위의 점을 찾지 못했습니다";
            return false;
        }
        return TryResolveExitPoint(area, datum, out point, out error);
    }

    private bool TryEvaluateSelectedExit3D(WorkingArea area, ExitBoundaryPoint point, out Vec3 position) =>
        TryEvaluateSelectedExit3D(area, point, out position, out _);

    /// <summary>선택 인출점의 실제 3D 표면 위치를 평가한다. 분할 자동 지정처럼 확정 전에
    /// 검증하는 호출자는 실패 사유가 필요하므로 별도 overload로 전달한다.</summary>
    private bool TryEvaluateSelectedExit3D(WorkingArea area, ExitBoundaryPoint point, out Vec3 position,
        out string failure)
    {
        position = default;
        failure = string.Empty;
        var side = _workingSide?.Confirmed;
        if (area == null)
        {
            failure = "작업영역이 없습니다.";
            return false;
        }
        if (side == null)
        {
            failure = "확정된 작업 방향이 없습니다.";
            return false;
        }
        var context = CommittedRouteContext.CaptureRegion(area);
        if (!TryEvaluateExitSample(side, context, point, point.Point,
                out SurfaceMappedSample sample, out failure))
        {
            if (string.IsNullOrWhiteSpace(failure))
                failure = "인출점을 작업면에 매핑할 수 없습니다.";
            return false;
        }
        position = sample.Point;
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
        {
            failure = "인출점의 3D 좌표가 유한하지 않습니다.";
            return false;
        }
        return true;
    }

    private static double[] ExitMarkerWire(Vec3 center, ProjectionFrame frame, bool diamond)
    {
        const double radius = 4.0;
        Vec3 x = frame.AxisX * radius, y = frame.AxisY * radius;
        Vec3[] points = diamond
            ? new[] { center + x, center + y, center - x, center - y, center + x }
            : new[] { center + x + y, center - x + y, center - x - y, center + x - y, center + x + y };
        var flat = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            flat[i * 3] = points[i].X;
            flat[i * 3 + 1] = points[i].Y;
            flat[i * 3 + 2] = points[i].Z;
        }
        return flat;
    }

    /// <summary>선택된 두 인출점을 각각 사각형(시작)/다이아몬드(종료) 마커로 표시한다.</summary>
    private void UpdateSelectedExitMarkers(WorkingArea area)
    {
        if (area == null)
        {
            Bridge.ClearExitCandidates();
            return;
        }
        var polylines = new List<double[]>();
        if (area.SelectedStartExit.HasValue
            && TryEvaluateSelectedExit3D(area, area.SelectedStartExit.Value, out Vec3 start))
            polylines.Add(ExitMarkerWire(start, area.Frame, diamond: false));
        if (area.SelectedEndExit.HasValue
            && TryEvaluateSelectedExit3D(area, area.SelectedEndExit.Value, out Vec3 end))
            polylines.Add(ExitMarkerWire(end, area.Frame, diamond: true));
        if (polylines.Count == 0) Bridge.ClearExitCandidates();
        else Bridge.ShowExitCandidates(polylines.ToArray());
        ApplyActiveExitEdgeHighlight();
    }

    private void OnExitPointPickMoved(int x, int y)
    {
        var area = _activeArea;
        if (area?.RegionExitBoundary == null) return;
        if (x < 0 || y < 0)
        {
            ApplyActiveExitEdgeHighlight();
            return;
        }
        if (!TryProjectExitPoint(area, x, y, out ExitBoundaryPoint point, out string error))
        {
            ApplyActiveExitEdgeHighlight();
            StatusText.Text = error + " — 외곽 Edge에 더 가까이 클릭하세요 (Esc 취소)";
            return;
        }
        if (TryEvaluateSelectedExit3D(area, point, out Vec3 position))
            Bridge.SetExitEdgeHighlight(ExitMarkerWire(position, area.Frame, diamond: true), 1.0, 0.55, 0.0);
        StatusText.Text = string.Format("{0} 인출점 후보: ({1:F1}, {2:F1}) — 클릭하여 지정 (Esc 취소)",
            _exitPointPickRole == 0 ? "시작" : "종료", point.Point.X, point.Point.Y);
    }

    private void OnExitPointPicked(int x, int y)
    {
        var area = _activeArea;
        if (area == null || _exitPointPickRole < 0) return;
        if (!TryProjectExitPoint(area, x, y, out ExitBoundaryPoint point, out string error))
        {
            StatusText.Text = error + " — 선택 모드를 유지합니다 (Esc 취소)";
            return;
        }

        ApplyPickedExitPoint(area, _exitPointPickRole, point);
    }

    /// <summary>해석된 인출점을 영역에 적용하고 선택 모드를 종료한다. 화면 픽과 시각테스트가
    /// 같은 후처리를 쓰도록 공통 경로로 분리했다.</summary>
    private void ApplyPickedExitPoint(WorkingArea area, int role, ExitBoundaryPoint point)
    {
        if (area == null) return;
        if (role == 0) area.SelectedStartExit = point;
        else if (role == 1) area.SelectedEndExit = point;
        else return;
        area.LastExitFailure = null;
        // 후보 쌍/제외 계획은 이전 점 기준이므로 무효화한다. 선택한 점 자체는 유지한다.
        area.AnchorCandidates.Clear();
        area.SelectedAnchorCandidate = null;
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        // 기존 UI/검증 경로와의 호환을 위해 좌표도 함께 갱신한다.
        area.StartAnchor = area.SelectedStartExit?.Point;
        area.EndAnchor = area.SelectedEndExit?.Point;

        CancelExitFacePick();
        SyncExitFaceUi(area);
        UpdateActionStates();
        StatusText.Text = area.HasSelectedExits
            ? "시작/종료 인출점이 지정되었습니다 — 경로를 생성하세요"
            : area.SelectedStartExit.HasValue
                ? "시작 인출점이 지정되었습니다 — 종료점을 선택하세요"
                : "종료 인출점이 지정되었습니다 — 시작점을 선택하세요";
    }

    private void PickExitFace_Click(object sender, RoutedEventArgs e)
    {
        var area = _activeArea;
        if (area == null) { StatusText.Text = "작업영역을 먼저 선택하세요"; return; }
        if (area.Region.Count != 1)
        {
            StatusText.Text = "하나로 연결된 적용영역에서만 인출면을 지정할 수 있습니다";
            return;
        }
        if (!ConfirmDiscardRouteEdits(area, "인출면 변경")) return;
        ExitVertexEdit();
        if (Viewport == null || !Viewport.IsReady) return;
        CancelExitFacePick();

        // 인출면은 확정 영역 외곽선의 반대편 면에서만 고른다 (근접 면 클릭 방식 대체).
        var frameNormal = area.Frame.Normal;
        var sides = Bridge.GetBoundaryExitSides(SortedFaceIds(area), GeometryTolerancePolicy.DistanceTolerance,
            frameNormal.X, frameNormal.Y, frameNormal.Z);
        if (sides == null || sides.Length == 0)
        {
            StatusText.Text = "인출 측면 후보가 없습니다 — 적용영역 외곽선에 인접한 면이 없거나 경계 매칭에 실패했습니다";
            return;
        }
        foreach (var side in sides)
        {
            if (side == null || side.FaceId < 0 || side.Points == null || side.Points.Length < 6) continue;
            _exitCandidates.Add(side);
        }
        if (_exitCandidates.Count == 0)
        {
            StatusText.Text = "인출 측면 후보가 없습니다 — 적용영역 외곽선에 인접한 면이 없거나 경계 매칭에 실패했습니다";
            return;
        }

        Bridge.ShowExitCandidates(_exitCandidatePolylines = _exitCandidates.Select(c => c.Points).ToArray());
        Viewport.ExitFacePickMode = true;
        Viewport.ViewLocked = true;
        StatusText.Text = string.Format("외곽선을 따라 표시된 인출 측면 후보 {0}개 — 후보 위로 마우스를 올리면 강조됩니다. 클릭하세요 (Esc 취소)",
            _exitCandidates.Count);
    }

    private void CancelExitFacePick()
    {
        _exitPointPickRole = -1;
        if (Viewport != null)
        {
            Viewport.ExitFacePickMode = false;
            Viewport.ViewLocked = false;
        }
        Bridge.ClearExitCandidates();
        Bridge.HighlightExitFace(-1);
        _exitCandidates.Clear();
        _exitCandidatePolylines = Array.Empty<double[]>();
        _exitCandidateHover = -1;
        if (_exitPointPresentationHeld && _exitPointPresentation != null)
        {
            Bridge.RestoreWorkingPresentation(_exitPointPresentation.Id);
            Bridge.ReleaseWorkingPresentation(_exitPointPresentation.Id);
        }
        _exitPointPresentation = null;
        _exitPointPresentationHeld = false;
        UpdateSelectedExitMarkers(_activeArea);
    }

    /// <summary>인출점 선택 중 커서 이동 — 커서 아래 원본 Edge 위치를 미리 보여준다.</summary>
    private void OnExitFacePickMoved(int x, int y)
    {
        if (_exitPointPickRole >= 0)
        {
            OnExitPointPickMoved(x, y);
            return;
        }

        if (_exitCandidatePolylines.Length == 0) return;
        int index = x < 0 || y < 0
            ? -1
            : Bridge.PickExitCandidate(_exitCandidatePolylines, x, y, 12.0);
        if (index != _exitCandidateHover)
        {
            _exitCandidateHover = index;
            Bridge.HighlightExitCandidate(index);
        }
    }

    private void OnExitFacePicked(int x, int y)
    {
        if (_exitPointPickRole >= 0)
        {
            OnExitPointPicked(x, y);
            return;
        }

        var area = _activeArea;
        if (area == null || _exitCandidates.Count == 0)
        {
            CancelExitFacePick();
            return;
        }

        // 후보 목록은 CancelExitFacePick에서 비워지므로 픽을 먼저 수행한다.
        int index = Bridge.PickExitCandidate(_exitCandidatePolylines, x, y, 12.0);
        if (index < 0)
        {
            StatusText.Text = "인출 측면 후보 위에서 클릭하세요 — Esc로 취소할 수 있습니다";
            return; // 선택 모드 유지
        }

        if (!ApplyExitCandidate(index, out string error))
        {
            StatusText.Text = error;
            return;
        }

        double length = 0;
        for (int i = 1; i < area.ExitChain.Count; i++) length += Dist(area.ExitChain[i - 1], area.ExitChain[i]);
        if (area.HasExitAnchors)
            StatusText.Text = string.Format("{0} 영역 인출면 Face {1} · 길이 {2:F1}mm — 시작/종료점 후보가 목록에 표시됩니다",
                area.Name, area.ExitFaceId, length);
        else
            StatusText.Text = area.LastExitFailure ?? "인출면은 지정됐지만 사용 가능한 시작/종료점 후보가 없습니다";
    }

    /// <summary>
    /// 후보 목록의 인덱스를 실제 인출면 상태에 적용한다. UI는 좌표 픽으로,
    /// 시각 테스트는 FaceId/좌표 지문으로 인덱스를 찾은 뒤 이 공통 경로를 호출한다.
    /// </summary>
    private bool ApplyExitCandidate(int index, out string error)
    {
        error = null;
        var area = _activeArea;
        if (area == null || index < 0 || index >= _exitCandidates.Count)
        {
            error = "선택한 인출면 후보를 찾을 수 없습니다";
            return false;
        }

        var chosen = _exitCandidates[index];
        long modelRevision = Bridge.GetModelRevision();
        var side = _workingSide?.Confirmed;
        if (side == null || side.Revision.Value != modelRevision || area.Frame == null)
        {
            error = "작업 방향 또는 모델이 변경되었습니다 — 인출면 후보를 다시 선택하세요";
            return false;
        }
        if (!TryValidateExitCandidateCoverage(chosen, modelRevision, out error)) return false;

        // Native의 원본 점/구간 인덱스를 그대로 보관한다. 표시용 중복점을 이유로
        // edge 범위를 합치거나 remap하지 않으며 설계 질의는 이 체인을 사용하지 않는다.
        var chain = new List<Vec2>();
        var chain3D = new List<Vec3>();
        for (int k = 0; k < chosen.Points.Length; k += 3)
        {
            var point = new Vec3(chosen.Points[k], chosen.Points[k + 1], chosen.Points[k + 2]);
            Vec2 datum = area.Frame.Project(point);
            if (!double.IsFinite(datum.X) || !double.IsFinite(datum.Y))
            {
                error = "선택한 인출 경계를 작업 평면에 투영하지 못했습니다";
                return false;
            }
            chain.Add(datum);
            chain3D.Add(point);
        }
        var references = chosen.EdgeReferences.Select(CadExitBoundary2D.CopyReference).ToList();
        var boundary = new CadExitBoundary2D(side.Id.Value, side.Revision.Value, references);
        foreach (ExitEdgeReference reference in references)
        {
            var binding = new ExitEdgeBinding(reference.Revision, side.Id.Value, side.Revision.Value,
                reference.EdgeKey, reference.FirstParameter, reference.LastParameter,
                reference.FirstParameter, reference.Reversed);
            ExitBoundaryResolution first = boundary.Evaluate(binding);
            ExitBoundaryResolution last = boundary.Evaluate(binding with { Parameter = reference.LastParameter });
            if (!first.Succeeded || !last.Succeeded)
            {
                error = ExplainExitBoundaryFailure(!first.Succeeded ? first.Failure : last.Failure);
                return false;
            }
        }

        CancelExitFacePick();
        area.ExitFaceId = chosen.FaceId;
        area.ExitFaceRevision = modelRevision;
        area.ExitChain = chain;
        area.ExitChain3D = chain3D;
        area.ExitChainFaceIds = chosen.SegmentFaceIds.ToList();
        area.ExitEdgeRefs = references;
        area.ExitBoundary = boundary;
        area.SelectedAnchorCandidate = null;
        // 표시 중인 경로의 수동 인출점은 실제 재생성 후보에서만 지운다.
        RecomputeAnchorCandidates(area);
        // 피킹 취소 시에는 아무것도 바꾸지 않는다. 실제 인출면이 바뀐 경우에도
        // 기존 결과가 화면에 남아 있으므로 재생성 성공 전에는 편집 표시를 지우지 않는다.
        SyncExitFaceUi(area);
        ApplyActiveExitEdgeHighlight();
        UpdateActionStates();
        return true;
    }

    /// <summary>원본 polyline의 모든 구간에 현재 모델의 유효한 CAD 참조가 정확히 하나씩 있어야 한다.</summary>
    private static bool TryValidateExitCandidateCoverage(ExitSideCandidate candidate, long modelRevision,
        out string error)
    {
        error = null;
        if (candidate?.Points == null || candidate.Points.Length < 6 || candidate.Points.Length % 3 != 0 ||
            candidate.Points.Any(value => !double.IsFinite(value)))
        {
            error = "선택한 인출 경계의 표시 좌표가 올바르지 않습니다";
            return false;
        }
        int pointCount = candidate.Points.Length / 3;
        if (candidate.FaceId < 0 || candidate.SegmentFaceIds == null ||
            candidate.SegmentFaceIds.Length != pointCount - 1 || candidate.SegmentFaceIds.Any(id => id < 0) ||
            candidate.EdgeReferences == null || candidate.EdgeReferences.Length == 0)
        {
            error = "인출 경계의 원본 edge/구간 면 참조가 빠져 있습니다 — 다른 후보를 선택하세요";
            return false;
        }
        var covered = new bool[pointCount - 1];
        foreach (ExitEdgeReference reference in candidate.EdgeReferences)
        {
            if (reference == null || reference.Revision != modelRevision || reference.EdgeKey <= 0 ||
                reference.FaceId < 0 || !double.IsFinite(reference.FirstParameter) ||
                !double.IsFinite(reference.LastParameter) || reference.LastParameter <= reference.FirstParameter ||
                reference.StartIndex < 0 || reference.EndIndex <= reference.StartIndex || reference.EndIndex >= pointCount)
            {
                error = "인출 경계의 원본 edge 참조/trim 범위가 유효하지 않거나 오래되었습니다";
                return false;
            }
            for (int segment = reference.StartIndex; segment < reference.EndIndex; segment++)
            {
                if (covered[segment] || candidate.SegmentFaceIds[segment] != reference.FaceId)
                {
                    error = "인출 경계의 원본 edge 참조가 겹치거나 구간 면 정보와 일치하지 않습니다";
                    return false;
                }
                covered[segment] = true;
            }
        }
        if (covered.Any(value => !value))
        {
            error = "원본 CAD edge에 연결되지 않은 인출 경계 구간이 있습니다 — 선택을 적용하지 않았습니다";
            return false;
        }
        return true;
    }

    /// <summary>UI와 자동 선택은 현재 목록의 DTO identity 및 저장된 원본 parameter를 적용한다.</summary>
    private bool ApplyAnchorCandidate(AnchoredCandidate candidate, out string error)
    {
        var area = _activeArea;
        if (!TrySelectAnchorCandidate(area, candidate, out error)) return false;
        SyncExitFaceUi(area);
        UpdateActionStates();
        StatusText.Text = string.Format("시작 ({0:F1}, {1:F1}) → 종료 ({2:F1}, {3:F1}) · 각도 {4:F0}° — 반대쪽 끝은 레인 위상으로 결정됩니다",
            candidate.Start.X, candidate.Start.Y, candidate.End.X, candidate.End.Y, candidate.AngleDeg);
        return true;
    }

    private static bool TrySelectAnchorCandidate(WorkingArea area, AnchoredCandidate candidate, out string error)
    {
        error = null;
        if (area == null || !area.HasExitFace)
        {
            error = "활성 작업영역의 인출면이 지정되지 않았습니다";
            return false;
        }
        if (candidate == null || string.IsNullOrEmpty(candidate.CandidateId) ||
            !area.AnchorCandidates.Any(current => ReferenceEquals(current, candidate)) ||
            area.AnchorCandidates.Count(current => string.Equals(current.CandidateId,
                candidate.CandidateId, StringComparison.Ordinal)) != 1)
        {
            error = "선택한 시작/종료점 후보 identity가 현재 목록에 없거나 중복되었습니다 — 다시 선택하세요";
            return false;
        }
        if (!candidate.StartBinding.HasValue || !candidate.EndBinding.HasValue)
        {
            error = "선택한 후보의 원본 edge binding이 없습니다 — 인출면을 다시 선택하세요";
            return false;
        }
        if (!double.IsFinite(candidate.Start.X) || !double.IsFinite(candidate.Start.Y) ||
            !double.IsFinite(candidate.End.X) || !double.IsFinite(candidate.End.Y))
        {
            error = "선택한 후보의 작업 평면 좌표가 유효하지 않습니다";
            return false;
        }
        ExitBoundaryResolution start = area.ExitBoundary.Evaluate(candidate.StartBinding.Value);
        ExitBoundaryResolution end = area.ExitBoundary.Evaluate(candidate.EndBinding.Value);
        if (!start.Succeeded || start.Failure.HasValue || !end.Succeeded || end.Failure.HasValue)
        {
            error = ExplainExitBoundaryFailure(!start.Succeeded || start.Failure.HasValue ? start.Failure : end.Failure);
            return false;
        }
        if (!double.IsFinite(start.Point.Value.Point.X) || !double.IsFinite(start.Point.Value.Point.Y) ||
            !double.IsFinite(end.Point.Value.Point.X) || !double.IsFinite(end.Point.Value.Point.Y) ||
            start.Point.Value.Binding != candidate.StartBinding || end.Point.Value.Binding != candidate.EndBinding ||
            start.Point.Value.Point.DistanceTo(candidate.Start) > ExitBoundaryConstraint.Tolerance ||
            end.Point.Value.Point.DistanceTo(candidate.End) > ExitBoundaryConstraint.Tolerance)
        {
            error = "선택한 후보의 원본 parameter와 작업 평면 좌표가 일치하지 않습니다";
            return false;
        }
        ExcludedAnchorCandidate plan = null;
        if (area.Exclusions.Count > 0)
        {
            var plans = area.ExcludedCandidates.Where(current => string.Equals(current.CandidateId,
                candidate.CandidateId, StringComparison.Ordinal)).ToList();
            if (plans.Count != 1 || plans[0].StartBinding != candidate.StartBinding ||
                plans[0].EndBinding != candidate.EndBinding)
            {
                error = "선택한 후보 identity에 대응하는 제외영역 경로 계획이 유일하지 않습니다";
                return false;
            }
            plan = plans[0];
        }
        area.SelectedAnchorCandidate = candidate;
        area.StartAnchor = candidate.Start;
        area.EndAnchor = candidate.End;
        area.LastExitFailure = null;
        area.ExcludedPlan = plan;
        return true;
    }

    private void ClearExitFace_Click(object sender, RoutedEventArgs e)
    {
        var area = _activeArea;
        if (area == null) return;
        if (!ConfirmDiscardRouteEdits(area, "인출점 초기화")) return;
        ExitVertexEdit();
        CancelExitFacePick();
        area.SelectedStartExit = null;
        area.SelectedEndExit = null;
        area.SelectedAnchorCandidate = null;
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.AnchorCandidates.Clear();
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        area.LastExitFailure = null;
        // 새 방식에서는 적용영역 외곽 경계를 유지하고 선택점만 지운다.
        // 예전 인출면 방식으로 만든 영역이면 기존 필드까지 초기화한다.
        if (area.RegionExitBoundary == null)
        {
            area.ExitFaceId = -1;
            area.ExitFaceRevision = 0;
            area.ExitChain = new List<Vec2>();
            area.ExitChain3D = new List<Vec3>();
            area.ExitChainFaceIds = new List<int>();
            area.ExitEdgeRefs = new List<ExitEdgeReference>();
            area.ExitBoundary = null;
        }
        SyncExitFaceUi(area);
        UpdateActionStates();
        StatusText.Text = "인출점을 초기화했습니다 — 패턴은 다시 생성해야 합니다";
    }

    /// <summary>현재 마진/간격/방향에서 인출면 위에 놓을 수 있는 시작·종료점 쌍을 다시 계산한다.</summary>
    private void RecomputeAnchorCandidates(WorkingArea area)
    {
        if (area == null) return;
        area.AnchorCandidates.Clear();
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.SelectedAnchorCandidate = null;
        area.LastExitFailure = null;
        if (!area.HasExitFace || area.Region.Count != 1) return;
        if (!TryGetParams(out double margin, out double spacing, out string parameterError))
        {
            area.LastExitFailure = parameterError;
            return;
        }
        var parameters = new PatternParams { Margin = margin, Spacing = spacing, Direction = area.Direction };
        if (area.Exclusions.Count > 0)
        {
            ExcludedCandidatesResult excluded;
            try
            {
                excluded = Serpentine.ComputeCandidatesComponents(area.Region[0], area.Exclusions,
                    parameters, area.ExitChain, area.Direction, boundary: area.ExitBoundary);
            }
            catch (Exception ex)
            {
                area.LastExitFailure = "시작/종료점 후보 계산 실패: " + ex.Message;
                ErrorReporter.ShowWarning("시작/종료점 후보 계산 실패", ex);
                return;
            }

            area.ExcludedCandidates = excluded.Candidates.ToList();
            area.ExcludedCandidateFailure = excluded.Failure;
            area.ExcludedPlanKey = BuildExcludedPlanKey(area, margin, spacing);
            foreach (var candidate in excluded.Candidates)
                area.AnchorCandidates.Add(candidate.ToAnchoredCandidate());
            if (excluded.Candidates.Count == 0)
            {
                area.LastExitFailure = ExplainComponentsFailure(excluded.Failure, excluded.BoundaryFailure);
                return;
            }
            if (excluded.Candidates.Count == 1)
            {
                if (!TrySelectAnchorCandidate(area, area.AnchorCandidates[0], out string selectionFailure))
                    area.LastExitFailure = selectionFailure;
            }
            return;
        }

        IReadOnlyList<AnchoredCandidate> candidates;
        try
        {
            candidates = Serpentine.ComputeCandidates(area.Region[0], null,
                parameters, area.ExitChain, area.Direction, boundary: area.ExitBoundary);
        }
        catch (Exception ex)
        {
            area.LastExitFailure = "시작/종료점 후보 계산 실패: " + ex.Message;
            ErrorReporter.ShowWarning("시작/종료점 후보 계산 실패", ex);
            return;
        }

        area.AnchorCandidates.AddRange(candidates);
        if (candidates.Count == 0)
        {
            area.LastExitFailure = ExplainNoCandidate(area, margin, spacing);
            return;
        }
        // 후보가 한 쌍뿐이면 그 쌍이 유일해다. 여러 쌍이면 사용자가 패턴 탭의 후보 목록에서 고른다.
        if (candidates.Count == 1)
        {
            if (!TrySelectAnchorCandidate(area, candidates[0], out string selectionFailure))
                area.LastExitFailure = selectionFailure;
        }
    }

    /// <summary>제외영역 경로 계획을 만든 입력이 그대로인지 비교할 지문.</summary>
    private string BuildExcludedPlanKey(WorkingArea area, double margin, double spacing)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(area.ExitFaceId).Append('|').Append(area.ExitFaceRevision).Append('|');
        sb.Append((int)area.Direction).Append('|');
        sb.Append(margin.ToString("R", inv)).Append('|').Append(spacing.ToString("R", inv)).Append('|');
        sb.Append(Bridge.GetModelRevision()).Append('|');
        var side = _workingSide?.Confirmed;
        sb.Append(side?.Id.Value ?? -1).Append('|').Append(side?.Revision.Value ?? -1).Append('|');
        foreach (var e in area.Exclusions)
            sb.Append(e.MinX.ToString("R", inv)).Append(',')
              .Append(e.MinY.ToString("R", inv)).Append(',')
              .Append(e.MaxX.ToString("R", inv)).Append(',')
              .Append(e.MaxY.ToString("R", inv)).Append(';');
        sb.Append('|');
        foreach (var point in area.ExitChain)
            sb.Append(point.X.ToString("R", inv)).Append(',').Append(point.Y.ToString("R", inv)).Append(';');
        sb.Append("|ER:");
        AppendExitReferences(sb, area.ExitEdgeRefs, inv);
        sb.Append("|R:");
        foreach (var ring in area.Region)
        {
            sb.Append('[');
            foreach (Vec2 point in ring)
                sb.Append(point.X.ToString("R", inv)).Append(',').Append(point.Y.ToString("R", inv)).Append(';');
            sb.Append(']');
        }
        sb.Append("|EBP:cad-original-edge-v2,").Append(ExitBoundaryConstraint.Tolerance.ToString("R", inv))
          .Append(',').Append(ExitBoundaryConstraint.AcceptanceTolerance.ToString("R", inv))
          .Append(',').Append(GeometryTolerancePolicy.Version);
        return sb.ToString();
    }

    /// <summary>제외영역 후보 계산이 실패한 이유를 사용자 문구로 바꾼다.</summary>
    private static string ExplainComponentsFailure(SerpentineComponentsFailureReason? failure,
        ExitBoundaryFailureReason? boundaryFailure = null) => failure switch
    {
        SerpentineComponentsFailureReason.InvalidInput =>
            "인출면/영역 입력이 올바르지 않습니다",
        SerpentineComponentsFailureReason.NoLanes =>
            "마진/간격 조건에서 생성되는 레인이 없습니다 — 간격을 줄이거나 영역을 넓히세요",
        SerpentineComponentsFailureReason.SplitByExclusion =>
            "제외영역이 작업영역을 완전히 가로질러 발열영역이 분리되었습니다 — 제외영역을 줄이거나 방향을 바꾸세요",
        SerpentineComponentsFailureReason.TooComplex =>
            "제외영역으로 레인 조각 조합이 너무 많아 경로 탐색 계산 한도를 넘었습니다 — 제외영역을 줄이거나 간격을 조정하세요",
        SerpentineComponentsFailureReason.NoCompletePath =>
            "제외영역 배치로 모든 레인을 한 번씩 잇는 경로가 없습니다 — 제외영역을 줄이거나 방향을 바꾸세요",
        SerpentineComponentsFailureReason.ExitBoundaryConflict => ExplainExitBoundaryFailure(boundaryFailure),
        _ => "인출면이 제외영역을 고려한 사행 경로의 양 끝을 지나지 않습니다 — 반대쪽 모서리나 다른 인출면을 선택하세요"
    };

    /// <summary>현재 입력으로 제외영역 경로 계획을 확보한다. 마진/간격이 바뀌었으면 후보를 다시 계산한다.</summary>
    private ExcludedAnchorCandidate TryPrepareExcludedPlan(WorkingArea area, out string failure)
    {
        failure = null;
        if (area == null || !area.HasExitFace)
        {
            failure = area?.LastExitFailure ?? "인출면이 지정되지 않았습니다";
            return null;
        }

        if (!TryGetParams(out double margin, out double spacing, out string parameterError))
        {
            failure = parameterError;
            return null;
        }
        string key = BuildExcludedPlanKey(area, margin, spacing);
        if (!string.Equals(area.ExcludedPlanKey, key, StringComparison.Ordinal))
            RecomputeAnchorCandidates(area);

        if (area.ExcludedCandidates.Count == 0)
        {
            failure = area.LastExitFailure ?? ExplainComponentsFailure(area.ExcludedCandidateFailure);
            return null;
        }
        if (!area.HasExitAnchors)
        {
            failure = "후보 목록에서 시작/종료점 쌍을 선택하세요";
            return null;
        }

        string candidateId = area.SelectedAnchorCandidate?.CandidateId;
        var plans = area.ExcludedCandidates.Where(candidate => !string.IsNullOrEmpty(candidateId) &&
            string.Equals(candidate.CandidateId, candidateId, StringComparison.Ordinal)).ToList();
        if (plans.Count != 1)
        {
            failure = "선택한 후보 identity에 대응하는 제외영역 경로 계획이 유일하지 않습니다 — 다시 선택하세요";
            return null;
        }
        var plan = plans[0];
        if (plan.StartBinding != area.SelectedAnchorCandidate.StartBinding ||
            plan.EndBinding != area.SelectedAnchorCandidate.EndBinding)
        {
            failure = "선택한 후보와 제외영역 경로 계획의 원본 edge binding이 일치하지 않습니다";
            return null;
        }
        area.ExcludedPlan = plan;
        return plan;
    }

    private string ExplainNoCandidate(WorkingArea area, double margin, double spacing)
    {
        try
        {
            var plain = Serpentine.Generate(area.Region[0], null,
                new PatternParams { Margin = margin, Spacing = spacing, Direction = area.Direction });
            // 짝수 레인은 시스템이 자동으로 맞추므로, 여기서 남는 사유는 "2레인조차 확보 불가" 또는 "인출면이 양 끝을 지나지 않음"뿐이다.
            if (plain.LaneCount < 2)
                return string.Format(
                    "간격 {0:F1}mm로는 레인 2개도 확보되지 않습니다 — 간격을 줄이거나 영역을 넓히세요",
                    spacing);
            return "인출면이 사행 경로의 양 끝을 지나지 않습니다 — 반대쪽 모서리나 다른 인출면을 선택하세요";
        }
        catch (Exception ex)
        {
            ErrorReporter.ShowWarning("시작/종료점 후보 없음 — 진단 중 오류", ex);
            return "시작/종료점 후보 없음: " + ex.Message;
        }
    }

    private static string LaneNote(SerpentineResult pattern) =>
        pattern != null && pattern.LaneCountAdjusted
            ? string.Format(" · 같은 면 인출을 위해 레인 {0}→{1}개로 조정",
                pattern.NominalLaneCount, pattern.LaneCount)
            : string.Empty;

    private static string ExplainExitBoundaryFailure(ExitBoundaryFailureReason? failure) => failure switch
    {
        ExitBoundaryFailureReason.StaleContext =>
            "모델 또는 작업 방향이 변경되어 인출 경계 참조가 오래되었습니다 — 인출면을 다시 선택하세요",
        ExitBoundaryFailureReason.MissingReference =>
            "인출 경계의 원본 CAD edge 참조가 없습니다 — 인출면을 다시 선택하세요",
        ExitBoundaryFailureReason.MultipleIntersections =>
            "같은 작업 평면 위치에 여러 원본 인출점이 대응하여 유일하게 결정할 수 없습니다",
        ExitBoundaryFailureReason.TangentOrOverlap =>
            "경로 연장선과 인출 경계가 접하거나 겹쳐 인출점을 유일하게 결정할 수 없습니다",
        ExitBoundaryFailureReason.DegenerateSegment =>
            "인출 경계의 작업 평면 투영 또는 경로 끝 구간이 퇴화했습니다",
        ExitBoundaryFailureReason.NoIntersection =>
            "경로 끝의 바깥 연장선이 원본 인출 경계와 만나지 않습니다",
        ExitBoundaryFailureReason.InvalidInput =>
            "인출 경계의 원본 parameter 또는 작업 평면 입력이 올바르지 않습니다",
        _ => "원본 CAD 인출 경계를 평가하지 못했습니다 — 기존 결과를 유지합니다"
    };

    private string ExplainAnchoredFailure(AnchoredFailureReason? failure,
        ExitBoundaryFailureReason? boundaryFailure = null) => failure switch
    {
        AnchoredFailureReason.ExitChainNotSpanning =>
            "인출면이 사행 경로의 양 끝을 지나지 않습니다 — 반대쪽 모서리나 다른 인출면을 선택하세요",
        AnchoredFailureReason.ParityConflict =>
            "레인을 2개 이상 확보하면서 양 끝을 같은 인출면에 놓을 수 없습니다 — 간격을 줄이거나 인출면 방향을 바꾸세요",
        AnchoredFailureReason.AnchorNotOnExitChain =>
            "선택한 시작/종료점이 인출면 위에 있지 않습니다 — 후보 목록에서 다시 선택하세요",
        AnchoredFailureReason.AnchorNotAtLaneEnd =>
            "시작 인출점이 마진 안쪽 사행 구간의 진행축 끝에 있지 않습니다 — 영역 외곽선에서 레인 방향 끝점을 선택하세요",
        AnchoredFailureReason.RegionSplit =>
            "적용영역이 분리되어 하나의 사행 경로로 연결할 수 없습니다",
        AnchoredFailureReason.NoLanes =>
            "시작 인출점 위치와 마진/간격 조건에서 생성되는 사행 레인이 없습니다 — 시작점이나 간격을 조정하세요",
        AnchoredFailureReason.NoCompletePath =>
            "선택한 인출점과 제외영역 조건으로 모든 레인을 잇는 완전 경로를 만들 수 없습니다 — 제외영역을 줄이거나 간격/방향을 조정하세요",
        AnchoredFailureReason.IndeterminateDirection =>
            "시작점과 종료점이 배치 축에서 같은 위치입니다 — 가로 배치는 두 점의 높이를, 세로 배치는 두 점의 좌우 위치를 다르게 선택하세요",
        AnchoredFailureReason.RequiredExtraLaneOutsideRegion =>
            "마지막 레인이 종료점 반대편에서 끝나 같은 간격으로 레인을 한 줄 더 만들어야 하지만, 그 위치에 발열영역 구간이 없습니다 — 기존 결과를 유지합니다",
        AnchoredFailureReason.ExitBoundaryConflict => ExplainExitBoundaryFailure(boundaryFailure),
        AnchoredFailureReason.InvalidInput =>
            "선택한 인출점 또는 영역 입력이 올바르지 않습니다",
        _ => "선택한 인출점 기준 경로를 생성할 수 없습니다"
    };
}
