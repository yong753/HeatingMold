using HeatingMold.Core.Area;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Validation;

namespace HeatingMold.Design.Area;

/// <summary>실제 trimmed 표면 요소를 가장 가까운 활성 회로에 중복 없이 배정합니다.</summary>
public sealed class AppliedAreaAllocator
{
    private const string LegacyVertexAndCenterAreaRuleVersion = "surface-area-v1";
    private const string CertifiedBoundaryAreaRuleVersion = "surface-area-v3";

    /// <summary>영역·곡선 길이·표면 분할 상태를 확인해 회로별 적용면적을 계산합니다.</summary>
    public OperationResult<AppliedAreaAllocation> Allocate(
        DesignProject project,
        HeatingRegion region,
        GeometrySnapshot geometry,
        IGeometryQueries geometryQueries,
        AppliedAreaAllocationSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        OperationResult<AppliedAreaAllocationMeasurement> result = AllocateWithSurface(
            project,
            region,
            geometry,
            geometryQueries,
            settings,
            cancellationToken);
        if (!result.IsSuccess)
            return OperationResult<AppliedAreaAllocation>.Failure(result.Issues);

        return OperationResult<AppliedAreaAllocation>.Success(
            result.GetValueOrThrow().Allocation,
            result.Issues);
    }

    /// <summary>면적 배정과 최종 재세분화에 사용한 동일 표면 요소를 함께 반환합니다.</summary>
    public OperationResult<AppliedAreaAllocationMeasurement> AllocateWithSurface(
        DesignProject project,
        HeatingRegion region,
        GeometrySnapshot geometry,
        IGeometryQueries geometryQueries,
        AppliedAreaAllocationSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        AppliedAreaAllocationSettings effectiveSettings = settings ??
            AppliedAreaAllocationSettings.Initial.WithRuleVersion(project.Rules.AreaRuleVersion);

        return AllocateAtRefinement(
            project,
            region,
            geometry,
            geometryQueries,
            effectiveSettings,
            refinementIndex: 0,
            cancellationToken);
    }

    private OperationResult<AppliedAreaAllocationMeasurement> AllocateAtRefinement(
        DesignProject project,
        HeatingRegion region,
        GeometrySnapshot geometry,
        IGeometryQueries geometryQueries,
        AppliedAreaAllocationSettings settings,
        int refinementIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(geometryQueries);
        string rule = settings.RuleVersion;

        if (!project.HeatingRegions.Any(candidate => candidate.Id == region.Id))
            return Failure("RegionNotInProject", "면적 배정 대상 영역이 현재 프로젝트에 없습니다.", project, region, rule);
        if (geometry.DependencyStamp.DesignRevision != project.Revision ||
            !string.Equals(geometry.DependencyStamp.SourceModelSha256, project.SourceModel.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("StaleAreaGeometry", "면적 배정 기하 스냅샷이 현재 프로젝트 revision 또는 원본 모델과 다릅니다.", project, region, rule);
        }

        GeometryToleranceProfile tolerances = WithAreaSettings(
            project.Rules.GeometryTolerances,
            settings,
            refinementIndex);
        GeometryQueryResult<SurfaceElementSet> query = geometryQueries.GetSurfaceElements(
            region,
            tolerances,
            cancellationToken);
        if (query.Status != GeometryQueryStatus.Success || query.Value is null)
        {
            return OperationResult<AppliedAreaAllocationMeasurement>.Failure(ValidationIssueFactory.QueryFailure(
                query,
                "SurfaceElementQuery",
                rule,
                region.Id.Value));
        }

        SurfaceElementSet surface = query.Value;
        if (surface.Elements.Count > settings.MaximumElementCount)
        {
            return Failure(
                "SurfaceElementBudgetExceeded",
                "Native 표면 요소 수가 요청된 배정 작업 한도를 넘었습니다.",
                project,
                region,
                rule,
                measuredValue: surface.Elements.Count,
                requiredValue: settings.MaximumElementCount,
                unit: "elements");
        }

        HeatingPathSegment[] activeSegments = project.HeatingPaths
            .Where(path => path.GenerationSource.RegionId == region.Id)
            .SelectMany(path => path.Segments)
            .Where(segment => segment.IsActive)
            .ToArray();
        Dictionary<HeatingPathSegmentId, CircuitId[]> circuitOwners = project.Circuits
            .SelectMany(circuit => circuit.Segments.Select(reference => (reference.SegmentId, circuit.Id)))
            .GroupBy(pair => pair.SegmentId)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Id).Distinct().ToArray());
        HeatingPathSegmentId[] ownedSegmentIds = activeSegments
            .Where(segment => circuitOwners.TryGetValue(segment.Id, out CircuitId[]? owners) && owners.Length == 1)
            .Select(segment => segment.Id)
            .ToArray();
        HeatingPathSegment[] segmentsWithInvalidOwnership = project.Circuits.Count == 0
            ? []
            : activeSegments.Where(segment =>
                !circuitOwners.TryGetValue(segment.Id, out CircuitId[]? owners) || owners.Length != 1).ToArray();
        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> curves = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value);
        Dictionary<FaceRefId, FaceReference[]> faceReferencesById = project.FaceReferences
            .GroupBy(face => face.Id)
            .ToDictionary(group => group.Key, group => group.ToArray());

        HeatingPathSegmentId[] missingCurves = ownedSegmentIds.Where(id => !curves.ContainsKey(id)).ToArray();
        if (missingCurves.Length > 0)
        {
            return Failure(
                "MissingAreaPathCurve",
                "회로 면적에 포함될 활성 발열 경로 곡선 측정값이 없습니다.",
                project,
                region,
                rule,
                missingCurves.Select(id => id.Value).ToArray());
        }

        HeatingPathSegmentId[] incompleteCurves = ownedSegmentIds
            .Where(id => !curves[id].Approximation.IsComplete)
            .ToArray();
        if (incompleteCurves.Length > 0)
        {
            return Failure(
                "IncompleteAreaPathApproximation",
                "불완전한 발열 곡선 표본으로 가장 가까운 회로를 판정할 수 없습니다.",
                project,
                region,
                rule,
                incompleteCurves.Select(id => id.Value).ToArray());
        }

        var circuitAreas = project.Circuits.ToDictionary(circuit => circuit.Id, _ => 0d);
        double unassignedArea = 0;
        var issues = new List<ValidationIssue>();
        bool hasUnresolvedElement = segmentsWithInvalidOwnership.Length > 0;
        bool hasElementOnCircuitBoundary = false;
        foreach (HeatingPathSegment segment in segmentsWithInvalidOwnership)
        {
            bool hasMultipleOwners = circuitOwners.TryGetValue(segment.Id, out CircuitId[]? owners) && owners.Length > 1;
            issues.Add(ValidationIssueFactory.Error(
                hasMultipleOwners ? "HeatingSegmentHasMultipleCircuitOwners" : "HeatingSegmentHasNoCircuitOwner",
                hasMultipleOwners
                    ? "활성 발열 구간을 둘 이상의 회로가 소유해 면적 배정을 확정할 수 없습니다."
                    : "활성 발열 구간을 소유하는 회로가 없어 전체 면적 배정의 수렴을 확정할 수 없습니다.",
                rule,
                [project.Id.Value, region.Id.Value, segment.Id.Value]));
        }

        foreach (SurfaceElement element in surface.Elements)
        {
            if (!region.FaceReferences.Contains(element.FaceReferenceId))
            {
                unassignedArea += element.SurfaceAreaMm2.Value;
                hasUnresolvedElement = true;
                issues.Add(ValidationIssueFactory.Error(
                    "SurfaceElementOutsideRegionFaceSet",
                    "Native 표면 요소의 Face 참조가 발열 영역의 선택 Face 목록에 없습니다.",
                    rule,
                    [region.Id.Value, element.FaceReferenceId.Value],
                    element.ModelCentroid,
                    element.WorkingUvCentroid,
                    measuredValue: element.SurfaceAreaMm2.Value,
                    unit: "mm2"));
                continue;
            }

            if (!element.Approximation.IsComplete)
            {
                unassignedArea += element.SurfaceAreaMm2.Value;
                hasUnresolvedElement = true;
                issues.Add(AreaElementIssue(
                    "SurfaceElementApproximationIncomplete",
                    "표면 요소 표본이 불완전해 해당 면적을 회로에 배정하지 않았습니다.",
                    project,
                    region,
                    element,
                    rule));
                continue;
            }

            if (ownedSegmentIds.Length == 0)
            {
                unassignedArea += element.SurfaceAreaMm2.Value;
                continue;
            }

            bool isLegacySamplingRule = string.Equals(
                settings.RuleVersion,
                LegacyVertexAndCenterAreaRuleVersion,
                StringComparison.Ordinal);
            bool requiresWholeElementCertificate = string.Equals(
                settings.RuleVersion,
                CertifiedBoundaryAreaRuleVersion,
                StringComparison.Ordinal);
            CircuitId[] winners = GetWinningCircuits(
                element,
                ownedSegmentIds,
                circuitOwners,
                curves,
                settings.DistanceTieToleranceMm,
                includeEdgeMidpointProbes: !isLegacySamplingRule);
            if (winners.Length == 1 && requiresWholeElementCertificate)
            {
                bool isUniquePlanarFace = faceReferencesById.TryGetValue(
                        element.FaceReferenceId,
                        out FaceReference[]? matchingFaces) &&
                    matchingFaces.Length == 1 &&
                    matchingFaces[0].GeometryKind == FaceGeometryKind.Plane;
                if (!TryCertifyCircuitForWholeElement(
                        element,
                        isUniquePlanarFace,
                        ownedSegmentIds,
                        circuitOwners,
                        curves,
                        settings.DistanceTieToleranceMm,
                        out CircuitId certifiedCircuit) ||
                    certifiedCircuit != winners[0])
                {
                    winners = [];
                }
            }
            if (winners.Length != 1)
            {
                unassignedArea += element.SurfaceAreaMm2.Value;
                hasUnresolvedElement = true;
                hasElementOnCircuitBoundary = true;
                issues.Add(AreaElementIssue(
                    "SurfaceElementCrossesCircuitBoundary",
                    requiresWholeElementCertificate
                        ? "요소 표본만으로는 부족해 한 회로가 요소 전체에서 가장 가깝다는 것을 확정하지 못했습니다. 재분할이 필요합니다."
                        : "표면 요소의 꼭짓점·변 중점과 중심이 서로 다른 회로에 가장 가까워 재분할이 필요합니다.",
                    project,
                    region,
                    element,
                    rule));
                continue;
            }

            circuitAreas[winners[0]] += element.SurfaceAreaMm2.Value;
        }

        double meshDeflectionMm = settings.MeshDeflectionMm / Math.Pow(2, refinementIndex);
        double meshAngularToleranceRad = settings.MeshAngularToleranceRad / Math.Pow(2, refinementIndex);
        double maximumElementArea = surface.Elements.Count == 0
            ? Math.Max(meshDeflectionMm * meshDeflectionMm, 1e-6)
            : surface.Elements.Max(element => element.SurfaceAreaMm2.Value);
        bool areaMeasurementResolved = surface.AreaErrorBoundMm2.Value <= settings.ConservationToleranceMm2;
        bool converged = !hasUnresolvedElement && areaMeasurementResolved;
        if (!areaMeasurementResolved)
        {
            issues.Add(ValidationIssueFactory.Error(
                "SurfaceAreaMeasurementUnresolved",
                "Native 실제 표면적 오차 상한이 요청된 보존 허용오차보다 큽니다.",
                rule,
                [region.Id.Value],
                measuredValue: surface.AreaErrorBoundMm2.Value,
                requiredValue: settings.ConservationToleranceMm2,
                unit: "mm2"));
        }

        bool needsRefinement = hasElementOnCircuitBoundary || !areaMeasurementResolved;
        if (needsRefinement && refinementIndex < settings.MaximumRefinementIterations)
        {
            return AllocateAtRefinement(
                project,
                region,
                geometry,
                geometryQueries,
                settings,
                refinementIndex + 1,
                cancellationToken);
        }

        var quality = new AreaAllocationQuality(
            meshDeflectionMm,
            meshAngularToleranceRad,
            maximumElementArea,
            surface.AreaErrorBoundMm2.Value,
            settings.ConservationToleranceMm2,
            iterationCount: refinementIndex + 1,
            isConverged: converged,
            ruleVersion: rule);
        var allocation = new AppliedAreaAllocation(
            geometry.DependencyStamp,
            surface.TotalSurfaceAreaMm2,
            circuitAreas.Select(pair => new KeyValuePair<CircuitId, AreaMm2>(pair.Key, new AreaMm2(pair.Value))),
            new AreaMm2(unassignedArea),
            quality);

        // 요소 전체를 회로나 미배정에 보냈기 때문에 합계는 Native 측정 총면적을 보존해야 합니다.
        if (Math.Abs(allocation.AreaResidualMm2) > settings.ConservationToleranceMm2)
        {
            issues.Add(ValidationIssueFactory.Error(
                "AppliedAreaConservationFailure",
                "회로별 면적과 미배정 면적 합계가 Native 전체 표면적을 보존하지 않습니다.",
                rule,
                [region.Id.Value],
                measuredValue: allocation.TotalAppliedAreaMm2.Value,
                requiredValue: allocation.TotalEffectiveSurfaceAreaMm2.Value,
                unit: "mm2"));
            return OperationResult<AppliedAreaAllocationMeasurement>.Failure(issues);
        }

        ValidationIssue[] warnings = issues
            .Select(issue => new ValidationIssue(
                issue.Code,
                ValidationSeverity.Warning,
                issue.MessageKey,
                issue.EntityIds,
                issue.RuleVersion,
                issue.Location3D,
                issue.DatumUv,
                issue.MeasuredValue,
                issue.RequiredValue,
                issue.Unit,
                issue.Evidence))
            .ToArray();
        return OperationResult<AppliedAreaAllocationMeasurement>.Success(
            new AppliedAreaAllocationMeasurement(allocation, surface),
            warnings);
    }

    private static CircuitId[] GetWinningCircuits(
        SurfaceElement element,
        IReadOnlyList<HeatingPathSegmentId> segmentIds,
        IReadOnlyDictionary<HeatingPathSegmentId, CircuitId[]> circuitOwners,
        IReadOnlyDictionary<HeatingPathSegmentId, CurveGeometrySnapshot> curves,
        double tieToleranceMm,
        bool includeEdgeMidpointProbes)
    {
        IReadOnlyList<Vec3> probes = GetElementProbePoints(element, includeEdgeMidpointProbes);
        var winners = new HashSet<CircuitId>();
        foreach (Vec3 probe in probes)
        {
            (CircuitId CircuitId, double Distance)[] distancesByCircuit = segmentIds
                .SelectMany(segmentId => circuitOwners[segmentId]
                    .Select(circuitId => (CircuitId: circuitId, Distance: DistanceToCurve(probe, curves[segmentId]))))
                .GroupBy(candidate => candidate.CircuitId)
                .Select(group => (CircuitId: group.Key, Distance: group.Min(candidate => candidate.Distance)))
                .ToArray();

            if (distancesByCircuit.Length == 0)
                continue;

            double minimumDistance = distancesByCircuit.Min(candidate => candidate.Distance);
            CircuitId winner = distancesByCircuit
                .Where(candidate => candidate.Distance <= minimumDistance + tieToleranceMm)
                .OrderBy(candidate => candidate.CircuitId.Value)
                .First()
                .CircuitId;
            winners.Add(winner);
        }

        return winners.OrderBy(id => id.Value).ToArray();
    }

    /// <summary>평면 요소와 선형 회로 곡선의 최근접 회로가 요소 전체에서 유지되는지 확인합니다.</summary>
    private static bool TryCertifyCircuitForWholeElement(
        SurfaceElement element,
        bool isPlanarFace,
        IReadOnlyList<HeatingPathSegmentId> segmentIds,
        IReadOnlyDictionary<HeatingPathSegmentId, CircuitId[]> circuitOwners,
        IReadOnlyDictionary<HeatingPathSegmentId, CurveGeometrySnapshot> curves,
        double tieToleranceMm,
        out CircuitId owner)
    {
        owner = default;
        if (!isPlanarFace || element.ModelVertices.Count != 3)
            return false;

        CircuitId[] circuitIds = segmentIds
            .SelectMany(segmentId => circuitOwners[segmentId])
            .Distinct()
            .OrderBy(circuitId => circuitId.Value)
            .ToArray();
        if (circuitIds.Length == 1)
        {
            owner = circuitIds[0];
            return true;
        }
        if (circuitIds.Length < 2)
            return false;

        var distancesByCircuit = new List<(CircuitId CircuitId, double Distance)>(circuitIds.Length);
        foreach (CircuitId circuitId in circuitIds)
        {
            double nearestDistance = double.PositiveInfinity;
            foreach (HeatingPathSegmentId segmentId in segmentIds)
            {
                if (!circuitOwners[segmentId].Contains(circuitId))
                    continue;

                if (!TryMeasureExactCurveDistance(
                        element.ModelCentroid,
                        curves[segmentId],
                        out double distance))
                {
                    return false;
                }

                nearestDistance = Math.Min(nearestDistance, distance);
            }

            if (!double.IsFinite(nearestDistance))
                return false;
            distancesByCircuit.Add((circuitId, nearestDistance));
        }

        (CircuitId CircuitId, double Distance)[] orderedDistances = distancesByCircuit
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.CircuitId.Value)
            .ToArray();
        double elementRadius = element.ModelVertices
            .Select(vertex => (vertex - element.ModelCentroid).Length)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Max();
        double requiredDistanceMargin = 2 * elementRadius + tieToleranceMm + 1e-9;
        if (!double.IsFinite(elementRadius) || !double.IsFinite(requiredDistanceMargin) ||
            orderedDistances[1].Distance - orderedDistances[0].Distance <= requiredDistanceMargin)
        {
            return false;
        }

        owner = orderedDistances[0].CircuitId;
        return true;
    }

    /// <summary>지원하는 exact 곡선 정의와 점 사이의 정확한 유클리드 거리를 계산합니다.</summary>
    private static bool TryMeasureExactCurveDistance(
        Vec3 point,
        CurveGeometrySnapshot curve,
        out double distance)
    {
        switch (curve.Definition)
        {
            case LineCurveDefinition line:
                distance = DistanceToSegment(point, line.Start, line.End);
                return double.IsFinite(distance);

            case ArcCurveDefinition arc:
                return TryMeasureExactArcDistance(point, arc, out distance);

            case PolylineCurveDefinition polyline:
            {
                double minimum = double.PositiveInfinity;
                for (int index = 0; index < polyline.Points.Count - 1; index++)
                {
                    minimum = Math.Min(
                        minimum,
                        DistanceToSegment(point, polyline.Points[index], polyline.Points[index + 1]));
                }

                distance = minimum;
                return double.IsFinite(distance);
            }

            default:
                distance = 0;
                return false;
        }
    }

    /// <summary>원호의 실제 각도 구간에서 점까지의 최단 거리를 계산합니다.</summary>
    private static bool TryMeasureExactArcDistance(
        Vec3 point,
        ArcCurveDefinition arc,
        out double distance)
    {
        const double FullTurnRad = 2 * Math.PI;
        Vec3 relative = point - arc.Center;
        double planarX = Vec3.Dot(relative, arc.Plane.AxisX);
        double planarY = Vec3.Dot(relative, arc.Plane.AxisY);
        double normalDistance = Vec3.Dot(relative, arc.Plane.Normal);
        double planarRadius = Math.Sqrt(planarX * planarX + planarY * planarY);
        double sweep = arc.EndAngleRad - arc.StartAngleRad;
        if (!double.IsFinite(planarRadius) || !double.IsFinite(normalDistance) ||
            !double.IsFinite(sweep) || sweep == 0)
        {
            distance = 0;
            return false;
        }

        if (Math.Abs(sweep) >= FullTurnRad)
        {
            double radialDifference = planarRadius - arc.RadiusMm.Value;
            distance = Math.Sqrt(radialDifference * radialDifference + normalDistance * normalDistance);
            return double.IsFinite(distance);
        }

        double angle = Math.Atan2(planarY, planarX);
        double angularOffset = sweep > 0
            ? NormalizePositiveAngle(angle - arc.StartAngleRad)
            : NormalizePositiveAngle(arc.StartAngleRad - angle);
        if (angularOffset <= Math.Abs(sweep) + 1e-12)
        {
            double radialDifference = planarRadius - arc.RadiusMm.Value;
            distance = Math.Sqrt(radialDifference * radialDifference + normalDistance * normalDistance);
            return double.IsFinite(distance);
        }

        Vec3 start = GetArcPoint(arc, arc.StartAngleRad);
        Vec3 end = GetArcPoint(arc, arc.EndAngleRad);
        distance = Math.Min((point - start).Length, (point - end).Length);
        return double.IsFinite(distance);
    }

    private static double NormalizePositiveAngle(double angle)
    {
        const double FullTurnRad = 2 * Math.PI;
        double normalized = angle % FullTurnRad;
        return normalized < 0 ? normalized + FullTurnRad : normalized;
    }

    private static Vec3 GetArcPoint(ArcCurveDefinition arc, double angle) =>
        arc.Center +
        arc.Plane.AxisX * (arc.RadiusMm.Value * Math.Cos(angle)) +
        arc.Plane.AxisY * (arc.RadiusMm.Value * Math.Sin(angle));

    /// <summary>표면 요소의 꼭짓점, 선택적인 변 중점과 중심을 최근접 회로 표본으로 만듭니다.</summary>
    private static IReadOnlyList<Vec3> GetElementProbePoints(
        SurfaceElement element,
        bool includeEdgeMidpoints)
    {
        Vec3[] vertices = element.ModelVertices.ToArray();
        var probes = new List<Vec3>(vertices.Length * (includeEdgeMidpoints ? 2 : 1) + 1);
        probes.AddRange(vertices);
        if (includeEdgeMidpoints)
        {
            for (int index = 0; index < vertices.Length; index++)
            {
                Vec3 start = vertices[index];
                Vec3 end = vertices[(index + 1) % vertices.Length];
                probes.Add((start + end) * 0.5);
            }
        }
        probes.Add(element.ModelCentroid);
        return probes;
    }

    private static double DistanceToCurve(Vec3 point, CurveGeometrySnapshot curve)
    {
        double minimum = double.PositiveInfinity;
        for (int index = 0; index < curve.Samples.Count - 1; index++)
            minimum = Math.Min(minimum, DistanceToSegment(point, curve.Samples[index], curve.Samples[index + 1]));

        return minimum;
    }

    private static double DistanceToSegment(Vec3 point, Vec3 start, Vec3 end)
    {
        Vec3 direction = end - start;
        double lengthSquared = Vec3.Dot(direction, direction);
        if (lengthSquared <= 1e-24)
            return (point - start).Length;

        double parameter = Math.Clamp(Vec3.Dot(point - start, direction) / lengthSquared, 0, 1);
        return (point - (start + direction * parameter)).Length;
    }

    private static GeometryToleranceProfile WithAreaSettings(
        GeometryToleranceProfile source,
        AppliedAreaAllocationSettings settings,
        int refinementIndex) => new(
        source.ConnectionToleranceMm,
        settings.MeshDeflectionMm / Math.Pow(2, refinementIndex),
        settings.MeshAngularToleranceRad / Math.Pow(2, refinementIndex),
        source.MappingLeafLengthMm,
        source.MappingChordDeviationMm,
        source.MappingMaximumDepth,
        source.MappingMaximumProbes,
        source.MaximumContinuousSampleSpacingMm);

    private static ValidationIssue AreaElementIssue(
        string code,
        string evidence,
        DesignProject project,
        HeatingRegion region,
        SurfaceElement element,
        string ruleVersion) => ValidationIssueFactory.Error(
        code,
        evidence,
        ruleVersion,
        [project.Id.Value, region.Id.Value, element.FaceReferenceId.Value],
        element.ModelCentroid,
        element.WorkingUvCentroid,
        measuredValue: element.SurfaceAreaMm2.Value,
        unit: "mm2");

    private static OperationResult<AppliedAreaAllocationMeasurement> Failure(
        string code,
        string evidence,
        DesignProject project,
        HeatingRegion region,
        string ruleVersion,
        IEnumerable<Guid>? entityIds = null,
        double? measuredValue = null,
        double? requiredValue = null,
        string? unit = null) => OperationResult<AppliedAreaAllocationMeasurement>.Failure(
        [ValidationIssueFactory.Error(
            code,
            evidence,
            ruleVersion,
            entityIds ?? [project.Id.Value, region.Id.Value],
            measuredValue: measuredValue,
            requiredValue: requiredValue,
            unit: unit)]);
}

/// <summary>표면 메시 정밀도, 동률 정책과 보존 목표를 가진 면적 배정 설정입니다.</summary>
public sealed record AppliedAreaAllocationSettings
{
    /// <summary>Native 표면 메시의 최대 현 편차(mm)입니다.</summary>
    public double MeshDeflectionMm { get; }

    /// <summary>Native 표면 메시의 최대 각도 공차(rad)입니다.</summary>
    public double MeshAngularToleranceRad { get; }

    /// <summary>동률로 볼 회로 거리 차이(mm)입니다.</summary>
    public double DistanceTieToleranceMm { get; }

    /// <summary>전체 면적 보존을 판단하는 오차 허용값(mm²)입니다.</summary>
    public double ConservationToleranceMm2 { get; }

    /// <summary>한 번의 영역 배정에 허용하는 최대 표면 요소 수입니다.</summary>
    public int MaximumElementCount { get; }

    /// <summary>경계 요소 또는 면적 오차가 남을 때 허용하는 추가 세분화 횟수입니다.</summary>
    public int MaximumRefinementIterations { get; }

    /// <summary>면적 분할 규칙 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>검증된 표면 메시와 거리·보존 허용오차 설정입니다.</summary>
    public static AppliedAreaAllocationSettings Initial { get; } = new(0.05, 0.1, 0.01, 0.1, 1_000_000, "surface-area-v3");

    /// <summary>양의 작업 한도와 유한한 거리·면적 공차로 설정을 만듭니다.</summary>
    public AppliedAreaAllocationSettings(
        double meshDeflectionMm,
        double meshAngularToleranceRad,
        double distanceTieToleranceMm,
        double conservationToleranceMm2,
        int maximumElementCount,
        string ruleVersion,
        int maximumRefinementIterations = 4)
    {
        MeshDeflectionMm = RequirePositive(meshDeflectionMm, nameof(meshDeflectionMm));
        MeshAngularToleranceRad = RequirePositive(meshAngularToleranceRad, nameof(meshAngularToleranceRad));
        DistanceTieToleranceMm = RequirePositive(distanceTieToleranceMm, nameof(distanceTieToleranceMm));
        ConservationToleranceMm2 = RequirePositive(conservationToleranceMm2, nameof(conservationToleranceMm2));
        if (maximumElementCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumElementCount));
        if (maximumRefinementIterations < 0 || maximumRefinementIterations > 16)
            throw new ArgumentOutOfRangeException(nameof(maximumRefinementIterations));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("면적 배정 규칙 버전이 필요합니다.", nameof(ruleVersion));

        MaximumElementCount = maximumElementCount;
        MaximumRefinementIterations = maximumRefinementIterations;
        RuleVersion = ruleVersion.Trim();
    }

    /// <summary>현재 수치 설정을 유지하면서 면적 계산 규칙 버전을 바꿉니다.</summary>
    public AppliedAreaAllocationSettings WithRuleVersion(string ruleVersion) => new(
        MeshDeflectionMm,
        MeshAngularToleranceRad,
        DistanceTieToleranceMm,
        ConservationToleranceMm2,
        MaximumElementCount,
        ruleVersion,
        MaximumRefinementIterations);

    private static double RequirePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "면적 설정은 양의 유한값이어야 합니다.");

        return value;
    }
}
