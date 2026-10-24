using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;
using CoreConnectionPath = HeatingMold.Core.Circuits.ConnectionPath;

namespace HeatingMold.Design.Validation;

/// <summary>발열 경로의 유효 영역·경계 여유와 연결 경로의 금형 이탈을 검사합니다.</summary>
public sealed class BoundaryValidator
{
    private readonly ExitSideResolver _exitSideResolver;

    /// <summary>경계 앵커가 허용된 출구 측면에 놓이는지 확인하는 resolver를 연결합니다.</summary>
    public BoundaryValidator(ExitSideResolver? exitSideResolver = null)
    {
        _exitSideResolver = exitSideResolver ?? new ExitSideResolver();
    }

    /// <summary>확정 곡선 표본을 영역 경계와 대조하고 연결 경로의 Native 충돌 질의를 수행합니다.</summary>
    public ValidationReport Validate(
        DesignProject project,
        GeometrySnapshot geometry,
        WorkingCoordinateSystem coordinateSystem,
        IGeometryQueries geometryQueries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(geometryQueries);

        string rule = project.Rules.ValidationRuleVersion;
        double connectionTolerance = project.Rules.GeometryTolerances.ConnectionToleranceMm;
        var issues = new List<ValidationIssue>();
        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> heatingCurves = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value);
        Dictionary<ConnectionPathSegmentId, CurveGeometrySnapshot> connectionCurves = geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .ToDictionary(curve => curve.ConnectionPathSegmentId!.Value);
        Dictionary<RegionId, HeatingRegion> regions = project.HeatingRegions.ToDictionary(region => region.Id);
        PlacementConstraints? placement = project.PlacementConstraints;
        if (project.WireSpecification is null)
        {
            issues.Add(ValidationIssueFactory.Error(
                "MissingWireSpecification",
                "발열 경계 여유를 계산할 도체 지름이 없습니다.",
                rule,
                [project.Id.Value]));
            return new ValidationReport(issues);
        }

        foreach (CoreHeatingPath path in project.HeatingPaths)
        {
            if (!regions.TryGetValue(path.GenerationSource.RegionId, out HeatingRegion? region))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingPathHeatingRegion",
                    "발열 경로가 참조하는 유효 영역이 프로젝트에 없습니다.",
                    rule,
                    [path.Id.Value, path.GenerationSource.RegionId.Value]));
                continue;
            }

            HeatingPathSegment[] activeSegments = path.Segments
                .Where(segment => segment.IsActive)
                .OrderBy(segment => segment.Sequence)
                .ToArray();
            var pathSamples = new List<PathSample>();
            foreach (HeatingPathSegment segment in activeSegments)
            {
                if (!heatingCurves.TryGetValue(segment.Id, out CurveGeometrySnapshot? curve))
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "MissingBoundaryCurveMeasurement",
                        "영역 경계를 검사할 활성 발열 곡선 측정값이 없습니다.",
                        rule,
                        [path.Id.Value, segment.Id.Value]));
                    continue;
                }

                if (!curve.Approximation.IsComplete ||
                    curve.Approximation.MaximumSampleSpacingMm > project.Rules.GeometryTolerances.MaximumContinuousSampleSpacingMm)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "BoundaryCurveApproximationIncomplete",
                        "곡선 표본의 완전성 또는 최대 간격이 경계 판정 한도를 만족하지 않습니다.",
                        rule,
                        [path.Id.Value, segment.Id.Value],
                        measuredValue: curve.Approximation.MaximumSampleSpacingMm,
                        requiredValue: project.Rules.GeometryTolerances.MaximumContinuousSampleSpacingMm,
                        unit: "mm"));
                    continue;
                }

                Vec2[] projected = curve.Samples.Select(coordinateSystem.Projection.Project).ToArray();
                for (int index = 0; index < projected.Length; index++)
                {
                    if (pathSamples.Count > 0 && index == 0 &&
                        pathSamples[^1].WorkingUv.DistanceTo(projected[index]) <= connectionTolerance)
                    {
                        continue;
                    }

                    pathSamples.Add(new PathSample(
                        projected[index],
                        curve.Approximation.MaximumMeasuredDeviationMm));
                }
            }

            if (pathSamples.Count < 2)
                continue;

            if (HasBoundaryCrossing(region, pathSamples))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "HeatingPathCrossesRegionBoundary",
                    "연속 곡선 표본 사이의 경로가 외곽·홀·제외 경계와 교차합니다.",
                    rule,
                    [path.Id.Value, region.Id.Value]));
            }

            bool startAnchorValid = placement is not null &&
                _exitSideResolver.Resolve(region, placement.StartAnchor.WorkingUv, connectionTolerance, rule).IsSuccess &&
                pathSamples[0].WorkingUv.DistanceTo(placement.StartAnchor.WorkingUv) <= connectionTolerance;
            bool endAnchorValid = placement is not null &&
                _exitSideResolver.Resolve(region, placement.EndAnchor.WorkingUv, connectionTolerance, rule).IsSuccess &&
                pathSamples[^1].WorkingUv.DistanceTo(placement.EndAnchor.WorkingUv) <= connectionTolerance;
            if (placement is null)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingPlacementConstraints",
                    "경계 검증에 필요한 생성 앵커와 여유 조건이 없습니다.",
                    rule,
                    [project.Id.Value]));
                continue;
            }

            if (!startAnchorValid || !endAnchorValid)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "HeatingPathAnchorMismatch",
                    "최종 발열 경로의 양 끝점이 지정된 출구 측면 앵커와 일치하지 않습니다.",
                    rule,
                    [path.Id.Value, region.Id.Value],
                    datumUv: pathSamples[0].WorkingUv));
                continue;
            }

            List<PathSample> checkedSamples = AddChordMidpoints(pathSamples);
            double requiredMargin = Math.Max(
                placement.BoundaryMarginMm.Value,
                project.WireSpecification.DiameterMm.Value / 2);
            int firstInsideInset = checkedSamples.FindIndex(sample =>
                DistanceToBoundary(region, sample.WorkingUv) - sample.ErrorBoundMm >= requiredMargin);
            int lastInsideInset = checkedSamples.FindLastIndex(sample =>
                DistanceToBoundary(region, sample.WorkingUv) - sample.ErrorBoundMm >= requiredMargin);
            if (firstInsideInset < 0 || firstInsideInset >= lastInsideInset)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "PathDoesNotReachInsetRegion",
                    "출구 측면 진입 구간을 지난 뒤 경계 여유를 확실히 만족하는 발열 경로가 없습니다.",
                    rule,
                    [path.Id.Value, region.Id.Value],
                    datumUv: checkedSamples[checkedSamples.Count / 2].WorkingUv,
                    requiredValue: requiredMargin,
                    unit: "mm"));
                continue;
            }

            for (int index = 0; index < checkedSamples.Count; index++)
            {
                PathSample sample = checkedSamples[index];
                if (!IsInsideEffectiveRegion(region, sample.WorkingUv))
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "HeatingPathOutsideEffectiveRegion",
                        "발열 경로 표본이 선택·포함·제외 연산으로 결정된 유효 영역 밖에 있습니다.",
                        rule,
                        [path.Id.Value, region.Id.Value],
                        datumUv: sample.WorkingUv));
                    continue;
                }

                double boundaryDistance = DistanceToBoundary(region, sample.WorkingUv);
                double lowerBound = boundaryDistance - sample.ErrorBoundMm;
                double upperBound = boundaryDistance + sample.ErrorBoundMm;
                bool isEndpointLeadIn = index < firstInsideInset && startAnchorValid;
                bool isEndpointLeadOut = index > lastInsideInset && endAnchorValid;
                if (isEndpointLeadIn || isEndpointLeadOut)
                    continue;

                if (upperBound < requiredMargin)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "HeatingPathBoundaryMarginViolation",
                        "발열 경로 중심선이 요구 경계 여유보다 경계에 가깝습니다.",
                        rule,
                        [path.Id.Value, region.Id.Value],
                        datumUv: sample.WorkingUv,
                        measuredValue: boundaryDistance,
                        requiredValue: requiredMargin,
                        unit: "mm"));
                }
                else if (lowerBound < requiredMargin)
                {
                    issues.Add(ValidationIssueFactory.Error(
                        "HeatingPathBoundaryMarginUnresolved",
                        "경계 거리의 근사 오차 범위가 요구 여유를 가로질러 확정할 수 없습니다.",
                        rule,
                        [path.Id.Value, region.Id.Value],
                        datumUv: sample.WorkingUv,
                        measuredValue: boundaryDistance,
                        requiredValue: requiredMargin,
                        unit: "mm"));
                }
            }
        }

        foreach (CoreConnectionPath path in project.ConnectionPaths)
        {
            if (path.Segments.Any(segment => !connectionCurves.ContainsKey(segment.Id)))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingConnectionPathMeasurement",
                    "확정 연결 경로의 Native 곡선 측정값이 없습니다.",
                    rule,
                    [path.Id.Value, path.ConnectionPointId.Value]));
                continue;
            }

            ConnectionPoint? connectionPoint = project.ConnectionPoints
                .FirstOrDefault(point => point.Id == path.ConnectionPointId);
            Circuit? circuit = connectionPoint is null
                ? null
                : project.Circuits.FirstOrDefault(candidate => candidate.Id == connectionPoint.CircuitId);
            Dictionary<HeatingPathSegmentId, CoreHeatingPath> pathsBySegment = project.HeatingPaths
                .SelectMany(heatingPath => heatingPath.Segments.Select(segment => (SegmentId: segment.Id, Path: heatingPath)))
                .ToDictionary(item => item.SegmentId, item => item.Path);
            RegionId[] regionIds = circuit?.Segments
                .Where(reference => pathsBySegment.ContainsKey(reference.SegmentId))
                .Select(reference => pathsBySegment[reference.SegmentId].GenerationSource.RegionId)
                .Distinct()
                .ToArray() ?? [];
            HeatingRegion? region = regionIds.Length == 1
                ? project.HeatingRegions.FirstOrDefault(candidate => candidate.Id == regionIds[0])
                : null;
            if (regionIds.Length > 1)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "ConnectionPathSpansMultipleRegions",
                    "하나의 연결 경로가 서로 다른 발열 영역의 경로 구간을 참조합니다.",
                    rule,
                    [path.Id.Value, path.ConnectionPointId.Value]));
            }
            else if (region is null)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingConnectionHeatingRegion",
                    "연결 경로의 발열 영역을 확인할 회로와 영역 참조가 없습니다.",
                    rule,
                    [path.Id.Value, path.ConnectionPointId.Value]));
            }
            if (region is not null)
            {
                foreach (ConnectionPathSegment segment in path.Segments.Where(segment => segment.GeometryDomain == GeometryDomain.OnMold))
                {
                    if (!connectionCurves.TryGetValue(segment.Id, out CurveGeometrySnapshot? curve))
                        continue;

                    foreach (Vec3 sample in curve.Samples)
                    {
                        Vec2 workingUv = coordinateSystem.Projection.Project(sample);
                        if (!IsInsideEffectiveRegion(region, workingUv))
                        {
                            issues.Add(ValidationIssueFactory.Error(
                                "OnMoldConnectionOutsideRegion",
                                "금형 위 연결 구간이 유효 영역 바깥으로 벗어납니다.",
                                rule,
                                [path.Id.Value, segment.Id.Value, region.Id.Value],
                                sample,
                                workingUv));
                            break;
                        }
                    }
                }
            }

            GeometryQueryResult<ConnectionObstacleCheck> query = geometryQueries.CheckConnectionPathObstacles(
                path,
                coordinateSystem,
                cancellationToken);
            if (query.Status != GeometryQueryStatus.Success || query.Value is null)
            {
                issues.AddRange(ValidationIssueFactory.QueryFailure(
                    query,
                    "ConnectionObstacleQuery",
                    rule,
                    path.Id.Value));
                continue;
            }

            ConnectionObstacleCheck check = query.Value;
            if (!check.IsClear)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "ConnectionPathObstruction",
                    "연결 경로가 금형과 충돌하거나 외부 연결 좌표까지 안전하게 이어지지 않습니다.",
                    rule,
                    [path.Id.Value, path.ConnectionPointId.Value],
                    check.FirstObstruction,
                    measuredValue: check.DistanceToObstruction?.Value,
                    unit: "mm"));
            }
            if (!check.MoldExitTransitionFound)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingMoldExitTransition",
                    "Native가 금형 표면에서 외부로 나가는 명시 전이를 확인하지 못했습니다.",
                    rule,
                    [path.Id.Value, path.ConnectionPointId.Value]));
            }
        }

        return new ValidationReport(issues);
    }

    private static List<PathSample> AddChordMidpoints(IReadOnlyList<PathSample> samples)
    {
        var expanded = new List<PathSample>(samples.Count * 2);
        for (int index = 0; index < samples.Count - 1; index++)
        {
            PathSample first = samples[index];
            PathSample second = samples[index + 1];
            expanded.Add(first);
            expanded.Add(new PathSample(
                new Vec2((first.WorkingUv.X + second.WorkingUv.X) / 2, (first.WorkingUv.Y + second.WorkingUv.Y) / 2),
                Math.Max(first.ErrorBoundMm, second.ErrorBoundMm)));
        }

        expanded.Add(samples[^1]);
        return expanded;
    }

    private static bool IsInsideEffectiveRegion(HeatingRegion region, Vec2 point)
    {
        const double tolerance = 1e-8;
        foreach (BoundaryLoop loop in region.EffectiveBoundaryLoops)
        {
            for (int index = 0; index < loop.Segments.Count; index++)
            {
                BoundarySegment edge = loop.Segments[index];
                if (DistanceToSegment(point, edge.Start, edge.End) <= tolerance)
                    return true;
            }
        }

        int containingDepth = region.EffectiveBoundaryLoops
            .Count(loop => PointInRing(loop.Vertices, point));
        return containingDepth % 2 == 1;
    }

    private static double DistanceToBoundary(HeatingRegion region, Vec2 point) =>
        region.EffectiveBoundaryLoops
            .SelectMany(loop => loop.Segments)
            .Min(segment => DistanceToSegment(point, segment.Start, segment.End));

    private static bool HasBoundaryCrossing(HeatingRegion region, IReadOnlyList<PathSample> samples)
    {
        const double tolerance = 1e-10;
        for (int pathIndex = 0; pathIndex < samples.Count - 1; pathIndex++)
        {
            Vec2 pathStart = samples[pathIndex].WorkingUv;
            Vec2 pathEnd = samples[pathIndex + 1].WorkingUv;
            foreach (BoundarySegment boundary in region.EffectiveBoundaryLoops.SelectMany(loop => loop.Segments))
            {
                double firstSide = Cross(pathEnd - pathStart, boundary.Start - pathStart);
                double secondSide = Cross(pathEnd - pathStart, boundary.End - pathStart);
                double thirdSide = Cross(boundary.End - boundary.Start, pathStart - boundary.Start);
                double fourthSide = Cross(boundary.End - boundary.Start, pathEnd - boundary.Start);
                bool crossesPath = firstSide > tolerance && secondSide < -tolerance ||
                    firstSide < -tolerance && secondSide > tolerance;
                bool crossesBoundary = thirdSide > tolerance && fourthSide < -tolerance ||
                    thirdSide < -tolerance && fourthSide > tolerance;
                if (crossesPath && crossesBoundary)
                    return true;
            }
        }

        return false;
    }

    private static double Cross(Vec2 first, Vec2 second) => first.X * second.Y - first.Y * second.X;

    private static double DistanceToSegment(Vec2 point, Vec2 start, Vec2 end)
    {
        Vec2 direction = end - start;
        double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
        double parameter = Math.Clamp(
            ((point.X - start.X) * direction.X + (point.Y - start.Y) * direction.Y) / lengthSquared,
            0,
            1);
        Vec2 closest = start + direction * parameter;
        return point.DistanceTo(closest);
    }

    private static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int current = 0, previous = ring.Count - 1; current < ring.Count; previous = current++)
        {
            Vec2 first = ring[current];
            Vec2 second = ring[previous];
            bool crosses = (first.Y > point.Y) != (second.Y > point.Y) &&
                point.X < (second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y) + first.X;
            if (crosses)
                inside = !inside;
        }

        return inside;
    }

    private sealed record PathSample(Vec2 WorkingUv, double ErrorBoundMm);
}
