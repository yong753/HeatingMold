using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>지원 곡선의 앵커 끝점에서 원형 도체 바깥면까지의 축방향 깊이를 계산합니다.</summary>
internal static class CurveEndpointDepthCalculator
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const double MinimumAxisProjectionLength = 1e-12;
    private const string RuleVersion = "curve-endpoint-depth-v3";

    /// <summary>앵커가 일치하는 곡선 끝의 단면을 측정하고 미지원 곡선은 미확정으로 반환합니다.</summary>
    public static GeometryQueryResult<DepthMeasurement> Measure(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        SurfaceHit surfaceHit,
        GeometryToleranceProfile tolerances)
    {
        if (wireDiameterMm.Value <= 0 || requiredInsertionDepthMm.Value < 0)
            return Unresolved("InvalidDepthInput", "도체 직경은 양수이고 요구 삽입 깊이는 0 이상이어야 합니다.");
        if (surfaceHit.FaceReferenceId != anchor.FaceReferenceId)
            return Unresolved("DepthAnchorFaceMismatch", "first-hit 면이 깊이 앵커의 안정 Face 참조와 다릅니다.");

        if (!TryGetEndpointGeometry(curve.Definition, atStart: true, out CurveEndpointGeometry startEndpoint) ||
            !TryGetEndpointGeometry(curve.Definition, atStart: false, out CurveEndpointGeometry endEndpoint))
        {
            return Unresolved(
                "UnsupportedDepthCurve",
                "현재 관리형 깊이 계산은 직선·다중선과 비주기 곡선 끝점만 지원합니다. 주기 곡선과 전체 Native 외피 측정은 지원하지 않습니다.",
                EntityId(curve));
        }

        if ((startEndpoint.Position - curve.Start).Length > tolerances.ConnectionToleranceMm ||
            (endEndpoint.Position - curve.End).Length > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "CurveSnapshotMismatch",
                "곡선 정의의 끝점이 최종 기하 스냅샷과 일치하지 않습니다.",
                EntityId(curve));
        }

        double anchorMismatch = (surfaceHit.ModelPoint - anchor.ModelPoint).Length;
        if (anchorMismatch > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "DepthAnchorChanged",
                "현재 first-hit 표면점이 저장된 깊이 앵커와 허용오차 밖으로 달라졌습니다.",
                EntityId(curve));
        }

        Vec3 axis = coordinateSystem.Projection.Normal;
        double startPlaneDistance = GetWorkingPlaneDistance(curve.Start, surfaceHit.ModelPoint, axis);
        double endPlaneDistance = GetWorkingPlaneDistance(curve.End, surfaceHit.ModelPoint, axis);
        bool startMatches = startPlaneDistance <= tolerances.ConnectionToleranceMm;
        bool endMatches = endPlaneDistance <= tolerances.ConnectionToleranceMm;
        if (startMatches == endMatches)
        {
            return Unresolved(
                startMatches ? "AmbiguousDepthAnchorEndpoint" : "DepthAnchorEndpointMismatch",
                startMatches
                    ? "두 곡선 끝점이 모두 앵커 작업 좌표와 일치해 깊이 측정 끝점을 선택할 수 없습니다."
                    : "곡선 끝점이 깊이 앵커 작업 좌표와 일치하지 않습니다.",
                EntityId(curve));
        }

        bool useStart = startMatches;
        Vec3 centerlinePoint = useStart ? curve.Start : curve.End;
        Vec3 tangentVector = useStart ? startEndpoint.Tangent : endEndpoint.Tangent;
        if (tangentVector.Length <= 0)
            return Unresolved("InvalidDepthTangent", "앵커 끝점의 곡선 접선을 계산할 수 없습니다.", EntityId(curve));

        Vec3 tangent = tangentVector.Normalized();
        double tangentAlongAxis = Math.Clamp(Vec3.Dot(tangent, axis), -1, 1);
        double axisSupport = (axis - tangent * tangentAlongAxis).Length;
        if (axisSupport <= MinimumAxisProjectionLength)
        {
            return Unresolved(
                "DepthAxisParallelToCurve",
                "작업축과 도체 접선이 평행해 작업축 광선과 원형 단면의 교점을 정할 수 없습니다.",
                EntityId(curve));
        }

        double wireRadiusMm = wireDiameterMm.Value / 2;
        Vec3 wireOutsidePoint = centerlinePoint + axis * (wireRadiusMm / axisSupport);
        double measuredDepthMm = Vec3.Dot(surfaceHit.ModelPoint - wireOutsidePoint, axis);
        if (!double.IsFinite(measuredDepthMm) || measuredDepthMm < 0)
        {
            return Unresolved(
                "WireOutsideSurface",
                "측정한 도체 바깥면이 선택 표면 바깥에 있거나 깊이 수치가 유효하지 않습니다.",
                EntityId(curve));
        }

        double coordinateScale = MaximumAbsoluteCoordinate(surfaceHit.ModelPoint, wireOutsidePoint);
        double numericError =
            (coordinateScale + wireRadiusMm + measuredDepthMm + 1) *
            MachineEpsilon *
            256 /
            axisSupport;
        if (!double.IsFinite(numericError) || numericError > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "DepthNumericallyUnresolved",
                "도체 접선과 작업축의 관계에서 깊이 오차가 허용 범위를 넘습니다.",
                EntityId(curve));
        }

        double errorBoundMm = Math.Max(tolerances.ConnectionToleranceMm, numericError);
        var measurement = new DepthMeasurement(
            requiredInsertionDepthMm,
            new LengthMm(measuredDepthMm),
            new LengthMm(errorBoundMm),
            surfaceHit.ModelPoint,
            wireOutsidePoint,
            isResolved: true);
        return GeometryQueryResult<DepthMeasurement>.Succeeded(measurement);
    }

    private static bool TryGetEndpointGeometry(
        CurveDefinition definition,
        bool atStart,
        out CurveEndpointGeometry endpoint)
    {
        switch (definition)
        {
            case LineCurveDefinition line:
                bool lineIsForward = definition.Direction == CurveDirection.Forward;
                Vec3 lineTangent = lineIsForward ? line.End - line.Start : line.Start - line.End;
                bool useLineStart = atStart == lineIsForward;
                return TryCreateEndpoint(useLineStart ? line.Start : line.End, lineTangent, out endpoint);
            case PolylineCurveDefinition polyline:
                bool polylineIsForward = definition.Direction == CurveDirection.Forward;
                IReadOnlyList<Vec3> points = polyline.Points;
                int firstPointIndex = polylineIsForward ? 0 : points.Count - 1;
                int lastPointIndex = points.Count - 1 - firstPointIndex;
                int endpointIndex = atStart ? firstPointIndex : lastPointIndex;
                int segmentOffset = atStart == polylineIsForward ? 1 : -1;
                int adjacentIndex = endpointIndex + segmentOffset;
                Vec3 polylinePoint = points[endpointIndex];
                Vec3 polylineTangent = atStart
                    ? points[adjacentIndex] - polylinePoint
                    : polylinePoint - points[adjacentIndex];
                return TryCreateEndpoint(polylinePoint, polylineTangent, out endpoint);
            case ArcCurveDefinition arc:
                bool arcIsForward = definition.Direction == CurveDirection.Forward;
                double angle = atStart == arcIsForward ? arc.StartAngleRad : arc.EndAngleRad;
                double traversalSign = Math.Sign(arc.EndAngleRad - arc.StartAngleRad) * (arcIsForward ? 1 : -1);
                Vec3 arcTangent = (
                    arc.Plane.AxisX * -Math.Sin(angle) +
                    arc.Plane.AxisY * Math.Cos(angle)) * traversalSign;
                return TryCreateEndpoint(EvaluateArcPoint(arc, angle), arcTangent, out endpoint);
            case BSplineCurveDefinition spline:
                bool splineIsForward = definition.Direction == CurveDirection.Forward;
                double parameter = atStart == splineIsForward ? spline.TrimStart : spline.TrimEnd;
                bool useLeftLimit = atStart != splineIsForward;
                if (!BSplineCurveEvaluator.TryCreate(spline, out BSplineCurveEvaluator? evaluator) ||
                    evaluator is null ||
                    !evaluator.TryEvaluate(parameter, useLeftLimit, out Vec3 point, out Vec3 tangent))
                {
                    endpoint = default;
                    return false;
                }

                if (!splineIsForward)
                    tangent *= -1;
                return TryCreateEndpoint(point, tangent, out endpoint);
            default:
                endpoint = default;
                return false;
        }
    }

    /// <summary>곡선 끝점의 위치와 진행 방향 접선을 함께 보관합니다.</summary>
    private readonly record struct CurveEndpointGeometry(Vec3 Position, Vec3 Tangent);

    /// <summary>끝점 위치와 접선이 유한한 값일 때만 결과로 반환합니다.</summary>
    private static bool TryCreateEndpoint(
        Vec3 position,
        Vec3 tangent,
        out CurveEndpointGeometry endpoint)
    {
        endpoint = new CurveEndpointGeometry(position, tangent);
        return IsFinite(position) && IsFinite(tangent);
    }

    private static bool IsFinite(Vec3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static Vec3 EvaluateArcPoint(ArcCurveDefinition arc, double angle) =>
        arc.Center +
        arc.Plane.AxisX * (arc.RadiusMm.Value * Math.Cos(angle)) +
        arc.Plane.AxisY * (arc.RadiusMm.Value * Math.Sin(angle));

    private static double GetWorkingPlaneDistance(Vec3 point, Vec3 planePoint, Vec3 planeNormal)
    {
        Vec3 difference = point - planePoint;
        Vec3 inPlane = difference - planeNormal * Vec3.Dot(difference, planeNormal);
        return inPlane.Length;
    }

    private static double MaximumAbsoluteCoordinate(Vec3 first, Vec3 second) => Math.Max(
        Math.Max(Math.Abs(first.X), Math.Abs(second.X)),
        Math.Max(
            Math.Max(Math.Abs(first.Y), Math.Abs(second.Y)),
            Math.Max(Math.Abs(first.Z), Math.Abs(second.Z))));

    private static Guid[] EntityId(CurveGeometrySnapshot curve) =>
        [curve.HeatingPathSegmentId?.Value ?? curve.ConnectionPathSegmentId!.Value.Value];

    private static GeometryQueryResult<DepthMeasurement> Unresolved(
        string code,
        string evidence,
        IEnumerable<Guid>? entityIds = null) => GeometryQueryResult<DepthMeasurement>.Unresolved(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Geometry.{code}",
            entityIds,
            RuleVersion,
            evidence: [evidence])]);
}
