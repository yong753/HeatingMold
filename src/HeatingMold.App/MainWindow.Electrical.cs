using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

public partial class MainWindow
{
    private bool _electricalUiReady;
    private bool _suppressElectricalInput;
    private bool _reviewCurrentAreaOnly;

    /// <summary>사양표 UI 표시용 행. 합계 행은 전류/전력만 채우고 IsTotal이 true다.</summary>
    private sealed record ElectricalSpecUiRow(
        string CircuitId,
        double? TargetPowerW,
        double? LengthM,
        double? ResistanceOhm,
        double? VoltageV,
        double? CurrentA,
        double? PowerW,
        string Verdict,
        string Note,
        bool IsTotal);

    private void InitializeElectricalUi()
    {
        // XAML의 표시 문자열은 culture에 따라 파싱되지 않을 수 있으므로 초기값을 다시 쓴다.
        if (WirePresetBox != null && WirePresetBox.SelectedIndex < 0)
            WirePresetBox.SelectedIndex = ElectricalSettingsResolver.Case1PresetIndex;
        if (AllowedVoltageBox != null)
            AllowedVoltageBox.Text = ElectricalSettingsResolver.DefaultAllowedVoltageText;
        if (AllowedCurrentBox != null)
            AllowedCurrentBox.Text = ElectricalSettingsResolver.DefaultCurrentLimitText;
        if (AllowedPowerBox != null)
            AllowedPowerBox.Text = ElectricalSettingsResolver.DefaultPowerLimitText;

        _electricalUiReady = true;
        RefreshElectricalTable();
        UpdateExportScopeText();
    }

    private void ReviewScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        _reviewCurrentAreaOnly = ReviewScopeBox != null && ReviewScopeBox.SelectedIndex == 1;
        if (!_electricalUiReady) return;
        UpdateCircuitTable();
        UpdateExportScopeText();
        StatusText.Text = _reviewCurrentAreaOnly
            ? "검토 범위: 현재 영역 — 전기 표와 내보내기 사양표에 현재 영역만 포함됩니다"
            : "검토 범위: 전체 영역";
    }

    private static string ReviewStateLabel(ReviewAreaState state) => state switch
    {
        ReviewAreaState.Missing => "미생성",
        ReviewAreaState.Stale => "재생성 필요",
        ReviewAreaState.Unverified => "근거 미확인",
        ReviewAreaState.InvalidLengths => "길이 불완전",
        _ => "정상"
    };

    /// <summary>사용자가 선택한 검토 범위의 영역을 필터링 전에 그대로 돌려준다.</summary>
    private List<WorkingArea> GetRequestedReviewAreas()
    {
        if (_reviewCurrentAreaOnly)
            return _activeArea == null
                ? new List<WorkingArea>()
                : new List<WorkingArea> { _activeArea };
        return _areas.ToList();
    }

    /// <summary>회로 길이 표에 쓸 수 있을 만큼 ID/조각/길이 대응이 온전한지 확인한다.</summary>
    private static bool HasUsableLengths(WorkingArea area, out string reason)
    {
        reason = null;
        if (area?.Pattern == null) { reason = "발열 경로가 없습니다"; return false; }
        if (area.Circuits == null || area.Circuits.Count == 0)
        {
            reason = "확정 회로 경로가 없습니다";
            return false;
        }
        int count = area.Pattern.RoutePieces.Count;
        if (count == 0) { reason = "경로 조각이 없습니다"; return false; }
        if (area.NativePieceIds == null || area.NativePieceIds.Length != count)
        { reason = "경로 ID와 조각 수가 일치하지 않습니다"; return false; }
        if (area.RoutePieceLengths == null || area.RoutePieceLengths.Length != count)
        { reason = "조각 길이 배열이 불완전합니다"; return false; }
        for (int i = 0; i < count; i++)
            if (!double.IsFinite(area.RoutePieceLengths[i]) || area.RoutePieceLengths[i] < 0)
            { reason = "유효하지 않은 조각 길이가 있습니다"; return false; }
        if (!HasUsableCircuits(area, out reason)) return false;
        return true;
    }

    /// <summary>영역 하나의 검토 상태를 판정한다. 표시 범위와 배분 대상이 같은 규칙을 쓴다.</summary>
    private ReviewAreaEntry ClassifyArea(WorkingArea area)
    {
        if (area.Pattern == null)
            return new ReviewAreaEntry(area, ReviewAreaState.Missing, "발열 경로 미생성");
        if (area.AppliedInputs == null)
            return new ReviewAreaEntry(area, ReviewAreaState.Unverified, "생성 조건 출처 확인 불가 — 재생성 필요");
        if (IsPatternInputStale(area))
            return new ReviewAreaEntry(area, ReviewAreaState.Stale, "입력 조건 변경 — 재생성 필요");
        if (!HasUsableLengths(area, out string lengthReason))
            return new ReviewAreaEntry(area, ReviewAreaState.InvalidLengths, lengthReason);
        return new ReviewAreaEntry(area, ReviewAreaState.Ready, "정상");
    }

    /// <summary>
    /// 배분값은 파생값이므로 배분을 계산할 수 없는 상태에서는 비운다. 이전 값을 남기면
    /// 분할 버튼이 이미 유효하지 않은 전력으로 분할 수를 계산한다.
    /// </summary>
    private void ClearSharedPowerAllocation()
    {
        foreach (WorkingArea area in _areas)
            if (area != null) area.TargetPowerW = null;
    }

    /// <summary>
    /// 공통 발열전력을 확정된 전체 영역의 실측 3D 길이 비율로 배분하고 각 영역의 배분값을 갱신한다.
    /// 검토 범위는 표시 범위일 뿐이므로 현재 영역만 볼 때도 그 영역에 전체 전력을 몰아주지 않는다.
    /// 배분 대상이 아닌 영역은 배분값을 비우고, 확정된 길이가 없으면 조용히 넘기지 않는다.
    /// </summary>
    private bool TryAllocateSharedPower(
        TargetPowerElectricalSettings settings,
        out IReadOnlyDictionary<string, double> allocatedByCircuit,
        out string error)
    {
        allocatedByCircuit = null;
        error = null;
        var request = new List<(string CircuitId, double LengthMm)>();
        var owners = new List<(WorkingArea Area, string CircuitId)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkingArea area in _areas)
        {
            if (area == null) continue;
            if (ClassifyArea(area).State != ReviewAreaState.Ready) continue;
            string prefix = string.IsNullOrEmpty(area.Name) ? "A-" : area.Name + "-";
            foreach (CircuitLengthInfo circuit in GetCircuitLengths(area, prefix))
            {
                if (!seen.Add(circuit.CircuitId))
                {
                    ClearSharedPowerAllocation();
                    error = "회로 ID가 중복되어 발열 전력을 배분할 수 없습니다 — " + circuit.CircuitId;
                    return false;
                }
                request.Add((circuit.CircuitId, circuit.HeatingLengthMm));
                owners.Add((area, circuit.CircuitId));
            }
        }

        if (request.Count == 0)
        {
            ClearSharedPowerAllocation();
            return true;
        }

        if (!TargetPowerAllocation.TryAllocate(request, settings.Limits.PowerLimitW,
                out IReadOnlyList<TargetPowerShare> shares, out string reason))
        {
            // 배분에 실패하면 이전 값은 지금 상태의 배분이 아니다. 분할이 재사용하지 않게 비운다.
            ClearSharedPowerAllocation();
            error = "공통 발열전력을 배분할 수 없습니다 — " + reason;
            return false;
        }

        var byId = new Dictionary<string, double>(StringComparer.Ordinal);
        var byArea = new Dictionary<WorkingArea, double>();
        for (int i = 0; i < shares.Count; i++)
        {
            byId[shares[i].CircuitId] = shares[i].TargetPowerW;
            byArea.TryGetValue(owners[i].Area, out double areaSum);
            byArea[owners[i].Area] = areaSum + shares[i].TargetPowerW;
        }
        foreach (WorkingArea area in _areas)
        {
            if (area == null) continue;
            area.TargetPowerW = byArea.TryGetValue(area, out double allocated) ? allocated : null;
        }
        allocatedByCircuit = byId;
        return true;
    }

    /// <summary>
    /// 검토 범위의 모든 영역을 먼저 분류한 뒤 준비된 영역만 합계에 넣는다.
    /// 전력 배분은 표시 범위가 아니라 확정된 전체 영역을 대상으로 하고, 표만 표시 범위로 거른다.
    /// 표·요약·내보내기 안내가 이 한 스냅샷을 공유한다.
    /// </summary>
    private ReviewSnapshot BuildReviewSnapshot()
    {
        var requested = GetRequestedReviewAreas();
        var entries = new List<ReviewAreaEntry>();
        foreach (WorkingArea area in requested)
        {
            if (area == null) continue;
            entries.Add(ClassifyArea(area));
        }

        var readyAreas = entries
            .Where(e => e.State == ReviewAreaState.Ready)
            .Select(e => e.Area)
            .ToList();

        ElectricalSpecTable table = null;
        ElectricalLimits limits = null;
        TargetPowerElectricalSettings settings = null;
        IReadOnlyDictionary<string, double> allocated = null;
        string electricalError = null;
        if (TryReadElectricalLimits(out settings, out electricalError))
        {
            limits = settings.Limits;
            if (!TryAllocateSharedPower(settings, out allocated, out string allocationError))
                electricalError = allocationError;
        }
        else
        {
            // 입력이 잘못되면 배분을 다시 할 수 없다. 이전 배분값을 남기면 분할 버튼이
            // 이미 유효하지 않은 전력으로 분할 수를 계산하므로 파생값을 비운다.
            ClearSharedPowerAllocation();
        }

        var circuits = new List<CircuitLengthInfo>();
        foreach (WorkingArea area in readyAreas)
        {
            string prefix = string.IsNullOrEmpty(area.Name) ? "A-" : area.Name + "-";
            foreach (CircuitLengthInfo circuit in GetCircuitLengths(area, prefix))
            {
                circuits.Add(allocated != null && allocated.TryGetValue(circuit.CircuitId, out double power)
                    ? circuit with { TargetPowerW = power }
                    : circuit);
            }
        }

        if (settings != null && electricalError == null && circuits.Count > 0)
        {
            var inputs = circuits
                .Select(c => new TargetPowerCircuitInput(
                    c.CircuitId, c.HeatingLengthMm, c.TargetPowerW))
                .ToList();
            table = ElectricalSpecTableBuilder.BuildFromTargetPower(inputs, settings.Wire, settings.Limits);
        }

        string blockReason = null;
        if (requested.Count == 0)
            blockReason = _reviewCurrentAreaOnly && _activeArea == null
                ? "현재 작업 영역이 없습니다"
                : "검토 범위에 작업 영역이 없습니다";
        else if (entries.Any(e => e.State != ReviewAreaState.Ready))
            blockReason = "준비되지 않은 영역: " + string.Join(", ",
                entries.Where(e => e.State != ReviewAreaState.Ready)
                    .Select(e => string.Format("{0}({1})", e.Area.Name, ReviewStateLabel(e.State))));
        else if (!string.IsNullOrEmpty(electricalError))
            blockReason = "전기 입력 오류 — " + electricalError;
        else if (table == null || table.Rows.Count == 0)
            blockReason = "내보낼 회로가 없습니다";

        return new ReviewSnapshot
        {
            RequestedAreas = requested,
            Entries = entries,
            ReadyAreas = readyAreas,
            Circuits = circuits,
            Table = table,
            Limits = limits,
            ElectricalError = electricalError,
            CanExport = blockReason == null,
            BlockReason = blockReason
        };
    }

    private void UpdateExportScopeText()
    {
        if (ExportScopeText == null) return;
        ReviewSnapshot snapshot = BuildReviewSnapshot();
        string scopeLabel = _reviewCurrentAreaOnly ? "현재 영역" : "전체 영역";
        string scope = _reviewCurrentAreaOnly
            ? string.Format("현재 영역({0}) — 다른 영역은 포함되지 않습니다", _activeArea?.Name ?? "없음")
            : scopeLabel;
        if (ElectricalSpecGrid != null)
            AutomationProperties.SetName(ElectricalSpecGrid, scopeLabel + " 전기 사양");
        if (ExportIncludeText != null)
            ExportIncludeText.Text = string.Format(
                "포함: 확정 작업평면 기준 금형 평면도·숨은선, {0} 발열경로·전기 사양표.", scopeLabel);
        if (StepExportScopeText != null)
        {
            int routed = snapshot.Entries.Count(entry => entry.State == ReviewAreaState.Ready);
            string wireText = ElectricalSettingsResolver.TryGetWire(
                WirePresetBox?.SelectedIndex ?? -1, out HeatingWireSpec wire)
                ? "Ø" + wire.DiameterMm.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " mm"
                : "규격 미선택";
            StepExportScopeText.Text = "STEP 범위: " + scopeLabel + " · 발열경로 "
                + routed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "개 영역 · 발열사 " + wireText;
        }
        string baseText = string.Format(
            "내보내기 범위: {0} · 준비 {1}/{2}개 — 확정 작업평면 기준 평면도와 발열경로·전기 사양표를 저장합니다. 이 DXF에는 다른 방향 투상·3D STEP·프로젝트 JSON이 포함되지 않습니다.",
            scope, snapshot.ReadyCount, snapshot.RequestedCount);
        ExportScopeText.Text = snapshot.CanExport
            ? baseText
            : baseText + " 사용 불가: " + snapshot.BlockReason;
    }

    private void WirePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_electricalUiReady || _suppressElectricalInput) return;
        RefreshElectricalTable();
        // STEP 범위 문구가 선택한 Case의 직경을 표시하므로 함께 갱신한다.
        UpdateExportScopeText();
    }

    /// <summary>
    /// 선택한 Case의 고정 직경·단위 저항을 프로젝트 저장용 문자열로 만든다.
    /// 화면에는 프리셋 이름에 이미 표시되므로 별도 입력 상자를 두지 않는다.
    /// </summary>
    private (string Diameter, string Resistance) GetFixedWireText()
    {
        if (!ElectricalSettingsResolver.TryGetWire(WirePresetBox?.SelectedIndex ?? -1, out HeatingWireSpec wire))
            return ("", "");
        return (wire.DiameterMm.ToString("0.###", CultureInfo.CurrentCulture),
                wire.ResistanceOhmPerMeter.ToString("0.###", CultureInfo.CurrentCulture));
    }

    private void ElectricalInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_electricalUiReady || _suppressElectricalInput) return;
        RefreshElectricalTable();
    }

    /// <summary>
    /// 전압 입력은 계산에 쓰지 않고 회로별 허용 전압 한도로만 해석한다.
    /// 전력 입력은 회로별 한도가 아니라 전체가 나누어 쓰는 총 발열전력이다.
    /// </summary>
    private bool TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string error)
    {
        return ElectricalSettingsResolver.TryResolveLimits(
            WirePresetBox?.SelectedIndex ?? -1,
            AllowedVoltageBox?.Text ?? "",
            AllowedCurrentBox?.Text ?? "",
            AllowedPowerBox?.Text ?? "",
            out settings, out error);
    }

    /// <summary>
    /// 확정된 발열 경로의 회로별 전체 길이로 전기 사양표를 다시 계산한다.
    /// 인출점 사이의 경로 전체를 한 번만 합산한다.
    /// </summary>
    private void RefreshElectricalTable() => RefreshElectricalTableCore(null);

    private void RefreshElectricalTableCore(ReviewSnapshot sharedSnapshot)
    {
        if (!_electricalUiReady || ElectricalSpecGrid == null) return;

        // 같은 동기 갱신 안에서 이미 만든 검토 결과만 전달받는다. 이벤트 사이에는
        // 캐시하지 않으므로 입력 변경/내보내기 재검증은 각자 새로 계산한다.
        ReviewSnapshot snapshot = sharedSnapshot ?? BuildReviewSnapshot();
        if (snapshot.ElectricalError != null)
        {
            SetElectricalRows(null);
            ElectricalSummaryText.Text = "전기 입력 오류 — " + snapshot.ElectricalError;
            return;
        }

        ElectricalSpecTable table = snapshot.Table;
        var rows = table == null
            ? new List<ElectricalSpecUiRow>()
            : table.Rows.Select(r => new ElectricalSpecUiRow(
                r.CircuitId,
                r.TargetPowerW,
                r.LengthM,
                r.ResistanceOhm,
                // 배분 전력이 없는 행은 0 V/0 A가 아니라 '계산 불가'다. 길이·저항은 확정값이므로 남긴다.
                r.IsTargetPowerMissing ? null : r.VoltageV,
                r.IsTargetPowerMissing ? null : r.CurrentA,
                r.IsTargetPowerMissing ? null : r.PowerW,
                ElectricalVerdictText(r), r.Note, false)).ToList();
        if (rows.Count > 0)
        {
            rows.Add(new ElectricalSpecUiRow(
                "합계", table.TotalTargetPowerW, null, null, null, table.TotalCurrentA, table.TotalPowerW,
                "", "", true));
        }
        SetElectricalRows(rows);

        string scope = _reviewCurrentAreaOnly ? "현재 영역 표시(전력은 전체 배분) · " : "전체 영역 기준 · ";
        string excluded = snapshot.ExcludedCount > 0
            ? string.Format(" · 준비되지 않은 영역 {0}개 제외", snapshot.ExcludedCount)
            : "";
        if (rows.Count == 0 || table == null || table.Rows.Count == 0)
        {
            ElectricalSummaryText.Text = scope
                + (_reviewCurrentAreaOnly ? "선택한 영역에 발열 경로가 없습니다" : "검토 범위에 발열 경로가 없습니다")
                + excluded;
            return;
        }

        // 계산 오류는 OK/NG와 섞지 않는다. 하나라도 있으면 '전체 OK'라고 말하지 않는다.
        int invalidCount = table.Rows.Count(r => !r.IsValid && !r.IsTargetPowerMissing);
        int readyCount = snapshot.Entries.Count(e => e.State == ReviewAreaState.Ready);
        bool allReady = readyCount == snapshot.Entries.Count;
        string verdicts;
        if (invalidCount > 0)
        {
            verdicts = string.Format(CultureInfo.CurrentCulture, " · OK {0} · NG {1} · {2}",
                table.WithinLimitCount, table.OverLimitCount,
                string.Format(CultureInfo.CurrentCulture, "계산 오류 {0}", invalidCount));
        }
        else if (table.HasOverLimitCircuits || table.IsSharedPowerOverLimit)
        {
            // 합계만 초과한 경우 회로별 NG 수는 0이므로, 판정 근거를 함께 적어 모순을 없앤다.
            string sharedNote = table.IsSharedPowerOverLimit ? " · 합계 전력 초과" : "";
            verdicts = string.Format(CultureInfo.CurrentCulture, " · OK {0} · NG {1}{2}",
                table.WithinLimitCount, table.OverLimitCount, sharedNote);
        }
        else if (!allReady)
        {
            // 일부 영역에 경로가 없으면 표에 남은 회로의 판정만으로 '전체 OK'라고 말하지 않는다.
            verdicts = string.Format(CultureInfo.CurrentCulture, " · 준비 {0}/{1}", readyCount,
                snapshot.Entries.Count);
        }
        else if (_reviewCurrentAreaOnly)
        {
            // 현재 영역만 표시할 때는 다른 영역의 판정을 확인하지 않았으므로 '전체 OK'가 아니다.
            verdicts = " · 표시 영역 OK";
        }
        else
            verdicts = " · 전체 OK";

        string limitText = snapshot.Limits == null
            ? ""
            : string.Format(CultureInfo.CurrentCulture, " · 허용 {0:0.###} A / {1:0.###} V",
                snapshot.Limits.CurrentLimitA, snapshot.Limits.VoltageLimitV);

        ElectricalSummaryText.Text = string.Format(CultureInfo.CurrentCulture,
            "{0}총 {1}회로{2} · 배분 {3:F2} kW / 한도 {4:F2} kW · 합계 {5:F2} A / {6:F1} W{7}{8}",
            scope, table.Rows.Count, verdicts, table.TotalTargetPowerW / 1000.0,
            snapshot.Limits == null ? 0.0 : snapshot.Limits.PowerLimitW / 1000.0,
            table.TotalCurrentA, table.TotalPowerW, limitText, excluded);
    }

    /// <summary>검토 탭의 사양표와 뷰포트의 확대 보기가 같은 행 집합을 보여 준다.</summary>
    private void SetElectricalRows(IReadOnlyList<ElectricalSpecUiRow> rows)
    {
        System.Collections.IEnumerable source = rows == null || rows.Count == 0 ? null : (System.Collections.IEnumerable)rows;
        if (ElectricalSpecGrid != null) ElectricalSpecGrid.ItemsSource = source;
        if (ExpandedElectricalGrid != null) ExpandedElectricalGrid.ItemsSource = source;
    }

    /// <summary>표의 판정 열. 미입력과 계산 오류는 OK/NG가 아니므로 '—'로 구분한다.</summary>
    private static string ElectricalVerdictText(ElectricalSpecRow row)
    {
        if (row.IsTargetPowerMissing) return "—";
        if (!row.IsValid) return "—";
        if (row.Assessment == null) return "";
        return row.Assessment.IsWithinLimits ? "OK" : "NG";
    }

    /// <summary>
    /// 선택한 영역의 회로별 전체 발열 길이를 표시 순서대로 만든다. 인출점 사이의 경로 전체를
    /// 한 번만 합산하며 별도의 인출부 길이는 두지 않는다. 선택 인출 경로의 마지막 연결선도
    /// 회로 조각으로 실제 길이를 가지므로 여기서 따로 걸러내지 않는다.
    /// 배분 전력은 공통 전력 배분이 채우므로 여기서는 비워 둔다.
    /// </summary>
    private List<CircuitLengthInfo> GetCircuitLengths(WorkingArea area, string prefix = "")
    {
        var result = new List<CircuitLengthInfo>();
        if (area?.Pattern == null) return result;

        if (area.CircuitPieceIndices == null || area.CircuitPieceIndices.Count == 0)
            return result;
        for (int i = 0; i < area.CircuitPieceIndices.Count; i++)
        {
            double heating = 0;
            foreach (int index in area.CircuitPieceIndices[i])
                heating += area.RoutePieceLengths.ElementAtOrDefault(index);
            result.Add(new CircuitLengthInfo(prefix + (i + 1), heating));
        }
        return result;
    }
}
