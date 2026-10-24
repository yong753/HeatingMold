#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>B-spline knot 구간을 오차 한도 안의 선분 곡선으로 근사합니다.</summary>
internal static class BSplineCurvePolylineApproximator
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const int MaximumSplineDegree = 8;
    private const int MaximumSplinePoleCount = 256;
    private const int MaximumSplinePointCount = 100_000;
    internal const int MaximumTotalSplinePointCount = 1_000_000;
    private const int MaximumSubdivisionDepth = 40;
    internal const string RuleVersion = "managed-rational-bspline-clearance-v5";

    /// <summary>매듭 구간 근사 곡선과 각 오차 성분을 보관합니다.</summary>
    internal sealed record SplineApproximation(
        IReadOnlyList<CurveGeometrySnapshot> SpanCurves,
        double MaximumChordDeviationMm,
        double EvaluationErrorMm,
        double EndpointMismatchMm,
        double MaximumKnotMismatchMm,
        bool HasCertifiedNoSelfContact);

    /// <summary>곡선을 knot 구간별 선분 snapshot으로 근사하고 오차 한도를 검증합니다.</summary>
    internal static bool TryApproximateSpline(
        CurveGeometrySnapshot curve,
        BSplineCurveDefinition spline,
        double requestedChordDeviationMm,
        double endpointToleranceMm,
        int remainingPointBudget,
        CancellationToken cancellationToken,
        out SplineApproximation? approximation,
        out string? errorCode,
        out string? diagnostic)
    {
        approximation = null;
        errorCode = "InvalidBSplineClearanceInput";
        diagnostic = "B-spline 간격 근사 입력을 확인하지 못했습니다.";

        if (spline.IsPeriodic)
        {
            errorCode = "PeriodicBSplineClearanceUnsupported";
            diagnostic = "주기 B-spline의 seam 자기접촉은 Native 정밀 질의가 필요합니다.";
            return false;
        }

        if (spline.Degree > MaximumSplineDegree)
        {
            errorCode = "BSplineApproximationBudgetExceeded";
            diagnostic = "B-spline 차수가 관리형 근사 한도를 넘었습니다.";
            return false;
        }

        bool hasMonotonePoleCoordinate = HasStrictlyMonotonePoleCoordinate(spline);
        if (spline.Degree > 2 && !hasMonotonePoleCoordinate)
        {
            errorCode = "BSplineSelfContactUnresolved";
            diagnostic = "3차 이상 B-spline의 극점이 어느 좌표에서도 엄격히 단조롭지 않아 자기 접촉을 배제할 수 없습니다.";
            return false;
        }

        if (spline.Poles.Count > MaximumSplinePoleCount)
        {
            errorCode = "BSplineApproximationBudgetExceeded";
            diagnostic = "B-spline 극점 수가 관리형 작업 한도를 넘었습니다.";
            return false;
        }

        if (!BSplineCurveEvaluator.TryCreate(spline, out BSplineCurveEvaluator? evaluator) ||
            evaluator is null)
        {
            errorCode = "InvalidBSplineParameterDomain";
            diagnostic = "B-spline trim 구간이 유효한 비주기 매개변수 범위에 있지 않습니다.";
            return false;
        }

        if (!TryCalculateCurvatureBound(spline, evaluator, out double curvatureBoundMm) ||
            !TryEstimateEvaluationError(spline, out double evaluationErrorMm))
        {
            errorCode = "BSplineErrorBoundUnresolved";
            diagnostic = "B-spline의 곡률 또는 수치 오차 상한을 유한하게 계산하지 못했습니다.";
            return false;
        }

        double availableChordDeviationMm = requestedChordDeviationMm - evaluationErrorMm;
        if (availableChordDeviationMm <= 0)
        {
            errorCode = "BSplineClearanceErrorBoundExceeded";
            diagnostic = "B-spline 평가 오차가 요청한 거리 근사 한도보다 큽니다.";
            return false;
        }

        var parameterBreaks = CreateParameterBreaks(spline);
        if (parameterBreaks.Count < 2 || parameterBreaks.Count > MaximumSplinePointCount)
        {
            errorCode = "BSplineApproximationBudgetExceeded";
            diagnostic = "B-spline 매듭 구간 수가 관리형 표본 한도를 넘었습니다.";
            return false;
        }

        int maximumPointCount = Math.Min(MaximumSplinePointCount, remainingPointBudget);
        if (maximumPointCount < 2)
        {
            errorCode = "BSplineApproximationBudgetExceeded";
            diagnostic = "B-spline 전체 표본 예산을 소진했습니다.";
            return false;
        }

        var ascendingSpanPoints = new List<List<Vec3>>(parameterBreaks.Count - 1);
        int generatedPointCount = 0;
        int probeCount = 0;
        double maximumChordDeviationMm = 0;
        Vec3? previousSpanEnd = null;
        for (int spanIndex = 0; spanIndex < parameterBreaks.Count - 1; spanIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                errorCode = "OperationCancelled";
                diagnostic = "B-spline 간격 질의가 취소되었습니다.";
                return false;
            }

            double spanStart = parameterBreaks[spanIndex];
            double spanEnd = parameterBreaks[spanIndex + 1];
            if (!TryEvaluate(
                    evaluator,
                    spanStart,
                    useLeftLimit: false,
                    cancellationToken,
                    maximumPointCount,
                    ref probeCount,
                    out Vec3 startPoint,
                    out errorCode,
                    out diagnostic) ||
                !TryEvaluate(
                    evaluator,
                    spanEnd,
                    useLeftLimit: true,
                    cancellationToken,
                    maximumPointCount,
                    ref probeCount,
                    out Vec3 endPoint,
                    out errorCode,
                    out diagnostic))
            {
                return false;
            }

            if (spline.Degree == 2 && !hasMonotonePoleCoordinate &&
                !TryCertifyQuadraticSpanIsNondegenerate(
                    evaluator,
                    spanStart,
                    spanEnd,
                    startPoint,
                    endPoint,
                    evaluationErrorMm,
                    maximumPointCount,
                    cancellationToken,
                    ref probeCount,
                    out errorCode,
                    out diagnostic))
            {
                return false;
            }

            if (previousSpanEnd is { } priorEnd &&
                (priorEnd - startPoint).Length > evaluationErrorMm + MachineEpsilon)
            {
                errorCode = "BSplineKnotDiscontinuity";
                diagnostic = "인접한 B-spline 매듭 구간의 위치가 수치 오차 범위 안에서 이어지지 않습니다.";
                return false;
            }

            var spanPoints = new List<Vec3>(2) { startPoint };
            generatedPointCount++;
            if (!AppendSpan(
                    evaluator,
                    spanStart,
                    spanEnd,
                    startPoint,
                    endPoint,
                    curvatureBoundMm,
                    availableChordDeviationMm,
                    maximumPointCount,
                    cancellationToken,
                    ref probeCount,
                    ref generatedPointCount,
                    spanPoints,
                    ref maximumChordDeviationMm,
                    out errorCode,
                    out diagnostic))
            {
                return false;
            }

            ascendingSpanPoints.Add(spanPoints);
            previousSpanEnd = endPoint;
        }

        if (spline.Direction == CurveDirection.Reversed)
        {
            ascendingSpanPoints.Reverse();
            foreach (List<Vec3> spanPoints in ascendingSpanPoints)
                spanPoints.Reverse();
        }

        double maximumKnotMismatchMm = 0;
        for (int spanIndex = 1; spanIndex < ascendingSpanPoints.Count; spanIndex++)
        {
            List<Vec3> previousSpan = ascendingSpanPoints[spanIndex - 1];
            List<Vec3> currentSpan = ascendingSpanPoints[spanIndex];
            double knotMismatchMm = (previousSpan[^1] - currentSpan[0]).Length;
            if (knotMismatchMm > evaluationErrorMm + MachineEpsilon)
            {
                errorCode = "BSplineKnotDiscontinuity";
                diagnostic = "B-spline 인접 매듭 구간의 수치 위치가 허용 오차 안에서 이어지지 않습니다.";
                return false;
            }

            maximumKnotMismatchMm = Math.Max(maximumKnotMismatchMm, knotMismatchMm);
            currentSpan[0] = previousSpan[^1];
        }

        if (ascendingSpanPoints.Count == 0 || ascendingSpanPoints.Any(points => points.Count < 2))
        {
            errorCode = "DegenerateBSplineClearanceCurve";
            diagnostic = "B-spline에서 간격 측정에 사용할 길이 있는 knot 구간을 만들지 못했습니다.";
            return false;
        }

        List<Vec3> firstSpanPoints = ascendingSpanPoints[0];
        List<Vec3> lastSpanPoints = ascendingSpanPoints[^1];
        Vec3 evaluatedStart = firstSpanPoints[0];
        Vec3 evaluatedEnd = lastSpanPoints[^1];
        double endpointMismatchMm = Math.Max(
            (evaluatedStart - curve.Start).Length,
            (evaluatedEnd - curve.End).Length);
        if (endpointMismatchMm > endpointToleranceMm)
        {
            errorCode = "CurveSnapshotMismatch";
            diagnostic = "B-spline 정의의 끝점이 측정 스냅샷과 일치하지 않습니다.";
            return false;
        }

        firstSpanPoints[0] = curve.Start;
        lastSpanPoints[^1] = curve.End;
        if (ascendingSpanPoints.Any(points =>
                points.Zip(points.Skip(1)).Any(pair => pair.First == pair.Second)))
        {
            errorCode = "DegenerateBSplineClearanceCurve";
            diagnostic = "B-spline 근사에 길이가 0인 연속 구간이 있습니다.";
            return false;
        }

        try
        {
            var spanCurves = new List<CurveGeometrySnapshot>(ascendingSpanPoints.Count);
            foreach (List<Vec3> spanPoints in ascendingSpanPoints)
            {
                var polyline = new PolylineCurveDefinition(spanPoints);
                double lengthMm = 0;
                double maximumSampleSpacingMm = 0;
                for (int pointIndex = 1; pointIndex < spanPoints.Count; pointIndex++)
                {
                    double segmentLengthMm = (spanPoints[pointIndex] - spanPoints[pointIndex - 1]).Length;
                    lengthMm += segmentLengthMm;
                    maximumSampleSpacingMm = Math.Max(maximumSampleSpacingMm, segmentLengthMm);
                }

                if (!double.IsFinite(lengthMm) || lengthMm <= 0)
                {
                    errorCode = "DegenerateBSplineClearanceCurve";
                    diagnostic = "B-spline 근사에서 유효한 양의 길이를 계산하지 못했습니다.";
                    return false;
                }

                double measuredDeviationMm = maximumChordDeviationMm +
                    evaluationErrorMm +
                    endpointMismatchMm +
                    maximumKnotMismatchMm;
                var curveApproximation = new CurveApproximationInfo(
                    requestedChordDeviationMm,
                    requestedAngularToleranceRad: 0,
                    measuredDeviationMm,
                    maximumSampleSpacingMm,
                    probeCount,
                    isComplete: true,
                    RuleVersion);
                spanCurves.Add(new CurveGeometrySnapshot(
                    curve.HeatingPathSegmentId,
                    curve.ConnectionPathSegmentId,
                    polyline,
                    new LengthMm(lengthMm),
                    spanPoints[0],
                    spanPoints[^1],
                    spanPoints,
                    curveApproximation));
            }

            if (spline.Degree >= 2 && !hasMonotonePoleCoordinate)
            {
                for (int spanIndex = 0; spanIndex < spanCurves.Count - 1; spanIndex++)
                {
                    CurveGeometrySnapshot firstSpan = spanCurves[spanIndex];
                    CurveGeometrySnapshot secondSpan = spanCurves[spanIndex + 1];
                    if (HasNonlocalAdjacentSpanBoundsOverlap(
                            firstSpan,
                            secondSpan,
                            firstSpan.End,
                            endpointToleranceMm))
                    {
                        errorCode = "BSplineSelfContactUnresolved";
                        diagnostic = "인접 2차 B-spline 매듭 구간의 경계가 접점 이외에서도 겹칠 수 있습니다.";
                        return false;
                    }
                }
            }

            approximation = new SplineApproximation(
                spanCurves,
                maximumChordDeviationMm,
                evaluationErrorMm,
                endpointMismatchMm,
                maximumKnotMismatchMm,
                spline.Degree <= 2 || hasMonotonePoleCoordinate);
        }
        catch (ArgumentException exception)
        {
            errorCode = "InvalidBSplineClearanceInput";
            diagnostic = string.Concat("B-spline knot 구간 근사를 만들지 못했습니다: ", exception.Message);
            return false;
        }

        errorCode = string.Empty;
        diagnostic = string.Empty;
        return true;
    }

    private static bool HasNonlocalAdjacentSpanBoundsOverlap(
        CurveGeometrySnapshot firstSpan,
        CurveGeometrySnapshot secondSpan,
        Vec3 joint,
        double jointToleranceMm)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            double overlapMinimum = Math.Max(
                MinimumCoordinate(firstSpan.Samples, axis),
                MinimumCoordinate(secondSpan.Samples, axis));
            double overlapMaximum = Math.Min(
                MaximumCoordinate(firstSpan.Samples, axis),
                MaximumCoordinate(secondSpan.Samples, axis));
            if (overlapMinimum > overlapMaximum)
                return false;

            double jointCoordinate = Coordinate(joint, axis);
            if (overlapMinimum < jointCoordinate - jointToleranceMm ||
                overlapMaximum > jointCoordinate + jointToleranceMm)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>2차 knot 구간이 직선 되감김으로 퇴화하지 않았는지 평가 오차와 함께 확인합니다.</summary>
    private static bool TryCertifyQuadraticSpanIsNondegenerate(
        BSplineCurveEvaluator evaluator,
        double spanStart,
        double spanEnd,
        Vec3 startPoint,
        Vec3 endPoint,
        double evaluationErrorMm,
        int maximumPointCount,
        CancellationToken cancellationToken,
        ref int probeCount,
        out string? errorCode,
        out string? diagnostic)
    {
        double midpointParameter = spanStart + (spanEnd - spanStart) / 2;
        if (midpointParameter <= spanStart || midpointParameter >= spanEnd)
        {
            errorCode = "BSplineApproximationPrecisionExceeded";
            diagnostic = "2차 B-spline knot 구간의 중간 매개변수를 double 정밀도로 구분하지 못했습니다.";
            return false;
        }

        if (!TryEvaluate(
                evaluator,
                midpointParameter,
                useLeftLimit: false,
                cancellationToken,
                maximumPointCount,
                ref probeCount,
                out Vec3 midpointPoint,
                out errorCode,
                out diagnostic))
        {
            return false;
        }

        Vec3 firstRay = midpointPoint - startPoint;
        Vec3 fullChord = endPoint - startPoint;
        double crossMagnitude = Vec3.Cross(firstRay, fullChord).Length;
        double crossErrorBound = 2 * evaluationErrorMm * (firstRay.Length + fullChord.Length) +
            4 * evaluationErrorMm * evaluationErrorMm;
        if (!double.IsFinite(crossMagnitude) || !double.IsFinite(crossErrorBound) ||
            crossMagnitude <= crossErrorBound)
        {
            errorCode = "BSplineSelfContactUnresolved";
            diagnostic = "2차 B-spline knot 구간이 직선 되감김으로 퇴화했는지 오차 한도 안에서 판별하지 못했습니다.";
            return false;
        }

        errorCode = null;
        diagnostic = null;
        return true;
    }

    private static double MinimumCoordinate(IReadOnlyList<Vec3> points, int axis) =>
        points.Min(point => Coordinate(point, axis));

    private static double MaximumCoordinate(IReadOnlyList<Vec3> points, int axis) =>
        points.Max(point => Coordinate(point, axis));

    private static double Coordinate(Vec3 point, int axis) => axis switch
    {
        0 => point.X,
        1 => point.Y,
        _ => point.Z
    };

    /// <summary>양의 가중치에서 한 축의 엄격 단조 극점이 곡선 투영도 단조로 유지함을 확인합니다.</summary>
    private static bool HasStrictlyMonotonePoleCoordinate(BSplineCurveDefinition spline)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            bool strictlyIncreasing = true;
            bool strictlyDecreasing = true;
            for (int poleIndex = 1; poleIndex < spline.Poles.Count; poleIndex++)
            {
                double previous = Coordinate(spline.Poles[poleIndex - 1], axis);
                double current = Coordinate(spline.Poles[poleIndex], axis);
                strictlyIncreasing &= current > previous;
                strictlyDecreasing &= current < previous;
                if (!strictlyIncreasing && !strictlyDecreasing)
                    break;
            }

            if (strictlyIncreasing || strictlyDecreasing)
                return true;
        }

        return false;
    }

    private static bool AppendSpan(
        BSplineCurveEvaluator evaluator,
        double parameterStart,
        double parameterEnd,
        Vec3 startPoint,
        Vec3 endPoint,
        double curvatureBoundMm,
        double availableChordDeviationMm,
        int maximumPointCount,
        CancellationToken cancellationToken,
        ref int probeCount,
        ref int generatedPointCount,
        List<Vec3> points,
        ref double maximumDeviationMm,
        out string? errorCode,
        out string? diagnostic,
        int subdivisionDepth = 0)
    {
        errorCode = null;
        diagnostic = null;
        if (cancellationToken.IsCancellationRequested)
        {
            errorCode = "OperationCancelled";
            diagnostic = "B-spline 간격 질의가 취소되었습니다.";
            return false;
        }

        double parameterSpan = parameterEnd - parameterStart;
        double deviationBoundMm = curvatureBoundMm * parameterSpan * parameterSpan / 8;
        if (!double.IsFinite(deviationBoundMm))
        {
            errorCode = "BSplineErrorBoundUnresolved";
            diagnostic = "B-spline 구간의 현 편차 상한이 유한하지 않습니다.";
            return false;
        }

        if (deviationBoundMm <= availableChordDeviationMm)
        {
            if (generatedPointCount >= maximumPointCount)
            {
                errorCode = "BSplineApproximationBudgetExceeded";
                diagnostic = "B-spline 간격 근사 표본 수가 관리형 작업 한도를 넘었습니다.";
                return false;
            }

            if (points[^1] != endPoint)
            {
                points.Add(endPoint);
                generatedPointCount++;
            }
            maximumDeviationMm = Math.Max(maximumDeviationMm, deviationBoundMm);
            return true;
        }

        if (subdivisionDepth >= MaximumSubdivisionDepth)
        {
            errorCode = "BSplineApproximationBudgetExceeded";
            diagnostic = "B-spline 현 편차 상한이 최대 세분화 깊이 안에서 수렴하지 않았습니다.";
            return false;
        }

        double midpoint = parameterStart + parameterSpan / 2;
        if (midpoint <= parameterStart || midpoint >= parameterEnd)
        {
            errorCode = "BSplineApproximationPrecisionExceeded";
            diagnostic = "B-spline 매개변수 구간을 double 정밀도로 더 세분화할 수 없습니다.";
            return false;
        }

        if (!TryEvaluate(
                evaluator,
                midpoint,
                useLeftLimit: false,
                cancellationToken,
                maximumPointCount,
                ref probeCount,
                out Vec3 midpointPoint,
                out errorCode,
                out diagnostic))
        {
            return false;
        }

        return AppendSpan(
                evaluator,
                parameterStart,
                midpoint,
                startPoint,
                midpointPoint,
                curvatureBoundMm,
                availableChordDeviationMm,
                maximumPointCount,
                cancellationToken,
                ref probeCount,
                ref generatedPointCount,
                points,
                ref maximumDeviationMm,
                out errorCode,
                out diagnostic,
                subdivisionDepth + 1) &&
            AppendSpan(
                evaluator,
                midpoint,
                parameterEnd,
                midpointPoint,
                endPoint,
                curvatureBoundMm,
                availableChordDeviationMm,
                maximumPointCount,
                cancellationToken,
                ref probeCount,
                ref generatedPointCount,
                points,
                ref maximumDeviationMm,
                out errorCode,
                out diagnostic,
                subdivisionDepth + 1);
    }

    private static bool TryEvaluate(
        BSplineCurveEvaluator evaluator,
        double parameter,
        bool useLeftLimit,
        CancellationToken cancellationToken,
        int maximumPointCount,
        ref int probeCount,
        out Vec3 point,
        out string? errorCode,
        out string? diagnostic)
    {
        point = default;
        if (cancellationToken.IsCancellationRequested)
        {
            errorCode = "OperationCancelled";
            diagnostic = "B-spline 간격 질의가 취소되었습니다.";
            return false;
        }

        if (++probeCount > maximumPointCount ||
            !evaluator.TryEvaluate(parameter, useLeftLimit, out point, out _))
        {
            errorCode = probeCount > maximumPointCount
                ? "BSplineApproximationBudgetExceeded"
                : "BSplineEvaluationFailed";
            diagnostic = probeCount > maximumPointCount
                ? "B-spline 평가 수가 관리형 작업 한도를 넘었습니다."
                : "B-spline 위치와 접선을 유한한 값으로 계산하지 못했습니다.";
            return false;
        }

        errorCode = null;
        diagnostic = null;
        return true;
    }

    private static bool TryCalculateCurvatureBound(
        BSplineCurveDefinition spline,
        BSplineCurveEvaluator evaluator,
        out double curvatureBoundMm)
    {
        curvatureBoundMm = 0;
        if (spline.Degree == 1)
            return true;

        Vec3 origin = spline.Poles[0];
        double maximumPoleOffsetMm = 0;
        double minimumWeight = double.PositiveInfinity;
        double maximumWeight = 0;
        foreach (Vec3 pole in spline.Poles)
            maximumPoleOffsetMm = Math.Max(maximumPoleOffsetMm, (pole - origin).Length);
        foreach (double weight in spline.Weights)
        {
            minimumWeight = Math.Min(minimumWeight, weight);
            maximumWeight = Math.Max(maximumWeight, weight);
        }

        double weightRatio = maximumWeight / minimumWeight;
        double minimumKnotSpan = evaluator.MinimumKnotSpan;
        double firstDerivativeBound =
            2 * spline.Degree / minimumKnotSpan * weightRatio;
        double secondDerivativeBasisBound =
            4 * spline.Degree * (spline.Degree - 1) /
            (minimumKnotSpan * minimumKnotSpan) * weightRatio;
        curvatureBoundMm =
            2 * maximumPoleOffsetMm * secondDerivativeBasisBound +
            4 * maximumPoleOffsetMm * firstDerivativeBound * firstDerivativeBound;
        return double.IsFinite(maximumPoleOffsetMm) && maximumPoleOffsetMm > 0 &&
            double.IsFinite(weightRatio) && weightRatio >= 1 &&
            double.IsFinite(curvatureBoundMm) && curvatureBoundMm >= 0;
    }

    private static bool TryEstimateEvaluationError(
        BSplineCurveDefinition spline,
        out double errorBoundMm)
    {
        double coordinateScale = 1;
        double weightRatio = spline.Weights.Max() / spline.Weights.Min();
        foreach (Vec3 pole in spline.Poles)
        {
            coordinateScale = Math.Max(coordinateScale, Math.Abs(pole.X));
            coordinateScale = Math.Max(coordinateScale, Math.Abs(pole.Y));
            coordinateScale = Math.Max(coordinateScale, Math.Abs(pole.Z));
        }

        double complexity = (spline.Poles.Count + 1) * (spline.Degree + 1);
        errorBoundMm = (coordinateScale + 1) * MachineEpsilon * 4096 *
            complexity * weightRatio * weightRatio;
        return double.IsFinite(weightRatio) && weightRatio >= 1 && double.IsFinite(errorBoundMm);
    }

    private static List<double> CreateParameterBreaks(BSplineCurveDefinition spline)
    {
        var parameterBreaks = new List<double> { spline.TrimStart };
        foreach (double knot in spline.Knots)
        {
            if (knot > spline.TrimStart && knot < spline.TrimEnd)
                parameterBreaks.Add(knot);
        }

        parameterBreaks.Add(spline.TrimEnd);
        return parameterBreaks;
    }
}
