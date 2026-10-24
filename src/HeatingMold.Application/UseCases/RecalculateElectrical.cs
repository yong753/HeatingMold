using System.Collections.ObjectModel;
using HeatingMold.Core.Area;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Validation;
using HeatingMold.Electrical.Area;
using HeatingMold.Electrical.Calculation;

namespace HeatingMold.Application.UseCases;

/// <summary>확정 길이 원장만 사용해 회로 전기값과 전기 만족 면적을 다시 계산합니다.</summary>
public sealed class RecalculateElectrical
{
    private readonly CircuitElectricalCalculator _calculator;
    private readonly ElectricallyCompliantAreaAggregator _areaAggregator;

    /// <summary>Electrical 계산기와 면적 집계기를 주입합니다.</summary>
    public RecalculateElectrical(
        CircuitElectricalCalculator? calculator = null,
        ElectricallyCompliantAreaAggregator? areaAggregator = null)
    {
        _calculator = calculator ?? new CircuitElectricalCalculator();
        _areaAggregator = areaAggregator ?? new ElectricallyCompliantAreaAggregator();
    }

    /// <summary>회로별 결과를 보존하고 선택 면적이 있으면 stamp를 검사해 다시 집계합니다.</summary>
    public OperationResult<ElectricalRecalculationResult> Execute(
        IEnumerable<CircuitElectricalInput> inputs,
        AppliedAreaAllocation? areaAllocation = null)
    {
        CircuitElectricalInput[] copiedInputs = inputs?.ToArray()
            ?? throw new ArgumentNullException(nameof(inputs));
        if (copiedInputs.Any(input => input is null))
            throw new ArgumentException("전기 계산 입력 목록은 null 항목을 포함할 수 없습니다.", nameof(inputs));

        ElectricalResult[] results = copiedInputs.Select(_calculator.Calculate).ToArray();
        ElectricallyCompliantAreaResult? areaResult = areaAllocation is null
            ? null
            : _areaAggregator.Aggregate(areaAllocation, results);
        return OperationResult<ElectricalRecalculationResult>.Success(
            new ElectricalRecalculationResult(results, areaAllocation, areaResult));
    }
}

/// <summary>전기 결과와 선택적인 전기 조건 만족 면적을 함께 전달합니다.</summary>
public sealed class ElectricalRecalculationResult
{
    /// <summary>회로마다 별도로 판정한 전기 계산 결과입니다.</summary>
    public IReadOnlyList<ElectricalResult> CircuitResults { get; }

    /// <summary>재계산에서 재사용한 Design 면적 분할입니다.</summary>
    public AppliedAreaAllocation? AreaAllocation { get; }

    /// <summary>Pass 회로만 더한 전기 만족 면적과 집계 상태입니다.</summary>
    public ElectricallyCompliantAreaResult? ElectricalAreaResult { get; }

    /// <summary>회로 결과와 면적 집계 결과를 방어 복사합니다.</summary>
    public ElectricalRecalculationResult(
        IEnumerable<ElectricalResult> circuitResults,
        AppliedAreaAllocation? areaAllocation,
        ElectricallyCompliantAreaResult? electricalAreaResult)
    {
        ElectricalResult[] copiedResults = circuitResults?.ToArray()
            ?? throw new ArgumentNullException(nameof(circuitResults));
        if (copiedResults.Any(result => result is null))
            throw new ArgumentException("재계산 결과에는 null 항목이 들어갈 수 없습니다.", nameof(circuitResults));
        if (electricalAreaResult is not null && areaAllocation is null)
            throw new ArgumentException("전기 면적 집계에는 원본 allocation이 필요합니다.", nameof(electricalAreaResult));

        CircuitResults = new ReadOnlyCollection<ElectricalResult>(copiedResults);
        AreaAllocation = areaAllocation;
        ElectricalAreaResult = electricalAreaResult;
    }
}
