using System;
using System.Collections.Generic;
using System.Linq;
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
    }

    private string DescribeExitState(WorkingArea area)
    {
        if (area == null) return "작업영역을 먼저 선택하세요";
        if (!string.IsNullOrEmpty(area.LastExitFailure)) return area.LastExitFailure;
        if (!area.HasExitFace) return "인출면 미지정 — 인출면 선택 후 외곽선에 강조된 인출 측면을 클릭하세요";
        if (!area.HasExitAnchors)
            return area.AnchorCandidates.Count == 0
                ? "인출면은 지정됐지만 사행 경로 양 끝이 이 면에 닿지 않습니다"
                : "2D 미리보기에서 시작점을 클릭하세요";
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

        area.Direction = direction;
        if (area.HasExitFace) RecomputeAnchorCandidates(area);
        DrawRegionCanvas();
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

        Bridge.ShowExitCandidates(_exitCandidates.Select(c => c.Points).ToArray());
        Viewport.ExitFacePickMode = true;
        StatusText.Text = string.Format("외곽선에 강조된 인출 측면 후보 {0}개 — 하나를 클릭하세요 (Esc 취소)",
            _exitCandidates.Count);
    }

    private void CancelExitFacePick()
    {
        if (Viewport != null) Viewport.ExitFacePickMode = false;
        Bridge.ClearExitCandidates();
        _exitCandidates.Clear();
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
        int index = Bridge.PickExitCandidate(_exitCandidates.Select(c => c.Points).ToArray(), x, y, 12.0);
        if (index < 0)
        {
            StatusText.Text = "강조된 외곽선 구간을 클릭하세요 — Esc로 취소할 수 있습니다";
            return; // 선택 모드 유지
        }

        var chosen = _exitCandidates[index];
        List<Vec2> chain = new();
        List<Vec3> chain3D = new();
        for (int i = 0; i + 2 < chosen.Points.Length; i += 3)
        {
            var p3 = new Vec3(chosen.Points[i], chosen.Points[i + 1], chosen.Points[i + 2]);
            var p = area.Frame.Project(p3);
            if (chain.Count == 0 || Dist(chain[^1], p) > 1e-6) { chain.Add(p); chain3D.Add(p3); }
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
        RecomputeAnchorCandidates(area);
        DrawRegionCanvas();
        SyncExitFaceUi(area);

        if (area.HasExitAnchors)
            StatusText.Text = string.Format("{0} 영역 인출면 Face {1} · 길이 {2:F1}mm — 시작/종료점이 2D 미리보기에 표시됩니다",
                area.Name, chosen.FaceId, length);
        else
            StatusText.Text = area.LastExitFailure ?? "인출면은 지정됐지만 사용 가능한 시작/종료점 후보가 없습니다";
    }

    private void ClearExitFace_Click(object sender, RoutedEventArgs e)
    {
        var area = _activeArea;
        if (area == null) return;
        CancelExitFacePick();
        area.ExitFaceId = -1;
        area.ExitFaceRevision = 0;
        area.ExitChain = new List<Vec2>();
        area.ExitChain3D = new List<Vec3>();
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.AnchorCandidates.Clear();
        area.LastExitFailure = null;
        DrawRegionCanvas();
        SyncExitFaceUi(area);
        StatusText.Text = "인출면 지정을 초기화했습니다 — 패턴은 다시 생성해야 합니다";
    }

    /// <summary>현재 마진/간격/방향에서 인출면 위에 놓을 수 있는 시작·종료점 쌍을 다시 계산한다.</summary>
    private void RecomputeAnchorCandidates(WorkingArea area)
    {
        area.AnchorCandidates.Clear();
        area.StartAnchor = null;
        area.EndAnchor = null;
        area.LastExitFailure = null;
        if (area == null || !area.HasExitFace || area.Region.Count != 1) return;

        TryGetParams(out double margin, out double spacing);
        IReadOnlyList<AnchoredCandidate> candidates;
        try
        {
            candidates = Serpentine.ComputeCandidates(area.Region[0], null,
                new PatternParams { Margin = margin, Spacing = spacing, Direction = area.Direction },
                area.ExitChain, area.Direction);
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
        // 후보가 한 쌍뿐이면 그 쌍이 유일해다. 여러 쌍이면 사용자가 2D 미리보기에서 고른다.
        if (candidates.Count == 1)
        {
            area.StartAnchor = candidates[0].Start;
            area.EndAnchor = candidates[0].End;
        }
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

    private void RegionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var area = _activeArea;
        if (area == null || area.AnchorCandidates.Count == 0 || RegionCanvas == null) return;
        Vec2 p = CanvasToWorld(e.GetPosition(RegionCanvas));

        int best = -1;
        double bestDistance = 5.0;  // mm — 화면 클릭 허용 오차
        for (int i = 0; i < area.AnchorCandidates.Count; i++)
        {
            var c = area.AnchorCandidates[i];
            double d = Math.Min(Dist(c.Start, p), Dist(c.End, p));
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        if (best < 0)
        {
            StatusText.Text = "시작점 표시 근처를 클릭하세요";
            return;
        }

        var chosen = area.AnchorCandidates[best];
        area.StartAnchor = chosen.Start;
        area.EndAnchor = chosen.End;
        area.LastExitFailure = null;
        DrawRegionCanvas();
        SyncExitFaceUi(area);
        StatusText.Text = string.Format("시작 ({0:F1}, {1:F1}) → 종료 ({2:F1}, {3:F1}) · 각도 {4:F0}° — 반대쪽 끝은 레인 위상으로 결정됩니다",
            chosen.Start.X, chosen.Start.Y, chosen.End.X, chosen.End.Y, chosen.AngleDeg);
        e.Handled = true;
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
            "선택한 시작/종료점이 인출면 위에 있지 않습니다 — 2D 미리보기에서 다시 선택하세요",
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

    /// <summary>2D 미리보기 캔버스 좌표를 영역 평면 좌표로 되돌린다 (WorldToCanvas의 역변환).</summary>
    private Vec2 CanvasToWorld(Point p)
    {
        var b = RegionBounds();
        double sx = Math.Max(1e-9, b.MaxX - b.MinX), sy = Math.Max(1e-9, b.MaxY - b.MinY);
        double scale = Math.Min((RegionCanvas.ActualWidth - 16) / sx, (RegionCanvas.ActualHeight - 16) / sy);
        if (scale <= 0 || !double.IsFinite(scale)) return new Vec2(b.MinX, b.MinY);
        double originX = (RegionCanvas.ActualWidth - sx * scale) / 2;
        double originY = (RegionCanvas.ActualHeight - sy * scale) / 2;
        return new Vec2(b.MinX + (p.X - originX) / scale, b.MaxY - (p.Y - originY) / scale);
    }
}
