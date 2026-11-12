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

    /// <summary>현재 인출점 픽 역할. 0=시작, 1=종료, -1=없음.</summary>
    private int _exitPointPickRole = -1;
    /// <summary>인출점 픽 중 작업 정면으로 전환하기 전의 카메라 스냅샷.</summary>
    private WorkingPresentation _exitPointPresentation;
    private bool _exitPointPresentationHeld;
    /// <summary>커서 픽 허용 거리(mm, 작업 평면). 정확 CAD 판정 tolerance와 별개다.</summary>
    private const double ExitPointPickTolerance = 5.0;

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
        // 선택 인출점 방식은 적용영역 전체 경계를 보여 준다.
        if (area.RegionExitBoundary is SplitRegionBoundary2D split &&
            split.Segments.Count > 0 && area.FaceIdSet.Count > 0)
            source = BuildSplitOutline3D(area, split);
        else if (area.RegionExitBoundary is EffectiveRegionBoundary2D effective &&
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
        // 방향은 몸통 경로만 바꾼다. 사용자가 고른 두 인출점은 유지한다.
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
        ExitExclusionSketch();
        Viewport.RectangleSelectionMode = false;
        Viewport.CircuitHoverMode = false;
        Bridge.ClearHover();
        CancelExitFacePick();

        var side = _workingSide?.Confirmed;
        if (side == null) { StatusText.Text = "작업면이 확정되지 않았습니다"; return; }

        _exitPointPickRole = role;

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

    private void CancelExitFacePick()
    {
        _exitPointPickRole = -1;
        if (Viewport != null)
        {
            Viewport.ExitFacePickMode = false;
            Viewport.ViewLocked = false;
        }
        Bridge.HighlightExitFace(-1);
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
        if (_exitPointPickRole >= 0) OnExitPointPickMoved(x, y);
    }

    private void OnExitFacePicked(int x, int y)
    {
        if (_exitPointPickRole >= 0) OnExitPointPicked(x, y);
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
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.LastExitFailure = null;
        SyncExitFaceUi(area);
        UpdateActionStates();
        StatusText.Text = "인출점을 초기화했습니다 — 패턴은 다시 생성해야 합니다";
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
