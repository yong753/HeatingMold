using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>최종 OCCT 곡선 표본을 따라 원형 도체의 삽입 깊이 범위를 계산합니다.</summary>
internal static class CurveDepthEnvelopeCalculator
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const double MinimumAxisProjectionLength = 1e-12;
    private const int MaximumRefinementDepth = 18;
    private const string RuleVersion = "occt-sampled-depth-envelope-v3";

    /// <summary>선택 Face 집합의 first-hit과 접선 수직 단면을 사용해 곡선 깊이 범위를 측정합니다.</summary>
    public static GeometryQueryResult<DepthMeasurement> Measure(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        IReadOnlyList<FaceRefId> selectedSurfaceFaces,
        GeometryToleranceProfile tolerances,
        Func<WorkingCoordinateSystem, Vec2, IReadOnlyList<FaceRefId>, CancellationToken,
            GeometryQueryResult<SurfaceHit>> queryFirstHit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(selectedSurfaceFaces);
        ArgumentNullException.ThrowIfNull(tolerances);
        ArgumentNullException.ThrowIfNull(queryFirstHit);

        FaceRefId[] selectedFaces = selectedSurfaceFaces.ToArray();
        if (wireDiameterMm.Value <= 0 || requiredInsertionDepthMm.Value < 0)
            return Unresolved("InvalidDepthInput", "도체 직경은 양수이고 요구 삽입 깊이는 0 이상이어야 합니다.");
        if (selectedFaces.Length == 0 || selectedFaces.Any(face => face.IsEmpty) ||
            selectedFaces.Distinct().Count() != selectedFaces.Length ||
            !selectedFaces.Contains(anchor.FaceReferenceId))
        {
            return Unresolved(
                "InvalidDepthSurfaceFaces",
                "깊이 측정 Face 집합은 중복 없이 유효해야 하며 앵커 Face를 포함해야 합니다.",
                EntityId(curve));
        }
        if (!curve.Approximation.IsComplete)
            return Unresolved("IncompleteDepthCurveApproximation", "최종 곡선 표본의 근사가 완료되지 않았습니다.", EntityId(curve));
        if (!double.IsFinite(curve.Approximation.MaximumSampleSpacingMm) ||
            curve.Approximation.MaximumSampleSpacingMm <= 0 ||
            !double.IsFinite(curve.Approximation.RequestedAngularToleranceRad) ||
            curve.Approximation.RequestedAngularToleranceRad <= 0)
        {
            return Unresolved("MissingDepthApproximationEvidence", "깊이 측정에 필요한 곡선 간격·각도 근거가 없습니다.", EntityId(curve));
        }
        if (cancellationToken.IsCancellationRequested)
            return Unresolved("OperationCancelled", "삽입 깊이 envelope 질의가 취소되었습니다.", EntityId(curve));

        GeometryQueryResult<SurfaceHit> anchorResult = queryFirstHit(
            coordinateSystem,
            anchor.WorkingUv,
            selectedFaces,
            cancellationToken);
        if (!anchorResult.HasValue)
            return CopyFailure(anchorResult);
        if (anchorResult.Value!.FaceReferenceId != anchor.FaceReferenceId)
        {
            return Unresolved(
                "DepthAnchorFaceMismatch",
                "앵커 first-hit 면이 저장된 안정 Face 참조와 다릅니다.",
                EntityId(curve));
        }
        if ((anchorResult.Value.ModelPoint - anchor.ModelPoint).Length > tolerances.ConnectionToleranceMm)
        {
            return Unresolved(
                "DepthAnchorChanged",
                "현재 first-hit 표면점이 저장된 깊이 앵커와 허용오차 밖으로 달라졌습니다.",
                EntityId(curve));
        }

        if (!TryBuildStations(curve, tolerances, out List<Vec3> stations, out string stationFailure))
            return Unresolved("DepthEnvelopeBudgetExceeded", stationFailure, EntityId(curve));
        if (HasTangentDiscontinuity(stations, curve.Approximation.RequestedAngularToleranceRad))
        {
            return Unresolved(
                "DepthTangentDiscontinuity",
                "곡선 표본에서 원형 단면 방향을 연속적으로 정할 수 없는 접선 꺾임이 발견되었습니다.",
                EntityId(curve));
        }

        var probeContext = new DepthProbeContext(
            wireDiameterMm,
            coordinateSystem,
            selectedFaces,
            tolerances,
            queryFirstHit,
            cancellationToken,
            curve);
        var initialMeasurements = new List<StationMeasurement>(stations.Count);
        for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Unresolved("OperationCancelled", "삽입 깊이 envelope 질의가 취소되었습니다.", EntityId(curve));

            Vec3 tangentVector = GetTangent(stations, stationIndex);
            if (!probeContext.TryMeasureStation(
                stations[stationIndex],
                tangentVector,
                out StationMeasurement stationMeasurement,
                out GeometryQueryResult<DepthMeasurement>? initialStationFailure))
            {
                return initialStationFailure!;
            }

            initialMeasurements.Add(stationMeasurement);
        }

        if (!TryRefineMeasurements(
            initialMeasurements,
            probeContext,
            out List<StationMeasurement> measurements,
            out double interpolationResidualMm,
            out GeometryQueryResult<DepthMeasurement>? refinementFailure))
        {
            return refinementFailure!;
        }

        if (measurements.Count < 2)
            return Unresolved("IncompleteDepthEnvelope", "곡선 깊이 범위를 판단할 표본이 두 개보다 적습니다.", EntityId(curve));

        double minimumDepth = double.PositiveInfinity;
        double maximumDepth = double.NegativeInfinity;
        int minimumIndex = -1;
        int maximumIndex = -1;
        for (int index = 0; index < measurements.Count; index++)
        {
            double depth = measurements[index].DepthMm;
            if (depth < minimumDepth)
            {
                minimumDepth = depth;
                minimumIndex = index;
            }
            if (depth > maximumDepth)
            {
                maximumDepth = depth;
                maximumIndex = index;
            }
        }

        double radiusMm = wireDiameterMm.Value / 2;
        double minimumAxisSupport = measurements.Min(measurement => measurement.AxisSupport);
        if (!TryEstimateAxisRayAngularError(
                radiusMm,
                minimumAxisSupport,
                curve.Approximation.RequestedAngularToleranceRad,
                out double tangentSupportErrorMm))
        {
            return Unresolved(
                "DepthAngularToleranceUnbounded",
                "접선 각도 오차 범위에 작업축 평행 방향이 포함되어 깊이 오차를 제한할 수 없습니다.",
                EntityId(curve));
        }
        double numericErrorMm = EstimateNumericError(
            measurements[minimumIndex].SurfacePoint,
            measurements[maximumIndex].WireOutsidePoint,
            radiusMm,
            maximumDepth);
        double errorBoundMm = Math.Max(
            tolerances.ConnectionToleranceMm,
            curve.Approximation.MaximumMeasuredDeviationMm + tangentSupportErrorMm +
            interpolationResidualMm * 2 + numericErrorMm);

        double representativeDepthMm = StableMidpoint(minimumDepth, maximumDepth);
        StationMeasurement minimum = measurements[minimumIndex];
        StationMeasurement maximum = measurements[maximumIndex];
        bool minimumIsWorst = Math.Abs(minimumDepth - requiredInsertionDepthMm.Value) >=
                              Math.Abs(maximumDepth - requiredInsertionDepthMm.Value);
        StationMeasurement worst = minimumIsWorst ? minimum : maximum;
        var measurement = new DepthMeasurement(
            requiredInsertionDepthMm,
            new LengthMm(representativeDepthMm),
            new LengthMm(minimumDepth),
            new LengthMm(maximumDepth),
            new LengthMm(errorBoundMm),
            worst.SurfacePoint,
            minimum.SurfacePoint,
            maximum.SurfacePoint,
            minimum.WireOutsidePoint,
            maximum.WireOutsidePoint,
            measurements.Count,
            isResolved: true,
            isComplete: true,
            RuleVersion);
        return GeometryQueryResult<DepthMeasurement>.Succeeded(measurement);
    }

    /// <summary>깊이 곡선의 중간점을 재질의해 표본 사이 극값을 적응적으로 찾습니다.</summary>
    private static bool TryRefineMeasurements(
        IReadOnlyList<StationMeasurement> initialMeasurements,
        DepthProbeContext probeContext,
        out List<StationMeasurement> refinedMeasurements,
        out double maximumResidualMm,
        out GeometryQueryResult<DepthMeasurement>? failure)
    {
        refinedMeasurements = [];
        maximumResidualMm = 0;
        failure = null;
        if (initialMeasurements.Count < 2)
        {
            failure = Unresolved(
                "IncompleteDepthEnvelope",
                "깊이 정밀화에 필요한 표본이 두 개보다 적습니다.",
                EntityId(probeContext.Curve));
            return false;
        }

        int probeCount = initialMeasurements.Count + 1; // 앵커 first-hit 한 번을 포함합니다.
        if (probeCount > probeContext.MaximumProbeCount)
        {
            failure = Unresolved(
                "DepthEnvelopeBudgetExceeded",
                "앵커 확인을 포함한 깊이 질의가 설정된 probe 한도를 넘습니다.",
                EntityId(probeContext.Curve));
            return false;
        }

        refinedMeasurements.Add(initialMeasurements[0]);
        for (int index = 0; index < initialMeasurements.Count - 1; index++)
        {
            if (!TryRefineInterval(
                initialMeasurements[index],
                initialMeasurements[index + 1],
                0,
                ref probeCount,
                refinedMeasurements,
                ref maximumResidualMm,
                probeContext,
                out failure))
            {
                refinedMeasurements.Clear();
                return false;
            }
        }

        return true;
    }

    /// <summary>중점 깊이의 선형 잔차가 허용치를 넘는 구간을 두 부분으로 나눕니다.</summary>
    private static bool TryRefineInterval(
        StationMeasurement start,
        StationMeasurement end,
        int refinementDepth,
        ref int probeCount,
        List<StationMeasurement> refinedMeasurements,
        ref double maximumResidualMm,
        DepthProbeContext probeContext,
        out GeometryQueryResult<DepthMeasurement>? failure)
    {
        failure = null;
        if (probeContext.CancellationToken.IsCancellationRequested)
        {
            failure = Unresolved(
                "OperationCancelled",
                "삽입 깊이 envelope 정밀화가 취소되었습니다.",
                EntityId(probeContext.Curve));
            return false;
        }
        if (probeCount >= probeContext.MaximumProbeCount)
        {
            failure = Unresolved(
                "DepthEnvelopeRefinementBudgetExceeded",
                "깊이 곡선의 중간 극값을 정밀화하는 동안 probe 한도를 넘었습니다.",
                EntityId(probeContext.Curve));
            return false;
        }

        Vec3 midpoint = start.CenterlinePoint + (end.CenterlinePoint - start.CenterlinePoint) * 0.5;
        Vec3 intervalTangent = end.CenterlinePoint - start.CenterlinePoint;
        if (midpoint == start.CenterlinePoint || midpoint == end.CenterlinePoint || intervalTangent.Length <= 0)
        {
            failure = Unresolved(
                "DepthEnvelopeRefinementPrecisionLimit",
                "깊이 곡선의 중간 위치를 부동소수점 정밀도로 표현할 수 없습니다.",
                EntityId(probeContext.Curve));
            return false;
        }

        if (!probeContext.TryMeasureStation(
            midpoint,
            intervalTangent,
            out StationMeasurement midpointMeasurement,
            out failure))
        {
            return false;
        }
        probeCount++;

        double linearDepth = StableMidpoint(start.DepthMm, end.DepthMm);
        double residualMm = Math.Abs(midpointMeasurement.DepthMm - linearDepth);
        if (!double.IsFinite(residualMm))
        {
            failure = Unresolved(
                "DepthEnvelopeRefinementUnresolved",
                "깊이 곡선의 중점 보간 잔차가 유한한 값이 아닙니다.",
                EntityId(probeContext.Curve));
            return false;
        }

        if (residualMm > probeContext.ResidualToleranceMm)
        {
            if (refinementDepth >= MaximumRefinementDepth)
            {
                failure = Unresolved(
                    "DepthEnvelopeRefinementLimitExceeded",
                    "중점 깊이 잔차가 허용치보다 커서 정밀화 한도 안에 극값을 좁히지 못했습니다.",
                    EntityId(probeContext.Curve));
                return false;
            }

            return TryRefineInterval(
                    start,
                    midpointMeasurement,
                    refinementDepth + 1,
                    ref probeCount,
                    refinedMeasurements,
                    ref maximumResidualMm,
                    probeContext,
                    out failure) &&
                TryRefineInterval(
                    midpointMeasurement,
                    end,
                    refinementDepth + 1,
                    ref probeCount,
                    refinedMeasurements,
                    ref maximumResidualMm,
                    probeContext,
                    out failure);
        }

        maximumResidualMm = Math.Max(maximumResidualMm, residualMm);
        refinedMeasurements.Add(midpointMeasurement);
        refinedMeasurements.Add(end);
        return true;
    }

    private static bool TryBuildStations(
        CurveGeometrySnapshot curve,
        GeometryToleranceProfile tolerances,
        out List<Vec3> stations,
        out string failure)
    {
        stations = [];
        failure = string.Empty;
        IReadOnlyList<Vec3> samples = curve.Samples;
        if (samples.Count < 2)
        {
            failure = "최종 곡선 표본이 두 개보다 적습니다.";
            return false;
        }

        double maximumSpacingMm = Math.Min(
            curve.Approximation.MaximumSampleSpacingMm,
            tolerances.MaximumContinuousSampleSpacingMm);
        double stationSpacingMm = maximumSpacingMm / 2;
        if (!double.IsFinite(stationSpacingMm) || stationSpacingMm <= 0)
        {
            failure = "깊이 측정 표본 간격이 유효하지 않습니다.";
            return false;
        }

        stations.Add(samples[0]);
        for (int sampleIndex = 0; sampleIndex < samples.Count - 1; sampleIndex++)
        {
            Vec3 start = samples[sampleIndex];
            Vec3 end = samples[sampleIndex + 1];
            double segmentLengthMm = (end - start).Length;
            if (!double.IsFinite(segmentLengthMm) || segmentLengthMm <= 0)
            {
                failure = "최종 곡선 표본에 길이가 0이거나 유효하지 않은 구간이 있습니다.";
                stations.Clear();
                return false;
            }

            double divisionsDouble = Math.Ceiling(segmentLengthMm / stationSpacingMm);
            if (!double.IsFinite(divisionsDouble) || divisionsDouble < 1 ||
                divisionsDouble > tolerances.MappingMaximumProbes ||
                stations.Count + divisionsDouble > tolerances.MappingMaximumProbes)
            {
                failure = "깊이 envelope 표본 생성이 설정된 질의 한도를 넘습니다.";
                stations.Clear();
                return false;
            }

            int divisions = checked((int)divisionsDouble);
            for (int division = 1; division <= divisions; division++)
            {
                double parameter = (double)division / divisions;
                stations.Add(start + (end - start) * parameter);
            }
        }

        if (stations.Count + 1 > tolerances.MappingMaximumProbes)
        {
            failure = "앵커 확인을 포함한 깊이 질의가 설정된 질의 한도를 넘습니다.";
            stations.Clear();
            return false;
        }

        return true;
    }

    private static Vec3 GetTangent(IReadOnlyList<Vec3> stations, int index)
    {
        if (index == 0)
            return stations[1] - stations[0];
        if (index == stations.Count - 1)
            return stations[^1] - stations[^2];

        return stations[index + 1] - stations[index - 1];
    }

    private static bool HasTangentDiscontinuity(IReadOnlyList<Vec3> stations, double angularToleranceRad)
    {
        double maximumTurnRad = angularToleranceRad + 1e-6;
        for (int index = 1; index < stations.Count - 1; index++)
        {
            Vec3 incoming = stations[index] - stations[index - 1];
            Vec3 outgoing = stations[index + 1] - stations[index];
            if (incoming.Length <= 0 || outgoing.Length <= 0)
                return true;

            double cosine = Math.Clamp(
                Vec3.Dot(incoming.Normalized(), outgoing.Normalized()),
                -1,
                1);
            if (Math.Acos(cosine) > maximumTurnRad)
                return true;
        }

        return false;
    }

    private static double EstimateNumericError(
        Vec3 surfacePoint,
        Vec3 wireOutsidePoint,
        double radiusMm,
        double measuredDepthMm)
    {
        double coordinateScale = Math.Max(
            Math.Max(Math.Abs(surfacePoint.X), Math.Abs(wireOutsidePoint.X)),
            Math.Max(
                Math.Max(Math.Abs(surfacePoint.Y), Math.Abs(wireOutsidePoint.Y)),
                Math.Max(Math.Abs(surfacePoint.Z), Math.Abs(wireOutsidePoint.Z))));
        double roundingUnit = MachineEpsilon * 256;
        return coordinateScale * roundingUnit +
               radiusMm * roundingUnit +
               measuredDepthMm * roundingUnit +
               roundingUnit;
    }

    /// <summary>두 깊이의 중점을 합산 overflow 없이 계산합니다.</summary>
    private static double StableMidpoint(double first, double second)
    {
        if (Math.Sign(first) != Math.Sign(second))
            return (first + second) / 2;

        return first + (second - first) / 2;
    }

    /// <summary>접선 각도 공차가 깊이 축방향 ray 교점에 만드는 최악의 오차를 계산합니다.</summary>
    private static bool TryEstimateAxisRayAngularError(
        double radiusMm,
        double axisSupport,
        double angularToleranceRad,
        out double errorMm)
    {
        errorMm = 0;
        if (!double.IsFinite(radiusMm) || radiusMm <= 0 ||
            !double.IsFinite(axisSupport) || axisSupport <= 0 ||
            !double.IsFinite(angularToleranceRad) || angularToleranceRad <= 0)
        {
            return false;
        }

        double tangentAngleRad = Math.Asin(Math.Clamp(axisSupport, 0, 1));
        double minimumTangentAngleRad = tangentAngleRad - angularToleranceRad;
        if (minimumTangentAngleRad <= 0)
            return false;

        double minimumAxisSupport = Math.Sin(minimumTangentAngleRad);
        errorMm = radiusMm * (1 / minimumAxisSupport - 1 / axisSupport);
        return double.IsFinite(errorMm) && errorMm >= 0;
    }

    private static bool IsFinite(Vec3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static Guid[] EntityId(CurveGeometrySnapshot curve) =>
        [curve.HeatingPathSegmentId?.Value ?? curve.ConnectionPathSegmentId!.Value.Value];

    private static GeometryQueryResult<DepthMeasurement> CopyFailure(GeometryQueryResult<SurfaceHit> source) =>
        new(source.Status, default, source.Issues);

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

    private readonly record struct StationMeasurement(
        Vec3 CenterlinePoint,
        double DepthMm,
        Vec3 SurfacePoint,
        Vec3 WireOutsidePoint,
        double AxisSupport);

    /// <summary>깊이 측정에 필요한 곡선·좌표계·표면 질의와 작업 한도를 묶습니다.</summary>
    private sealed class DepthProbeContext
    {
        private readonly LengthMm _wireDiameterMm;
        private readonly WorkingCoordinateSystem _coordinateSystem;
        private readonly IReadOnlyList<FaceRefId> _selectedFaces;
        private readonly Func<WorkingCoordinateSystem, Vec2, IReadOnlyList<FaceRefId>, CancellationToken,
            GeometryQueryResult<SurfaceHit>> _queryFirstHit;

        /// <summary>깊이 측정 대상 곡선입니다.</summary>
        public CurveGeometrySnapshot Curve { get; }

        /// <summary>앵커 확인을 포함해 허용하는 총 표면 질의 수입니다.</summary>
        public int MaximumProbeCount { get; }

        /// <summary>중점 보간 정밀화에서 허용하는 깊이 잔차(mm)입니다.</summary>
        public double ResidualToleranceMm { get; }

        /// <summary>질의 및 적응 정밀화의 취소 상태입니다.</summary>
        public CancellationToken CancellationToken { get; }

        /// <summary>한 깊이 측정 요청의 변경 불가한 query 문맥을 보관합니다.</summary>
        public DepthProbeContext(
            LengthMm wireDiameterMm,
            WorkingCoordinateSystem coordinateSystem,
            IReadOnlyList<FaceRefId> selectedFaces,
            GeometryToleranceProfile tolerances,
            Func<WorkingCoordinateSystem, Vec2, IReadOnlyList<FaceRefId>, CancellationToken,
                GeometryQueryResult<SurfaceHit>> queryFirstHit,
            CancellationToken cancellationToken,
            CurveGeometrySnapshot curve)
        {
            _wireDiameterMm = wireDiameterMm;
            _coordinateSystem = coordinateSystem;
            _selectedFaces = selectedFaces;
            _queryFirstHit = queryFirstHit;
            CancellationToken = cancellationToken;
            Curve = curve;
            MaximumProbeCount = tolerances.MappingMaximumProbes;
            ResidualToleranceMm = tolerances.ConnectionToleranceMm / 4;
        }

        /// <summary>한 위치에서 작업축 광선과 원형 단면의 교점 깊이를 측정합니다.</summary>
        public bool TryMeasureStation(
            Vec3 centerlinePoint,
            Vec3 tangentVector,
            out StationMeasurement measurement,
            out GeometryQueryResult<DepthMeasurement>? failure)
        {
            measurement = default;
            if (!IsFinite(tangentVector) || tangentVector.Length <= 0)
            {
                failure = Unresolved(
                    "InvalidDepthTangent",
                    "최종 곡선 표본의 진행 접선을 계산할 수 없습니다.",
                    EntityId(Curve));
                return false;
            }

            Vec3 axis = _coordinateSystem.Projection.Normal;
            Vec3 tangent = tangentVector.Normalized();
            double tangentAlongAxis = Math.Clamp(Vec3.Dot(tangent, axis), -1, 1);
            double axisSupport = (axis - tangent * tangentAlongAxis).Length;
            if (axisSupport <= MinimumAxisProjectionLength)
            {
                failure = Unresolved(
                    "DepthAxisParallelToCurve",
                    "작업축과 도체 접선이 평행해 작업축 광선과 원형 단면의 교점을 정할 수 없습니다.",
                    EntityId(Curve));
                return false;
            }

            double radiusMm = _wireDiameterMm.Value / 2;
            Vec3 wireOutsidePoint = centerlinePoint + axis * (radiusMm / axisSupport);
            Vec2 workingUv = _coordinateSystem.Projection.Project(centerlinePoint);
            GeometryQueryResult<SurfaceHit> surfaceResult = _queryFirstHit(
                _coordinateSystem,
                workingUv,
                _selectedFaces,
                CancellationToken);
            if (!surfaceResult.HasValue)
            {
                failure = CopyFailure(surfaceResult);
                return false;
            }

            Vec3 surfacePoint = surfaceResult.Value!.ModelPoint;
            double measuredDepthMm = Vec3.Dot(surfacePoint - wireOutsidePoint, axis);
            if (!double.IsFinite(measuredDepthMm) || measuredDepthMm < 0)
            {
                failure = Unresolved(
                    "WireOutsideSurface",
                    "도체 바깥면이 first-hit 표면보다 바깥에 있거나 깊이가 유효하지 않습니다.",
                    EntityId(Curve));
                return false;
            }

            measurement = new StationMeasurement(
                centerlinePoint,
                measuredDepthMm,
                surfacePoint,
                wireOutsidePoint,
                axisSupport);
            failure = null;
            return true;
        }
    }
}
