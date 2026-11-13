using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App;

/// <summary>
/// 검토 표에 표시할 회로 한 행. 계산된 값과 '표시만 남기는' 값을 한 구조로 전달한다.
/// 검토 탭 표와 뷰포트 확대 보기가 같은 행 집합을 쓰도록 생성도 한 곳에서만 한다.
/// </summary>
internal sealed record ElectricalReviewCircuitRow(
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

/// <summary>
/// 표시용 회로 목록의 한 항목. 확정 회로 identity와 검증된 실측 길이만 담는다.
/// 실제 3D 길이가 없으면 <see cref="LengthMm"/>가 null이며 0으로 채우지 않는다.
/// </summary>
internal sealed record ReviewCircuitDisplayEntry(string CircuitId, double? LengthMm, bool IsStale);

internal static class ElectricalReviewRows
{
    /// <summary>표시 목록을 못 만든 이유. 표시용 목록은 비고 계산·출력 자격에는 쓰지 않는다.</summary>
    public const string CircuitListFailureNote = "회로 목록을 만들 수 없습니다";

    /// <summary>
    /// 확정 회로 ID·길이를 표시용 목록으로 만든다. 계산 자격(Ready)과 무관하다.
    /// 패턴이 없으면 빈 목록(실패 아님), 회로 색인이 깨졌으면 실패 이유를 돌려준다.
    /// </summary>
    public static bool TryBuildDisplayEntries(
        WorkingArea area,
        bool stale,
        out IReadOnlyList<ReviewCircuitDisplayEntry> entries,
        out string reason)
    {
        entries = Array.Empty<ReviewCircuitDisplayEntry>();
        reason = "";
        if (area == null) return true;

        var layout = area.Circuits;
        if (area.Pattern == null || layout == null || layout.Count == 0) return true;

        var indices = area.CircuitPieceIndices;
        if (indices == null || indices.Count != layout.Count)
        {
            reason = "확정 회로 구성 정보를 읽을 수 없습니다";
            return false;
        }

        string prefix = area.Name + "-";
        var list = new List<ReviewCircuitDisplayEntry>(layout.Count);
        for (int i = 0; i < layout.Count; i++)
        {
            var circuitIndices = indices[i];
            if (circuitIndices == null || circuitIndices.Count == 0)
            {
                reason = string.Format(CultureInfo.CurrentCulture,
                    "회로 {0}의 확정 조각 정보를 읽을 수 없습니다", i + 1);
                return false;
            }

            double lengthMm = 0.0;
            var lengths = area.RoutePieceLengths;
            foreach (int index in circuitIndices)
            {
                // 확정 조각 인덱스가 범위를 벗어나거나 길이가 비어 있으면 0으로 덮지 않고 실패한다.
                if (index < 0 || lengths == null || index >= lengths.Length
                    || double.IsNaN(lengths[index]) || double.IsInfinity(lengths[index]))
                {
                    reason = string.Format(CultureInfo.CurrentCulture,
                        "회로 {0}의 확정 실측 길이를 읽을 수 없습니다", i + 1);
                    return false;
                }
                lengthMm += lengths[index];
            }

            if (!(lengthMm > 0.0))
            {
                reason = string.Format(CultureInfo.CurrentCulture,
                    "회로 {0}의 확정 실측 길이가 올바르지 않습니다", i + 1);
                return false;
            }

            list.Add(new ReviewCircuitDisplayEntry(prefix + (i + 1).ToString(CultureInfo.InvariantCulture), lengthMm, stale));
        }

        entries = list;
        return true;
    }

    /// <summary>
    /// 표 행을 만든다. 계산 Table이 있는 회로는 Table 행을 그대로 쓰고,
    /// Table에서 빠진 확정 회로(재생성 필요 등)는 계산값 없이 표시 전용 행으로 남긴다.
    /// </summary>
    public static IReadOnlyList<ElectricalReviewCircuitRow> Build(IReadOnlyList<ReviewCircuitDisplayEntry> display, ElectricalSpecTable table)
    {
        var rows = new List<ElectricalReviewCircuitRow>();
        var tableRows = new Dictionary<string, ElectricalSpecRow>(StringComparer.Ordinal);
        if (table != null)
        {
            foreach (ElectricalSpecRow row in table.Rows) tableRows[row.CircuitId] = row;
        }

        // 현재 조건의 길이 기준으로 정렬된 Table 순서를 유지하고, Table에 없는 표시 행만 뒤에 붙인다.
        if (table != null)
        {
            foreach (ElectricalSpecRow row in table.Rows)
            {
                string note = row.Note;
                if (display != null)
                {
                    ReviewCircuitDisplayEntry entry = display.FirstOrDefault(e => string.Equals(e.CircuitId, row.CircuitId, StringComparison.Ordinal));
                    if (entry != null && entry.IsStale) note = NoteFor(entry.IsStale, note);
                }

                rows.Add(FromTable(row, note));
            }
        }

        if (display == null) return rows;
        foreach (ReviewCircuitDisplayEntry entry in display)
        {
            if (tableRows.ContainsKey(entry.CircuitId)) continue;
            // 길이만 유지하고 저항·전력·전압·전류는 비운다. 0으로 채우면 계산된 값처럼 보인다.
            rows.Add(new ElectricalReviewCircuitRow(
                entry.CircuitId,
                null,
                entry.LengthMm.HasValue ? entry.LengthMm.Value / 1000.0 : null,
                null, null, null, null,
                "—",
                NoteFor(entry.IsStale, ""),
                false));
        }

        return rows;
    }

    /// <summary>계산된 Table 행에 표시 상태 비고를 덧붙인다.</summary>
    public static string NoteFor(bool stale, string baseNote)
    {
        if (stale)
        {
            const string staleNote = "케이스 변경 — 재생성 필요 · 이전 확정 길이";
            return string.IsNullOrEmpty(baseNote) ? staleNote : baseNote + " · " + staleNote;
        }

        return baseNote ?? "";
    }

    private static ElectricalReviewCircuitRow FromTable(ElectricalSpecRow row, string note)
    {
        return new ElectricalReviewCircuitRow(
            row.CircuitId,
            row.TargetPowerW,
            row.LengthM,
            row.ResistanceOhm,
            // 배분 전력이 없는 행은 0 V/0 A가 아니라 '계산 불가'다. 길이·저항은 확정값이므로 남긴다.
            row.IsTargetPowerMissing ? null : row.VoltageV,
            row.IsTargetPowerMissing ? null : row.CurrentA,
            row.IsTargetPowerMissing ? null : row.PowerW,
            VerdictText(row),
            note,
            false);
    }

    /// <summary>표의 판정 열. 미입력과 계산 오류는 OK/NG가 아니므로 '—'로 구분한다.</summary>
    public static string VerdictText(ElectricalSpecRow row)
    {
        if (row == null) return "";
        if (row.IsTargetPowerMissing) return "—";
        if (!row.IsValid) return "—";
        CircuitAssessment assessment = row.Assessment;
        if (assessment == null) return "";
        return assessment.IsWithinLimits ? "OK" : "NG";
    }
}
