using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Area;
using HeatingMold.Electrical.Calculation;

namespace HeatingMold.Electrical.Tests;

/// <summary>회로 전기 상태에 따른 실제 적용면적 집계를 검증합니다.</summary>
public sealed class ElectricallyCompliantAreaAggregatorTests
{
    private readonly ElectricallyCompliantAreaAggregator _aggregator = new();

    /// <summary>Pass 회로 면적만 만족 면적으로 집계하고 전체 면적과 구분합니다.</summary>
    [Fact]
    public void Aggregate_CountsOnlyPassCircuitAreas()
    {
        CircuitId passingId = CircuitId.New();
        CircuitId exceededId = CircuitId.New();
        DependencyStamp stamp = CreateStamp(1);
        AppliedAreaAllocation allocation = CreateAllocation(stamp, (passingId, 60), (exceededId, 40));
        ElectricalResult[] results =
        [
            Calculate(passingId, stamp, resistanceOhmsPerMeter: 4),
            Calculate(exceededId, stamp, resistanceOhmsPerMeter: 1)
        ];

        ElectricallyCompliantAreaResult result = _aggregator.Aggregate(allocation, results);

        Assert.Equal(AreaAggregationStatus.Complete, result.Status);
        Assert.Equal(100, result.Allocation.TotalAppliedAreaMm2.Value);
        Assert.Equal(60, result.ElectricallyCompliantAppliedAreaMm2?.Value);
        Assert.Equal(40, result.NonCompliantAppliedAreaMm2?.Value);
        Assert.Equal(0, result.UnevaluatedAppliedAreaMm2?.Value);
    }

    /// <summary>회로 결과가 빠지면 해당 면적을 미평가로 남기고 완료로 표시하지 않습니다.</summary>
    [Fact]
    public void Aggregate_MissingCircuitResultReturnsUnevaluatedArea()
    {
        CircuitId firstId = CircuitId.New();
        CircuitId missingId = CircuitId.New();
        DependencyStamp stamp = CreateStamp(1);
        AppliedAreaAllocation allocation = CreateAllocation(stamp, (firstId, 60), (missingId, 40));

        ElectricallyCompliantAreaResult result = _aggregator.Aggregate(
            allocation,
            [Calculate(firstId, stamp, resistanceOhmsPerMeter: 4)]);

        Assert.Equal(AreaAggregationStatus.Incomplete, result.Status);
        Assert.Equal(60, result.ElectricallyCompliantAppliedAreaMm2?.Value);
        Assert.Equal(40, result.UnevaluatedAppliedAreaMm2?.Value);
    }

    /// <summary>면적과 전기 결과의 stamp가 다르면 최신 면적 합계를 제공하지 않습니다.</summary>
    [Fact]
    public void Aggregate_StaleStampRejectsDerivedArea()
    {
        CircuitId circuitId = CircuitId.New();
        AppliedAreaAllocation allocation = CreateAllocation(CreateStamp(1), (circuitId, 100));
        ElectricalResult staleResult = Calculate(circuitId, CreateStamp(2), resistanceOhmsPerMeter: 4);

        ElectricallyCompliantAreaResult result = _aggregator.Aggregate(allocation, [staleResult]);

        Assert.Equal(AreaAggregationStatus.StaleDerivedResult, result.Status);
        Assert.Null(result.ElectricallyCompliantAppliedAreaMm2);
        Assert.Contains("StaleDerivedResult", result.Issues);
    }

    /// <summary>회로 결과가 중복되면 일부 결과만 임의로 선택하지 않습니다.</summary>
    [Fact]
    public void Aggregate_DuplicateResultsReturnsInvalidState()
    {
        CircuitId circuitId = CircuitId.New();
        DependencyStamp stamp = CreateStamp(1);
        AppliedAreaAllocation allocation = CreateAllocation(stamp, (circuitId, 100));
        ElectricalResult result = Calculate(circuitId, stamp, resistanceOhmsPerMeter: 4);

        ElectricallyCompliantAreaResult aggregate = _aggregator.Aggregate(allocation, [result, result]);

        Assert.Equal(AreaAggregationStatus.InvalidInput, aggregate.Status);
        Assert.Null(aggregate.ElectricallyCompliantAppliedAreaMm2);
        Assert.Contains("DuplicateCircuitElectricalResult", aggregate.Issues);
    }

    private static ElectricalResult Calculate(CircuitId circuitId, DependencyStamp stamp, double resistanceOhmsPerMeter)
    {
        var input = new CircuitElectricalInput(
            circuitId,
            new WireSpecification("SUS 316L", new LengthMm(0.3), new ResistanceOhmsPerMeter(resistanceOhmsPerMeter)),
            new Supply(new VoltageVrms(220), new VoltageVrms(220), new CurrentAmps(16)),
            new LengthBreakdown(
            [
                new LengthLedgerEntry(HeatingPathSegmentId.New(), null, LengthContributionKind.HeatingBody,
                    GeometryDomain.OnMold, new LengthMm(10_000), default)
            ]),
            stamp);
        return new CircuitElectricalCalculator().Calculate(input);
    }

    private static AppliedAreaAllocation CreateAllocation(
        DependencyStamp stamp,
        params (CircuitId CircuitId, double Area)[] circuitAreas)
    {
        double total = circuitAreas.Sum(area => area.Area);
        return new AppliedAreaAllocation(
            stamp,
            new AreaMm2(total),
            circuitAreas.Select(area => new KeyValuePair<CircuitId, AreaMm2>(
                area.CircuitId, new AreaMm2(area.Area))),
            default,
            new AreaAllocationQuality(0.05, 0.1, 1, 0.01, 0.01, 3, true, "area-v1"));
    }

    private static DependencyStamp CreateStamp(long revision) => new(
        new DesignRevision(revision),
        new string('a', 64),
        new string('b', 64),
        new string('c', 64),
        "electrical-v1");
}
