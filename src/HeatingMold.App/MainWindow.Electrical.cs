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
        double? LengthM,
        double? ResistanceOhm,
        double? VoltageV,
        double? CurrentA,
        double? PowerW,
        string Note,
        bool IsTotal);

    private void InitializeElectricalUi()
    {
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
            if (!TryRefreshCircuits(area, out string circuitFailure))
            { reason = circuitFailure; return false; }
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
        int turns = area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector);
        if (area.TurnEnabled == null || area.TurnEnabled.Count < turns)
        { reason = "회로 분할 상태가 조각과 일치하지 않습니다"; return false; }
        if (!HasUsableCircuits(area, out reason)) return false;
        return true;
    }

    /// <summary>
    /// 검토 범위의 모든 영역을 먼저 분류한 뒤 준비된 영역만 합계에 넣는다.
    /// 표·요약·내보내기 안내가 이 한 스냅샷을 공유한다.
    /// </summary>
    private ReviewSnapshot BuildReviewSnapshot()
    {
        var requested = GetRequestedReviewAreas();
        var entries = new List<ReviewAreaEntry>();
        foreach (WorkingArea area in requested)
        {
            if (area == null) continue;
            if (area.Pattern == null)
                entries.Add(new ReviewAreaEntry(area, ReviewAreaState.Missing, "발열 경로 미생성"));
            else if (area.AppliedInputs == null)
                entries.Add(new ReviewAreaEntry(area, ReviewAreaState.Unverified, "생성 조건 출처 확인 불가 — 재생성 필요"));
            else if (IsPatternInputStale(area))
                entries.Add(new ReviewAreaEntry(area, ReviewAreaState.Stale, "입력 조건 변경 — 재생성 필요"));
            else if (!HasUsableLengths(area, out string lengthReason))
                entries.Add(new ReviewAreaEntry(area, ReviewAreaState.InvalidLengths, lengthReason));
            else
                entries.Add(new ReviewAreaEntry(area, ReviewAreaState.Ready, "정상"));
        }

        var readyAreas = entries
            .Where(e => e.State == ReviewAreaState.Ready)
            .Select(e => e.Area)
            .ToList();
        var circuits = new List<CircuitLengthInfo>();
        foreach (WorkingArea area in readyAreas)
        {
            string prefix = string.IsNullOrEmpty(area.Name) ? "A-" : area.Name + "-";
            circuits.AddRange(GetCircuitLengths(area, prefix));
        }

        ElectricalSpecTable table = null;
        PowerSupplySpec supply = null;
        string electricalError = null;
        if (TryReadElectricalSettings(out HeatingWireSpec wire, out supply, out electricalError))
        {
            var inputs = circuits
                .Select(c => new ElectricalCircuitInput(c.CircuitId, c.HeatingLengthMm))
                .ToList();
            table = ElectricalSpecTableBuilder.Build(inputs, wire, supply);
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
            Supply = supply,
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
        if (CircuitLengthGrid != null)
            AutomationProperties.SetName(CircuitLengthGrid, scopeLabel + " 회로별 길이");
        if (ElectricalSpecGrid != null)
            AutomationProperties.SetName(ElectricalSpecGrid, scopeLabel + " 전기 사양");
        if (ExportIncludeText != null)
            ExportIncludeText.Text = string.Format(
                "포함: 전체 금형의 4방향 투상도·숨은선, {0} 전기 사양표.", scopeLabel);
        string baseText = string.Format(
            "내보내기 범위: {0} · 준비 {1}/{2}개 — 전기 사양표와 전체 모델 4투상/숨은선을 저장합니다. 가열 경로·3D STEP·세션은 포함되지 않습니다.",
            scope, snapshot.ReadyCount, snapshot.RequestedCount);
        ExportScopeText.Text = snapshot.CanExport
            ? baseText
            : baseText + " 사용 불가: " + snapshot.BlockReason;
    }

    private void WirePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_electricalUiReady || _suppressElectricalInput) return;
        switch (WirePresetBox.SelectedIndex)
        {
            case 0: ApplyWireSpec(HeatingWireSpec.Case1); break;
            case 1: ApplyWireSpec(HeatingWireSpec.Case2); break;
        }
    }

    private void ApplyWireSpec(HeatingWireSpec spec)
    {
        _suppressElectricalInput = true;
        WireDiameterBox.Text = spec.DiameterMm.ToString("0.###", CultureInfo.CurrentCulture);
        WireResistanceBox.Text = spec.ResistanceOhmPerMeter.ToString("0.###", CultureInfo.CurrentCulture);
        _suppressElectricalInput = false;
        RefreshElectricalTable();
    }

    private void ElectricalInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_electricalUiReady || _suppressElectricalInput) return;

        // 직경/저항을 직접 고치면 프리셋 선택을 "직접 입력"으로 되돌린다.
        if ((ReferenceEquals(sender, WireDiameterBox) || ReferenceEquals(sender, WireResistanceBox))
            && WirePresetBox.SelectedIndex != 2)
        {
            _suppressElectricalInput = true;
            WirePresetBox.SelectedIndex = 2;
            _suppressElectricalInput = false;
        }

        RefreshElectricalTable();
    }

    private bool TryReadElectricalSettings(out HeatingWireSpec wire, out PowerSupplySpec supply, out string error)
    {
        wire = null;
        supply = null;
        if (!TryReadPositive(WireDiameterBox.Text, "발열사 직경", out double diameter, out error)) return false;
        if (!TryReadPositive(WireResistanceBox.Text, "단위 길이당 저항", out double resistance, out error)) return false;
        if (!TryReadNonNegative(AppliedVoltageBox.Text, "인가전압", out double voltage, out error)) return false;

        wire = new HeatingWireSpec { DiameterMm = diameter, ResistanceOhmPerMeter = resistance };
        supply = new PowerSupplySpec { VoltageV = voltage };
        error = null;
        return true;
    }

    private static bool TryReadPositive(string text, string name, out double value, out string error)
    {
        error = "";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || !double.IsFinite(value) || value <= 0)
        {
            error = name + "은(는) 0보다 큰 숫자여야 합니다";
            return false;
        }
        return true;
    }

    private static bool TryReadNonNegative(string text, string name, out double value, out string error)
    {
        error = "";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || !double.IsFinite(value) || value < 0)
        {
            error = name + "은(는) 0 이상의 숫자여야 합니다";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 현재 입력값과 선택된 검토 범위의 확정된 발열 경로로 전기 사양을 계산한다.
    /// UI 표시와 DXF 출력이 같은 계산 경로를 사용하도록 캐시 없이 매번 계산한다.
    /// </summary>
    private bool TryBuildCurrentElectricalTable(out ElectricalSpecTable table, out PowerSupplySpec supply, out string error)
    {
        ReviewSnapshot snapshot = BuildReviewSnapshot();
        table = snapshot.Table;
        supply = snapshot.Supply;
        error = snapshot.ElectricalError;
        if (error != null) return false;
        if (table == null)
        {
            error = snapshot.BlockReason ?? "전기 사양을 계산할 수 없습니다";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 확정된 발열 경로의 회로별 전체 길이로 전기 사양표를 다시 계산한다.
    /// 인출점 사이의 경로 전체를 한 번만 합산한다.
    /// </summary>
    private void RefreshElectricalTable()
    {
        if (!_electricalUiReady || ElectricalSpecGrid == null) return;

        ReviewSnapshot snapshot = BuildReviewSnapshot();
        if (snapshot.ElectricalError != null)
        {
            ElectricalSpecGrid.ItemsSource = null;
            ElectricalSummaryText.Text = "전기 입력 오류 — " + snapshot.ElectricalError;
            return;
        }

        ElectricalSpecTable table = snapshot.Table;
        var rows = table == null
            ? new List<ElectricalSpecUiRow>()
            : table.Rows.Select(r => new ElectricalSpecUiRow(
                r.CircuitId, r.LengthM, r.ResistanceOhm, r.VoltageV, r.CurrentA, r.PowerW,
                r.Message, false)).ToList();
        if (rows.Count > 0)
        {
            rows.Add(new ElectricalSpecUiRow(
                "합계", null, null, null, table.TotalCurrentA, table.TotalPowerW, "", true));
        }
        ElectricalSpecGrid.ItemsSource = rows;

        string scope = _reviewCurrentAreaOnly ? "현재 영역 기준 · " : "전체 영역 기준 · ";
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

        ElectricalSummaryText.Text = string.Format(CultureInfo.CurrentCulture,
            "{0}총 {1}회로 · 합계 {2:F2} A / {3:F1} W (인가전압 {4:F0} V){5}",
            scope, table.Rows.Count, table.TotalCurrentA, table.TotalPowerW,
            snapshot.Supply.VoltageV, excluded);
    }

    /// <summary>
    /// 현재 회로별 전체 발열 길이를 표시 순서대로 만든다. 인출점 사이의 경로 전체를
    /// 한 번만 합산하며 별도의 인출부 길이는 두지 않는다.
    /// </summary>
    private List<CircuitLengthInfo> GetCircuitLengths(WorkingArea area, string prefix = "")
    {
        var result = new List<CircuitLengthInfo>();
        if (area?.Pattern == null) return result;

        if (area.CircuitPieceIndices == null || area.CircuitPieceIndices.Count == 0)
            TryRefreshCircuits(area, out _);
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
