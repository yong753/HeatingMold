using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

public partial class MainWindow
{
    private bool _electricalUiReady;
    private bool _suppressElectricalInput;
    private ElectricalSpecTable _lastElectricalTable;

    private sealed record CircuitLengthInfo(string CircuitId, double HeatingLengthMm, double LeadLengthMm);

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
    /// 확정된 발열 경로의 회로별 길이로 전기 사양표를 다시 계산한다.
    /// L은 가열부(HeatingRun + 활성 Turn + FixedConnector)만 합산하고 ExitPiece는 외부 인출로 제외한다.
    /// </summary>
    private void RefreshElectricalTable()
    {
        if (!_electricalUiReady || ElectricalSpecGrid == null) return;

        if (!TryReadElectricalSettings(out HeatingWireSpec wire, out PowerSupplySpec supply, out string error))
        {
            ElectricalSpecGrid.ItemsSource = null;
            ElectricalSummaryText.Text = "입력 오류 ? " + error;
            _lastElectricalTable = null;
            return;
        }

        var inputs = new List<ElectricalCircuitInput>();
        var areas = _areas.Where(a => a?.Pattern != null && a.RoutePieceLengths.Length > 0).ToList();
        WorkingArea active = _activeArea;
        if (active?.Pattern != null && active.RoutePieceLengths.Length > 0 && !areas.Contains(active))
            areas.Add(active);

        foreach (WorkingArea area in areas)
        {
            string prefix = string.IsNullOrEmpty(area.Name) ? "A-" : area.Name + "-";
            List<CircuitLengthInfo> lengths = GetCircuitLengths(area, prefix);
            foreach (CircuitLengthInfo length in lengths)
                inputs.Add(new ElectricalCircuitInput(length.CircuitId, length.HeatingLengthMm));
        }

        ElectricalSpecTable table = ElectricalSpecTableBuilder.Build(inputs, wire, supply);
        _lastElectricalTable = table;

        var rows = table.Rows
            .Select(r => new ElectricalSpecUiRow(
                r.CircuitId, r.LengthM, r.ResistanceOhm, r.VoltageV, r.CurrentA, r.PowerW,
                r.Message, false))
            .ToList();
        if (rows.Count > 0)
        {
            rows.Add(new ElectricalSpecUiRow(
                "합계", null, null, null, table.TotalCurrentA, table.TotalPowerW, "", true));
        }
        ElectricalSpecGrid.ItemsSource = rows;

        ElectricalSummaryText.Text = table.Rows.Count == 0
            ? "도면에 발열 경로가 없습니다"
            : string.Format(CultureInfo.CurrentCulture,
                "총 {0}회로 ? 합계 {1:F2} A / {2:F1} W (인가전압 {3:F0} V)",
                table.Rows.Count, table.TotalCurrentA, table.TotalPowerW, supply.VoltageV);
    }

    /// <summary>
    /// 현재 회로별 가열부 길이를 표시 순서대로 만든다. 인출부 길이는 함께 보관한다.
    /// </summary>
    private List<CircuitLengthInfo> GetCircuitLengths(WorkingArea area, string prefix = "")
    {
        var result = new List<CircuitLengthInfo>();
        if (area?.Pattern == null) return result;

        List<List<int>> groups = CircuitPieceGroups(area);
        var lengthById = new Dictionary<int, double>();
        var heatingIds = new HashSet<int>();
        for (int i = 0; i < area.NativePieceIds.Length; i++)
        {
            lengthById[area.NativePieceIds[i]] = area.RoutePieceLengths.ElementAtOrDefault(i);
            if (i < area.Pattern.RoutePieces.Count
                && area.Pattern.RoutePieces[i].Kind != SerpentineRoutePieceKind.ExitPiece)
                heatingIds.Add(area.NativePieceIds[i]);
        }

        for (int i = 0; i < groups.Count; i++)
        {
            double heating = groups[i].Where(id => heatingIds.Contains(id))
                .Sum(id => lengthById.TryGetValue(id, out double value) ? value : 0);
            result.Add(new CircuitLengthInfo(prefix + (i + 1), heating,
                area.LeadLengths.ElementAtOrDefault(i)));
        }
        return result;
    }
}
