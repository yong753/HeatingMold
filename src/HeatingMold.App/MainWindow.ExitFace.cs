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

    /// <summary>패턴 탭 후보 목록의 표시 행. 후보 목록이 재계산돼도 선택값이 어긋나지 않도록 좌표를 함께 보관한다.</summary>
    private sealed record AnchorCandidateRow(int Index, string Label, Vec2 Start, Vec2 End, double AngleDeg);
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
                    c.Start, c.End, c.AngleDeg));
            }
            if (!area.StartAnchor.HasValue || !area.EndAnchor.HasValue) return;
            foreach (AnchorCandidateRow row in AnchorCandidateList.Items)
            {
                if (Dist(row.Start, area.StartAnchor.Value) < 1e-6 &&
                    Dist(row.End, area.EndAnchor.Value) < 1e-6)
                {
                    AnchorCandidateList.SelectedItem = row;
                    break;
                }
            }
        }
        finally { _suppressAnchorSelection = false; }
    }

    private void AnchorCandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAnchorSelection) return;
        var area = _activeArea;
        if (area == null || AnchorCandidateList?.SelectedItem is not AnchorCandidateRow row) return;
        area.StartAnchor = row.Start;
        area.EndAnchor = row.End;
        area.LastExitFailure = null;
        area.ExcludedPlan = area.Exclusions.Count > 0
            ? area.ExcludedCandidates.FirstOrDefault(c =>
                Dist(c.Start, row.Start) <= 1e-4 && Dist(c.End, row.End) <= 1e-4)
            : null;
        SyncExitFaceUi(area);
        UpdateActionStates();
        StatusText.Text = string.Format("시작 ({0:F1}, {1:F1}) → 종료 ({2:F1}, {3:F1}) · 각도 {4:F0}° — 반대쪽 끝은 레인 위상으로 결정됩니다",
            row.Start.X, row.Start.Y, row.End.X, row.End.Y, row.AngleDeg);
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
        // 인출 후보가 재계산되어 기존 앵커가 사라졌으므로 편집 상태도 함께 정리한다.
        ClearRouteEditState(area);
        SyncExitFaceUi(area);
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
        var sides = Bridge.GetBoundaryExitSides(SortedFaceIds(area), 0.05);
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
        StatusText.Text = string.Format("외곽선을 따라 표시된 인출 측면 후보 {0}개 — 후보 위로 마우스를 올리면 강조됩니다. 클릭하세요 (Esc 취소)",
            _exitCandidates.Count);
    }

    private void CancelExitFacePick()
    {
        if (Viewport != null) Viewport.ExitFacePickMode = false;
        Bridge.ClearExitCandidates();
        _exitCandidates.Clear();
        _exitCandidatePolylines = Array.Empty<double[]>();
        _exitCandidateHover = -1;
    }

    /// <summary>인출면 선택 중 커서 이동 — 커서 아래 후보만 강조한다.</summary>
    private void OnExitFacePickMoved(int x, int y)
    {
        if (_exitCandidatePolylines.Length == 0) return;
        int index = x < 0 || y < 0
            ? -1
            : Bridge.PickExitCandidate(_exitCandidatePolylines, x, y, 12.0);
        if (index == _exitCandidateHover) return;
        _exitCandidateHover = index;
        Bridge.HighlightExitCandidate(index);
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

        var chosen = _exitCandidates[index];
        int pointCount = chosen.Points.Length / 3;
        int[] segmentFaceIds = chosen.SegmentFaceIds;
        if (segmentFaceIds == null || segmentFaceIds.Length != Math.Max(0, pointCount - 1))
        {
            // 구간별 면 정보가 없거나 어긋나면 대표 면 id로 대체한다 (구(舊) 브리지 호환).
            segmentFaceIds = new int[Math.Max(0, pointCount - 1)];
            for (int k = 0; k < segmentFaceIds.Length; k++) segmentFaceIds[k] = chosen.FaceId;
        }

        List<Vec2> chain = new();
        List<Vec3> chain3D = new();
        List<int> chainFaceIds = new();
        int lastKept = 0;
        for (int k = 0; k < pointCount; k++)
        {
            var p3 = new Vec3(chosen.Points[k * 3], chosen.Points[k * 3 + 1], chosen.Points[k * 3 + 2]);
            var p = area.Frame.Project(p3);
            if (chain.Count == 0) { chain.Add(p); chain3D.Add(p3); continue; }
            if (Dist(chain[^1], p) <= 1e-6) continue;
            // 중복점 제거로 압축되는 구간의 면 id가 서로 다르면 라벨이 모호하므로 선택을 거부한다.
            int segment = segmentFaceIds[k - 1];
            bool consistent = true;
            for (int t = lastKept; t < k; t++)
                if (segmentFaceIds[t] != segment) { consistent = false; break; }
            if (!consistent)
            {
                CancelExitFacePick();
                StatusText.Text = "선택한 인출 측면의 구간 면 정보가 일관되지 않습니다 — 다른 구간을 선택하세요";
                return;
            }
            chain.Add(p);
            chain3D.Add(p3);
            chainFaceIds.Add(segment);
            lastKept = k;
        }
        double length = 0;
        for (int i = 1; i < chain.Count; i++) length += Dist(chain[i - 1], chain[i]);

        CancelExitFacePick();
        if (chain.Count < 2)
        {
            StatusText.Text = "선택한 인출 측면의 경계선을 투영하지 못했습니다 — 다른 구간을 선택하세요";
            return;
        }

        area.ExitFaceId = chosen.FaceId;
        area.ExitFaceRevision = Bridge.GetModelRevision();
        area.ExitChain = chain;
        area.ExitChain3D = chain3D;
        area.ExitChainFaceIds = chainFaceIds;
        RecomputeAnchorCandidates(area);
        // 인출면이 실제로 바뀐 뒤에만 편집 상태를 지운다(피킹 취소 시에는 보존).
        ClearRouteEditState(area);
        SyncExitFaceUi(area);

        if (area.HasExitAnchors)
            StatusText.Text = string.Format("{0} 영역 인출면 Face {1} · 길이 {2:F1}mm — 시작/종료점 후보가 목록에 표시됩니다",
                area.Name, chosen.FaceId, length);
        else
            StatusText.Text = area.LastExitFailure ?? "인출면은 지정됐지만 사용 가능한 시작/종료점 후보가 없습니다";
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
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.AnchorCandidates.Clear();
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        area.LastExitFailure = null;
        ClearRouteEditState(area);
        SyncExitFaceUi(area);
        StatusText.Text = "인출면 지정을 초기화했습니다 — 패턴은 다시 생성해야 합니다";
    }

    /// <summary>현재 마진/간격/방향에서 인출면 위에 놓을 수 있는 시작·종료점 쌍을 다시 계산한다.</summary>
    private void RecomputeAnchorCandidates(WorkingArea area)
    {
        area.AnchorCandidates.Clear();
        area.ExcludedCandidates.Clear();
        area.ExcludedPlan = null;
        area.ExcludedCandidateFailure = null;
        area.ExcludedPlanKey = "";
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.LastExitFailure = null;
        if (area == null || !area.HasExitFace || area.Region.Count != 1) return;

        TryGetParams(out double margin, out double spacing);
        var parameters = new PatternParams { Margin = margin, Spacing = spacing, Direction = area.Direction };
        if (area.Exclusions.Count > 0)
        {
            ExcludedCandidatesResult excluded;
            try
            {
                excluded = Serpentine.ComputeCandidatesComponents(area.Region[0], area.Exclusions,
                    parameters, area.ExitChain, area.Direction);
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
                area.AnchorCandidates.Add(new AnchoredCandidate(candidate.Start, candidate.End, candidate.AngleDeg));
            if (excluded.Candidates.Count == 0)
            {
                area.LastExitFailure = ExplainComponentsFailure(excluded.Failure);
                return;
            }
            if (excluded.Candidates.Count == 1)
            {
                area.StartAnchor = excluded.Candidates[0].Start;
                area.EndAnchor = excluded.Candidates[0].End;
                area.ExcludedPlan = excluded.Candidates[0];
            }
            return;
        }

        IReadOnlyList<AnchoredCandidate> candidates;
        try
        {
            candidates = Serpentine.ComputeCandidates(area.Region[0], null,
                parameters, area.ExitChain, area.Direction);
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
            area.StartAnchor = candidates[0].Start;
            area.EndAnchor = candidates[0].End;
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
        return sb.ToString();
    }

    /// <summary>제외영역 후보 계산이 실패한 이유를 사용자 문구로 바꾼다.</summary>
    private static string ExplainComponentsFailure(SerpentineComponentsFailureReason? failure) => failure switch
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

        TryGetParams(out double margin, out double spacing);
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

        var plan = area.ExcludedPlan;
        if (plan == null && area.StartAnchor.HasValue && area.EndAnchor.HasValue)
        {
            plan = area.ExcludedCandidates.FirstOrDefault(c =>
                Dist(c.Start, area.StartAnchor.Value) <= 1e-4 &&
                Dist(c.End, area.EndAnchor.Value) <= 1e-4);
            area.ExcludedPlan = plan;
        }
        if (plan == null)
        {
            failure = "후보 목록에서 시작/종료점 쌍을 선택하세요";
            return null;
        }
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

    private string ExplainAnchoredFailure(AnchoredFailureReason? failure) => failure switch
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
        _ => "인출면 기준 경로를 생성할 수 없습니다"
    };

    /// <summary>회로별 인출 리드(ExitPiece) 길이를 합산해 접속 길이로 기록한다.</summary>
    private void UpdateExitLeadLengths(WorkingArea area)
    {
        if (area?.Pattern == null)
        {
            if (area != null) area.LeadLengths = Array.Empty<double>();
            return;
        }
        var groups = CircuitPieceGroups(area);
        var leads = new double[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            double lead = 0;
            for (int id = 0; id < area.Pattern.RoutePieces.Count; id++)
            {
                if (area.Pattern.RoutePieces[id].Kind != SerpentineRoutePieceKind.ExitPiece) continue;
                int native = id < area.NativePieceIds.Length ? area.NativePieceIds[id] : -1;
                if (native < 0 || !groups[i].Contains(native)) continue;
                lead += area.RoutePieceLengths.ElementAtOrDefault(id);
            }
            leads[i] = lead;
        }
        area.LeadLengths = leads;
        if (ReferenceEquals(area, _activeArea)) _leadLengths = leads;
    }
}
