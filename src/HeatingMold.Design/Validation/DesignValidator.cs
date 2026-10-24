using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Validation;

/// <summary>최종 설계 검사를 정해진 순서로 실행하고 첫 차단 단계에서 멈춥니다.</summary>
public sealed class DesignValidator
{
    private readonly BoundaryValidator _boundaryValidator;
    private readonly ContinuityValidator _continuityValidator;
    private readonly SameSideValidator _sameSideValidator;
    private readonly DepthValidator _depthValidator;
    private readonly ClearanceValidator _clearanceValidator;

    /// <summary>독립 검증기를 연결해 영역부터 최소 간격까지의 파이프라인을 구성합니다.</summary>
    public DesignValidator(
        BoundaryValidator? boundaryValidator = null,
        ContinuityValidator? continuityValidator = null,
        SameSideValidator? sameSideValidator = null,
        DepthValidator? depthValidator = null,
        ClearanceValidator? clearanceValidator = null)
    {
        _boundaryValidator = boundaryValidator ?? new BoundaryValidator();
        _continuityValidator = continuityValidator ?? new ContinuityValidator();
        _sameSideValidator = sameSideValidator ?? new SameSideValidator();
        _depthValidator = depthValidator ?? new DepthValidator();
        _clearanceValidator = clearanceValidator ?? new ClearanceValidator();
    }

    /// <summary>현재 설계와 Native 기하 스냅샷을 검증하고 확정 가능 여부를 반환합니다.</summary>
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
        ValidationReport referenceReport = project.ValidateReferences();
        if (!referenceReport.IsValid)
            return referenceReport;

        ValidationIssue[] inputIssues = ValidateInputs(project, geometry, coordinateSystem, rule);
        if (inputIssues.Length > 0)
            return new ValidationReport(inputIssues);

        var issues = new List<ValidationIssue>();
        if (project.HeatingPaths.Count == 0)
            issues.Add(ValidationIssueFactory.Error(
                "MissingHeatingPath",
                "최종 설계 검증에는 발열 경로가 하나 이상 필요합니다.",
                rule,
                [project.Id.Value]));
        if (project.Circuits.Count == 0)
            issues.Add(ValidationIssueFactory.Error(
                "MissingCircuit",
                "전기·연결 검증에는 회로가 하나 이상 필요합니다.",
                rule,
                [project.Id.Value]));
        if (project.WireSpecification is null || project.PlacementConstraints is null)
        {
            issues.Add(ValidationIssueFactory.Error(
                "MissingDesignPlacementInputs",
                "최종 검증에는 발열사 사양과 배치 조건이 필요합니다.",
                rule,
                [project.Id.Value]));
        }
        if (issues.Count > 0)
            return new ValidationReport(issues);

        AppendStage(_boundaryValidator.Validate(project, geometry, coordinateSystem, geometryQueries, cancellationToken));
        if (HasErrors())
            return new ValidationReport(issues);

        AppendStage(_continuityValidator.Validate(
            project,
            geometry,
            project.Rules.GeometryTolerances.ConnectionToleranceMm,
            rule));
        if (HasErrors())
            return new ValidationReport(issues);

        AppendStage(_sameSideValidator.Validate(
            project,
            project.Rules.GeometryTolerances.ConnectionToleranceMm,
            rule));
        if (HasErrors())
            return new ValidationReport(issues);

        SurfaceAnchor[] anchors = project.ConnectionPoints
            .Select(point => point.SurfaceAnchor)
            .Concat(
            [
                project.PlacementConstraints!.StartAnchor,
                project.PlacementConstraints.EndAnchor
            ])
            .DistinctBy(anchor => (anchor.FaceReferenceId, anchor.FaceUv, anchor.WorkingUv))
            .ToArray();
        CurveGeometrySnapshot[] activeHeatingCurves = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .Where(curve => project.HeatingPaths
                .SelectMany(path => path.Segments)
                .Any(segment => segment.Id == curve.HeatingPathSegmentId!.Value && segment.IsActive))
            .ToArray();
        Dictionary<HeatingPathSegmentId, IReadOnlyList<FaceRefId>> selectedFacesBySegment = project.HeatingPaths
            .SelectMany(path =>
            {
                HeatingRegion region = project.HeatingRegions.Single(candidate =>
                    candidate.Id == path.GenerationSource.RegionId);
                return path.Segments
                    .Where(segment => segment.IsActive)
                    .Select(segment => new KeyValuePair<HeatingPathSegmentId, IReadOnlyList<FaceRefId>>(
                        segment.Id,
                        region.FaceReferences));
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        AppendStage(_depthValidator.Validate(
            activeHeatingCurves,
            project.WireSpecification!,
            project.PlacementConstraints!.InsertionDepthMm,
            anchors,
            coordinateSystem,
            geometryQueries,
            project.Rules.GeometryTolerances.ConnectionToleranceMm,
            rule,
            cancellationToken,
            selectedFacesBySegment));
        if (HasErrors())
            return new ValidationReport(issues);

        IReadOnlyList<CurveSegmentAdjacency> localClearanceAdjacencies =
            new HeatingPathCurveAdjacencyResolver().Resolve(project.HeatingPaths, activeHeatingCurves);
        AppendStage(_clearanceValidator.Validate(
            activeHeatingCurves,
            project.WireSpecification!.DiameterMm,
            project.PlacementConstraints!.MinimumClearGapMm,
            project.Rules.GeometryTolerances,
            geometryQueries,
            rule,
            localClearanceAdjacencies,
            cancellationToken));
        return new ValidationReport(issues);

        void AppendStage(ValidationReport stage) => issues.AddRange(stage.Issues);
        bool HasErrors() => issues.Any(issue => issue.Severity == ValidationSeverity.Error);
    }

    private static ValidationIssue[] ValidateInputs(
        DesignProject project,
        GeometrySnapshot geometry,
        WorkingCoordinateSystem coordinateSystem,
        string ruleVersion)
    {
        var issues = new List<ValidationIssue>();
        DependencyStamp stamp = geometry.DependencyStamp;
        if (stamp.DesignRevision != project.Revision ||
            !string.Equals(stamp.SourceModelSha256, project.SourceModel.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssueFactory.Error(
                "StaleGeometrySnapshot",
                "기하 스냅샷의 설계 revision 또는 원본 모델 해시가 현재 프로젝트와 다릅니다.",
                ruleVersion,
                [project.Id.Value]));
        }
        if (geometry.ModelRevision != coordinateSystem.Revision)
        {
            issues.Add(ValidationIssueFactory.Error(
                "StaleGeometryContext",
                "Native 곡선 스냅샷과 작업 좌표계가 서로 다른 모델 기하 revision을 참조합니다.",
                ruleVersion,
                [project.Id.Value]));
        }

        HashSet<HeatingPathSegmentId> expectedHeatingIds = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Where(segment => segment.IsActive)
            .Select(segment => segment.Id)
            .ToHashSet();
        HashSet<ConnectionPathSegmentId> expectedConnectionIds = project.ConnectionPaths
            .SelectMany(path => path.Segments)
            .Select(segment => segment.Id)
            .ToHashSet();
        HashSet<HeatingPathSegmentId> actualHeatingIds = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .Select(curve => curve.HeatingPathSegmentId!.Value)
            .ToHashSet();
        HashSet<ConnectionPathSegmentId> actualConnectionIds = geometry.Curves
            .Where(curve => curve.ConnectionPathSegmentId.HasValue)
            .Select(curve => curve.ConnectionPathSegmentId!.Value)
            .ToHashSet();

        if (!expectedHeatingIds.SetEquals(actualHeatingIds) || !expectedConnectionIds.SetEquals(actualConnectionIds))
        {
            issues.Add(ValidationIssueFactory.Error(
                "GeometryCurveSetMismatch",
                "최종 기하 스냅샷의 곡선 ID 집합이 활성 설계 구간과 일치하지 않습니다.",
                ruleVersion,
                [project.Id.Value]));
        }

        if (coordinateSystem.Id.Value == 0)
        {
            issues.Add(ValidationIssueFactory.Error(
                "InvalidGeometryDocumentContext",
                "Native 검증에 연결된 작업 좌표계 context ID가 유효하지 않습니다.",
                ruleVersion,
                [project.Id.Value]));
        }

        return issues.ToArray();
    }
}
