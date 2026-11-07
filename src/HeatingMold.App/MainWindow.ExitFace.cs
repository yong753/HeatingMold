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
            DirectionBox.SelectedIndex = area == null ? 0 : (int)area.Direction;
            DirectionBox.IsEnabled = area != null;
            _suppressDirectionChange = false;
        }
        if (PickExitFaceButton != null)
        {
            PickExitFaceButton.IsEnabled = area != null && area.Region.Count == 1;
            PickExitFaceButton.Content = area != null && area.HasExitFace ? "인출면 다시 선택" : "인출면 선택";
        }
        if (ClearExitFaceButton != null) ClearExitFaceButton.IsEnabled = area != null && area.HasExitFace;
        if (ExitFaceStatusText != null) ExitFaceStatusText.Text = DescribeExitState(area);
        SyncAnchorCandidateList(area);
    }

    /// <summary>
    /// 활성 영역의 인출 경계 edge를 영역 원색(무광)의 폴리라인으로 계속 강조한다.
    /// 영역을 전환하면 이전 edge 강조는 교체되고, 인출면이 없으면 해제된다.
    /// </summary>
    private void ApplyActiveExitEdgeHighlight()
    {
        var area = _activeArea;
        if (area == null || !area.HasExitFace || area.ExitChain3D == null || area.ExitChain3D.Count < 2)
        {
            Bridge.ClearExitEdgeHighlight();
            return;
        }
        // 정본 체인은 native 점 인덱스를 그대로 보존한다. 렌더링 배열에서만 실제로
        // 같은 3D 점을 제거하며, 같은 XY/다른 깊이의 점은 합치지 않는다.
        var displayPoints = new List<Vec3>();
        foreach (Vec3 point in area.ExitChain3D)
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
        if (!string.IsNullOrEmpty(area.LastExitFailure)) return area.LastExitFailure;
        if (!area.HasExitFace) return "인출면 미지정 — 인출면 선택 후 외곽선에 강조된 인출 측면을 클릭하세요";
        if (!area.HasExitAnchors)
            return area.AnchorCandidates.Count == 0
                ? "인출면은 지정됐지만 사행 경로 양 끝이 이 면에 닿지 않습니다"
                : "후보 목록에서 시작/종료점 쌍을 선택하세요";
        Vec2 s = area.StartAnchor.Value, t = area.EndAnchor.Value;
        return string.Format("인출면 Face {0} · 시작 ({1:F1}, {2:F1}) → 종료 ({3:F1}, {4:F1})",
            area.ExitFaceId, s.X, s.Y, t.X, t.Y);
    }

    private void DirectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDirectionChange) return;
        var area = _activeArea;
        if (area == null) return;
        var direction = (PatternDirection)Math.Max(0, DirectionBox.SelectedIndex);
        if (area.Direction == direction) return;
        if (!ConfirmDiscardRouteEdits(area, "배치 방향 변경"))
        {
            // 취소하면 콤보 표시를 실제 영역 방향으로 되돌린다.
            _suppressDirectionChange = true;
            DirectionBox.SelectedIndex = (int)area.Direction;
            _suppressDirectionChange = false;
            return;
        }
        if (area.Direction == direction) return;

        ExitVertexEdit();
        area.Direction = direction;
        if (area.HasExitFace) RecomputeAnchorCandidates(area);
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
        _ => "자동 (장축)"
    };

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
        var sides = Bridge.GetBoundaryExitSides(SortedFaceIds(area), 0.05,
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
    }

    /// <summary>인출면 선택 중 커서 이동 — 커서 아래 후보 Edge와 후보 면을 강조한다.</summary>
    private void OnExitFacePickMoved(int x, int y)
    {
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

    /// <summary>
    /// 기존 좌표 호출자의 호환 경로. 같은 표시 좌표에 여러 원본 후보가 있으면 임의 선택하지 않는다.
    /// </summary>
    private bool ApplyAnchorCandidate(Vec2 start, Vec2 end, out string error)
    {
        error = null;
        var area = _activeArea;
        if (area == null)
        {
            error = "활성 작업영역이 없습니다";
            return false;
        }

        var matches = area.AnchorCandidates.Where(c =>
            Dist(c.Start, start) <= 1e-6 && Dist(c.End, end) <= 1e-6).ToList();
        if (matches.Count != 1)
        {
            error = matches.Count == 0
                ? "요청한 시작/종료점 후보가 현재 목록에 없습니다"
                : "같은 좌표에 원본 edge가 다른 후보가 여러 개 있습니다 — 후보 identity로 선택하세요";
            return false;
        }
        return ApplyAnchorCandidate(matches[0], out error);
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
        if (!ConfirmDiscardRouteEdits(area, "인출면 초기화")) return;
        ExitVertexEdit();
        CancelExitFacePick();
        area.ExitFaceId = -1;
        area.ExitFaceRevision = 0;
        area.ExitChain = new List<Vec2>();
        area.ExitChain3D = new List<Vec3>();
        area.ExitChainFaceIds = new List<int>();
        area.ExitEdgeRefs = new List<ExitEdgeReference>();
        area.ExitBoundary = null;
        area.SelectedAnchorCandidate = null;
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.AnchorCandidates.Clear();
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        area.LastExitFailure = null;
        SyncExitFaceUi(area);
        ApplyActiveExitEdgeHighlight();
        UpdateActionStates();
        StatusText.Text = "인출면 지정을 초기화했습니다 — 패턴은 다시 생성해야 합니다";
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
        sb.Append("|EBP:cad-original-edge-v1,").Append(ExitBoundaryConstraint.Tolerance.ToString("R", inv));
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
            "제외영역으로 레인 조각이 너무 많아(20개 초과) 경로를 계산할 수 없습니다 — 제외영역을 줄이거나 간격을 조정하세요",
        SerpentineComponentsFailureReason.TooManyFragments =>
            "레인 조각이 너무 많습니다(4000개 초과) — 제외영역을 줄이세요",
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
            "선택한 점이 사행 레인 끝점이 아닙니다 — 후보 위치 중에서 선택하세요",
        AnchoredFailureReason.RegionSplit =>
            "적용영역이 분리되어 하나의 사행 경로로 연결할 수 없습니다",
        AnchoredFailureReason.NoLanes =>
            "마진/간격 조건에서 생성되는 레인이 없습니다",
        AnchoredFailureReason.ExitBoundaryConflict => ExplainExitBoundaryFailure(boundaryFailure),
        _ => "인출면 기준 경로를 생성할 수 없습니다"
    };
}
