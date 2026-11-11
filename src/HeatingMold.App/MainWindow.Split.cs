using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 적용영역 자체를 여러 독립 영역으로 나눈다. 분할 결과는 영구적인 부모/자식 관계가 없는
/// 일반 작업영역이며, 영역마다 회로 하나를 생성한다. 분할은 다음 순서를 지킨다.
/// 1) 목표전력·허용치·마진/간격으로 필요한 영역 수를 계산한다.
/// 2) 예상 배치 길이가 균등해지도록 절단선을 정하고 자식 영역을 준비한다.
/// 3) 부모의 시작/종료 인출점이 있으면 그 측면을 따라 새 영역의 인출점을 자동으로 정한다.
///    (마음에 들지 않으면 기존 인출점 버튼으로 다시 선택할 수 있다.)
/// 4) 생성 후 실측 3D 길이 비율로 총 목표전력을 다시 배분한다.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 한 번의 분할이 만들 수 있는 영역 수 상한. 의미상 2분할 제한이 아니라 폭주 입력을
    /// 명시적으로 거부하기 위한 자원 상한이며, 초과하면 오류로 보고한다.
    /// </summary>
    private const int MaxSplitParts = 256;

    private sealed class SplitFamily
    {
        public string Key { get; init; } = "";
        public double TotalTargetPowerW { get; set; }
        public string SourceAreaName { get; init; } = "";
        public List<WorkingArea> Parts { get; } = new();
    }

    private readonly Dictionary<string, SplitFamily> _splitFamilies = new();
    private bool _applyingSplitAllocation;

    private void SplitArea_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SplitActiveArea();
        }
        catch (Exception ex)
        {
            SetSplitStatus("영역 분할 실패: " + ex.Message);
            ErrorReporter.ShowError("영역 분할 실패", ex);
        }
    }

    private void SplitActiveArea()
    {
        WorkingArea area = _activeArea;
        if (area == null)
        {
            SetSplitStatus("분할할 작업영역을 먼저 선택하세요.");
            return;
        }
        if (Viewport != null && Viewport.VertexEditMode)
        {
            SetSplitStatus("꼭지점 편집을 종료한 뒤 분할하세요.");
            return;
        }
        if (area.Region.Count != 1)
        {
            SetSplitStatus("적용영역이 단일 폐합 영역일 때만 분할할 수 있습니다.");
            return;
        }
        if (area.RegionCadBoundary == null)
        {
            SetSplitStatus("적용영역의 원본 CAD 외곽 참조가 없어 분할할 수 없습니다 — 영역을 다시 확정하세요.");
            return;
        }
        if (area.TargetPowerW is not double totalPower || !(totalPower > 0))
        {
            SetSplitStatus(area.TargetPowerError ?? "목표 발열전력을 먼저 입력해야 분할 수를 계산할 수 있습니다.");
            return;
        }
        if (!TryGetParams(out double margin, out double spacing, out string parameterError))
        {
            SetSplitStatus("마진/간격 입력 오류 — " + parameterError);
            return;
        }
        if (!TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string limitError))
        {
            SetSplitStatus("전기 한도 입력 오류 — " + limitError);
            return;
        }

        // 부모 예상 길이: 실제 생성과 같은 규칙(유효영역 ∩ 마진)으로 한 번만 추정한다.
        AreaSplitResult parentEstimate = AreaSplitter.Split(area.Region[0], area.Exclusions,
            area.Direction, margin, spacing, 1);
        if (!parentEstimate.Succeeded || parentEstimate.Bands.Count != 1)
        {
            SetSplitStatus("분할할 수 없습니다 — " + parentEstimate.Failure);
            return;
        }

        if (!TargetPowerSplitPlanner.TryPlan(totalPower, parentEstimate.Bands[0].EstimatedLengthMm,
                settings.Wire, settings.Limits, out TargetPowerSplitPlan plan, out string planError))
        {
            SetSplitStatus("분할 수를 계산할 수 없습니다 — " + planError);
            return;
        }
        if (plan.CurrentExceedsLimit)
        {
            SetSplitStatus("분할해도 해결되지 않습니다 — " + plan.Note);
            return;
        }
        if (plan.Parts <= 1)
        {
            SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
                "현재 한도 이내이므로 분할이 필요하지 않습니다 (예상 길이 {0:F1} mm, 예상 전류 {1:F2} A).",
                plan.TotalLengthMm, plan.PerCircuitCurrentA));
            return;
        }
        if (plan.Parts > MaxSplitParts)
        {
            SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
                "필요한 분할 수가 너무 큽니다 — {0}개. 목표 발열전력과 허용 전압/전류/전력을 확인하세요.", plan.Parts));
            return;
        }

        bool autoExits = SplitExitPointPlanner.HasAssignablePair(area);
        if (HasUncommittedRoute(area))
        {
            string exitNote = autoExits
                ? "분할이 확정되면 기존 경로는 폐기되고, 새 영역의 시작/종료점은 원래 측면을 따라 자동으로 지정됩니다. 필요하면 다시 선택할 수 있습니다."
                : "분할이 확정되면 경로·인출점은 각 새 영역에서 다시 선택합니다.";
            MessageBoxResult answer = MessageBox.Show(this,
                string.Format("{0} 영역의 현재 발열경로를 폐기하고 분할할까요?\n{1}",
                    area.Name, exitNote),
                "영역 분할", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                SetSplitStatus("분할을 취소했습니다 — 기존 영역과 경로를 유지합니다.");
                return;
            }
        }

        AreaSplitResult split = AreaSplitter.Split(area.Region[0], area.Exclusions,
            area.Direction, margin, spacing, plan.Parts);
        if (!split.Succeeded)
        {
            SetSplitStatus("분할에 실패했습니다 — " + split.Failure);
            return;
        }
        if (split.Bands.Count != plan.Parts)
        {
            SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
                "분할 결과 영역 수가 계획과 다릅니다 — 계획 {0}개, 계산 {1}개.", plan.Parts, split.Bands.Count));
            return;
        }

        if (!TryBuildSplitChildren(area, split, totalPower, out List<WorkingArea> children,
                out string childFailure))
        {
            SetSplitStatus("분할 영역을 준비할 수 없습니다 — " + childFailure);
            return;
        }

        if (autoExits)
        {
            // 부모의 시작/종료 측면을 각 영역으로 옮긴다. 하나라도 유일하게 결정되지 않으면
            // 자식 영역에는 아무 점도 기록하지 않고 분할 자체를 확정하지 않는다.
            if (!SplitExitPointPlanner.TryAssign(area, children, out string exitFailure))
            {
                SetSplitStatus("분할 영역의 인출점을 자동으로 정할 수 없습니다 — " + exitFailure);
                return;
            }
            // 확정 전에 각 점의 3D 표면 위치를 검증한다. 실패하면 부모 영역·경로·선택점은 그대로 남는다.
            foreach (WorkingArea child in children)
            {
                if (!TryEvaluateSelectedExit3D(child, child.SelectedStartExit!.Value,
                        out _, out string startError))
                {
                    SetSplitStatus(child.Name + " 시작점을 표면에 올릴 수 없습니다 — " + startError);
                    return;
                }
                if (!TryEvaluateSelectedExit3D(child, child.SelectedEndExit!.Value,
                        out _, out string endError))
                {
                    SetSplitStatus(child.Name + " 종료점을 표면에 올릴 수 없습니다 — " + endError);
                    return;
                }
            }
        }

        // 원자적 교체: 모든 자식 영역 경계가 준비된 뒤 한 번의 네이티브 경로 커밋으로
        // 부모 경로를 삭제하고 최종 영역 집합을 확정한다. 실패하면 부모는 그대로 남는다.
        var finalAreas = _areas.Where(existing => !ReferenceEquals(existing, area)).ToList();
        finalAreas.AddRange(children);
        if (!TryCommitAreaRouteCandidates(finalAreas,
                new Dictionary<WorkingArea, WorkingArea>(), out _, out _))
        {
            SetSplitStatus("분할 확정에 실패했습니다 — 기존 영역과 경로를 유지합니다.");
            return;
        }

        DetachFromSplitFamily(area);

        if (area.AreaId > 0)
        {
            try { Bridge.ClearAreaHighlight(area.AreaId); }
            catch (Exception) { }
        }

        _areas.Clear();
        _areas.AddRange(finalAreas);
        string familyKey = "split-" + area.Name + "-" + Guid.NewGuid().ToString("N");
        foreach (WorkingArea child in children)
        {
            child.AreaId = ++_nextAreaId;
            child.SplitFamilyKey = familyKey;
        }

        var family = new SplitFamily
        {
            Key = familyKey,
            TotalTargetPowerW = totalPower,
            SourceAreaName = area.Name,
        };
        family.Parts.AddRange(children);
        _splitFamilies[family.Key] = family;

        ApplyAreaColors();
        ActivateArea(children[0]);
        RefreshAreaList();
        UpdateCircuitTable();
        UpdateActionStates();

        string partsText = string.Join(", ", children.Select(child => child.Name));
        string exitText = autoExits
            ? "시작/종료점은 원래 측면을 따라 자동으로 지정했습니다 — 경로를 생성하세요(필요하면 다시 선택 가능)."
            : "각 영역에서 시작점과 종료점을 선택한 뒤 생성하세요.";
        SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
            "{0} 영역을 {1}개 독립 영역으로 분할했습니다 — {2}. {3}",
            area.Name, children.Count, partsText, exitText));
        if (!string.IsNullOrEmpty(_routePresentationWarning))
            SetSplitStatus(StatusText.Text + " · 표시 경고: " + _routePresentationWarning);
    }

    /// <summary>분할 밴드마다 독립 영역과 그 artificial 절단선 provenance를 준비한다.</summary>
    private bool TryBuildSplitChildren(WorkingArea parent, AreaSplitResult split,
        double totalTargetPowerW, out List<WorkingArea> children, out string failure)
    {
        children = new List<WorkingArea>();
        failure = string.Empty;

        List<(Vec2 A, Vec2 B)> newCuts = BuildCutLines(parent.Region[0], split);
        if (newCuts.Count != split.Bands.Count - 1)
        {
            failure = "절단선 수와 분할 영역 수가 대응하지 않습니다.";
            return false;
        }
        // 상위 분할에서 물려받은 절단선도 함께 넘긴다. 이미 절단선이던 경계가 원본 CAD edge나
        // 새 절단선으로 둔갑하지 않고 자기 provenance를 그대로 유지하게 하고, 저장·재열기에서도
        // 같은 전체 선분 식별자로 복원되게 한다.
        List<(Vec2 A, Vec2 B)> cutLines = parent.SplitCutLines
            .Concat(newCuts)
            .Distinct()
            .ToList();

        var usedNames = new HashSet<string>(_areas.Select(existing => existing.Name), StringComparer.Ordinal);
        var usedColors = new HashSet<System.Windows.Media.Color>(_areas.Select(existing => existing.Color));
        int parentColorIndex = Array.IndexOf(AreaColors, parent.Color);
        if (parentColorIndex < 0) parentColorIndex = 0;
        double provisionalPower = totalTargetPowerW / split.Bands.Count;

        for (int i = 0; i < split.Bands.Count; i++)
        {
            AreaSplitBand band = split.Bands[i];
            if (!SplitRegionBoundary2D.TryCreate(parent.RegionCadBoundary, parent.Region[0],
                    band.Outer, parent.Exclusions, cutLines,
                    out SplitRegionBoundary2D boundary, out string boundaryFailure))
            {
                failure = string.Format(CultureInfo.CurrentCulture,
                    "{0}번 분할 영역의 경계를 만들 수 없습니다 — {1}", i + 1, boundaryFailure);
                return false;
            }
            if (boundary.Segments.Count == 0)
            {
                failure = string.Format(CultureInfo.CurrentCulture,
                    "{0}번 분할 영역에 절단선 경계가 없습니다.", i + 1);
                return false;
            }

            string name = NextSplitAreaName(parent.Name, usedNames);
            usedNames.Add(name);
            System.Windows.Media.Color color = NextSplitAreaColor(parentColorIndex, usedColors);
            usedColors.Add(color);

            var child = new WorkingArea
            {
                Name = name,
                Color = color,
                Frame = parent.Frame,
                Direction = parent.Direction,
                MarginText = parent.MarginText,
                SpacingText = parent.SpacingText,
                AreaId = 0,
                ExitFaceId = -1,
                ExitFaceRevision = 0,
                ExitChain = parent.ExitChain.ToList(),
                ExitChain3D = parent.ExitChain3D.ToList(),
                ExitChainFaceIds = parent.ExitChainFaceIds.ToList(),
                ExitEdgeRefs = parent.ExitEdgeRefs.Select(CadExitBoundary2D.CopyReference).ToList(),
                Exclusions = parent.Exclusions.ToList(),
                RegionCadBoundary = parent.RegionCadBoundary,
                RegionExitBoundary = boundary,
                ExitBoundary = boundary,
                RegionBoundary = new ExactRegionContainment(boundary.CreateContainment()),
                // 전체 절단선(A/B)을 저장한다. 잘라낸 구간을 저장하면 재열기에서 절단선 식별자가
                // 달라져 그 위에 저장된 인출점을 복원할 수 없다.
                SplitCutLines = cutLines.ToList(),
                TargetPowerText = provisionalPower.ToString("0.###", CultureInfo.CurrentCulture),
            };
            child.FaceIdSet.UnionWith(parent.FaceIdSet);
            child.Region.Add(band.Outer.ToList());
            ApplyTargetPowerText(child);
            children.Add(child);
        }

        if (children.Count == 0)
        {
            failure = "분할 영역이 없습니다.";
            return false;
        }
        return true;
    }

    /// <summary>밴드 절단 좌표를 작업평면 전체 절단선으로 바꾼다. 분할 축만 고정하고 다른 축은 외곽 밖까지 연장한다.</summary>
    private static List<(Vec2 A, Vec2 B)> BuildCutLines(
        IReadOnlyList<Vec2> parentOuter, AreaSplitResult split)
    {
        var lines = new List<(Vec2 A, Vec2 B)>();
        if (parentOuter.Count == 0) return lines;

        double minX = parentOuter.Min(point => point.X);
        double maxX = parentOuter.Max(point => point.X);
        double minY = parentOuter.Min(point => point.Y);
        double maxY = parentOuter.Max(point => point.Y);
        double padding = Math.Max(1.0, Math.Max(maxX - minX, maxY - minY) * 0.1);

        foreach (double cut in split.Cuts)
        {
            lines.Add(split.SplitAxisIsY
                ? (new Vec2(minX - padding, cut), new Vec2(maxX + padding, cut))
                : (new Vec2(cut, minY - padding), new Vec2(cut, maxY + padding)));
        }
        return lines;
    }

    private static string NextSplitAreaName(string parentName, HashSet<string> usedNames)
    {
        string baseName = string.IsNullOrEmpty(parentName) ? "A" : parentName;
        for (int suffix = 1; suffix < 100000; suffix++)
        {
            string candidate = baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            if (!usedNames.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("분할 영역 이름을 만들 수 없습니다");
    }

    private static System.Windows.Media.Color NextSplitAreaColor(
        int parentColorIndex, HashSet<System.Windows.Media.Color> usedColors)
    {
        for (int step = 1; step <= AreaColors.Length; step++)
        {
            System.Windows.Media.Color candidate = AreaColors[(parentColorIndex + step) % AreaColors.Length];
            if (!usedColors.Contains(candidate)) return candidate;
        }
        return AreaColors[(parentColorIndex + usedColors.Count) % AreaColors.Length];
    }

    /// <summary>
    /// 분할 가족에서 영역을 떼어낸다. 남은 영역의 총 목표전력은 떼어낸 영역의 현재 배분만큼
    /// 줄어들고, 이미 확정된 다른 영역의 목표전력은 다시 배분하지 않는다.
    /// </summary>
    private void DetachFromSplitFamily(WorkingArea area)
    {
        if (area == null || string.IsNullOrEmpty(area.SplitFamilyKey)) return;
        string key = area.SplitFamilyKey;
        area.SplitFamilyKey = "";
        if (!_splitFamilies.TryGetValue(key, out SplitFamily family)) return;

        family.Parts.Remove(area);
        if (area.TargetPowerW is double allocated)
            family.TotalTargetPowerW = Math.Max(0.0, family.TotalTargetPowerW - allocated);
        if (family.Parts.Count == 0)
            _splitFamilies.Remove(key);
    }

    /// <summary>
    /// 분할 가족의 모든 영역에 회로가 준비되면 실측 3D 길이 비율로 총 목표전력을 다시 배분한다.
    /// 배분 후에는 각 영역이 자기 목표전력을 소유하므로 다른 영역 편집이 값을 바꾸지 않는다.
    /// </summary>
    private void TryApplySplitFamilyAllocation()
    {
        if (_applyingSplitAllocation || _splitFamilies.Count == 0) return;
        bool changed = false;
        List<string> keys = _splitFamilies.Keys.ToList();
        foreach (string key in keys)
        {
            if (!_splitFamilies.TryGetValue(key, out SplitFamily family)) continue;
            if (family.Parts.Any(part => !_areas.Contains(part)))
            {
                _splitFamilies.Remove(key);
                continue;
            }
            if (family.Parts.Any(part => !HasUsableLengths(part, out _) || part.Circuits.Count != 1))
                continue;

            var circuits = family.Parts
                .Select(part => (part.Name + "-1",
                    GetCircuitLengths(part).Sum(circuit => circuit.HeatingLengthMm)))
                .ToList();
            if (!TargetPowerAllocation.TryAllocate(circuits, family.TotalTargetPowerW,
                    out IReadOnlyList<TargetPowerShare> shares, out string allocationError))
            {
                _splitFamilies.Remove(key);
                foreach (WorkingArea part in family.Parts) part.SplitFamilyKey = "";
                SetSplitStatus("분할 목표전력 배분 실패 — " + allocationError);
                continue;
            }

            _applyingSplitAllocation = true;
            try
            {
                // 가족을 먼저 제거한다. 배분 중 갱신되는 전기 표가 다시 배분을 호출하지 않게 한다.
                _splitFamilies.Remove(key);
                for (int i = 0; i < family.Parts.Count; i++)
                {
                    WorkingArea part = family.Parts[i];
                    part.SplitFamilyKey = "";
                    part.TargetPowerText = shares[i].TargetPowerW.ToString("0.###", CultureInfo.CurrentCulture);
                    ApplyTargetPowerText(part);
                }
                if (_activeArea != null && family.Parts.Contains(_activeArea) && TargetPowerBox != null)
                {
                    _suppressPatternInputWriteBack = true;
                    try { TargetPowerBox.Text = _activeArea.TargetPowerText; }
                    finally { _suppressPatternInputWriteBack = false; }
                }
            }
            finally
            {
                _applyingSplitAllocation = false;
            }

            changed = true;
            SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
                "{0} 분할 영역의 실측 길이로 목표 발열전력을 배분했습니다 — 총 {1:F1} W.",
                family.SourceAreaName, family.TotalTargetPowerW));
        }

        if (!changed) return;
        RefreshElectricalTable();
        UpdateExportScopeText();
    }
    private void SetSplitStatus(string message)
    {
        if (SplitStatusText != null) SplitStatusText.Text = message ?? "";
    }
}
