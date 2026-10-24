using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Validation;

/// <summary>기하 곡선 최단거리로 발열 경로 간 외곽 간격과 자기 접촉을 검사합니다.</summary>
public sealed class ClearanceValidator
{
    /// <summary>측정 오차 구간 전체가 최소 간격 이상일 때만 통과시킵니다.</summary>
    public ValidationReport Validate(
        IEnumerable<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        IGeometryQueries geometryQueries,
        string ruleVersion,
        CancellationToken cancellationToken = default) => Validate(
            curves,
            wireDiameterMm,
            requiredClearGapMm,
            tolerances,
            geometryQueries,
            ruleVersion,
            Array.Empty<CurveSegmentAdjacency>(),
            cancellationToken);

    /// <summary>설계가 확인한 연속 구간 관계를 전달해 접점 제외를 제한합니다.</summary>
    public ValidationReport Validate(
        IEnumerable<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        IGeometryQueries geometryQueries,
        string ruleVersion,
        IReadOnlyCollection<CurveSegmentAdjacency> localAdjacencies,
        CancellationToken cancellationToken = default)
    {
        CurveGeometrySnapshot[] heatingCurves = curves?.Where(curve => curve.HeatingPathSegmentId.HasValue).ToArray()
            ?? throw new ArgumentNullException(nameof(curves));
        ArgumentNullException.ThrowIfNull(tolerances);
        ArgumentNullException.ThrowIfNull(geometryQueries);
        ArgumentNullException.ThrowIfNull(localAdjacencies);
        if (wireDiameterMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(wireDiameterMm));
        if (requiredClearGapMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredClearGapMm));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("간격 검증 규칙 버전이 필요합니다.", nameof(ruleVersion));
        if (heatingCurves.Any(curve => !curve.Approximation.IsComplete))
        {
            ValidationIssue[] incompleteIssues = heatingCurves
                .Where(curve => !curve.Approximation.IsComplete)
                .Select(curve => ValidationIssueFactory.Error(
                    "IncompleteClearanceCurveApproximation",
                    "불완전한 곡선 근사로 최소 간격을 확정할 수 없습니다.",
                    ruleVersion,
                    [curve.HeatingPathSegmentId!.Value.Value]))
                .ToArray();
            return new ValidationReport(incompleteIssues);
        }
        if (heatingCurves.Length == 0)
        {
            return new ValidationReport(
            [
                ValidationIssueFactory.Error(
                    "MissingHeatingCurveForClearance",
                    "최소 간격을 확인할 활성 발열 곡선이 없습니다.",
                    ruleVersion)
            ]);
        }

        GeometryQueryResult<ClearanceMeasurement> query = geometryQueries.MeasureClearance(
            heatingCurves,
            wireDiameterMm,
            requiredClearGapMm,
            tolerances,
            localAdjacencies,
            cancellationToken);
        if (query.Status != GeometryQueryStatus.Success || query.Value is null)
        {
            return new ValidationReport(ValidationIssueFactory.QueryFailure(
                query,
                "ClearanceQuery",
                ruleVersion,
                heatingCurves[0].HeatingPathSegmentId!.Value.Value));
        }

        ClearanceMeasurement measurement = query.Value;
        if (!measurement.IsComplete)
        {
            return new ValidationReport(
            [
                ValidationIssueFactory.Error(
                    "ClearanceMeasurementIncomplete",
                    "최단거리 질의가 모든 비국소 곡선 구간을 정밀 판정하지 못했습니다.",
                    ruleVersion,
                    heatingCurves.Select(curve => curve.HeatingPathSegmentId!.Value.Value),
                    measurement.FirstLocation,
                    measuredValue: measurement.MinimumClearGapMm.Value,
                    requiredValue: requiredClearGapMm.Value,
                    unit: "mm")
            ]);
        }

        double measured = measurement.MinimumClearGapMm.Value;
        double error = measurement.ErrorBoundMm.Value;
        if (measured + error < requiredClearGapMm.Value)
        {
            return new ValidationReport(
            [
                ValidationIssueFactory.Error(
                    "MinimumClearGapViolation",
                    "실제 발열사 외곽 간격이 요구 최소 간격보다 작습니다.",
                    ruleVersion,
                    heatingCurves.Select(curve => curve.HeatingPathSegmentId!.Value.Value),
                    measurement.FirstLocation,
                    measuredValue: measured,
                    requiredValue: requiredClearGapMm.Value,
                    unit: "mm")
            ]);
        }

        if (measured - error < requiredClearGapMm.Value)
        {
            return new ValidationReport(
            [
                ValidationIssueFactory.Error(
                    "MinimumClearGapUnresolved",
                    "최단거리 오차 범위가 요구 최소 간격 경계를 가로질러 추가 정밀화가 필요합니다.",
                    ruleVersion,
                    heatingCurves.Select(curve => curve.HeatingPathSegmentId!.Value.Value),
                    measurement.FirstLocation,
                    measuredValue: measured,
                    requiredValue: requiredClearGapMm.Value,
                    unit: "mm")
            ]);
        }

        return new ValidationReport([]);
    }
}
