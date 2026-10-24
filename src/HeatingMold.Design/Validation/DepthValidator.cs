using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Validation;

/// <summary>표면 기하 질의로 최종 발열 곡선의 삽입 깊이를 검사합니다.</summary>
public sealed class DepthValidator
{
    /// <summary>각 활성 곡선을 질의하고 깊이 오차 범위가 요구값을 만족하는지 확인합니다.</summary>
    public ValidationReport Validate(
        IEnumerable<CurveGeometrySnapshot> curves,
        WireSpecification wireSpecification,
        LengthMm requiredInsertionDepthMm,
        IEnumerable<SurfaceAnchor> referenceAnchors,
        WorkingCoordinateSystem coordinateSystem,
        IGeometryQueries geometryQueries,
        double comparisonToleranceMm,
        string ruleVersion,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<HeatingPathSegmentId, IReadOnlyList<FaceRefId>>? selectedFacesBySegment = null)
    {
        CurveGeometrySnapshot[] copiedCurves = curves?.ToArray()
            ?? throw new ArgumentNullException(nameof(curves));
        SurfaceAnchor[] anchors = referenceAnchors?.ToArray()
            ?? throw new ArgumentNullException(nameof(referenceAnchors));
        ArgumentNullException.ThrowIfNull(wireSpecification);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(geometryQueries);
        if (requiredInsertionDepthMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredInsertionDepthMm));
        if (!double.IsFinite(comparisonToleranceMm) || comparisonToleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(comparisonToleranceMm));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("깊이 검증 규칙 버전이 필요합니다.", nameof(ruleVersion));

        var issues = new List<ValidationIssue>();
        foreach (CurveGeometrySnapshot curve in copiedCurves)
        {
            if (!curve.HeatingPathSegmentId.HasValue)
                continue;
            if (!curve.Approximation.IsComplete)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "IncompleteDepthCurveApproximation",
                    "불완전한 곡선 근사로 삽입 깊이를 확정할 수 없습니다.",
                    ruleVersion,
                    [curve.HeatingPathSegmentId.Value.Value]));
                continue;
            }

            IReadOnlyList<FaceRefId>? selectedFaces = null;
            if (selectedFacesBySegment is not null &&
                !selectedFacesBySegment.TryGetValue(curve.HeatingPathSegmentId.Value, out selectedFaces))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingDepthSurfaceFaces",
                    "발열 구간이 속한 영역의 선택 Face 집합이 없습니다.",
                    ruleVersion,
                    [curve.HeatingPathSegmentId.Value.Value]));
                continue;
            }

            SurfaceAnchor? referenceAnchor = FindNearestAnchor(curve, anchors, selectedFaces);
            if (referenceAnchor is null)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingDepthReferenceAnchor",
                    "깊이 질의에 사용할 선택 표면 앵커가 없습니다.",
                    ruleVersion,
                    [curve.HeatingPathSegmentId.Value.Value]));
                continue;
            }

            GeometryQueryResult<DepthMeasurement> query;
            if (selectedFaces is not null)
            {
                query = geometryQueries.MeasureDepth(
                    curve,
                    wireSpecification.DiameterMm,
                    referenceAnchor,
                    coordinateSystem,
                    requiredInsertionDepthMm,
                    selectedFaces,
                    cancellationToken);
            }
            else
            {
                query = geometryQueries.MeasureDepth(
                    curve,
                    wireSpecification.DiameterMm,
                    referenceAnchor,
                    coordinateSystem,
                    requiredInsertionDepthMm,
                    cancellationToken);
            }
            if (query.Status != GeometryQueryStatus.Success || query.Value is null)
            {
                issues.AddRange(ValidationIssueFactory.QueryFailure(
                    query,
                    "DepthQuery",
                    ruleVersion,
                    curve.HeatingPathSegmentId.Value.Value));
                continue;
            }

            DepthMeasurement measurement = query.Value;
            double minimumDifference = Math.Abs(
                measurement.MinimumMeasuredInsertionDepthMm.Value - requiredInsertionDepthMm.Value);
            double maximumDifference = Math.Abs(
                measurement.MaximumMeasuredInsertionDepthMm.Value - requiredInsertionDepthMm.Value);
            double difference = Math.Max(minimumDifference, maximumDifference);
            double allowedDifference = measurement.ErrorBoundMm.Value + comparisonToleranceMm;
            bool hasRequiredMeasurement = measurement.IsResolved && measurement.IsComplete &&
                                          (selectedFaces is null || measurement.IsCurveEnvelope);
            if (!hasRequiredMeasurement || difference > allowedDifference)
            {
                string code = hasRequiredMeasurement ? "InsertionDepthMismatch" : "InsertionDepthUnresolved";
                string evidence = hasRequiredMeasurement
                    ? "측정 깊이가 오차 한도를 고려해도 요구 삽입 깊이와 일치하지 않습니다."
                    : "곡선 전체의 깊이 측정이 완료되지 않았거나 유효한 허용오차 안에서 판정되지 않았습니다.";
                bool minimumIsWorse = minimumDifference >= maximumDifference;
                double reportedDepth = minimumIsWorse
                    ? measurement.MinimumMeasuredInsertionDepthMm.Value
                    : measurement.MaximumMeasuredInsertionDepthMm.Value;
                Vec3 reportedLocation = minimumIsWorse
                    ? measurement.MinimumDepthWireOutsidePoint
                    : measurement.MaximumDepthWireOutsidePoint;
                issues.Add(ValidationIssueFactory.Error(
                    code,
                    evidence,
                    ruleVersion,
                    [curve.HeatingPathSegmentId.Value.Value],
                    reportedLocation,
                    measuredValue: reportedDepth,
                    requiredValue: requiredInsertionDepthMm.Value,
                    unit: "mm"));
            }
        }

        return new ValidationReport(issues);
    }

    private static SurfaceAnchor? FindNearestAnchor(
        CurveGeometrySnapshot curve,
        IReadOnlyList<SurfaceAnchor> anchors,
        IReadOnlyList<FaceRefId>? selectedFaces)
    {
        IEnumerable<SurfaceAnchor> candidates = selectedFaces is null
            ? anchors
            : anchors.Where(anchor => selectedFaces.Contains(anchor.FaceReferenceId));
        SurfaceAnchor[] compatibleAnchors = candidates.ToArray();
        if (compatibleAnchors.Length == 0)
            return null;

        return compatibleAnchors
            .Select(anchor => new
            {
                Anchor = anchor,
                Distance = curve.Samples.Min(sample => (sample - anchor.ModelPoint).Length)
            })
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Anchor.FaceReferenceId.Value)
            .Select(candidate => candidate.Anchor)
            .First();
    }
}
