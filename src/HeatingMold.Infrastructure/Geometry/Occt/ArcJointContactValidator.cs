using System;
using System.Collections.Generic;
using System.Threading;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>선언된 원호 접점에서 추가 교차가 없는지 보수적으로 확인합니다.</summary>
internal static class ArcJointContactValidator
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const double FullTurnRad = 2 * Math.PI;
    private const int MaximumJointSegmentChecks = 1_000_000;

    /// <summary>원호와 직선형 곡선의 선언된 접점 밖 추가 교차를 확인합니다.</summary>
    public static ArcJointContactStatus Validate(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        IReadOnlyCollection<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        double endpointToleranceMm,
        CancellationToken cancellationToken)
    {
        int segmentChecks = 0;
        foreach ((int firstIndex, int secondIndex) in localCurvePairs)
        {
            if (cancellationToken.IsCancellationRequested)
                return ArcJointContactStatus.Cancelled;

            if (firstIndex < 0 || firstIndex >= curves.Count ||
                secondIndex < 0 || secondIndex >= curves.Count)
            {
                continue;
            }

            CurveGeometrySnapshot first = curves[firstIndex];
            CurveGeometrySnapshot second = curves[secondIndex];
            if (first.Definition is not ArcCurveDefinition &&
                second.Definition is not ArcCurveDefinition)
            {
                continue;
            }

            if ((first.End - second.Start).Length > endpointToleranceMm)
                continue;

            if (first.Definition is ArcCurveDefinition &&
                second.Definition is ArcCurveDefinition)
            {
                return ArcJointContactStatus.UnsupportedCurvePair;
            }

            ArcCurveDefinition arc = first.Definition as ArcCurveDefinition ??
                (ArcCurveDefinition)second.Definition;
            CurveGeometrySnapshot otherCurve = first.Definition is ArcCurveDefinition ? second : first;
            if (!TryGetPolylinePoints(otherCurve.Definition, out List<Vec3> points))
                return ArcJointContactStatus.UnsupportedCurvePair;

            for (int segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex++)
            {
                if ((segmentChecks & 1023) == 0 && cancellationToken.IsCancellationRequested)
                    return ArcJointContactStatus.Cancelled;
                if (++segmentChecks > MaximumJointSegmentChecks)
                    return ArcJointContactStatus.BudgetExceeded;

                ArcJointContactStatus status = CheckSegmentForAdditionalIntersection(
                    arc,
                    points[segmentIndex],
                    points[segmentIndex + 1],
                    first.End,
                    endpointToleranceMm);
                if (status != ArcJointContactStatus.Certified)
                    return status;
            }
        }

        return ArcJointContactStatus.Certified;
    }

    private static ArcJointContactStatus CheckSegmentForAdditionalIntersection(
        ArcCurveDefinition arc,
        Vec3 start,
        Vec3 end,
        Vec3 declaredJoint,
        double endpointToleranceMm)
    {
        Vec3 startFromCenter = start - arc.Center;
        Vec3 endFromCenter = end - arc.Center;
        double startPlaneDistance = Vec3.Dot(startFromCenter, arc.Plane.Normal);
        double endPlaneDistance = Vec3.Dot(endFromCenter, arc.Plane.Normal);
        double numericToleranceMm = CalculateNumericTolerance(arc, start, end);

        if (Math.Abs(startPlaneDistance) <= numericToleranceMm &&
            Math.Abs(endPlaneDistance) <= numericToleranceMm)
        {
            return CheckCoplanarSegment(
                arc,
                startFromCenter,
                endFromCenter,
                declaredJoint,
                endpointToleranceMm,
                numericToleranceMm);
        }

        double planeDistanceChange = startPlaneDistance - endPlaneDistance;
        if (Math.Abs(planeDistanceChange) <= numericToleranceMm)
            return ArcJointContactStatus.Certified;

        double parameter = startPlaneDistance / planeDistanceChange;
        double parameterTolerance = numericToleranceMm / Math.Max((end - start).Length, numericToleranceMm);
        if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
            return ArcJointContactStatus.Certified;

        parameter = Math.Clamp(parameter, 0, 1);
        Vec3 intersection = start + (end - start) * parameter;
        double radialDistance = GetPlaneCoordinates(intersection - arc.Center, arc).Length;
        if (Math.Abs(radialDistance - arc.RadiusMm.Value) > numericToleranceMm)
            return ArcJointContactStatus.Certified;

        return IsOnArc(intersection, arc, numericToleranceMm)
            ? ClassifyIntersection(intersection, declaredJoint, endpointToleranceMm)
            : ArcJointContactStatus.Certified;
    }

    private static ArcJointContactStatus CheckCoplanarSegment(
        ArcCurveDefinition arc,
        Vec3 startFromCenter,
        Vec3 endFromCenter,
        Vec3 declaredJoint,
        double endpointToleranceMm,
        double numericToleranceMm)
    {
        Vec2 start = GetPlaneCoordinates(startFromCenter, arc);
        Vec2 end = GetPlaneCoordinates(endFromCenter, arc);
        Vec2 direction = end - start;
        double quadratic = Dot(direction, direction);
        if (!double.IsFinite(quadratic) || quadratic <= 0)
            return ArcJointContactStatus.NumericalUncertainty;

        double linear = 2 * Dot(start, direction);
        double constant = Dot(start, start) - arc.RadiusMm.Value * arc.RadiusMm.Value;
        double discriminant = linear * linear - 4 * quadratic * constant;
        double discriminantTolerance = MachineEpsilon * 256 *
            (Math.Abs(linear * linear) + Math.Abs(4 * quadratic * constant) + 1);
        if (!double.IsFinite(discriminant) || !double.IsFinite(discriminantTolerance))
            return ArcJointContactStatus.NumericalUncertainty;
        if (discriminant < -discriminantTolerance)
            return ArcJointContactStatus.Certified;

        double squareRoot = Math.Sqrt(Math.Max(0, discriminant));
        double firstParameter = (-linear - squareRoot) / (2 * quadratic);
        double secondParameter = (-linear + squareRoot) / (2 * quadratic);
        double parameterTolerance = numericToleranceMm /
            Math.Max(Math.Sqrt(quadratic), numericToleranceMm);

        ArcJointContactStatus firstStatus = CheckIntersectionParameter(
            firstParameter,
            parameterTolerance,
            start,
            direction,
            arc,
            declaredJoint,
            endpointToleranceMm,
            numericToleranceMm);
        if (firstStatus != ArcJointContactStatus.Certified)
            return firstStatus;

        if (Math.Abs(secondParameter - firstParameter) <= parameterTolerance)
            return ArcJointContactStatus.Certified;

        return CheckIntersectionParameter(
            secondParameter,
            parameterTolerance,
            start,
            direction,
            arc,
            declaredJoint,
            endpointToleranceMm,
            numericToleranceMm);
    }

    private static ArcJointContactStatus CheckIntersectionParameter(
        double parameter,
        double parameterTolerance,
        Vec2 start,
        Vec2 direction,
        ArcCurveDefinition arc,
        Vec3 declaredJoint,
        double endpointToleranceMm,
        double numericToleranceMm)
    {
        if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
            return ArcJointContactStatus.Certified;

        parameter = Math.Clamp(parameter, 0, 1);
        Vec2 point = start + direction * parameter;
        double angle = Math.Atan2(point.Y, point.X);
        if (!IsAngleOnArc(angle, arc, numericToleranceMm))
            return ArcJointContactStatus.Certified;

        Vec3 intersection = arc.Center +
            arc.Plane.AxisX * point.X +
            arc.Plane.AxisY * point.Y;
        return ClassifyIntersection(intersection, declaredJoint, endpointToleranceMm);
    }

    private static ArcJointContactStatus ClassifyIntersection(
        Vec3 intersection,
        Vec3 declaredJoint,
        double endpointToleranceMm) =>
        (intersection - declaredJoint).Length <= endpointToleranceMm
            ? ArcJointContactStatus.Certified
            : ArcJointContactStatus.AdditionalIntersection;

    private static bool IsOnArc(Vec3 point, ArcCurveDefinition arc, double numericToleranceMm)
    {
        double angle = Math.Atan2(
            Vec3.Dot(point - arc.Center, arc.Plane.AxisY),
            Vec3.Dot(point - arc.Center, arc.Plane.AxisX));
        return IsAngleOnArc(angle, arc, numericToleranceMm);
    }

    private static bool IsAngleOnArc(double angle, ArcCurveDefinition arc, double numericToleranceMm)
    {
        double startAngle = arc.Direction == CurveDirection.Forward
            ? arc.StartAngleRad
            : arc.EndAngleRad;
        double endAngle = arc.Direction == CurveDirection.Forward
            ? arc.EndAngleRad
            : arc.StartAngleRad;
        double sweep = endAngle - startAngle;
        double angularTolerance = numericToleranceMm / arc.RadiusMm.Value +
            MachineEpsilon * 128 * (Math.Abs(startAngle) + Math.Abs(endAngle) + 1);
        double directedOffset = sweep >= 0
            ? PositiveModulo(angle - startAngle)
            : PositiveModulo(startAngle - angle);

        return Math.Abs(sweep) <= FullTurnRad + angularTolerance &&
            directedOffset <= Math.Abs(sweep) + angularTolerance;
    }

    private static double PositiveModulo(double angle)
    {
        double normalized = angle % FullTurnRad;
        return normalized < 0 ? normalized + FullTurnRad : normalized;
    }

    private static Vec2 GetPlaneCoordinates(Vec3 point, ArcCurveDefinition arc) => new(
        Vec3.Dot(point, arc.Plane.AxisX),
        Vec3.Dot(point, arc.Plane.AxisY));

    private static double Dot(Vec2 first, Vec2 second) => first.X * second.X + first.Y * second.Y;

    private static double CalculateNumericTolerance(
        ArcCurveDefinition arc,
        Vec3 start,
        Vec3 end)
    {
        double coordinateScale = Math.Max(
            arc.RadiusMm.Value,
            Math.Max(MaximumAbsoluteCoordinate(arc.Center),
                Math.Max(MaximumAbsoluteCoordinate(start), MaximumAbsoluteCoordinate(end))));
        return Math.Max(1, coordinateScale) * MachineEpsilon * 512;
    }

    private static double MaximumAbsoluteCoordinate(Vec3 point) => Math.Max(
        Math.Abs(point.X),
        Math.Max(Math.Abs(point.Y), Math.Abs(point.Z)));

    private static bool TryGetPolylinePoints(CurveDefinition definition, out List<Vec3> points)
    {
        switch (definition)
        {
            case LineCurveDefinition line:
                points = [line.Start, line.End];
                break;
            case PolylineCurveDefinition polyline:
                points = [.. polyline.Points];
                break;
            default:
                points = [];
                return false;
        }

        if (definition.Direction == CurveDirection.Reversed)
            points.Reverse();
        return true;
    }
}

/// <summary>원호 접점 교차 검사의 판정 결과입니다.</summary>
internal enum ArcJointContactStatus
{
    /// <summary>선언된 끝점 밖 추가 교차가 없음을 확인했습니다.</summary>
    Certified,

    /// <summary>선언된 끝점 이외의 추가 교차를 찾았습니다.</summary>
    AdditionalIntersection,

    /// <summary>지원하지 않는 곡선 조합이라 추가 교차를 판정하지 못했습니다.</summary>
    UnsupportedCurvePair,

    /// <summary>수치 조건 때문에 교차 여부를 안정적으로 판정하지 못했습니다.</summary>
    NumericalUncertainty,

    /// <summary>연결 접점 선분 검사 작업이 취소되었습니다.</summary>
    Cancelled,

    /// <summary>연결 접점 선분 검사 한도를 넘었습니다.</summary>
    BudgetExceeded
}
