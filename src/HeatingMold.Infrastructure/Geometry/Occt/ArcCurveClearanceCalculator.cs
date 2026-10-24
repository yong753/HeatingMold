#nullable enable annotations

using System;
using System.Collections.Generic;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>원호와 다른 곡선 사이의 거리를 오차 한도 안에서 근사합니다.</summary>
internal static class ArcCurveClearanceCalculator
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const double MaximumSupportedAngleRad = 1_000_000;
    private const int MaximumArcSegmentCount = 100_000;
    private const int MaximumTotalArcSampleCount = 1_000_000;
    private const string RuleVersion = "managed-arc-clearance-v1";

    /// <summary>원호를 제한 오차 다중선으로 근사하고 기존 접촉 규칙으로 간격을 검사합니다.</summary>
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

        double chordDeviationLimitMm = tolerances.ConnectionToleranceMm / 64;
        var approximatedCurves = new List<CurveGeometrySnapshot>(curves.Count);
        var arcCurveIndices = new HashSet<int>();
        double maximumChordDeviationMm = 0;
        double maximumCoordinateScale = 1;
        double maximumAngleMagnitudeRad = 0;
        int totalArcSampleCount = 0;

        for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "원호 간격 질의가 취소되었습니다.");

            CurveGeometrySnapshot curve = curves[curveIndex];
            if (curve is null)
                return Unresolved("InvalidClearanceInput", "간격 질의에 null 곡선이 포함되어 있습니다.");
            if (!curve.Approximation.IsComplete)
            {
                return Unresolved(
                    "IncompleteCurveDefinition",
                    "불완전한 곡선 결과로 최소 간격을 확정할 수 없습니다.",
                    [GetCurveId(curve)]);
            }

            if (curve.Definition is not ArcCurveDefinition arc)
            {
                if (curve.Definition is BSplineCurveDefinition)
                {
                    return Unresolved(
                        "UnsupportedClearanceCurve",
                        "B-spline 자기접촉은 Native 정밀 질의가 필요합니다.",
                        [GetCurveId(curve)]);
                }

                approximatedCurves.Add(curve);
                continue;
            }

            if (!TryApproximateArc(
                    curve,
                    arc,
                    chordDeviationLimitMm,
                    tolerances.ConnectionToleranceMm,
                    MaximumTotalArcSampleCount - totalArcSampleCount,
                    cancellationToken,
                    out CurveGeometrySnapshot? approximatedCurve,
                    out double chordDeviationMm,
                    out double coordinateScale,
                    out double angleMagnitudeRad,
                    out string? errorCode,
                    out string? diagnostic))
            {
                return Unresolved(errorCode!, diagnostic!, [GetCurveId(curve)]);
            }

            approximatedCurves.Add(approximatedCurve!);
            arcCurveIndices.Add(curveIndex);
            totalArcSampleCount += ((PolylineCurveDefinition)approximatedCurve!.Definition).Points.Count;
            maximumChordDeviationMm = Math.Max(maximumChordDeviationMm, chordDeviationMm);
            maximumCoordinateScale = Math.Max(maximumCoordinateScale, coordinateScale);
            maximumAngleMagnitudeRad = Math.Max(maximumAngleMagnitudeRad, angleMagnitudeRad);
        }

        ArcJointContactStatus jointStatus = ArcJointContactValidator.Validate(
            curves,
            localCurvePairs,
            tolerances.ConnectionToleranceMm,
            cancellationToken);
        if (jointStatus != ArcJointContactStatus.Certified)
        {
            if (jointStatus == ArcJointContactStatus.Cancelled)
                return Unresolved("OperationCancelled", "원호 연결 접점 검사가 취소되었습니다.");

            string diagnostic = jointStatus switch
            {
                ArcJointContactStatus.AdditionalIntersection =>
                    "선언된 원호 연결 끝점 밖에서 추가 교차가 발견되어 간격을 확정하지 않습니다.",
                ArcJointContactStatus.UnsupportedCurvePair =>
                    "이 원호 연결 곡선 조합은 접점 주변의 추가 교차를 관리형으로 인증하지 않습니다.",
                ArcJointContactStatus.BudgetExceeded =>
                    "원호 연결 접점 검사 선분 한도를 넘어 간격을 확정하지 않습니다.",
                _ => "원호 연결 접점의 추가 교차 여부를 수치적으로 확정할 수 없습니다."
            };
            return Unresolved(
                "CurvedEndpointContactNotCertified",
                diagnostic);
        }

        double approximationErrorMm = 2 * maximumChordDeviationMm +
            EstimateEvaluationError(maximumCoordinateScale, maximumAngleMagnitudeRad);
        GeometryQueryResult<ClearanceMeasurement> polylineResult = LinearCurveClearanceCalculator.Measure(
            approximatedCurves,
            wireDiameterMm,
            requiredClearGapMm,
            localCurvePairs,
            tolerances,
            cancellationToken,
            arcCurveIndices,
            approximationErrorMm);

        if (polylineResult.Status != GeometryQueryStatus.Success || polylineResult.Value is not { } measurement)
            return polylineResult;

        double totalErrorBoundMm = measurement.ErrorBoundMm.Value + approximationErrorMm;
        if (!double.IsFinite(totalErrorBoundMm) || totalErrorBoundMm > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "ArcClearanceErrorBoundExceeded",
                "원호 근사와 수치 오차를 합친 최소거리 오차가 요청 한도를 넘었습니다.");
        }

        return GeometryQueryResult<ClearanceMeasurement>.Succeeded(new ClearanceMeasurement(
            measurement.MinimumCenterlineDistanceMm,
            measurement.MinimumClearGapMm,
            measurement.FirstLocation,
            measurement.SecondLocation,
            new LengthMm(totalErrorBoundMm),
            isComplete: true));
    }

    /// <summary>원호 하나를 관리형 간격 질의에서 사용할 오차 제한 다중선으로 변환합니다.</summary>
    internal static bool TryApproximateArc(
        CurveGeometrySnapshot curve,
        ArcCurveDefinition arc,
        double maximumChordDeviationMm,
        double endpointToleranceMm,
        int maximumPointCount,
        CancellationToken cancellationToken,
        out CurveGeometrySnapshot? approximatedCurve,
        out double measuredChordDeviationMm,
        out double coordinateScale,
        out double angleMagnitudeRad,
        out string? errorCode,
        out string? diagnostic)
    {
        approximatedCurve = null;
        measuredChordDeviationMm = 0;
        coordinateScale = 1;
        angleMagnitudeRad = 0;
        errorCode = "InvalidArcClearanceInput";
        diagnostic = "원호 간격 질의의 입력을 검증하지 못했습니다.";

        double startAngle = arc.Direction == CurveDirection.Forward ? arc.StartAngleRad : arc.EndAngleRad;
        double endAngle = arc.Direction == CurveDirection.Forward ? arc.EndAngleRad : arc.StartAngleRad;
        if (Math.Abs(startAngle) > MaximumSupportedAngleRad || Math.Abs(endAngle) > MaximumSupportedAngleRad)
        {
            errorCode = "ArcAngleRangeUnsupported";
            diagnostic = "원호 각도가 수치적으로 안정적인 관리형 범위를 벗어났습니다.";
            return false;
        }

        double sweep = endAngle - startAngle;
        double sweepMagnitude = Math.Abs(sweep);
        angleMagnitudeRad = Math.Max(Math.Abs(startAngle), Math.Abs(endAngle));
        if (!double.IsFinite(sweepMagnitude) || sweepMagnitude == 0)
            return false;

        double fullSweepRad = 2 * Math.PI;
        double angleRoundoff = MachineEpsilon * 64 * (Math.Abs(startAngle) + Math.Abs(endAngle) + 1);
        if (sweepMagnitude > fullSweepRad + angleRoundoff)
        {
            errorCode = "RepeatedArcSweepNotCertified";
            diagnostic = "한 바퀴를 넘는 반복 원호의 자기접촉을 관리형 계산으로 확정하지 않습니다.";
            return false;
        }

        bool isClosed = Math.Abs(sweepMagnitude - fullSweepRad) <= angleRoundoff;
        if (isClosed)
        {
            sweep = Math.CopySign(fullSweepRad, sweep);
            sweepMagnitude = fullSweepRad;
        }

        double radiusMm = arc.RadiusMm.Value;
        double maximumSweepPerSegment = 4 * Math.Asin(Math.Sqrt(
            Math.Min(maximumChordDeviationMm / (2 * radiusMm), 0.5)));
        maximumSweepPerSegment = Math.Min(maximumSweepPerSegment, Math.PI / 4);
        if (!double.IsFinite(maximumSweepPerSegment) || maximumSweepPerSegment <= 0)
        {
            errorCode = "ArcApproximationBudgetExceeded";
            diagnostic = "요청한 원호 거리 오차를 표현할 분할 간격을 계산할 수 없습니다.";
            return false;
        }

        double requestedSegmentCount = Math.Ceiling(sweepMagnitude / maximumSweepPerSegment);
        if (!double.IsFinite(requestedSegmentCount) ||
            requestedSegmentCount > MaximumArcSegmentCount ||
            requestedSegmentCount + 1 > maximumPointCount)
        {
            errorCode = "ArcApproximationBudgetExceeded";
            diagnostic = "원호 거리 근사에 필요한 분할 수가 관리형 작업 한도를 넘었습니다.";
            return false;
        }

        int segmentCount = Math.Max(1, (int)requestedSegmentCount);
        var points = new Vec3[segmentCount + 1];
        for (int index = 0; index <= segmentCount; index++)
        {
            if ((index & 1023) == 0 && cancellationToken.IsCancellationRequested)
            {
                errorCode = "OperationCancelled";
                diagnostic = "원호 간격 질의가 취소되었습니다.";
                return false;
            }

            double parameter = (double)index / segmentCount;
            double angle = startAngle + sweep * parameter;
            points[index] = EvaluateArc(arc, angle);
        }

        Vec3 expectedStart = EvaluateArc(arc, startAngle);
        Vec3 expectedEnd = EvaluateArc(arc, startAngle + sweep);
        if ((expectedStart - curve.Start).Length > endpointToleranceMm ||
            (expectedEnd - curve.End).Length > endpointToleranceMm)
        {
            errorCode = "CurveSnapshotMismatch";
            diagnostic = "원호 정의의 끝점이 측정 스냅샷과 일치하지 않습니다.";
            return false;
        }

        if (isClosed)
        {
            double closureDeviationMm = (expectedEnd - expectedStart).Length;
            measuredChordDeviationMm = Math.Max(measuredChordDeviationMm, closureDeviationMm);
            points[^1] = points[0];
        }

        for (int index = 0; index < points.Length; index++)
        {
            Vec3 point = points[index];
            coordinateScale = Math.Max(coordinateScale, MaximumAbsoluteCoordinate(point));
        }
        coordinateScale = Math.Max(coordinateScale, MaximumAbsoluteCoordinate(arc.Center));
        coordinateScale = Math.Max(coordinateScale, radiusMm);

        double angularStep = sweepMagnitude / segmentCount;
        double chordSagittaMm = 2 * radiusMm * Math.Pow(Math.Sin(angularStep / 4), 2);
        measuredChordDeviationMm += chordSagittaMm;
        double polylineLength = 0;
        double chordLength = 0;
        for (int index = 1; index < points.Length; index++)
        {
            if ((index & 1023) == 0 && cancellationToken.IsCancellationRequested)
            {
                errorCode = "OperationCancelled";
                diagnostic = "원호 간격 질의가 취소되었습니다.";
                return false;
            }

            double segmentLength = (points[index] - points[index - 1]).Length;
            polylineLength += segmentLength;
            chordLength = Math.Max(chordLength, segmentLength);
        }
        if (!double.IsFinite(polylineLength) || polylineLength <= 0)
        {
            errorCode = "InvalidArcClearanceInput";
            diagnostic = "원호 다중선 근사에서 유효한 길이를 계산하지 못했습니다.";
            return false;
        }

        try
        {
            var polyline = new PolylineCurveDefinition(points);
            var approximation = new CurveApproximationInfo(
                maximumChordDeviationMm,
                maximumSweepPerSegment,
                measuredChordDeviationMm,
                chordLength,
                points.Length,
                isComplete: true,
                RuleVersion);
            approximatedCurve = new CurveGeometrySnapshot(
                curve.HeatingPathSegmentId,
                curve.ConnectionPathSegmentId,
                polyline,
                new LengthMm(polylineLength),
                points[0],
                points[^1],
                points,
                approximation);
        }
        catch (ArgumentException exception)
        {
            errorCode = "InvalidArcClearanceInput";
            diagnostic = string.Concat("원호 다중선 근사를 만들지 못했습니다: ", exception.Message);
            return false;
        }

        errorCode = string.Empty;
        diagnostic = string.Empty;
        return true;
    }

    /// <summary>선언된 곡선 연결이 원호 끝점에 닿는지 확인합니다.</summary>
    internal static bool HasDeclaredArcJoint(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        IReadOnlyCollection<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        double endpointToleranceMm)
    {
        foreach ((int firstIndex, int secondIndex) in localCurvePairs)
        {
            if (firstIndex < 0 || firstIndex >= curves.Count || secondIndex < 0 || secondIndex >= curves.Count)
                continue;

            bool containsArc = curves[firstIndex].Definition is ArcCurveDefinition ||
                curves[secondIndex].Definition is ArcCurveDefinition;
            bool endpointsMeet =
                (curves[firstIndex].End - curves[secondIndex].Start).Length <= endpointToleranceMm;
            if (containsArc && endpointsMeet)
                return true;
        }

        return false;
    }

    private static Vec3 EvaluateArc(ArcCurveDefinition arc, double angle) =>
        arc.Center +
        arc.Plane.AxisX * (arc.RadiusMm.Value * Math.Cos(angle)) +
        arc.Plane.AxisY * (arc.RadiusMm.Value * Math.Sin(angle));

    /// <summary>좌표 크기와 각도에 따른 원호 평가 오차 상한을 계산합니다.</summary>
    internal static double EstimateEvaluationError(double coordinateScale, double angleMagnitudeRad) =>
        (coordinateScale + 1) * MachineEpsilon * 1024 * (angleMagnitudeRad + 1);

    private static double MaximumAbsoluteCoordinate(Vec3 point) => Math.Max(
        Math.Abs(point.X),
        Math.Max(Math.Abs(point.Y), Math.Abs(point.Z)));

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
            RuleVersion,
            evidence: [evidence])]);
}
