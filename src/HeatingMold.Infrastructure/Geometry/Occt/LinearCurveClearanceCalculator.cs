using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>직선과 다중선 정의의 선분 쌍에서 비국소 최소 간격을 계산합니다.</summary>
internal static class LinearCurveClearanceCalculator
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const double ParallelAngleTolerance = 1e-6;
    private const int MaximumCandidatePairChecks = 10_000_000;
    private const string RuleVersion = "linear-curve-clearance-v2";

    /// <summary>직선형 곡선의 비국소 간격을 계산하고 지원 범위 밖에서는 미확정으로 반환합니다.</summary>
    public static GeometryQueryResult<ClearanceMeasurement> Measure(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        IReadOnlyCollection<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? ignoreSameCurveContacts = null,
        double additionalCurveDistanceErrorMm = 0,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)>? ignoreCurvePairs = null,
        IReadOnlySet<int>? ignoreLocalCurveContactValidation = null)
    {
        if (curves.Count == 0)
            return Unresolved("NoClearanceCurves", "간격을 측정할 곡선이 없습니다.");
        if (wireDiameterMm.Value <= 0 || requiredClearGapMm.Value < 0 ||
            !double.IsFinite(additionalCurveDistanceErrorMm) || additionalCurveDistanceErrorMm < 0)
            return Unresolved("InvalidClearanceInput", "도체 직경은 양수이고 요구 간격은 0 이상이어야 합니다.");

        if (curves.Any(curve => curve is null))
            return Unresolved("InvalidClearanceInput", "간격 질의에 null 곡선이 포함되어 있습니다.");

        List<LineSegment> segments = [];
        List<List<LineSegment>> segmentsByCurve = [];
        double coordinateScale = 1;
        for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "선분 간격 질의가 취소되었습니다.");

            CurveGeometrySnapshot curve = curves[curveIndex];
            if (!curve.Approximation.IsComplete)
            {
                return Unresolved(
                    "IncompleteCurveDefinition",
                    "불완전한 곡선 결과로 최소 간격을 확정할 수 없습니다.",
                    [curve.HeatingPathSegmentId?.Value ?? curve.ConnectionPathSegmentId!.Value.Value]);
            }

            if (!TryGetPolylinePoints(curve.Definition, out List<Vec3> points))
            {
                return Unresolved(
                    "UnsupportedClearanceCurve",
                    "현재 관리형 간격 계산은 직선과 다중선만 지원합니다. 원호와 B-spline은 Native 정밀 질의가 필요합니다.",
                    [curve.HeatingPathSegmentId?.Value ?? curve.ConnectionPathSegmentId!.Value.Value]);
            }

            if (curve.Definition.Direction == CurveDirection.Reversed)
                points.Reverse();

            if (!EndpointsMatchSnapshot(points[0], points[^1], curve, tolerances.ConnectionToleranceMm))
            {
                return Unresolved(
                    "CurveSnapshotMismatch",
                    "곡선 정의의 끝점이 측정 스냅샷과 일치하지 않습니다.",
                    [curve.HeatingPathSegmentId?.Value ?? curve.ConnectionPathSegmentId!.Value.Value]);
            }

            bool isClosed = points[0] == points[^1];
            int segmentCount = points.Count - 1;
            List<LineSegment> curveSegments = [];
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                Vec3 start = points[segmentIndex];
                Vec3 end = points[segmentIndex + 1];
                coordinateScale = Math.Max(coordinateScale, MaximumAbsoluteCoordinate(start, end));
                if (!TryCreateSegment(curveIndex, segmentIndex, segmentCount, isClosed, start, end, out LineSegment segment))
                    return Unresolved("InvalidCurveSegment", "곡선 정의에서 유효한 유한 선분을 만들 수 없습니다.");
                segments.Add(segment);
                curveSegments.Add(segment);
            }

            segmentsByCurve.Add(curveSegments);
        }

        if (segments.Count < 2)
            return Unresolved("NoNonlocalCurvePair", "비국소 간격을 측정할 서로 다른 선분 쌍이 없습니다.");

        HashSet<(int FirstCurveIndex, int SecondCurveIndex)> declaredCurvePairs = [.. localCurvePairs];
        var spatialIndex = new LineSegmentIndex(segments);

        if (!TryValidateLocalContacts(
            segmentsByCurve,
            declaredCurvePairs,
            ignoreLocalCurveContactValidation,
            tolerances.ConnectionToleranceMm,
            out GeometryQueryResult<ClearanceMeasurement>? localContactFailure))
        {
            return localContactFailure!;
        }

        int candidatePairChecks = 0;
        GeometryQueryResult<ClearanceMeasurement>? endpointFailure = FindUnclassifiedEndpointContact(
            segments,
            segmentsByCurve,
            spatialIndex,
            declaredCurvePairs,
            ignoreCurvePairs,
            tolerances.ConnectionToleranceMm,
            ref candidatePairChecks,
            cancellationToken);
        if (endpointFailure is not null)
            return endpointFailure;

        double bestDistance = double.PositiveInfinity;
        double maximumDistanceError = 0;
        Vec3 bestFirstLocation = default;
        Vec3 bestSecondLocation = default;
        int seedFirstIndex = -1;
        int seedSecondIndex = -1;

        // 첫 유효 쌍을 먼저 측정해 나머지 공간 탐색의 안전한 상한으로 사용합니다.
        for (int firstIndex = 0; firstIndex < segments.Count - 1 && seedFirstIndex < 0; firstIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "선분 간격 질의가 취소되었습니다.");

            for (int secondIndex = firstIndex + 1; secondIndex < segments.Count; secondIndex++)
            {
                LineSegment first = segments[firstIndex];
                LineSegment second = segments[secondIndex];
                if (ShouldIgnoreCurvePair(first, second, ignoreCurvePairs))
                    continue;
                if (ShouldIgnoreSameCurveContact(first, second, ignoreSameCurveContacts))
                    continue;

                if (++candidatePairChecks > MaximumCandidatePairChecks)
                    return Unresolved("ClearancePairBudgetExceeded", "선분 쌍 질의 한도를 넘었습니다.");
                if ((candidatePairChecks & 1023) == 0 && cancellationToken.IsCancellationRequested)
                    return Unresolved("OperationCancelled", "선분 간격 질의가 취소되었습니다.");

                if (IsLocalNeighbor(
                        first,
                        second,
                        segmentsByCurve,
                        declaredCurvePairs,
                        tolerances.ConnectionToleranceMm))
                {
                    if (first.CurveIndex != second.CurveIndex &&
                        (!TryGetSharedEndpoint(first, second, tolerances.ConnectionToleranceMm, out Vec3 sharedPoint) ||
                         !IsDeclaredCurveJoint(first, second, sharedPoint, declaredCurvePairs, tolerances.ConnectionToleranceMm)))
                    {
                        continue;
                    }

                    LocalContactKind contact = ClassifyLocalContact(first, second, tolerances.ConnectionToleranceMm);
                    if (contact == LocalContactKind.SeparateAtJoint)
                        continue;
                    if (contact == LocalContactKind.NumericallyAmbiguous)
                        return Unresolved("LocalContactUnresolved", "공유 접점에서 선분이 거의 같은 방향으로 겹치는지 수치적으로 구분할 수 없습니다.");
                }

                if (!TryMeasureSegmentPair(
                    first,
                    second,
                    tolerances.ConnectionToleranceMm,
                    out bestDistance,
                    out maximumDistanceError,
                    out bestFirstLocation,
                    out bestSecondLocation))
                {
                    return Unresolved("ClearanceNumericallyUnresolved", "선분 쌍이 평행에 가까워 최소 거리를 안정적으로 확정하지 못했습니다.");
                }

                seedFirstIndex = firstIndex;
                seedSecondIndex = secondIndex;
                break;
            }
        }

        if (seedFirstIndex < 0 || !double.IsFinite(bestDistance))
            return Unresolved("NoNonlocalCurvePair", "국소 공유 접점을 제외한 비국소 선분 쌍이 없습니다.");

        for (int firstIndex = 0; firstIndex < segments.Count - 1; firstIndex++)
        {
            if ((firstIndex & 127) == 0 && cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "선분 간격 질의가 취소되었습니다.");

            double candidateDistanceLimit = MaximumRelevantAabbDistance(
                bestDistance,
                maximumDistanceError + additionalCurveDistanceErrorMm,
                coordinateScale);
            List<int> candidateIndices = spatialIndex.FindWithinDistance(firstIndex, candidateDistanceLimit);
            foreach (int secondIndex in candidateIndices)
            {
                if (secondIndex <= firstIndex ||
                    firstIndex == seedFirstIndex && secondIndex == seedSecondIndex)
                {
                    continue;
                }

                if (++candidatePairChecks > MaximumCandidatePairChecks)
                    return Unresolved("ClearancePairBudgetExceeded", "선분 쌍 질의 한도를 넘었습니다.");
                if ((candidatePairChecks & 1023) == 0 && cancellationToken.IsCancellationRequested)
                    return Unresolved("OperationCancelled", "선분 간격 질의가 취소되었습니다.");

                LineSegment first = segments[firstIndex];
                LineSegment second = segments[secondIndex];
                if (ShouldIgnoreCurvePair(first, second, ignoreCurvePairs))
                    continue;
                if (ShouldIgnoreSameCurveContact(first, second, ignoreSameCurveContacts))
                    continue;

                if (IsLocalNeighbor(
                        first,
                        second,
                        segmentsByCurve,
                        declaredCurvePairs,
                        tolerances.ConnectionToleranceMm))
                {
                    if (first.CurveIndex != second.CurveIndex &&
                        (!TryGetSharedEndpoint(first, second, tolerances.ConnectionToleranceMm, out Vec3 sharedPoint) ||
                         !IsDeclaredCurveJoint(first, second, sharedPoint, declaredCurvePairs, tolerances.ConnectionToleranceMm)))
                    {
                        continue;
                    }

                    LocalContactKind contact = ClassifyLocalContact(first, second, tolerances.ConnectionToleranceMm);
                    if (contact == LocalContactKind.SeparateAtJoint)
                        continue;
                    if (contact == LocalContactKind.NumericallyAmbiguous)
                        return Unresolved("LocalContactUnresolved", "공유 접점에서 선분이 거의 같은 방향으로 겹치는지 수치적으로 구분할 수 없습니다.");
                }

                double aabbDistance = AabbDistance(first, second);
                double aabbError = EstimateErrorBound(coordinateScale, aabbDistance);
                if (aabbDistance - aabbError - additionalCurveDistanceErrorMm >
                    bestDistance + maximumDistanceError)
                    continue;

                if (!TryMeasureSegmentPair(
                    first,
                    second,
                    tolerances.ConnectionToleranceMm,
                    out double distance,
                    out double distanceError,
                    out Vec3 firstLocation,
                    out Vec3 secondLocation))
                {
                    return Unresolved("ClearanceNumericallyUnresolved", "선분 쌍이 평행에 가까워 최소 거리를 안정적으로 확정하지 못했습니다.");
                }

                maximumDistanceError = Math.Max(maximumDistanceError, distanceError);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestFirstLocation = firstLocation;
                bestSecondLocation = secondLocation;
            }
        }

        var measurement = new ClearanceMeasurement(
            new LengthMm(bestDistance),
            new LengthMm(bestDistance - wireDiameterMm.Value),
            bestFirstLocation,
            bestSecondLocation,
            new LengthMm(maximumDistanceError),
            isComplete: true);
        return GeometryQueryResult<ClearanceMeasurement>.Succeeded(measurement);
    }

    private static GeometryQueryResult<ClearanceMeasurement>? FindUnclassifiedEndpointContact(
        IReadOnlyList<LineSegment> segments,
        IReadOnlyList<List<LineSegment>> segmentsByCurve,
        LineSegmentIndex spatialIndex,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)> declaredCurvePairs,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)>? ignoredCurvePairs,
        double endpointToleranceMm,
        ref int candidatePairChecks,
        CancellationToken cancellationToken)
    {
        for (int firstIndex = 0; firstIndex < segments.Count; firstIndex++)
        {
            if ((firstIndex & 127) == 0 && cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "선분 간격 질의가 취소되었습니다.");

            LineSegment first = segments[firstIndex];
            HashSet<int> nearbySegments = [];
            spatialIndex.CollectIntersecting(AxisAlignedBounds.AroundPoint(first.Start, endpointToleranceMm), nearbySegments);
            spatialIndex.CollectIntersecting(AxisAlignedBounds.AroundPoint(first.End, endpointToleranceMm), nearbySegments);

            foreach (int secondIndex in nearbySegments.Order())
            {
                if (secondIndex <= firstIndex)
                    continue;

                if (++candidatePairChecks > MaximumCandidatePairChecks)
                    return Unresolved("ClearancePairBudgetExceeded", "선분 쌍 질의 한도를 넘었습니다.");

                LineSegment second = segments[secondIndex];
                if (first.CurveIndex == second.CurveIndex ||
                    ShouldIgnoreCurvePair(first, second, ignoredCurvePairs) ||
                    !TryGetSharedEndpoint(first, second, endpointToleranceMm, out _))
                {
                    continue;
                }

                if (IsDeclaredCurveJointNeighborhood(
                        first,
                        second,
                        segmentsByCurve,
                        declaredCurvePairs,
                        endpointToleranceMm))
                    continue;

                return Unresolved(
                    "UnclassifiedCurveEndpointContact",
                    "공유 끝점의 설계 연결 관계 또는 연속 끝점 방향이 일치하지 않아 접촉을 안전하게 분류할 수 없습니다.");
            }
        }

        return null;
    }

    private static bool TryValidateLocalContacts(
        IReadOnlyList<List<LineSegment>> segmentsByCurve,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)> declaredCurvePairs,
        IReadOnlySet<int>? ignoreLocalCurveContactValidation,
        double endpointToleranceMm,
        out GeometryQueryResult<ClearanceMeasurement>? failure)
    {
        for (int curveIndex = 0; curveIndex < segmentsByCurve.Count; curveIndex++)
        {
            if (ignoreLocalCurveContactValidation?.Contains(curveIndex) == true)
                continue;

            List<LineSegment> curveSegments = segmentsByCurve[curveIndex];
            for (int index = 0; index < curveSegments.Count - 1; index++)
            {
                if (!TryValidateLocalContact(curveSegments[index], curveSegments[index + 1], endpointToleranceMm, out failure))
                    return false;
            }

            if (curveSegments.Count > 1 && curveSegments[0].IsClosed &&
                !TryValidateLocalContact(curveSegments[0], curveSegments[^1], endpointToleranceMm, out failure))
            {
                return false;
            }
        }

        foreach ((int firstCurveIndex, int secondCurveIndex) in declaredCurvePairs)
        {
            if (firstCurveIndex < 0 || firstCurveIndex >= segmentsByCurve.Count ||
                secondCurveIndex < 0 || secondCurveIndex >= segmentsByCurve.Count ||
                segmentsByCurve[firstCurveIndex].Count == 0 || segmentsByCurve[secondCurveIndex].Count == 0)
            {
                continue;
            }

            LineSegment first = segmentsByCurve[firstCurveIndex][^1];
            LineSegment second = segmentsByCurve[secondCurveIndex][0];
            bool isDeclaredJoint =
                IsDeclaredCurveJoint(first, second, first.End, declaredCurvePairs, endpointToleranceMm) ||
                IsDeclaredCurveJoint(second, first, second.End, declaredCurvePairs, endpointToleranceMm);
            if (isDeclaredJoint && !TryValidateLocalContact(first, second, endpointToleranceMm, out failure))
                return false;
        }

        failure = null;
        return true;
    }

    private static bool TryValidateLocalContact(
        LineSegment first,
        LineSegment second,
        double endpointToleranceMm,
        out GeometryQueryResult<ClearanceMeasurement>? failure)
    {
        if (TryGetSharedEndpoint(first, second, endpointToleranceMm, out _) &&
            ClassifyLocalContact(first, second, endpointToleranceMm) == LocalContactKind.NumericallyAmbiguous)
        {
            failure = Unresolved("LocalContactUnresolved", "공유 접점에서 선분이 거의 같은 방향으로 겹치는지 수치적으로 구분할 수 없습니다.");
            return false;
        }

        failure = null;
        return true;
    }

    private static double MaximumRelevantAabbDistance(
        double bestDistance,
        double maximumDistanceError,
        double coordinateScale)
    {
        double relativeError = MachineEpsilon * 512;
        double coordinateError = (coordinateScale + 1) * relativeError;
        return (bestDistance + maximumDistanceError + coordinateError) / (1 - relativeError);
    }

    private static bool TryGetPolylinePoints(CurveDefinition definition, out List<Vec3> points)
    {
        switch (definition)
        {
            case LineCurveDefinition line:
                points = [line.Start, line.End];
                return true;
            case PolylineCurveDefinition polyline:
                points = polyline.Points.ToList();
                return true;
            default:
                points = [];
                return false;
        }
    }

    private static bool EndpointsMatchSnapshot(
        Vec3 start,
        Vec3 end,
        CurveGeometrySnapshot curve,
        double toleranceMm) =>
        (start - curve.Start).Length <= toleranceMm && (end - curve.End).Length <= toleranceMm;

    private static bool TryCreateSegment(
        int curveIndex,
        int segmentIndex,
        int segmentCount,
        bool isClosed,
        Vec3 start,
        Vec3 end,
        out LineSegment segment)
    {
        segment = default;
        Vec3 direction = end - start;
        double length = direction.Length;
        if (!double.IsFinite(length) || length <= 0)
            return false;

        segment = new LineSegment(curveIndex, segmentIndex, segmentCount, isClosed, start, end, direction);
        return true;
    }

    private static bool IsLocalNeighbor(
        LineSegment first,
        LineSegment second,
        IReadOnlyList<List<LineSegment>> segmentsByCurve,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        double endpointToleranceMm)
    {
        if (first.CurveIndex == second.CurveIndex)
        {
            int indexDifference = Math.Abs(first.SegmentIndex - second.SegmentIndex);
            return indexDifference == 1 ||
                first.IsClosed && indexDifference == first.SegmentCount - 1;
        }

        return TryGetSharedEndpoint(first, second, endpointToleranceMm, out _) &&
            IsDeclaredCurveJointNeighborhood(
                first,
                second,
                segmentsByCurve,
                localCurvePairs,
                endpointToleranceMm);
    }

    private static bool ShouldIgnoreSameCurveContact(
        LineSegment first,
        LineSegment second,
        IReadOnlySet<int>? ignoredCurveIndices) =>
        first.CurveIndex == second.CurveIndex && ignoredCurveIndices?.Contains(first.CurveIndex) == true;

    private static bool ShouldIgnoreCurvePair(
        LineSegment first,
        LineSegment second,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)>? ignoredCurvePairs) =>
        ignoredCurvePairs is not null &&
        (ignoredCurvePairs.Contains((first.CurveIndex, second.CurveIndex)) ||
         ignoredCurvePairs.Contains((second.CurveIndex, first.CurveIndex)));

    private static bool IsDeclaredCurveJoint(
        LineSegment first,
        LineSegment second,
        Vec3 sharedPoint,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)> curvePairs,
        double endpointToleranceMm)
    {
        bool firstToSecond =
            curvePairs.Contains((first.CurveIndex, second.CurveIndex)) &&
            first.SegmentIndex == first.SegmentCount - 1 &&
            second.SegmentIndex == 0 &&
            (sharedPoint - first.End).Length <= endpointToleranceMm &&
            (sharedPoint - second.Start).Length <= endpointToleranceMm;
        bool secondToFirst =
            curvePairs.Contains((second.CurveIndex, first.CurveIndex)) &&
            second.SegmentIndex == second.SegmentCount - 1 &&
            first.SegmentIndex == 0 &&
            (sharedPoint - second.End).Length <= endpointToleranceMm &&
            (sharedPoint - first.Start).Length <= endpointToleranceMm;

        return firstToSecond || secondToFirst;
    }

    private static bool IsDeclaredCurveJointNeighborhood(
        LineSegment first,
        LineSegment second,
        IReadOnlyList<List<LineSegment>> segmentsByCurve,
        IReadOnlySet<(int FirstCurveIndex, int SecondCurveIndex)> curvePairs,
        double endpointToleranceMm)
    {
        if (curvePairs.Contains((first.CurveIndex, second.CurveIndex)) &&
            first.CurveIndex < segmentsByCurve.Count && second.CurveIndex < segmentsByCurve.Count)
        {
            Vec3 joint = segmentsByCurve[first.CurveIndex][^1].End;
            if ((first.End - joint).Length <= endpointToleranceMm &&
                (second.Start - joint).Length <= endpointToleranceMm)
            {
                return true;
            }
        }

        if (curvePairs.Contains((second.CurveIndex, first.CurveIndex)) &&
            second.CurveIndex < segmentsByCurve.Count && first.CurveIndex < segmentsByCurve.Count)
        {
            Vec3 joint = segmentsByCurve[second.CurveIndex][^1].End;
            if ((first.Start - joint).Length <= endpointToleranceMm &&
                (second.End - joint).Length <= endpointToleranceMm)
            {
                return true;
            }
        }

        return false;
    }

    private static LocalContactKind ClassifyLocalContact(
        LineSegment first,
        LineSegment second,
        double endpointToleranceMm)
    {
        if (!TryGetSharedEndpoint(first, second, endpointToleranceMm, out Vec3 sharedPoint))
            return LocalContactKind.SeparateAtJoint;

        Vec3 firstOther = OtherEndpoint(first, sharedPoint);
        Vec3 secondOther = OtherEndpoint(second, sharedPoint);
        Vec3 firstRay = firstOther - sharedPoint;
        Vec3 secondRay = secondOther - sharedPoint;
        double dot = Vec3.Dot(firstRay, secondRay);
        Vec3 cross = Vec3.Cross(firstRay, secondRay);
        double scale = firstRay.Length * secondRay.Length;
        if (cross.X == 0 && cross.Y == 0 && cross.Z == 0)
        {
            return dot <= 0
                ? LocalContactKind.SeparateAtJoint
                : LocalContactKind.Overlapping;
        }

        double sine = cross.Length / scale;
        if (sine > ParallelAngleTolerance)
            return LocalContactKind.SeparateAtJoint;

        if (dot < 0)
            return LocalContactKind.SeparateAtJoint;

        return cross.X == 0 && cross.Y == 0 && cross.Z == 0
            ? LocalContactKind.Overlapping
            : LocalContactKind.NumericallyAmbiguous;
    }

    private static bool TryGetSharedEndpoint(
        LineSegment first,
        LineSegment second,
        double endpointToleranceMm,
        out Vec3 sharedPoint)
    {
        Vec3[] firstEndpoints = [first.Start, first.End];
        Vec3[] secondEndpoints = [second.Start, second.End];
        foreach (Vec3 firstEndpoint in firstEndpoints)
        {
            foreach (Vec3 secondEndpoint in secondEndpoints)
            {
                if ((firstEndpoint - secondEndpoint).Length <= endpointToleranceMm)
                {
                    sharedPoint = firstEndpoint;
                    return true;
                }
            }
        }

        sharedPoint = default;
        return false;
    }

    private static Vec3 OtherEndpoint(LineSegment segment, Vec3 sharedPoint) =>
        (segment.Start - sharedPoint).Length <= (segment.End - sharedPoint).Length
            ? segment.End
            : segment.Start;

    private static bool TryMeasureSegmentPair(
        LineSegment first,
        LineSegment second,
        double maximumAllowedErrorMm,
        out double distance,
        out double distanceErrorBound,
        out Vec3 firstLocation,
        out Vec3 secondLocation)
    {
        Vec3 offset = first.Start - second.Start;
        double firstLengthSquared = Vec3.Dot(first.Direction, first.Direction);
        double secondLengthSquared = Vec3.Dot(second.Direction, second.Direction);
        double directionDot = Vec3.Dot(first.Direction, second.Direction);
        double firstOffsetDot = Vec3.Dot(first.Direction, offset);
        double secondOffsetDot = Vec3.Dot(second.Direction, offset);

        Vec3 directionCross = Vec3.Cross(first.Direction, second.Direction);
        if (directionCross.X == 0 && directionCross.Y == 0 && directionCross.Z == 0)
        {
            if (!TryMeasureParallelSegments(
                first,
                second,
                out distance,
                out firstLocation,
                out secondLocation))
            {
                distanceErrorBound = 0;
                return false;
            }

            distanceErrorBound = EstimateErrorBound(MaximumAbsoluteCoordinate(first, second), distance);
            return distanceErrorBound <= maximumAllowedErrorMm;
        }

        double crossLength = directionCross.Length;
        double directionMagnitudeProduct = Math.Sqrt(firstLengthSquared) * Math.Sqrt(secondLengthSquared);
        double sineOfDirectionAngle = crossLength / directionMagnitudeProduct;
        double denominator = crossLength * crossLength;
        double conditioningScale = firstLengthSquared * secondLengthSquared;
        if (!double.IsFinite(denominator) || !double.IsFinite(conditioningScale) ||
            !double.IsFinite(sineOfDirectionAngle))
        {
            distance = 0;
            distanceErrorBound = 0;
            firstLocation = default;
            secondLocation = default;
            return false;
        }

        if (sineOfDirectionAngle <= ParallelAngleTolerance ||
            denominator <= conditioningScale * ParallelAngleTolerance * ParallelAngleTolerance)
        {
            return TryMeasureNearlyParallelSegments(
                first,
                second,
                maximumAllowedErrorMm,
                out distance,
                out distanceErrorBound,
                out firstLocation,
                out secondLocation);
        }

        double firstParameter = Math.Clamp(
            (directionDot * secondOffsetDot - firstOffsetDot * secondLengthSquared) / denominator,
            0,
            1);
        double secondParameter = Math.Clamp(
            (directionDot * firstParameter + secondOffsetDot) / secondLengthSquared,
            0,
            1);

        if (secondParameter == 0)
        {
            firstParameter = Math.Clamp(-firstOffsetDot / firstLengthSquared, 0, 1);
        }
        else if (secondParameter == 1)
        {
            firstParameter = Math.Clamp((directionDot - firstOffsetDot) / firstLengthSquared, 0, 1);
        }

        firstLocation = first.Start + first.Direction * firstParameter;
        secondLocation = second.Start + second.Direction * secondParameter;
        distance = (firstLocation - secondLocation).Length;
        double conditionFactor = 1 / (sineOfDirectionAngle * sineOfDirectionAngle);
        distanceErrorBound = EstimateErrorBound(MaximumAbsoluteCoordinate(first, second), distance) * conditionFactor;
        return double.IsFinite(distance) &&
            double.IsFinite(distanceErrorBound) &&
            distanceErrorBound <= maximumAllowedErrorMm;
    }

    /// <summary>후보 거리 상한과 AABB 하한의 간격이 허용오차 안인지 확인합니다.</summary>
    private static bool TryMeasureNearlyParallelSegments(
        LineSegment first,
        LineSegment second,
        double maximumAllowedErrorMm,
        out double distance,
        out double distanceErrorBound,
        out Vec3 firstLocation,
        out Vec3 secondLocation)
    {
        if (!TryMeasureParallelSegments(first, second, out distance, out firstLocation, out secondLocation))
        {
            distanceErrorBound = 0;
            return false;
        }

        double coordinateScale = MaximumAbsoluteCoordinate(first, second);
        double aabbDistance = AabbDistance(first, second);
        double aabbError = EstimateErrorBound(coordinateScale, aabbDistance);
        double lowerDistanceBound = Math.Max(0, aabbDistance - aabbError);
        double candidateError = EstimateErrorBound(coordinateScale, distance);
        distanceErrorBound = Math.Abs(distance - lowerDistanceBound) + candidateError;

        return double.IsFinite(distanceErrorBound) && distanceErrorBound <= maximumAllowedErrorMm;
    }

    private static bool TryMeasureParallelSegments(
        LineSegment first,
        LineSegment second,
        out double distance,
        out Vec3 firstLocation,
        out Vec3 secondLocation)
    {
        SegmentDistance best = default;
        best.Distance = double.PositiveInfinity;

        Consider(first.Start, second, firstPointIsOnFirst: true, ref best);
        Consider(first.End, second, firstPointIsOnFirst: true, ref best);
        Consider(second.Start, first, firstPointIsOnFirst: false, ref best);
        Consider(second.End, first, firstPointIsOnFirst: false, ref best);

        distance = best.Distance;
        firstLocation = best.FirstLocation;
        secondLocation = best.SecondLocation;
        return double.IsFinite(distance);
    }

    private static void Consider(Vec3 point, LineSegment segment, bool firstPointIsOnFirst, ref SegmentDistance best)
    {
        double lengthSquared = Vec3.Dot(segment.Direction, segment.Direction);
        double parameter = Math.Clamp(Vec3.Dot(point - segment.Start, segment.Direction) / lengthSquared, 0, 1);
        Vec3 projected = segment.Start + segment.Direction * parameter;
        double distance = (point - projected).Length;
        if (distance >= best.Distance)
            return;

        best.Distance = distance;
        best.FirstLocation = firstPointIsOnFirst ? point : projected;
        best.SecondLocation = firstPointIsOnFirst ? projected : point;
    }

    private static double AabbDistance(LineSegment first, LineSegment second) =>
        AabbDistance(
            new AxisAlignedBounds(first.Minimum, first.Maximum),
            new AxisAlignedBounds(second.Minimum, second.Maximum));

    private static double AabbDistance(LineSegment segment, AxisAlignedBounds bounds) =>
        AabbDistance(new AxisAlignedBounds(segment.Minimum, segment.Maximum), bounds);

    private static double AabbDistance(AxisAlignedBounds first, AxisAlignedBounds second)
    {
        double xGap = AxisGap(first.Minimum.X, first.Maximum.X, second.Minimum.X, second.Maximum.X);
        double yGap = AxisGap(first.Minimum.Y, first.Maximum.Y, second.Minimum.Y, second.Maximum.Y);
        double zGap = AxisGap(first.Minimum.Z, first.Maximum.Z, second.Minimum.Z, second.Maximum.Z);
        return new Vec3(xGap, yGap, zGap).Length;
    }

    private static double AxisGap(double firstMinimum, double firstMaximum, double secondMinimum, double secondMaximum)
    {
        if (firstMaximum < secondMinimum)
            return secondMinimum - firstMaximum;
        if (secondMaximum < firstMinimum)
            return firstMinimum - secondMaximum;
        return 0;
    }

    private static double MaximumAbsoluteCoordinate(Vec3 first, Vec3 second) => Math.Max(
        Math.Max(Math.Abs(first.X), Math.Abs(second.X)),
        Math.Max(
            Math.Max(Math.Abs(first.Y), Math.Abs(second.Y)),
            Math.Max(Math.Abs(first.Z), Math.Abs(second.Z))));

    private static double MaximumAbsoluteCoordinate(LineSegment first, LineSegment second) => Math.Max(
        MaximumAbsoluteCoordinate(first.Start, first.End),
        MaximumAbsoluteCoordinate(second.Start, second.End));

    private static double EstimateErrorBound(double coordinateScale, double distance) =>
        (coordinateScale + (double.IsFinite(distance) ? distance : 0) + 1) * MachineEpsilon * 512;

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

    private enum LocalContactKind
    {
        SeparateAtJoint,
        Overlapping,
        NumericallyAmbiguous
    }

    private struct SegmentDistance
    {
        public double Distance;
        public Vec3 FirstLocation;
        public Vec3 SecondLocation;
    }

    private readonly record struct LineSegment(
        int CurveIndex,
        int SegmentIndex,
        int SegmentCount,
        bool IsClosed,
        Vec3 Start,
        Vec3 End,
        Vec3 Direction)
    {
        public Vec3 Minimum => new(
            Math.Min(Start.X, End.X),
            Math.Min(Start.Y, End.Y),
            Math.Min(Start.Z, End.Z));

        public Vec3 Maximum => new(
            Math.Max(Start.X, End.X),
            Math.Max(Start.Y, End.Y),
            Math.Max(Start.Z, End.Z));
    }

    private readonly record struct AxisAlignedBounds(Vec3 Minimum, Vec3 Maximum)
    {
        public static AxisAlignedBounds AroundPoint(Vec3 point, double expansionMm)
        {
            Vec3 expansion = new(expansionMm, expansionMm, expansionMm);
            return new AxisAlignedBounds(point - expansion, point + expansion);
        }

        public bool Intersects(AxisAlignedBounds other) =>
            Minimum.X <= other.Maximum.X && Maximum.X >= other.Minimum.X &&
            Minimum.Y <= other.Maximum.Y && Maximum.Y >= other.Minimum.Y &&
            Minimum.Z <= other.Maximum.Z && Maximum.Z >= other.Minimum.Z;

        public AxisAlignedBounds Union(AxisAlignedBounds other) => new(
            new Vec3(
                Math.Min(Minimum.X, other.Minimum.X),
                Math.Min(Minimum.Y, other.Minimum.Y),
                Math.Min(Minimum.Z, other.Minimum.Z)),
            new Vec3(
                Math.Max(Maximum.X, other.Maximum.X),
                Math.Max(Maximum.Y, other.Maximum.Y),
                Math.Max(Maximum.Z, other.Maximum.Z)));

        public int WidestAxis
        {
            get
            {
                double xSpan = HalfSpan(Minimum.X, Maximum.X);
                double ySpan = HalfSpan(Minimum.Y, Maximum.Y);
                double zSpan = HalfSpan(Minimum.Z, Maximum.Z);
                return xSpan >= ySpan && xSpan >= zSpan ? 0 : ySpan >= zSpan ? 1 : 2;
            }
        }

        public static double CenterCoordinate(AxisAlignedBounds bounds, int axis) => axis switch
        {
            0 => Center(bounds.Minimum.X, bounds.Maximum.X),
            1 => Center(bounds.Minimum.Y, bounds.Maximum.Y),
            _ => Center(bounds.Minimum.Z, bounds.Maximum.Z)
        };

        private static double HalfSpan(double minimum, double maximum) => maximum * 0.5 - minimum * 0.5;

        private static double Center(double minimum, double maximum) => minimum * 0.5 + maximum * 0.5;
    }

    private sealed class LineSegmentIndex
    {
        private const int LeafCapacity = 8;

        private readonly LineSegment[] _segments;
        private readonly int[] _orderedSegmentIndices;
        private readonly BoundsNode _root;

        public LineSegmentIndex(IReadOnlyList<LineSegment> segments)
        {
            _segments = segments.ToArray();
            _orderedSegmentIndices = Enumerable.Range(0, _segments.Length).ToArray();
            _root = BuildNode(0, _orderedSegmentIndices.Length);
        }

        public List<int> FindWithinDistance(int segmentIndex, double maximumDistance)
        {
            var matches = new List<int>();
            var pending = new Stack<BoundsNode>();
            LineSegment segment = _segments[segmentIndex];
            pending.Push(_root);

            while (pending.TryPop(out BoundsNode? node))
            {
                if (AabbDistance(segment, node.Bounds) > maximumDistance)
                    continue;

                if (node.IsLeaf)
                {
                    for (int offset = 0; offset < node.Count; offset++)
                    {
                        int candidateIndex = _orderedSegmentIndices[node.Start + offset];
                        if (candidateIndex > segmentIndex &&
                            AabbDistance(segment, _segments[candidateIndex]) <= maximumDistance)
                        {
                            matches.Add(candidateIndex);
                        }
                    }

                    continue;
                }

                pending.Push(node.Right!);
                pending.Push(node.Left!);
            }

            matches.Sort();
            return matches;
        }

        public void CollectIntersecting(AxisAlignedBounds query, HashSet<int> matches)
        {
            var pending = new Stack<BoundsNode>();
            pending.Push(_root);

            while (pending.TryPop(out BoundsNode? node))
            {
                if (!query.Intersects(node.Bounds))
                    continue;

                if (node.IsLeaf)
                {
                    for (int offset = 0; offset < node.Count; offset++)
                    {
                        int segmentIndex = _orderedSegmentIndices[node.Start + offset];
                        AxisAlignedBounds segmentBounds = new(
                            _segments[segmentIndex].Minimum,
                            _segments[segmentIndex].Maximum);
                        if (query.Intersects(segmentBounds))
                            matches.Add(segmentIndex);
                    }

                    continue;
                }

                pending.Push(node.Right!);
                pending.Push(node.Left!);
            }
        }

        private BoundsNode BuildNode(int start, int count)
        {
            AxisAlignedBounds bounds = GetBounds(start, count);
            if (count <= LeafCapacity)
                return new BoundsNode(bounds, start, count);

            int axis = bounds.WidestAxis;
            Array.Sort(_orderedSegmentIndices, start, count, Comparer<int>.Create((firstIndex, secondIndex) =>
            {
                AxisAlignedBounds first = new(_segments[firstIndex].Minimum, _segments[firstIndex].Maximum);
                AxisAlignedBounds second = new(_segments[secondIndex].Minimum, _segments[secondIndex].Maximum);
                int centerComparison = AxisAlignedBounds.CenterCoordinate(first, axis)
                    .CompareTo(AxisAlignedBounds.CenterCoordinate(second, axis));
                return centerComparison != 0 ? centerComparison : firstIndex.CompareTo(secondIndex);
            }));

            int leftCount = count / 2;
            BoundsNode left = BuildNode(start, leftCount);
            BoundsNode right = BuildNode(start + leftCount, count - leftCount);
            return new BoundsNode(bounds, left, right);
        }

        private AxisAlignedBounds GetBounds(int start, int count)
        {
            LineSegment first = _segments[_orderedSegmentIndices[start]];
            AxisAlignedBounds bounds = new(first.Minimum, first.Maximum);
            for (int offset = 1; offset < count; offset++)
            {
                LineSegment segment = _segments[_orderedSegmentIndices[start + offset]];
                bounds = bounds.Union(new AxisAlignedBounds(segment.Minimum, segment.Maximum));
            }

            return bounds;
        }

        private sealed class BoundsNode
        {
            public BoundsNode(AxisAlignedBounds bounds, int start, int count)
            {
                Bounds = bounds;
                Start = start;
                Count = count;
            }

            public BoundsNode(AxisAlignedBounds bounds, BoundsNode left, BoundsNode right)
            {
                Bounds = bounds;
                Left = left;
                Right = right;
            }

            public AxisAlignedBounds Bounds { get; }
            public int Start { get; }
            public int Count { get; }
            public BoundsNode? Left { get; }
            public BoundsNode? Right { get; }
            public bool IsLeaf => Left is null;
        }
    }
}
