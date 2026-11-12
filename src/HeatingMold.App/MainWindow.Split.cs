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
/// 일반 작업영역이며, 영역마다 회로 하나를 생성한다. 한 번의 자동분할은 최대
/// <see cref="TargetPowerSplitPlanner.MaxParts"/>개 영역까지이며, 그보다 많이 필요하면
/// 일부만 분할하지 않고 기존 영역과 경로를 유지한 채 오류로 보고한다. 분할은 다음 순서를 지킨다.
/// 1) 이 영역이 배분받은 발열 전력·허용치·마진/간격으로 필요한 영역 수를 계산한다.
/// 2) 예상 배치 길이가 균등해지도록 절단선을 정하고 자식 영역을 준비한다.
/// 3) 부모의 시작/종료 인출점이 있으면 그 측면을 따라 새 영역의 인출점을 자동으로 정한다.
///    (마음에 들지 않으면 기존 인출점 버튼으로 다시 선택할 수 있다.)
/// 4) 분할 후 검토 계산이 확정된 전체 영역의 실측 3D 길이 비율로 공통 발열전력을 다시 배분한다.
///    분할은 총 발열전력을 늘리지 않는다.
/// </summary>
public partial class MainWindow
{
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
        // 분할 수는 '지금' 배분된 전력으로 계산해야 한다. 마진·간격이나 다른 영역의 경로가
        // 바뀌었을 수 있으므로 클릭 시점에 배분을 다시 계산한다. 형상은 건드리지 않는다.
        UpdateCircuitTable();
        if (IsPatternInputStale(area))
        {
            SetSplitStatus("입력 조건이 변경되어 경로가 오래되었습니다 — 경로를 다시 생성한 뒤 분할하세요.");
            return;
        }
        if (area.RegionCadBoundary == null)
        {
            SetSplitStatus("적용영역의 원본 CAD 외곽 참조가 없어 분할할 수 없습니다 — 영역을 다시 확정하세요.");
            return;
        }
        // 분할은 공통 발열전력의 총량을 늘리지 않는다. 이 영역이 배분받은 몫을 기준으로
        // 필요 전압이 허용치를 넘지 않도록 나눌 수 있는 조각 수만 계산한다.
        if (area.TargetPowerW is not double totalPower || !(totalPower > 0))
        {
            SetSplitStatus("발열 전력이 배분되지 않아 분할 수를 계산할 수 없습니다 — 검토 탭에서 발열 전력과 경로를 확인하세요.");
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
            // 상한 초과도 여기서 거부된다. 이 시점까지는 영역·경로·선택점을 바꾸지 않았다.
            SetSplitStatus("분할 계획을 세울 수 없습니다 — " + planError + ". 기존 영역과 경로는 그대로 유지합니다.");
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

        bool autoExits = SplitExitPointPlanner.HasAssignablePair(area);
        if (HasUncommittedRoute(area))
        {
            string exitNote = autoExits
                ? "분할이 확정되면 기존 경로는 폐기되고, 새 영역의 시작/종료점은 원래 범위 안에서 자동으로 지정되며 경로도 같은 분할에서 자동 생성됩니다. 필요하면 다시 선택할 수 있습니다."
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

        List<WorkingArea> children;
        var preparedPatterns = new Dictionary<WorkingArea, SerpentineResult>();
        double balanceRatio = double.NaN;

        if (autoExits)
        {
            // 면적 근사만으로 확정하지 않는다. 실제 선택-인출점 2D 생성기로 후보 절단을
            // 평가해 경로 길이가 가장 균등한 분할을 고르고, 그때 만든 패턴을 그대로 쓴다.
            if (!TryPrepareBalancedSplit(area, split, plan.Parts, margin, spacing,
                    out AreaSplitResult balancedSplit, out children, out preparedPatterns,
                    out balanceRatio, out string balanceFailure))
            {
                SetSplitStatus("분할에 실패했습니다 — " + balanceFailure);
                return;
            }
            split = balancedSplit;
        }
        else
        {
            if (!split.Succeeded)
            {
                SetSplitStatus("분할에 실패했습니다 — " + split.Failure);
                return;
            }
            if (!TryBuildSplitChildren(area, split, out children,
                    out string childFailure))
            {
                SetSplitStatus("분할 영역을 준비할 수 없습니다 — " + childFailure);
                return;
            }
        }

        if (split.Bands.Count != plan.Parts)
        {
            SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
                "분할 결과 영역 수가 계획과 다릅니다 — 계획 {0}개, 계산 {1}개.", plan.Parts, split.Bands.Count));
            return;
        }

        // 자식 AreaId는 경로 커밋 전에 확정한다. 후보 검증·하이라이트 준비가 같은 값을
        // 참조하므로 먼저 예약하되, 커밋이 실패하면 전역 카운터는 그대로 둔다.
        int nextAreaId = _nextAreaId;
        foreach (WorkingArea child in children)
            child.AreaId = ++nextAreaId;

        if (autoExits)
        {
            // 인출점은 실제 2D 생성기로 후보를 평가할 때 이미 배정했다. 확정 전에 각 점의
            // 3D 표면 위치를 검증한다. 실패하면 부모 영역·경로·선택점은 그대로 남는다.
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

        // 자식 경로는 후보 평가에서 이미 생성한 패턴을 그대로 게시한다. 여기서 다시 생성하면
        // 평가한 2D 길이와 확정 경로가 달라질 수 있으므로 재생성하지 않는다.
        var candidates = new Dictionary<WorkingArea, WorkingArea>();
        if (autoExits)
        {
            foreach (WorkingArea child in children)
            {
                WorkingArea fork = ForkAreaRoute(child);
                if (!preparedPatterns.TryGetValue(child, out SerpentineResult pattern))
                {
                    SetSplitStatus(child.Name + " 영역의 준비된 2D 경로가 없습니다 — 분할을 확정하지 않습니다.");
                    return;
                }
                ApplyPreparedPattern(fork, pattern,
                    new List<IReadOnlyList<Vec2>> { pattern.Path });
                candidates[child] = fork;
            }
        }

        // 원자적 교체: 모든 자식 영역 경계와 경로가 준비된 뒤 한 번의 네이티브 경로 커밋으로
        // 부모 경로를 삭제하고 자식 경로를 등록한다. 실패하면 부모는 그대로 남는다.
        var finalAreas = _areas.Where(existing => !ReferenceEquals(existing, area)).ToList();
        finalAreas.AddRange(children);
        if (!TryCommitAreaRouteCandidates(finalAreas, candidates, out _, out _))
        {
            SetSplitStatus("분할 확정에 실패했습니다 — 기존 영역과 경로를 유지합니다.");
            return;
        }

        _nextAreaId = nextAreaId;
        if (area.AreaId > 0)
        {
            try { Bridge.ClearAreaHighlight(area.AreaId); }
            catch (Exception) { }
        }

        _areas.Clear();
        _areas.AddRange(finalAreas);
        // 분할된 영역은 부모와 같은 공통 발열전력을 길이 비율로 나누어 쓴다.
        // 총량은 늘지 않으며, 배분은 검토 계산이 확정된 전체 영역을 대상으로 다시 수행한다.
        ApplyAreaColors();
        ActivateArea(children[0]);
        RefreshAreaList();
        UpdateCircuitTable();
        UpdateActionStates();

        string partsText = string.Join(", ", children.Select(child => child.Name));
        string exitText = autoExits
            ? "시작/종료점은 원래 범위 안에서 마진을 확보해 자동 지정하고 경로도 자동 생성했습니다(필요하면 다시 선택·생성 가능)."
            : "각 영역에서 시작점과 종료점을 선택한 뒤 생성하세요.";
        string balanceText = autoExits && double.IsFinite(balanceRatio)
            ? string.Format(CultureInfo.CurrentCulture,
                " 실제 2D 경로 길이 편차 {0:F3}배.", balanceRatio)
            : string.Empty;
        SetSplitStatus(string.Format(CultureInfo.CurrentCulture,
            "{0} 영역을 {1}개 독립 영역으로 분할했습니다 — {2}. {3}{4}",
            area.Name, children.Count, partsText, exitText, balanceText));
        if (!string.IsNullOrEmpty(_routePresentationWarning))
            SetSplitStatus(SplitStatusText.Text + " · 표시 경고: " + _routePresentationWarning);
    }

    /// <summary>분할 밴드마다 독립 영역과 그 artificial 절단선 provenance를 준비한다.</summary>
    private bool TryBuildSplitChildren(WorkingArea parent, AreaSplitResult split,
        out List<WorkingArea> children, out string failure)
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

        // 테스트/초기화 전 창에서는 _areas가 아직 없을 수 있다. 실제 제품 경로에서는 항상
        // 채워져 있으며, 비어 있으면 새 이름/색을 그대로 쓰면 된다.
        IReadOnlyList<WorkingArea> existingAreas = _areas != null ? _areas : Array.Empty<WorkingArea>();
        var usedNames = new HashSet<string>(existingAreas.Select(existing => existing.Name), StringComparer.Ordinal);
        var usedColors = new HashSet<System.Windows.Media.Color>(existingAreas.Select(existing => existing.Color));
        int parentColorIndex = Array.IndexOf(AreaColors, parent.Color);
        if (parentColorIndex < 0) parentColorIndex = 0;

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
            };
            child.FaceIdSet.UnionWith(parent.FaceIdSet);
            child.Region.Add(band.Outer.ToList());
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

    private void SetSplitStatus(string message)
    {
        if (SplitStatusText != null) SplitStatusText.Text = message ?? "";
    }
}
