#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>B-spline과 원호를 오차 한도로 근사해 곡선 간격을 검사합니다.</summary>
internal static class BSplineCurveClearanceCalculator
{
    private const int MaximumTotalCurvePointCount = 1_000_000;

    /// <summary>곡선 근사와 기존 선분 간격 검사 결과를 합칩니다.</summary>
    public static GeometryQueryResult<ClearanceMeasurement> Measure(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        IReadOnlyCollection<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken)
    {
        if (curves.Count == 0)
            return Unresolved("NoClearanceCurves", "간격을 측정할 곡선이 없습니다.");
        if (wireDiameterMm.Value <= 0 || requiredClearGapMm.Value < 0)
            return Unresolved("InvalidClearanceInput", "도체 직경은 양수이고 요구 간격은 0 이상이어야 합니다.");
        if (curves.Any(curve => curve is null))
            return Unresolved("InvalidClearanceInput", "간격 질의에 null 곡선이 포함되어 있습니다.");

        if (HasDeclaredSplineJoint(curves, localCurvePairs, tolerances.ConnectionToleranceMm))
        {
            return Unresolved(
                "CurvedEndpointContactNotCertified",
                "B-spline 연결 끝점은 추가 접촉과 국소 접촉을 관리형 근사에서 구분하지 않습니다.");
        }

        if (ArcCurveClearanceCalculator.HasDeclaredArcJoint(
                curves,
                localCurvePairs,
                tolerances.ConnectionToleranceMm))
        {
            return Unresolved(
                "CurvedEndpointContactNotCertified",
                "원호가 포함된 연결 끝점은 관리형 근사에서 국소 접촉과 추가 교차를 구분하지 않습니다.");
        }

        double requestedChordDeviationMm = tolerances.ConnectionToleranceMm / 64;
        var approximatedCurves = new List<CurveGeometrySnapshot>(curves.Count);
        var ignoredSameCurveContacts = new HashSet<int>();
        var ignoredLocalCurveContactValidation = new HashSet<int>();
        var ignoredCurvePairs = new HashSet<(int FirstCurveIndex, int SecondCurveIndex)>();
        var expandedLocalCurvePairs = new HashSet<(int FirstCurveIndex, int SecondCurveIndex)>();
        var curveIndicesByInput = new List<List<int>>(curves.Count);
        double maximumSingleCurveApproximationErrorMm = 0;
        int totalCurvePointCount = 0;

        for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "곡선 간격 질의가 취소되었습니다.");

            CurveGeometrySnapshot curve = curves[curveIndex];
            if (!curve.Approximation.IsComplete)
            {
                return Unresolved(
                    "IncompleteCurveDefinition",
                    "불완전한 곡선 결과로 최소 간격을 확정할 수 없습니다.",
                    [GetCurveId(curve)]);
            }

            var pieceIndices = new List<int>();
            if (curve.Definition is ArcCurveDefinition arc)
            {
                if (!ArcCurveClearanceCalculator.TryApproximateArc(
                        curve,
                        arc,
                        requestedChordDeviationMm,
                        tolerances.ConnectionToleranceMm,
                        MaximumTotalCurvePointCount - totalCurvePointCount,
                        cancellationToken,
                        out CurveGeometrySnapshot? approximatedArc,
                        out double arcDeviationMm,
                        out double coordinateScale,
                        out double angleMagnitudeRad,
                        out string? arcErrorCode,
                        out string? arcDiagnostic))
                {
                    return Unresolved(arcErrorCode!, arcDiagnostic!, [GetCurveId(curve)]);
                }

                int arcIndex = approximatedCurves.Count;
                approximatedCurves.Add(approximatedArc!);
                pieceIndices.Add(arcIndex);
                ignoredSameCurveContacts.Add(arcIndex);
                ignoredLocalCurveContactValidation.Add(arcIndex);
                totalCurvePointCount += ((PolylineCurveDefinition)approximatedArc!.Definition).Points.Count;
                curveIndicesByInput.Add(pieceIndices);

                double arcEvaluationErrorMm = ArcCurveClearanceCalculator.EstimateEvaluationError(
                    coordinateScale,
                    angleMagnitudeRad);
                double singleArcApproximationErrorMm = arcDeviationMm + arcEvaluationErrorMm;
                if (!double.IsFinite(singleArcApproximationErrorMm))
                {
                    return Unresolved(
                        "ArcClearanceErrorBoundExceeded",
                        "원호 근사와 평가 오차를 유한한 값으로 계산하지 못했습니다.",
                        [GetCurveId(curve)]);
                }

                maximumSingleCurveApproximationErrorMm = Math.Max(
                    maximumSingleCurveApproximationErrorMm,
                    singleArcApproximationErrorMm);
                continue;
            }

            if (curve.Definition is not BSplineCurveDefinition spline)
            {
                pieceIndices.Add(approximatedCurves.Count);
                approximatedCurves.Add(curve);
                curveIndicesByInput.Add(pieceIndices);
                continue;
            }

            if (!BSplineCurvePolylineApproximator.TryApproximateSpline(
                    curve,
                    spline,
                    requestedChordDeviationMm,
                    tolerances.ConnectionToleranceMm,
                    MaximumTotalCurvePointCount - totalCurvePointCount,
                    cancellationToken,
                    out BSplineCurvePolylineApproximator.SplineApproximation? approximation,
                    out string? errorCode,
                    out string? diagnostic))
            {
                return Unresolved(errorCode!, diagnostic!, [GetCurveId(curve)]);
            }

            foreach (CurveGeometrySnapshot spanCurve in approximation!.SpanCurves)
            {
                int spanCurveIndex = approximatedCurves.Count;
                approximatedCurves.Add(spanCurve);
                pieceIndices.Add(spanCurveIndex);
                ignoredSameCurveContacts.Add(spanCurveIndex);
                if (approximation.HasCertifiedNoSelfContact)
                    ignoredLocalCurveContactValidation.Add(spanCurveIndex);
                totalCurvePointCount += spanCurve.Samples.Count;
            }

            for (int spanIndex = 0; spanIndex < pieceIndices.Count - 1; spanIndex++)
            {
                var spanPair = (pieceIndices[spanIndex], pieceIndices[spanIndex + 1]);
                if (spline.Degree == 1)
                {
                    expandedLocalCurvePairs.Add(spanPair);
                    continue;
                }
                ignoredCurvePairs.Add(spanPair);
            }
            curveIndicesByInput.Add(pieceIndices);

            double singleCurveApproximationErrorMm = approximation.MaximumChordDeviationMm +
                approximation.EvaluationErrorMm +
                approximation.EndpointMismatchMm +
                approximation.MaximumKnotMismatchMm;
            maximumSingleCurveApproximationErrorMm = Math.Max(
                maximumSingleCurveApproximationErrorMm,
                singleCurveApproximationErrorMm);
        }

        foreach ((int firstCurveIndex, int secondCurveIndex) in localCurvePairs)
        {
            if (firstCurveIndex < 0 || firstCurveIndex >= curveIndicesByInput.Count ||
                secondCurveIndex < 0 || secondCurveIndex >= curveIndicesByInput.Count)
            {
                return Unresolved("InvalidClearanceAdjacency", "간격 질의의 연결 곡선 인덱스가 범위를 벗어났습니다.");
            }

            List<int> firstPieces = curveIndicesByInput[firstCurveIndex];
            List<int> secondPieces = curveIndicesByInput[secondCurveIndex];
            if (firstPieces.Count == 0 || secondPieces.Count == 0)
                return Unresolved("InvalidClearanceAdjacency", "간격 질의의 연결 곡선 조각이 비어 있습니다.");

            expandedLocalCurvePairs.Add((firstPieces[^1], secondPieces[0]));
        }

        // 두 근사 곡선 사이 거리나 한 곡선 내부 자기접촉에는 각 위치의 오차가 모두 더해질 수 있습니다.
        double approximationErrorMm = 2 * maximumSingleCurveApproximationErrorMm;
        if (!double.IsFinite(approximationErrorMm) ||
            approximationErrorMm > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "BSplineClearanceErrorBoundExceeded",
                "곡선 분할 오차와 평가 오차의 합이 연결 공차를 넘었습니다.");
        }

        GeometryQueryResult<ClearanceMeasurement> polylineResult = LinearCurveClearanceCalculator.Measure(
            approximatedCurves,
            wireDiameterMm,
            requiredClearGapMm,
            expandedLocalCurvePairs,
            tolerances,
            cancellationToken,
            ignoreSameCurveContacts: ignoredSameCurveContacts,
            additionalCurveDistanceErrorMm: approximationErrorMm,
            ignoreCurvePairs: ignoredCurvePairs,
            ignoreLocalCurveContactValidation: ignoredLocalCurveContactValidation);

        if (polylineResult.Status != GeometryQueryStatus.Success ||
            polylineResult.Value is not { } measurement)
        {
            return polylineResult;
        }

        double totalErrorBoundMm = measurement.ErrorBoundMm.Value + approximationErrorMm;
        if (!double.IsFinite(totalErrorBoundMm) || totalErrorBoundMm > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "BSplineClearanceErrorBoundExceeded",
                "B-spline 근사와 선분 거리 오차를 합친 최소거리 오차가 요청 한도를 넘었습니다.");
        }

        return GeometryQueryResult<ClearanceMeasurement>.Succeeded(new ClearanceMeasurement(
            measurement.MinimumCenterlineDistanceMm,
            measurement.MinimumClearGapMm,
            measurement.FirstLocation,
            measurement.SecondLocation,
            new LengthMm(totalErrorBoundMm),
            isComplete: true));
    }

    private static bool HasDeclaredSplineJoint(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        IReadOnlyCollection<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        double endpointToleranceMm)
    {
        foreach ((int firstIndex, int secondIndex) in localCurvePairs)
        {
            if (firstIndex < 0 || firstIndex >= curves.Count || secondIndex < 0 || secondIndex >= curves.Count)
                continue;

            bool containsSpline = curves[firstIndex].Definition is BSplineCurveDefinition ||
                curves[secondIndex].Definition is BSplineCurveDefinition;
            CurveGeometrySnapshot first = curves[firstIndex];
            CurveGeometrySnapshot second = curves[secondIndex];
            bool endpointsMeet =
                (first.Start - second.Start).Length <= endpointToleranceMm ||
                (first.Start - second.End).Length <= endpointToleranceMm ||
                (first.End - second.Start).Length <= endpointToleranceMm ||
                (first.End - second.End).Length <= endpointToleranceMm;
            if (containsSpline && endpointsMeet)
                return true;
        }

        return false;
    }

    private static Guid GetCurveId(CurveGeometrySnapshot curve) =>
        curve.HeatingPathSegmentId?.Value ?? curve.ConnectionPathSegmentId!.Value.Value;

    private static GeometryQueryResult<ClearanceMeasurement> Unresolved(
        string code,
        string evidence,
        IEnumerable<Guid>? entityIds = null) => GeometryQueryResult<ClearanceMeasurement>.Unresolved(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Geometry.{code}",
            entityIds,
            BSplineCurvePolylineApproximator.RuleVersion,
            evidence: [evidence])]);
}
