using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.HeatingPath;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.GeometryMapping;

/// <summary>2D 경로 초안을 전체 모델 first-hit으로 매핑해 안정 3D 경로로 만듭니다.</summary>
public sealed class HeatingPathMaterializer
{
    /// <summary>초안·앵커·revision을 확인하고 Native가 보정한 최종 곡선을 경로로 만듭니다.</summary>
    public OperationResult<CoreHeatingPath> Materialize(
        HeatingPathDraft draft,
        DesignProject project,
        WireSpecification wireSpecification,
        PlacementConstraints placementConstraints,
        WorkingCoordinateSystem coordinateSystem,
        ModelRevision expectedModelRevision,
        IGeometryQueries geometryQueries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(wireSpecification);
        ArgumentNullException.ThrowIfNull(placementConstraints);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(geometryQueries);

        string rule = project.Rules.GenerationRuleVersion;
        HeatingRegion? region = project.HeatingRegions.SingleOrDefault(candidate => candidate.Id == draft.RegionId);
        if (draft.ProjectId != project.Id || draft.BaseRevision != project.Revision || region is null)
        {
            return Failure("StaleHeatingPathDraft", "경로 초안의 프로젝트·revision·영역이 현재 설계와 다릅니다.",
                project, draft, rule);
        }
        if (coordinateSystem.Revision != expectedModelRevision)
        {
            return Failure("StaleSurfaceMappingContext", "작업 좌표계와 요청한 모델 기하 revision이 다릅니다.",
                project, draft, rule);
        }
        if (!region.FaceReferences.Contains(placementConstraints.StartAnchor.FaceReferenceId) ||
            !region.FaceReferences.Contains(placementConstraints.EndAnchor.FaceReferenceId))
        {
            return Failure("HeatingPathAnchorOutsideRegion", "경로 시작 또는 끝 앵커가 선택 Face에 속하지 않습니다.",
                project, draft, rule);
        }
        if (draft.ControlPoints[0].WorkingUv.DistanceTo(placementConstraints.StartAnchor.WorkingUv) >
                project.Rules.GeometryTolerances.ConnectionToleranceMm ||
            draft.ControlPoints[^1].WorkingUv.DistanceTo(placementConstraints.EndAnchor.WorkingUv) >
                project.Rules.GeometryTolerances.ConnectionToleranceMm)
        {
            return Failure("HeatingPathDraftAnchorMismatch", "초안 양 끝점이 사용자가 지정한 고정 앵커와 다릅니다.",
                project, draft, rule);
        }

        FaceRefId[] selectedFaceIds = region.FaceReferences.ToArray();
        Dictionary<FaceRefId, int> faceIndexById = selectedFaceIds
            .Select((faceId, index) => (faceId, index))
            .ToDictionary(item => item.faceId, item => item.index);
        var hitCache = new Dictionary<Vec2, SurfaceHit>();
        int probeCount = 0;
        string? queryFailureCode = null;
        string? queryFailureEvidence = null;

        bool TryGetHit(Vec2 workingUv, out SurfaceHit hit)
        {
            if (hitCache.TryGetValue(workingUv, out hit!))
                return true;
            if (cancellationToken.IsCancellationRequested)
            {
                queryFailureCode = "OperationCancelled";
                queryFailureEvidence = "표면 경로 매핑이 취소되었습니다.";
                hit = null!;
                return false;
            }
            if (probeCount >= project.Rules.GeometryTolerances.MappingMaximumProbes)
            {
                queryFailureCode = "SurfaceMappingProbeBudgetExceeded";
                queryFailureEvidence = "전체 경로 매핑 질의가 요청한 최대 probe 수를 넘었습니다.";
                hit = null!;
                return false;
            }

            probeCount++;
            GeometryQueryResult<SurfaceHit> query = geometryQueries.QueryFirstHit(
                coordinateSystem,
                workingUv,
                selectedFaceIds,
                cancellationToken);
            if (query.Status != GeometryQueryStatus.Success || query.Value is null)
            {
                queryFailureCode = query.Status switch
                {
                    GeometryQueryStatus.Miss => "PathLeavesSelectedSurface",
                    GeometryQueryStatus.Tangent => "SurfaceMappingTangentHit",
                    GeometryQueryStatus.StaleGeometryContext => "StaleSurfaceMappingContext",
                    GeometryQueryStatus.Unresolved => "SurfaceMappingUnresolved",
                    _ => "SurfaceMappingQueryFailed"
                };
                queryFailureEvidence = query.Issues.FirstOrDefault()?.Evidence.FirstOrDefault()
                    ?? $"작업 좌표 ({workingUv.X:F6}, {workingUv.Y:F6})의 표면 first-hit 상태는 {query.Status}입니다.";
                hit = null!;
                return false;
            }
            if (!faceIndexById.ContainsKey(query.Value.FaceReferenceId))
            {
                queryFailureCode = "FirstHitOutsideSelectedFaces";
                queryFailureEvidence = "전체 모델 first-hit Face가 발열 영역의 선택 Face 집합에 없습니다.";
                hit = null!;
                return false;
            }

            hit = query.Value;
            hitCache.Add(workingUv, hit);
            return true;
        }

        ControlPoint[] controlPoints = new ControlPoint[draft.ControlPoints.Count];
        for (int index = 0; index < draft.ControlPoints.Count; index++)
        {
            HeatingPathDraftControlPoint draftPoint = draft.ControlPoints[index];
            if (!TryGetHit(draftPoint.WorkingUv, out SurfaceHit hit))
            {
                return Failure(
                    queryFailureCode ?? "SurfaceMappingFailed",
                    queryFailureEvidence ?? "공유 절점을 선택 표면에 매핑하지 못했습니다.",
                    project,
                    draft,
                    rule,
                    draftPoint.Id.Value,
                    hitLocation: null,
                    datumUv: draftPoint.WorkingUv);
            }

            controlPoints[index] = new ControlPoint(draftPoint.Id, hit.ModelPoint);
        }

        if ((controlPoints[0].Position - placementConstraints.StartAnchor.ModelPoint).Length >
                project.Rules.GeometryTolerances.ConnectionToleranceMm ||
            (controlPoints[^1].Position - placementConstraints.EndAnchor.ModelPoint).Length >
                project.Rules.GeometryTolerances.ConnectionToleranceMm)
        {
            return Failure("HeatingPathAnchorModelMismatch", "Native first-hit 결과가 고정 시작·끝 앵커의 모델 좌표와 다릅니다.",
                project, draft, rule);
        }

        var adjustedPathsBySegment = new Dictionary<HeatingPathSegmentId, InsertionDepthAdjustedPath>(draft.Segments.Count);
        string? depthAdjustmentAlgorithmVersion = null;
        foreach (HeatingPathDraftSegment draftSegment in draft.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SurfaceMappedSample? TryMapSample(Vec2 workingUv)
            {
                if (!TryGetHit(workingUv, out SurfaceHit hit))
                    return null;
                return new SurfaceMappedSample(
                    workingUv,
                    hit.ModelPoint,
                    faceIndexById[hit.FaceReferenceId]);
            }

            bool mappedSuccessfully = SurfacePathMapper.TryMapWithMetadata(
                draftSegment.WorkingUvPoints,
                TryMapSample,
                out List<SurfaceMappedSample> mappedSamples,
                out string mappingFailure,
                project.Rules.GeometryTolerances.MappingMaximumProbes,
                project.Rules.GeometryTolerances.MappingLeafLengthMm,
                project.Rules.GeometryTolerances.MappingChordDeviationMm,
                project.Rules.GeometryTolerances.MappingMaximumDepth);
            if (!mappedSuccessfully)
            {
                return Failure(
                    queryFailureCode ?? "SurfacePathMappingFailed",
                    queryFailureEvidence ?? mappingFailure,
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }

            ValidationIssue? continuityIssue = ValidateSampleContinuity(
                mappedSamples,
                selectedFaceIds,
                geometryQueries,
                project.Rules.GeometryTolerances.MaximumContinuousSampleSpacingMm,
                rule,
                project.Id.Value,
                draft.Id.Value,
                draftSegment.Id.Value,
                cancellationToken);
            if (continuityIssue is not null)
                return OperationResult<CoreHeatingPath>.Failure([continuityIssue]);

            GeometryQueryResult<InsertionDepthAdjustedPath> adjustment = geometryQueries.AdjustInsertionDepth(
                mappedSamples.Select(sample => sample.DatumPoint).ToArray(),
                region.FaceReferences,
                placementConstraints.StartAnchor,
                wireSpecification.DiameterMm,
                placementConstraints.InsertionDepthMm,
                coordinateSystem,
                project.Rules.GeometryTolerances,
                cancellationToken);
            if (!adjustment.HasValue)
            {
                string adjustmentFailure = adjustment.Issues.FirstOrDefault()?.Evidence.FirstOrDefault()
                    ?? $"Native 깊이 보정 상태는 {adjustment.Status}입니다.";
                return Failure(
                    cancellationToken.IsCancellationRequested ? "OperationCancelled" : MapDepthAdjustmentFailure(adjustment.Status),
                    adjustmentFailure,
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }

            InsertionDepthAdjustedPath adjustedPath = adjustment.Value!;
            if (depthAdjustmentAlgorithmVersion is not null &&
                !string.Equals(depthAdjustmentAlgorithmVersion, adjustedPath.AlgorithmVersion, StringComparison.Ordinal))
            {
                return Failure(
                    "InsertionDepthAlgorithmVersionMismatch",
                    "한 경로의 구간에 서로 다른 Native 깊이 보정 알고리즘 버전이 반환되었습니다.",
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }
            depthAdjustmentAlgorithmVersion = adjustedPath.AlgorithmVersion;

            Vec3[] adjustedPoints = adjustedPath.Curve.Points.ToArray();
            if (adjustedPoints.Length < 2)
            {
                return Failure(
                    "InsertionDepthAdjustmentUnresolved",
                    "Native 깊이 보정 결과에 연속 곡선을 만들 표본이 부족합니다.",
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }

            double maximumAdjustedStepMm = adjustedPoints
                .Zip(adjustedPoints.Skip(1), (first, second) => (second - first).Length)
                .DefaultIfEmpty(0)
                .Max();
            if (maximumAdjustedStepMm > project.Rules.GeometryTolerances.MaximumContinuousSampleSpacingMm)
            {
                return Failure(
                    "InsertionDepthAdjustmentStepDiscontinuity",
                    "깊이 보정으로 인접 최종 곡선 표본 간격이 연속성 한도를 넘었습니다.",
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }

            adjustedPathsBySegment.Add(draftSegment.Id, adjustedPath);
        }

        Dictionary<ControlPointId, Vec3> adjustedControlPointPositions = [];
        foreach (HeatingPathDraftControlPoint draftPoint in draft.ControlPoints)
        {
            var endpointPositions = draft.Segments
                .SelectMany(segment =>
                {
                    IReadOnlyList<Vec3> points = adjustedPathsBySegment[segment.Id].Curve.Points;
                    var endpoints = new List<Vec3>(2);
                    if (segment.StartControlPointId == draftPoint.Id)
                        endpoints.Add(points[0]);
                    if (segment.EndControlPointId == draftPoint.Id)
                        endpoints.Add(points[^1]);
                    return endpoints;
                })
                .ToArray();
            if (endpointPositions.Length == 0)
                continue;

            double maximumEndpointDifferenceMm = endpointPositions
                .SelectMany((first, index) => endpointPositions.Skip(index + 1)
                    .Select(second => (first - second).Length))
                .DefaultIfEmpty(0)
                .Max();
            if (maximumEndpointDifferenceMm > project.Rules.GeometryTolerances.ConnectionToleranceMm)
            {
                return Failure(
                    "InsertionDepthSharedEndpointMismatch",
                    "공유 절점에 연결되는 경로 구간의 깊이 보정 결과가 연결 공차 안에서 일치하지 않습니다.",
                    project,
                    draft,
                    rule,
                    draftPoint.Id.Value);
            }

            Vec3 commonPosition = endpointPositions
                .Aggregate(default(Vec3), (sum, point) => sum + point) * (1.0 / endpointPositions.Length);
            adjustedControlPointPositions.Add(draftPoint.Id, commonPosition);
        }

        for (int index = 0; index < controlPoints.Length; index++)
        {
            HeatingPathDraftControlPoint draftPoint = draft.ControlPoints[index];
            if (adjustedControlPointPositions.TryGetValue(draftPoint.Id, out Vec3 position))
                controlPoints[index] = new ControlPoint(draftPoint.Id, position);
        }

        var pathSegments = new List<HeatingPathSegment>(draft.Segments.Count);
        foreach (HeatingPathDraftSegment draftSegment in draft.Segments)
        {
            Vec3[] adjustedPoints = adjustedPathsBySegment[draftSegment.Id].Curve.Points.ToArray();
            if (adjustedControlPointPositions.TryGetValue(draftSegment.StartControlPointId, out Vec3 startPosition))
                adjustedPoints[0] = startPosition;
            if (adjustedControlPointPositions.TryGetValue(draftSegment.EndControlPointId, out Vec3 endPosition))
                adjustedPoints[^1] = endPosition;

            double maximumFinalStepMm = adjustedPoints
                .Zip(adjustedPoints.Skip(1), (first, second) => (second - first).Length)
                .Max();
            if (maximumFinalStepMm > project.Rules.GeometryTolerances.MaximumContinuousSampleSpacingMm)
            {
                return Failure(
                    "InsertionDepthAdjustmentStepDiscontinuity",
                    "공유 절점을 맞춘 뒤 최종 곡선 표본 간격이 연속성 한도를 넘었습니다.",
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }

            try
            {
                var curve = new PolylineCurveDefinition(adjustedPoints);
                pathSegments.Add(new HeatingPathSegment(
                    draftSegment.Id,
                    draftSegment.Kind,
                    draftSegment.StartControlPointId,
                    draftSegment.EndControlPointId,
                    draftSegment.Sequence,
                    isActive: true,
                    curve,
                    draftSegment.LaneIndex));
            }
            catch (ArgumentException exception)
            {
                return Failure(
                    "DegenerateSurfaceMappedCurve",
                    $"표면 매핑 결과로 유효한 곡선을 만들 수 없습니다: {exception.Message}",
                    project,
                    draft,
                    rule,
                    draftSegment.Id.Value);
            }
        }

        var generationSource = new HeatingPathGenerationSource(
            region.Id,
            placementConstraints,
            wireSpecification,
            $"{rule}+{depthAdjustmentAlgorithmVersion ?? "native-axis-ray-bracket-v2"}");
        try
        {
            var path = new CoreHeatingPath(
                draft.Id,
                generationSource,
                controlPoints,
                pathSegments,
                pathSegments);
            return OperationResult<CoreHeatingPath>.Success(path);
        }
        catch (ArgumentException exception)
        {
            return Failure("InvalidSurfaceMappedPath", exception.Message, project, draft, rule);
        }
    }

    /// <summary>Native 깊이 보정 실패 상태를 사용자 수정에 필요한 안정 코드로 바꿉니다.</summary>
    private static string MapDepthAdjustmentFailure(GeometryQueryStatus status) => status switch
    {
        GeometryQueryStatus.Miss => "InsertionDepthSurfaceMiss",
        GeometryQueryStatus.Tangent => "InsertionDepthSurfaceTangent",
        GeometryQueryStatus.StaleGeometryContext => "StaleSurfaceMappingContext",
        GeometryQueryStatus.Failure => "InsertionDepthNativeFailure",
        _ => "InsertionDepthAdjustmentUnresolved"
    };

    private static ValidationIssue? ValidateSampleContinuity(
        IReadOnlyList<SurfaceMappedSample> samples,
        IReadOnlyList<FaceRefId> selectedFaceIds,
        IGeometryQueries geometryQueries,
        double maximumStepMm,
        string ruleVersion,
        Guid projectId,
        Guid pathId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        for (int index = 1; index < samples.Count; index++)
        {
            SurfaceMappedSample previous = samples[index - 1];
            SurfaceMappedSample current = samples[index];
            double step = (current.Point - previous.Point).Length;
            if (step > maximumStepMm)
            {
                return CreateIssue(
                    "SurfaceMappingStepDiscontinuity",
                    "연속 표면 표본 사이의 모델 좌표 step이 허용 한도를 넘었습니다.",
                    ruleVersion,
                    [projectId, pathId, segmentId],
                    current.Point,
                    current.DatumPoint,
                    step,
                    maximumStepMm,
                    "mm");
            }

            FaceRefId previousFace = selectedFaceIds[previous.FaceId];
            FaceRefId currentFace = selectedFaceIds[current.FaceId];
            if (previousFace == currentFace)
                continue;

            GeometryQueryResult<bool> adjacency = geometryQueries.AreAdjacent(
                previousFace,
                currentFace,
                cancellationToken);
            if (adjacency.Status != GeometryQueryStatus.Success || adjacency.Value != true)
            {
                string code = adjacency.Status == GeometryQueryStatus.Unresolved ||
                    adjacency.Status == GeometryQueryStatus.StaleGeometryContext
                    ? "SurfaceAdjacencyUnresolved"
                    : "NonAdjacentSurfaceTransition";
                return CreateIssue(
                    code,
                    "경로가 실제로 공유 Edge를 가진 Face 사이에서만 전환하는지 확인할 수 없습니다.",
                    ruleVersion,
                    [projectId, pathId, segmentId, previousFace.Value, currentFace.Value],
                    current.Point,
                    current.DatumPoint,
                    measuredValue: step,
                    requiredValue: maximumStepMm,
                    unit: "mm");
            }
        }

        return null;
    }

    private static ValidationIssue CreateIssue(
        string code,
        string evidence,
        string ruleVersion,
        IEnumerable<Guid> entityIds,
        Vec3? hitLocation = null,
        Vec2? datumUv = null,
        double? measuredValue = null,
        double? requiredValue = null,
        string? unit = null) => new(
        code,
        ValidationSeverity.Error,
        $"Design.{code}",
        entityIds,
        ruleVersion,
        hitLocation,
        datumUv,
        measuredValue,
        requiredValue,
        unit,
        [evidence]);

    private static OperationResult<CoreHeatingPath> Failure(
        string code,
        string evidence,
        DesignProject project,
        HeatingPathDraft draft,
        string ruleVersion,
        params Guid[] entityIds) => OperationResult<CoreHeatingPath>.Failure(
        [CreateIssue(
            code,
            evidence,
            ruleVersion,
            new[] { project.Id.Value, draft.Id.Value, draft.RegionId.Value }.Concat(entityIds))]);

    private static OperationResult<CoreHeatingPath> Failure(
        string code,
        string evidence,
        DesignProject project,
        HeatingPathDraft draft,
        string ruleVersion,
        Guid entityId,
        Vec3? hitLocation,
        Vec2? datumUv) => OperationResult<CoreHeatingPath>.Failure(
        [CreateIssue(
            code,
            evidence,
            ruleVersion,
            [project.Id.Value, draft.Id.Value, draft.RegionId.Value, entityId],
            hitLocation,
            datumUv)]);
}
