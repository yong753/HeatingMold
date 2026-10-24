#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Area;

namespace HeatingMold.App;

/// <summary>확정 세션의 전기·면적 결과를 화면 표시용 행으로 변환합니다.</summary>
internal static class ElectricalResultsPresentation
{
    /// <summary>현재 확정 세션의 계산 상태와 회로별 표시 데이터를 만듭니다.</summary>
    public static ElectricalPanelData Build(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DesignProject? project = snapshot.CurrentProject;
        if (project is null)
        {
            return new ElectricalPanelData(
                "확정 설계의 전기 계산 결과가 없습니다.",
                Array.Empty<CircuitElectricalDisplayRow>(),
                "적용면적 분할은 아직 계산되지 않았습니다.",
                string.Empty,
                Array.Empty<CircuitAppliedAreaDisplayRow>());
        }

        bool hasElectricalDraft = snapshot.CurrentInputDraft?.Kind == InputDraftKind.ElectricalSpecification;
        DependencyStamp? currentStamp = snapshot.Geometry?.DependencyStamp;
        IReadOnlyList<CircuitElectricalDisplayRow> circuitRows = BuildCircuitRows(
            project.Circuits,
            snapshot.ElectricalResults,
            currentStamp,
            hasElectricalDraft);
        string electricalStatus = BuildElectricalStatus(
            project.Circuits.Count,
            circuitRows,
            hasElectricalDraft);
        AreaPanelData areaPanel = BuildAreaPanel(snapshot, circuitRows, hasElectricalDraft);

        return new ElectricalPanelData(
            electricalStatus,
            circuitRows,
            areaPanel.Status,
            areaPanel.Summary,
            areaPanel.CircuitRows);
    }

    /// <summary>회로 ID와 계산 결과를 대조해 미계산·오래된 상태를 분리한 행을 만듭니다.</summary>
    internal static IReadOnlyList<CircuitElectricalDisplayRow> BuildCircuitRows(
        IEnumerable<Circuit> circuits,
        IEnumerable<ElectricalResult> electricalResults,
        DependencyStamp? currentStamp,
        bool hasElectricalDraft)
    {
        ArgumentNullException.ThrowIfNull(circuits);
        ArgumentNullException.ThrowIfNull(electricalResults);

        Circuit[] orderedCircuits = circuits.OrderBy(circuit => circuit.Sequence).ToArray();
        IGrouping<CircuitId, ElectricalResult>[] resultGroups = electricalResults
            .GroupBy(result => result.CircuitId)
            .ToArray();
        var resultsByCircuit = resultGroups.ToDictionary(group => group.Key, group => group.ToArray());
        var rows = new List<CircuitElectricalDisplayRow>(orderedCircuits.Length + resultGroups.Length);
        var displayedCircuitIds = new HashSet<CircuitId>();

        foreach (Circuit circuit in orderedCircuits)
        {
            displayedCircuitIds.Add(circuit.Id);
            string circuitLabel = FormatCircuitLabel(circuit.Sequence, circuit.Id);
            if (!resultsByCircuit.TryGetValue(circuit.Id, out ElectricalResult[]? matches))
            {
                rows.Add(CircuitElectricalDisplayRow.NotCalculated(circuitLabel, circuit.Id));
                continue;
            }

            if (matches.Length > 1)
            {
                rows.Add(CircuitElectricalDisplayRow.InvalidDuplicate(circuitLabel, matches[0]));
                continue;
            }

            rows.Add(CircuitElectricalDisplayRow.FromResult(
                circuitLabel,
                matches[0],
                currentStamp is not null && matches[0].DependencyStamp == currentStamp,
                hasElectricalDraft));
        }

        foreach (IGrouping<CircuitId, ElectricalResult> resultGroup in resultGroups
                     .Where(group => !displayedCircuitIds.Contains(group.Key)))
        {
            ElectricalResult result = resultGroup.First();
            rows.Add(CircuitElectricalDisplayRow.Orphan(
                FormatOrphanCircuitLabel(result.CircuitId),
                result));
        }

        return rows.AsReadOnly();
    }

    /// <summary>회로 수와 각 행의 상태를 한글 요약으로 만듭니다.</summary>
    private static string BuildElectricalStatus(
        int circuitCount,
        IReadOnlyList<CircuitElectricalDisplayRow> rows,
        bool hasElectricalDraft)
    {
        if (circuitCount == 0)
            return "전기 계산 대상 회로가 없습니다.";

        int passingCount = rows.Count(row => row.StatusKind == "Pass");
        int exceededCount = rows.Count(row => row.StatusKind == "LimitExceeded");
        int incompleteCount = rows.Count(row => row.StatusKind is "Missing" or "Incomplete");
        int invalidCount = rows.Count(row => row.StatusKind == "InvalidInput");
        int staleCount = rows.Count(row => row.StatusKind == "Stale");
        string summary = string.Format(
            CultureInfo.CurrentCulture,
            "통과 {0} · 한도 초과 {1} · 미완료 {2} · 입력 오류 {3} · 이전 결과 {4}",
            passingCount,
            exceededCount,
            incompleteCount,
            invalidCount,
            staleCount);

        return hasElectricalDraft
            ? string.Concat("전기 입력 초안 미적용 · 아래 값은 마지막 확정 조건 기준입니다.\n", summary)
            : summary;
    }

    /// <summary>면적 분할, 전기 상태별 면적과 계산 품질을 함께 표시합니다.</summary>
    private static AreaPanelData BuildAreaPanel(
        SessionSnapshot snapshot,
        IReadOnlyList<CircuitElectricalDisplayRow> electricalRows,
        bool hasElectricalDraft)
    {
        AppliedAreaAllocation? allocation = snapshot.AreaAllocation;
        if (allocation is null)
        {
            return new AreaPanelData(
                "적용면적 분할은 아직 계산되지 않았습니다.",
                string.Empty,
                Array.Empty<CircuitAppliedAreaDisplayRow>());
        }

        bool allocationIsCurrent = snapshot.Geometry is { } geometry &&
            allocation.DependencyStamp == geometry.DependencyStamp;
        string freshness = allocationIsCurrent ? "현재 분할" : "이전 분할 · 현재 기하 결과와 지문이 다릅니다.";
        string convergence = allocation.Quality.IsConverged
            ? "면적 보존 수렴"
            : "면적 보존 수렴 미확인";
        string status = string.Format(
            CultureInfo.CurrentCulture,
            "{0} · {1} · 분할 반복 {2}회",
            freshness,
            convergence,
            allocation.Quality.IterationCount);

        var summaryLines = new List<string>
        {
            $"전체 적용면적: {FormatArea(allocation.TotalAppliedAreaMm2.Value)} cm²",
            $"미배정 적용면적: {FormatArea(allocation.UnassignedAppliedAreaMm2.Value)} cm²",
            $"전체 면적 잔차: {FormatArea(allocation.AreaResidualMm2)} cm²",
            $"측정 면적 오차 상한: {FormatArea(allocation.Quality.MeasuredAreaErrorBoundMm2)} cm²",
            $"배정 허용 잔차: {FormatArea(allocation.Quality.ConservationToleranceMm2)} cm²"
        };

        ElectricallyCompliantAreaResult? electricalArea = snapshot.ElectricalAreaResult;
        if (electricalArea is null)
        {
            summaryLines.Add("전기 조건별 면적 합계: 계산되지 않음");
        }
        else if (!allocationIsCurrent || electricalArea.Allocation.DependencyStamp != allocation.DependencyStamp)
        {
            summaryLines.Add("전기 조건별 면적 합계: 오래된 결과라 현재 값으로 표시하지 않음");
        }
        else
        {
            summaryLines.Add(string.Concat(
                "전기 조건별 집계: ",
                FormatAreaAggregationStatus(electricalArea.Status)));
            if (electricalArea.ElectricallyCompliantAppliedAreaMm2 is { } compliantArea)
                summaryLines.Add($"전기 한도 통과 회로 적용면적: {FormatArea(compliantArea.Value)} cm²");
            if (electricalArea.NonCompliantAppliedAreaMm2 is { } nonCompliantArea)
                summaryLines.Add($"초과·무효 회로 적용면적: {FormatArea(nonCompliantArea.Value)} cm²");
            if (electricalArea.UnevaluatedAppliedAreaMm2 is { } unevaluatedArea)
                summaryLines.Add($"미평가 회로 적용면적: {FormatArea(unevaluatedArea.Value)} cm²");
        }

        if (hasElectricalDraft)
            summaryLines.Add("전기 입력 초안은 미적용 상태입니다. 전기별 면적은 마지막 확정 조건으로 집계했습니다.");
        summaryLines.Add("기하상 적용면적이며 실제 온도 도달 면적이나 가열 성능을 뜻하지 않습니다.");

        var electricalStatusByCircuit = electricalRows
            .GroupBy(row => row.CircuitId)
            .ToDictionary(group => group.Key, group => group.First());
        CircuitAppliedAreaDisplayRow[] circuitRows = allocation.CircuitAppliedAreas
            .OrderBy(pair => pair.Key.Value)
            .Select(pair =>
            {
                bool hasElectricalRow = electricalStatusByCircuit.TryGetValue(
                    pair.Key,
                    out CircuitElectricalDisplayRow? row);
                string circuitStatus = !allocationIsCurrent
                    ? "이전 면적 분할"
                    : !hasElectricalRow
                        ? "전기 결과 없음"
                        : row!.StatusKind == "Stale"
                            ? "이전 전기 결과"
                            : row.Status;
                if (allocationIsCurrent && !allocation.Quality.IsConverged)
                    circuitStatus = string.Concat("면적 수렴 미확인 · ", circuitStatus);
                if (hasElectricalDraft && allocationIsCurrent)
                    circuitStatus = string.Concat("전기 초안 미적용 · ", circuitStatus);

                return new CircuitAppliedAreaDisplayRow(
                    string.Concat(
                        hasElectricalRow ? row!.CircuitLabel : FormatOrphanCircuitLabel(pair.Key),
                        " · ",
                        FormatArea(pair.Value.Value),
                        " cm²"),
                    circuitStatus);
            })
            .ToArray();

        return new AreaPanelData(status, string.Join(Environment.NewLine, summaryLines), circuitRows);
    }

    /// <summary>한도·입력 문제 코드를 작업자가 읽을 수 있는 문장으로 바꿉니다.</summary>
    internal static string FormatResultDetails(ElectricalResult result, bool isCurrent, bool hasElectricalDraft)
    {
        var lines = new List<string>();
        if (!isCurrent)
            lines.Add("이전 계산 결과이며 현재 확정 형상에 적용할 수 없습니다.");
        if (hasElectricalDraft)
            lines.Add("전기 입력 초안 미적용 · 마지막 확정 조건의 결과입니다.");

        foreach (ElectricalLimitResult limit in result.Limits.OrderBy(item => item.Kind))
        {
            string label = GetLimitLabel(limit.Kind);
            string state = FormatLimitStatus(limit.Status);
            if (limit.MeasuredValue is { } measured && limit.AllowedValue is { } allowed)
            {
                string exceedance = limit.ExceedanceRatio is { } ratio && limit.Status == LimitCheckStatus.Exceeded
                    ? string.Format(CultureInfo.CurrentCulture, " · {0:P1} 초과", ratio)
                    : string.Empty;
                lines.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}: {1} / {2} {3} · {4}{5}",
                    label,
                    FormatNumber(measured),
                    FormatNumber(allowed),
                    limit.UnitSymbol,
                    state,
                    exceedance));
            }
            else
            {
                lines.Add(string.Concat(label, ": ", state));
            }
        }

        string[] missingFields = result.InputIssues
            .Where(issue => issue.StartsWith("Missing:", StringComparison.Ordinal))
            .Select(issue => issue["Missing:".Length..])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] invalidIssues = result.InputIssues
            .Where(issue => !issue.StartsWith("Missing:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missingFields.Length > 0)
            lines.Add(string.Concat("필수 입력 누락: ", string.Join(", ", missingFields.Select(TranslateMissingField))));
        if (invalidIssues.Length > 0)
            lines.Add(string.Concat("무효 입력 확인 필요: ", string.Join(", ", invalidIssues.Select(TranslateInputIssue))));
        if (result.FailureCode == "InvalidInput")
            lines.Add("입력 값 또는 계산 범위를 확인해야 합니다.");
        if (result.Status == ElectricalStatus.Incomplete && result.InputIssues.Count == 0 &&
            !result.Limits.Any(limit => limit.Status == LimitCheckStatus.Missing))
        {
            lines.Add("필수 전기 입력이 없어 판정을 마치지 못했습니다.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>회로의 제한별 실제값과 표시 한도를 묶어 출력합니다.</summary>
    internal static string FormatValueAndLimit(
        ElectricalResult result,
        ElectricalLimitKind kind,
        double? measuredValue,
        string unit)
    {
        ElectricalLimitResult? limit = result.Limits.FirstOrDefault(item => item.Kind == kind);
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}: {1} / {2} {3}",
            GetLimitValueLabel(kind),
            FormatNullable(measuredValue),
            FormatNullable(limit?.AllowedValue),
            unit);
    }

    /// <summary>저항에 포함한 발열사 구간과 제외한 연결선 길이를 구분해 표시합니다.</summary>
    internal static string FormatLengthBreakdown(LengthBreakdown lengthBreakdown) => string.Format(
        CultureInfo.CurrentCulture,
        "본 발열 {0} + 발열사 연장 {1} + 발열 여유 {2} = 저항 길이 {3} mm\n별도 연결선 {4} mm · 연결선 여유 {5} mm (저항 제외)",
        FormatNumber(lengthBreakdown.HeatingBodyMm.Value),
        FormatNumber(lengthBreakdown.HeatingExtensionMm.Value),
        FormatNumber(lengthBreakdown.HeatingExtraAllowanceMm.Value),
        FormatNumber(lengthBreakdown.ResistanceLengthMm.Value),
        FormatNumber(lengthBreakdown.SeparateConnectionWireMm.Value),
        FormatNumber(lengthBreakdown.SeparateConnectionWireExtraAllowanceMm.Value));

    /// <summary>회로 안정 ID를 짧은 식별자와 순번으로 표시합니다.</summary>
    private static string FormatCircuitLabel(int sequence, CircuitId circuitId) => string.Format(
        CultureInfo.CurrentCulture,
        "회로 {0} · {1}",
        sequence + 1,
        ShortId(circuitId.Value));

    /// <summary>설계에서 찾을 수 없는 결과 ID를 식별 가능한 짧은 문자열로 표시합니다.</summary>
    private static string FormatOrphanCircuitLabel(CircuitId circuitId) =>
        string.Concat("설계 회로 없음 · ", ShortId(circuitId.Value));

    /// <summary>전역 고유 식별자의 앞 여덟 글자를 화면용 이름으로 만듭니다.</summary>
    private static string ShortId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture)[..8];

    /// <summary>회로 결과 상태를 한글로 표시합니다.</summary>
    internal static string FormatElectricalStatus(ElectricalStatus status) => status switch
    {
        ElectricalStatus.Pass => "전기 한도 통과",
        ElectricalStatus.LimitExceeded => "전기 한도 초과",
        ElectricalStatus.Incomplete => "전기 판정 미완료",
        ElectricalStatus.InvalidInput => "전기 입력 오류",
        _ => "알 수 없는 전기 상태"
    };

    /// <summary>전압·전류·전력 한도 종류를 한글 이름으로 바꿉니다.</summary>
    private static string GetLimitLabel(ElectricalLimitKind kind) => kind switch
    {
        ElectricalLimitKind.MaximumVoltage => "최대 전압",
        ElectricalLimitKind.MaximumCurrent => "최대 전류",
        ElectricalLimitKind.MaximumPower => "최대 전력(선택)",
        _ => "전기 한도"
    };

    /// <summary>전압·전류·전력 행에 쓸 짧은 한도 이름을 반환합니다.</summary>
    private static string GetLimitValueLabel(ElectricalLimitKind kind) => kind switch
    {
        ElectricalLimitKind.MaximumVoltage => "전압/최대",
        ElectricalLimitKind.MaximumCurrent => "전류/최대",
        ElectricalLimitKind.MaximumPower => "전력/최대(선택)",
        _ => "실제/최대"
    };

    /// <summary>개별 한도 판정 상태를 한글로 바꿉니다.</summary>
    private static string FormatLimitStatus(LimitCheckStatus status) => status switch
    {
        LimitCheckStatus.WithinLimit => "통과",
        LimitCheckStatus.Exceeded => "초과",
        LimitCheckStatus.Missing => "한도 입력 없음",
        LimitCheckStatus.Invalid => "수치 확인 필요",
        _ => "판정 불가"
    };

    /// <summary>전기 면적 집계 상태를 한글로 표시합니다.</summary>
    private static string FormatAreaAggregationStatus(AreaAggregationStatus status) => status switch
    {
        AreaAggregationStatus.Complete => "모든 회로 평가 완료",
        AreaAggregationStatus.Incomplete => "일부 회로 미평가",
        AreaAggregationStatus.StaleDerivedResult => "오래된 결과",
        AreaAggregationStatus.InvalidInput => "입력 오류",
        _ => "알 수 없는 집계 상태"
    };

    /// <summary>입력 진단 코드를 화면에서 읽을 수 있는 한글로 바꿉니다.</summary>
    private static string TranslateInputIssue(string issue) => issue switch
    {
        "InvalidCircuitId" => "회로 ID",
        "InvalidMaterialName" => "재료명",
        "InvalidDiameter" => "도체 직경",
        "InvalidResistancePerMeter" => "단위 길이당 저항",
        "InvalidResistanceLength" => "저항 대상 길이",
        "InvalidActualVoltage" => "실제 전압",
        "InvalidMaximumVoltage" => "최대 전압",
        "InvalidMaximumCurrent" => "최대 전류",
        "InvalidMaximumPower" => "최대 전력",
        _ => "입력 수치"
    };

    /// <summary>누락된 필드 코드를 사용자가 찾을 입력 항목 이름으로 바꿉니다.</summary>
    private static string TranslateMissingField(string field) => field switch
    {
        "ActualVoltageVrms" => "실제 전압",
        "MaximumVoltageVrms" => "최대 전압",
        "MaximumCurrentAmps" => "최대 전류",
        "MaximumPowerWatts" => "최대 전력",
        _ => "필수 입력"
    };

    /// <summary>선택 값이 없으면 대시로 표시하고 유한한 수치만 반올림해 출력합니다.</summary>
    private static string FormatNullable(double? value) => value.HasValue
        ? FormatNumber(value.Value)
        : "—";

    /// <summary>전기값을 판정과 혼동하지 않도록 표시용 두 자리로 반올림합니다.</summary>
    internal static string FormatNumber(double value) => value.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>면적 단위를 mm²에서 cm²로 바꾸어 두 자리로 표시합니다.</summary>
    private static string FormatArea(double areaMm2) => FormatNumber(areaMm2 / 100d);

}

/// <summary>전기·면적 패널의 한 번에 적용할 표시 데이터를 보관합니다.</summary>
internal sealed class ElectricalPanelData
{
    /// <summary>회로별 전기 결과와 현재성 요약입니다.</summary>
    public string ElectricalStatus { get; }

    /// <summary>회로별 전기 결과 목록입니다.</summary>
    public IReadOnlyList<CircuitElectricalDisplayRow> CircuitRows { get; }

    /// <summary>면적 분할의 현재성·수렴 상태입니다.</summary>
    public string AreaStatus { get; }

    /// <summary>전체·전기 상태별 면적과 품질 정보를 줄 단위로 보관합니다.</summary>
    public string AreaSummary { get; }

    /// <summary>회로별 배정 면적과 전기 상태를 보관합니다.</summary>
    public IReadOnlyList<CircuitAppliedAreaDisplayRow> CircuitAreaRows { get; }

    /// <summary>전기·면적 표시 데이터의 읽기 전용 묶음을 만듭니다.</summary>
    public ElectricalPanelData(
        string electricalStatus,
        IReadOnlyList<CircuitElectricalDisplayRow> circuitRows,
        string areaStatus,
        string areaSummary,
        IReadOnlyList<CircuitAppliedAreaDisplayRow> circuitAreaRows)
    {
        ElectricalStatus = electricalStatus;
        CircuitRows = circuitRows;
        AreaStatus = areaStatus;
        AreaSummary = areaSummary;
        CircuitAreaRows = circuitAreaRows;
    }
}

/// <summary>회로 하나의 전기 수치와 판정 설명을 화면에 표시합니다.</summary>
internal sealed class CircuitElectricalDisplayRow
{
    /// <summary>순번과 안정 ID 일부를 포함한 회로 이름입니다.</summary>
    public string CircuitLabel { get; }

    /// <summary>사용자가 확인할 전기 계산 상태입니다.</summary>
    public string Status { get; }

    /// <summary>색상 스타일과 집계에서 쓰는 상태 키입니다.</summary>
    public string StatusKind { get; }

    /// <summary>저항 대상 길이와 계산 저항입니다.</summary>
    public string LengthAndResistance { get; }

    /// <summary>발열 경로·발열사 연장·연결선의 길이 포함 규칙입니다.</summary>
    public string LengthBreakdownDetails { get; }

    /// <summary>실제 전압과 회로별 최대 전압입니다.</summary>
    public string VoltageAndLimit { get; }

    /// <summary>회로 전류와 회로별 최대 전류입니다.</summary>
    public string CurrentAndLimit { get; }

    /// <summary>회로 전력과 선택된 최대 전력입니다.</summary>
    public string PowerAndLimit { get; }

    /// <summary>한도별 세부 판정·초과량·입력 누락 설명입니다.</summary>
    public string Details { get; }

    /// <summary>면적 행과 회로 결과를 안정 ID로 대응합니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>모든 표시 문자열과 회로 ID를 보관하는 행을 만듭니다.</summary>
    private CircuitElectricalDisplayRow(
        CircuitId circuitId,
        string circuitLabel,
        string status,
        string statusKind,
        string lengthAndResistance,
        string lengthBreakdownDetails,
        string voltageAndLimit,
        string currentAndLimit,
        string powerAndLimit,
        string details)
    {
        CircuitId = circuitId;
        CircuitLabel = circuitLabel;
        Status = status;
        StatusKind = statusKind;
        LengthAndResistance = lengthAndResistance;
        LengthBreakdownDetails = lengthBreakdownDetails;
        VoltageAndLimit = voltageAndLimit;
        CurrentAndLimit = currentAndLimit;
        PowerAndLimit = powerAndLimit;
        Details = details;
    }

    /// <summary>전기 계산 결과가 없는 회로를 0으로 오인하지 않게 표시합니다.</summary>
    public static CircuitElectricalDisplayRow NotCalculated(string circuitLabel, CircuitId circuitId) => new(
        circuitId,
        circuitLabel,
        "계산 결과 없음",
        "Missing",
        "저항 대상 길이 — · 저항 —",
        "길이 원장 계산 결과 없음",
        "전압/최대 — / — V RMS",
        "전류/최대 — / — A",
        "전력/최대 — / — W",
        "현재 확정 설계에서 계산한 결과가 없습니다.");

    /// <summary>동일 회로 결과가 중복된 경우 수치를 유효 판정처럼 표시하지 않습니다.</summary>
    public static CircuitElectricalDisplayRow InvalidDuplicate(string circuitLabel, ElectricalResult result) => new(
        result.CircuitId,
        circuitLabel,
        "중복 전기 결과 · 판정 불가",
        "InvalidInput",
        "저항 대상 길이 — · 저항 —",
        "길이 원장 표시 중단 · 중복 결과를 확인하세요",
        "전압/최대 — / — V RMS",
        "전류/최대 — / — A",
        "전력/최대 — / — W",
        "같은 회로 ID의 전기 결과가 여러 개 있어 이 행의 수치를 숨겼습니다.");

    /// <summary>현재 계산과 오래된 결과를 구분해 표시 문자열을 구성합니다.</summary>
    public static CircuitElectricalDisplayRow FromResult(
        string circuitLabel,
        ElectricalResult result,
        bool isCurrent,
        bool hasElectricalDraft)
    {
        string statusKind = isCurrent ? result.Status.ToString() : "Stale";
        string formattedStatus = ElectricalResultsPresentation.FormatElectricalStatus(result.Status);
        string status = isCurrent
            ? formattedStatus
            : string.Concat("이전 결과 · ", formattedStatus);
        double? resistanceLengthMeters = result.ResistanceLengthMeters;
        string lengthText = resistanceLengthMeters.HasValue
            ? ElectricalResultsPresentation.FormatNumber(resistanceLengthMeters.Value)
            : "—";
        string resistanceText = result.ResistanceOhms is { } resistance
            ? ElectricalResultsPresentation.FormatNumber(resistance.Value)
            : "—";

        return new CircuitElectricalDisplayRow(
            result.CircuitId,
            circuitLabel,
            status,
            statusKind,
            $"저항 대상 길이 {lengthText} m · 저항 {resistanceText} Ω",
            ElectricalResultsPresentation.FormatLengthBreakdown(result.LengthBreakdown),
            ElectricalResultsPresentation.FormatValueAndLimit(
                result, ElectricalLimitKind.MaximumVoltage, result.ActualVoltageVrms?.Value, "V RMS"),
            ElectricalResultsPresentation.FormatValueAndLimit(
                result, ElectricalLimitKind.MaximumCurrent, result.CurrentAmps?.Value, "A"),
            ElectricalResultsPresentation.FormatValueAndLimit(
                result, ElectricalLimitKind.MaximumPower, result.PowerWatts?.Value, "W"),
            ElectricalResultsPresentation.FormatResultDetails(result, isCurrent, hasElectricalDraft));
    }

    /// <summary>설계 회로에서 빠진 결과를 오래된 참조로 따로 표시합니다.</summary>
    public static CircuitElectricalDisplayRow Orphan(string circuitLabel, ElectricalResult result) => new(
        result.CircuitId,
        circuitLabel,
        "설계 회로와 연결되지 않은 결과",
        "Stale",
        "저항 대상 길이 — · 저항 —",
        "길이 원장을 현재 회로와 연결할 수 없습니다.",
        "전압/최대 — / — V RMS",
        "전류/최대 — / — A",
        "전력/최대 — / — W",
        "현재 프로젝트의 회로 목록과 연결되지 않은 결과입니다.");
}

/// <summary>회로별 기하상 적용면적과 전기 판정 상태를 한 줄로 표시합니다.</summary>
internal sealed class CircuitAppliedAreaDisplayRow
{
    /// <summary>회로 식별자와 배정된 적용면적입니다.</summary>
    public string Text { get; }

    /// <summary>해당 회로의 전기 상태 또는 오래됨 안내입니다.</summary>
    public string Status { get; }

    /// <summary>면적과 전기 상태를 한글 문장으로 묶어 보관합니다.</summary>
    public CircuitAppliedAreaDisplayRow(string text, string status)
    {
        Text = string.Concat(text, " · ", status);
        Status = status;
    }
}

/// <summary>면적 패널의 요약과 회로별 행을 함께 전달합니다.</summary>
internal sealed class AreaPanelData
{
    /// <summary>면적 분할이 현재 형상과 일치하는지 나타냅니다.</summary>
    public string Status { get; }

    /// <summary>전체·전기 상태별 면적과 품질 설명입니다.</summary>
    public string Summary { get; }

    /// <summary>회로별 배정 면적과 상태입니다.</summary>
    public IReadOnlyList<CircuitAppliedAreaDisplayRow> CircuitRows { get; }

    /// <summary>면적 패널에 표시할 상태와 행을 보관합니다.</summary>
    public AreaPanelData(
        string status,
        string summary,
        IReadOnlyList<CircuitAppliedAreaDisplayRow> circuitRows)
    {
        Status = status;
        Summary = summary;
        CircuitRows = circuitRows;
    }
}
