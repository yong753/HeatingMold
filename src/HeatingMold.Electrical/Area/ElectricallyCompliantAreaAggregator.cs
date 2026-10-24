using System.Collections.ObjectModel;
using HeatingMold.Core.Area;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Electrical.Area;

/// <summary>전기 상태와 지문이 맞는 회로의 적용면적만 합산합니다.</summary>
public sealed class ElectricallyCompliantAreaAggregator
{
    /// <summary>회로별 면적 상태와 전기 조건 만족 면적을 계산합니다.</summary>
    public ElectricallyCompliantAreaResult Aggregate(
        AppliedAreaAllocation allocation,
        IEnumerable<ElectricalResult> electricalResults)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ElectricalResult[] results = electricalResults?.ToArray()
            ?? throw new ArgumentNullException(nameof(electricalResults));
        if (results.Any(result => result is null))
            throw new ArgumentException("전기 결과 목록은 null 항목을 포함할 수 없습니다.", nameof(electricalResults));

        string[] issues = [];
        var duplicateResults = results
            .GroupBy(result => result.CircuitId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateResults.Length > 0)
        {
            return ElectricallyCompliantAreaResult.Invalid(
                allocation,
                ["DuplicateCircuitElectricalResult"]);
        }

        var resultByCircuit = results.ToDictionary(result => result.CircuitId);
        CircuitId[] unexpectedCircuits = resultByCircuit.Keys
            .Where(circuitId => !allocation.CircuitAppliedAreas.ContainsKey(circuitId))
            .ToArray();
        if (unexpectedCircuits.Length > 0)
        {
            return ElectricallyCompliantAreaResult.Invalid(
                allocation,
                ["ElectricalResultCircuitNotInAllocation"]);
        }

        if (results.Any(result => result.CircuitId.IsEmpty))
        {
            return ElectricallyCompliantAreaResult.Invalid(
                allocation,
                ["InvalidCircuitId"]);
        }

        if (results.Any(result => result.DependencyStamp != allocation.DependencyStamp))
        {
            return ElectricallyCompliantAreaResult.Stale(allocation, ["StaleDerivedResult"]);
        }

        double compliantArea = 0;
        double nonCompliantArea = 0;
        double unevaluatedArea = 0;
        bool hasIncompleteResult = false;
        foreach (KeyValuePair<CircuitId, AreaMm2> circuitArea in allocation.CircuitAppliedAreas)
        {
            if (!resultByCircuit.TryGetValue(circuitArea.Key, out ElectricalResult? result))
            {
                unevaluatedArea += circuitArea.Value.Value;
                hasIncompleteResult = true;
                continue;
            }

            switch (result.Status)
            {
                case ElectricalStatus.Pass:
                    compliantArea += circuitArea.Value.Value;
                    break;
                case ElectricalStatus.LimitExceeded:
                case ElectricalStatus.InvalidInput:
                    nonCompliantArea += circuitArea.Value.Value;
                    break;
                case ElectricalStatus.Incomplete:
                    unevaluatedArea += circuitArea.Value.Value;
                    hasIncompleteResult = true;
                    break;
                default:
                    return ElectricallyCompliantAreaResult.Invalid(
                        allocation,
                        ["UnknownElectricalStatus"]);
            }
        }

        if (!double.IsFinite(compliantArea) || !double.IsFinite(nonCompliantArea) || !double.IsFinite(unevaluatedArea))
        {
            return ElectricallyCompliantAreaResult.Invalid(
                allocation,
                ["AreaAggregationOutOfRange"]);
        }

        return new ElectricallyCompliantAreaResult(
            allocation,
            hasIncompleteResult ? AreaAggregationStatus.Incomplete : AreaAggregationStatus.Complete,
            new AreaMm2(compliantArea),
            new AreaMm2(nonCompliantArea),
            new AreaMm2(unevaluatedArea),
            Array.Empty<string>());
    }
}

/// <summary>전기 조건에 따른 면적 합계와 미평가 상태를 나타냅니다.</summary>
public enum AreaAggregationStatus
{
    /// <summary>모든 회로의 최신 전기 결과를 반영했습니다.</summary>
    Complete,

    /// <summary>회로 결과가 누락되거나 필수 전기값이 없습니다.</summary>
    Incomplete,

    /// <summary>면적과 전기 결과의 입력 지문이 다릅니다.</summary>
    StaleDerivedResult,

    /// <summary>중복·알 수 없는 회로나 합계 범위 오류가 있습니다.</summary>
    InvalidInput
}

/// <summary>배정 면적과 전기 상태를 함께 보여주는 집계 스냅샷입니다.</summary>
public sealed class ElectricallyCompliantAreaResult
{
    /// <summary>전기 결과를 적용한 원본 면적 분할입니다.</summary>
    public AppliedAreaAllocation Allocation { get; }

    /// <summary>전기 면적 집계의 유효 상태입니다.</summary>
    public AreaAggregationStatus Status { get; }

    /// <summary>Pass 상태 회로에 배정된 면적 합계입니다.</summary>
    public AreaMm2? ElectricallyCompliantAppliedAreaMm2 { get; }

    /// <summary>초과 또는 무효 판정 회로에 배정된 면적 합계입니다.</summary>
    public AreaMm2? NonCompliantAppliedAreaMm2 { get; }

    /// <summary>결과 누락·불완전 입력으로 평가하지 못한 면적입니다.</summary>
    public AreaMm2? UnevaluatedAppliedAreaMm2 { get; }

    /// <summary>집계 판단의 표준 진단 코드입니다.</summary>
    public IReadOnlyList<string> Issues { get; }

    /// <summary>회로별 전기 상태의 면적 합계와 완료 상태를 만듭니다.</summary>
    public ElectricallyCompliantAreaResult(
        AppliedAreaAllocation allocation,
        AreaAggregationStatus status,
        AreaMm2? electricallyCompliantAppliedAreaMm2,
        AreaMm2? nonCompliantAppliedAreaMm2,
        AreaMm2? unevaluatedAppliedAreaMm2,
        IEnumerable<string> issues)
    {
        Allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        if (status is AreaAggregationStatus.Complete or AreaAggregationStatus.Incomplete &&
            (!electricallyCompliantAppliedAreaMm2.HasValue ||
             !nonCompliantAppliedAreaMm2.HasValue ||
             !unevaluatedAppliedAreaMm2.HasValue))
        {
            throw new ArgumentException("평가 결과에는 각 전기 면적 합계가 필요합니다.");
        }
        if (status is AreaAggregationStatus.StaleDerivedResult or AreaAggregationStatus.InvalidInput &&
            (electricallyCompliantAppliedAreaMm2.HasValue ||
             nonCompliantAppliedAreaMm2.HasValue ||
             unevaluatedAppliedAreaMm2.HasValue))
        {
            throw new ArgumentException("stale 또는 무효 입력은 최신 면적 합계를 제공할 수 없습니다.");
        }

        ElectricallyCompliantAppliedAreaMm2 = electricallyCompliantAppliedAreaMm2;
        NonCompliantAppliedAreaMm2 = nonCompliantAppliedAreaMm2;
        UnevaluatedAppliedAreaMm2 = unevaluatedAppliedAreaMm2;
        string[] copiedIssues = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (copiedIssues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("집계 진단 코드는 비어 있을 수 없습니다.", nameof(issues));
        Issues = new ReadOnlyCollection<string>(copiedIssues);
    }

    internal static ElectricallyCompliantAreaResult Stale(AppliedAreaAllocation allocation, IEnumerable<string> issues) =>
        new(allocation, AreaAggregationStatus.StaleDerivedResult, null, null, null, issues);

    internal static ElectricallyCompliantAreaResult Invalid(AppliedAreaAllocation allocation, IEnumerable<string> issues) =>
        new(allocation, AreaAggregationStatus.InvalidInput, null, null, null, issues);
}
