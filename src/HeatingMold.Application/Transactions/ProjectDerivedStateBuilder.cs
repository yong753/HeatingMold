using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Area;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.Quality;
using HeatingMold.Design.Validation;
using HeatingMold.Electrical.Area;
using HeatingMold.Electrical.Calculation;

namespace HeatingMold.Application.Transactions;

/// <summary>복원한 최종 곡선으로 회로 길이 원장과 전기 결과를 다시 만듭니다.</summary>
public sealed class ProjectDerivedStateBuilder
{
    private readonly CircuitElectricalCalculator _calculator;
    private readonly ElectricallyCompliantAreaAggregator _areaAggregator;
    private readonly ConnectionLengthLedgerBuilder _ledgerBuilder;
    private readonly AppliedAreaAllocator _areaAllocator;
    private readonly CenterPitchQualityAnalyzer _qualityAnalyzer;
    private readonly DesignValidator _designValidator;
    private readonly Func<DesignProject, IPreparedGeometry, ValidationReport>? _designValidationOverride;

    /// <summary>영속 전기 규칙을 실행할 Electrical 계산기를 연결합니다.</summary>
    public ProjectDerivedStateBuilder(
        CircuitElectricalCalculator? calculator = null,
        ElectricallyCompliantAreaAggregator? areaAggregator = null,
        ConnectionLengthLedgerBuilder? ledgerBuilder = null,
        AppliedAreaAllocator? areaAllocator = null,
        DesignValidator? designValidator = null,
        Func<DesignProject, IPreparedGeometry, ValidationReport>? designValidationOverride = null,
        CenterPitchQualityAnalyzer? qualityAnalyzer = null)
    {
        _calculator = calculator ?? new CircuitElectricalCalculator();
        _areaAggregator = areaAggregator ?? new ElectricallyCompliantAreaAggregator();
        _ledgerBuilder = ledgerBuilder ?? new ConnectionLengthLedgerBuilder();
        _areaAllocator = areaAllocator ?? new AppliedAreaAllocator();
        _designValidator = designValidator ?? new DesignValidator();
        _designValidationOverride = designValidationOverride;
        _qualityAnalyzer = qualityAnalyzer ?? new CenterPitchQualityAnalyzer();
    }

    /// <summary>캐시를 권위 있는 값으로 쓰지 않고 복원된 형상 길이에서 파생 결과를 계산합니다.</summary>
    public OperationResult<CandidateDerivedState> Build(
        DesignProject project,
        IPreparedGeometry preparedGeometry,
        AppliedAreaAllocation? cachedAreaAllocation = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preparedGeometry);

        ValidationReport referenceReport = project.ValidateReferences();
        if (!referenceReport.IsValid)
            return OperationResult<CandidateDerivedState>.Failure(referenceReport.Issues);

        AppliedAreaAllocation? areaAllocation = cachedAreaAllocation is not null &&
            cachedAreaAllocation.DependencyStamp == preparedGeometry.Snapshot.DependencyStamp &&
            string.Equals(
                cachedAreaAllocation.Quality.RuleVersion,
                project.Rules.AreaRuleVersion,
                StringComparison.Ordinal)
                ? cachedAreaAllocation
                : null;
        if (project.Circuits.Count > 0)
        {
            ValidationReport designReport = ValidateDesign(project, preparedGeometry);
            if (!designReport.IsValid)
                return OperationResult<CandidateDerivedState>.Failure(designReport.Issues);
        }

        var warnings = new List<ValidationIssue>();
        AreaBuildResult? areaBuild = null;
        if (project.Circuits.Count == 0)
        {
            if (areaAllocation is null && project.HeatingRegions.Count > 0)
            {
                OperationResult<AreaBuildResult> allocationResult = AllocateAreas(project, preparedGeometry, warnings);
                if (!allocationResult.IsSuccess)
                    return OperationResult<CandidateDerivedState>.Failure(allocationResult.Issues);
                areaBuild = allocationResult.GetValueOrThrow();
                areaAllocation = areaBuild.Allocation;
            }

            IReadOnlyList<CenterPitchQualityReport> qualityReports = AnalyzeQuality(
                project,
                preparedGeometry,
                warnings,
                areaBuild?.Surfaces);

            return OperationResult<CandidateDerivedState>.Success(
                new CandidateDerivedState(areaAllocation: areaAllocation, centerPitchQualityReports: qualityReports), warnings);
        }
        if (project.WireSpecification is null)
        {
            return Failure(project, "MissingWireSpecification",
                "회로가 있지만 프로젝트 발열 도체 사양이 없습니다.");
        }

        var electricalResults = new List<ElectricalResult>(project.Circuits.Count);
        foreach (Circuit circuit in project.Circuits.OrderBy(circuit => circuit.Sequence))
        {
            OperationResult<LengthBreakdown> ledgerResult = _ledgerBuilder.Build(
                project, circuit, preparedGeometry.Snapshot);
            if (!ledgerResult.IsSuccess)
                return OperationResult<CandidateDerivedState>.Failure(ledgerResult.Issues);

            var input = new CircuitElectricalInput(
                circuit.Id,
                project.WireSpecification,
                circuit.Supply,
                ledgerResult.GetValueOrThrow(),
                preparedGeometry.Snapshot.DependencyStamp);
            electricalResults.Add(_calculator.Calculate(input));
        }

        if (areaAllocation is null && project.HeatingRegions.Count > 0)
        {
            OperationResult<AreaBuildResult> allocationResult = AllocateAreas(project, preparedGeometry, warnings);
            if (!allocationResult.IsSuccess)
                return OperationResult<CandidateDerivedState>.Failure(allocationResult.Issues);
            areaBuild = allocationResult.GetValueOrThrow();
            areaAllocation = areaBuild.Allocation;
        }

        ElectricallyCompliantAreaResult? electricalAreaResult = areaAllocation is null
            ? null
            : _areaAggregator.Aggregate(areaAllocation, electricalResults);
        IReadOnlyList<CenterPitchQualityReport> centerPitchQualityReports = AnalyzeQuality(
            project,
            preparedGeometry,
            warnings,
            areaBuild?.Surfaces);
        return OperationResult<CandidateDerivedState>.Success(
            new CandidateDerivedState(electricalResults, areaAllocation, electricalAreaResult, centerPitchQualityReports),
            warnings);
    }

    private IReadOnlyList<CenterPitchQualityReport> AnalyzeQuality(
        DesignProject project,
        IPreparedGeometry preparedGeometry,
        ICollection<ValidationIssue> warnings,
        IReadOnlyDictionary<RegionId, SurfaceElementSet>? allocatedSurfaces)
    {
        var reports = new List<CenterPitchQualityReport>();
        foreach (HeatingRegion region in project.HeatingRegions)
        {
            bool hasActiveHeatingRun = project.HeatingPaths
                .Where(path => path.GenerationSource.RegionId == region.Id)
                .SelectMany(path => path.Segments)
                .Any(segment => segment.IsActive && segment.Kind == HeatingPathSegmentKind.HeatingRun);
            if (!hasActiveHeatingRun)
                continue;

            SurfaceElementSet? surface = null;
            if (allocatedSurfaces is not null)
                allocatedSurfaces.TryGetValue(region.Id, out surface);
            if (surface is null)
            {
                GeometryQueryResult<SurfaceElementSet> surfaceResult = preparedGeometry.Queries.GetSurfaceElements(
                    region,
                    project.Rules.GeometryTolerances);
                if (surfaceResult.Status != GeometryQueryStatus.Success || surfaceResult.Value is null)
                {
                    AddAsWarnings(surfaceResult.Issues, warnings);
                    continue;
                }

                surface = surfaceResult.Value;
            }

            OperationResult<CenterPitchQualityReport> qualityResult = _qualityAnalyzer.Analyze(
                project,
                region,
                preparedGeometry.Snapshot,
                surface);
            if (qualityResult.IsSuccess)
            {
                reports.Add(qualityResult.GetValueOrThrow());
                AddAsWarnings(qualityResult.Issues, warnings);
            }
            else
            {
                AddAsWarnings(qualityResult.Issues, warnings);
            }
        }

        return reports.AsReadOnly();
    }

    private static void AddAsWarnings(IEnumerable<ValidationIssue> issues, ICollection<ValidationIssue> warnings)
    {
        foreach (ValidationIssue issue in issues)
        {
            warnings.Add(new ValidationIssue(
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
                issue.Evidence));
        }
    }

    private ValidationReport ValidateDesign(DesignProject project, IPreparedGeometry preparedGeometry)
    {
        if (_designValidationOverride is not null)
            return _designValidationOverride(project, preparedGeometry);

        if (preparedGeometry.CoordinateSystem is null)
        {
            return new ValidationReport(
            [CreateIssue(
                project,
                "MissingWorkingCoordinateSystemContext",
                "최종 설계 검증에 사용할 Native 작업 좌표계 context가 없습니다.",
                project.Id.Value)]);
        }

        return _designValidator.Validate(
            project,
            preparedGeometry.Snapshot,
            preparedGeometry.CoordinateSystem,
            preparedGeometry.Queries);
    }

    private OperationResult<AreaBuildResult> AllocateAreas(
        DesignProject project,
        IPreparedGeometry preparedGeometry,
        ICollection<ValidationIssue> warnings)
    {
        var allocations = new List<AppliedAreaAllocation>(project.HeatingRegions.Count);
        var surfaces = new Dictionary<RegionId, SurfaceElementSet>(project.HeatingRegions.Count);
        AppliedAreaAllocationSettings settings = AppliedAreaAllocationSettings.Initial
            .WithRuleVersion(project.Rules.AreaRuleVersion);
        foreach (HeatingRegion region in project.HeatingRegions)
        {
            OperationResult<AppliedAreaAllocationMeasurement> result = _areaAllocator.AllocateWithSurface(
                project,
                region,
                preparedGeometry.Snapshot,
                preparedGeometry.Queries,
                settings);
            if (!result.IsSuccess)
                return OperationResult<AreaBuildResult>.Failure(result.Issues);

            AppliedAreaAllocationMeasurement measurement = result.GetValueOrThrow();
            allocations.Add(measurement.Allocation);
            surfaces.Add(region.Id, measurement.Surface);
            foreach (ValidationIssue issue in result.Issues)
                warnings.Add(issue);
        }

        return OperationResult<AreaBuildResult>.Success(new AreaBuildResult(
            CombineAreaAllocations(allocations),
            surfaces));
    }

    private static AppliedAreaAllocation CombineAreaAllocations(IReadOnlyList<AppliedAreaAllocation> allocations)
    {
        if (allocations.Count == 0)
            throw new ArgumentException("합산할 영역 면적 배정이 없습니다.", nameof(allocations));

        DependencyStamp dependencyStamp = allocations[0].DependencyStamp;
        if (allocations.Any(allocation => allocation.DependencyStamp != dependencyStamp))
            throw new ArgumentException("여러 영역의 면적 배정은 같은 기하 입력 지문이어야 합니다.", nameof(allocations));

        double totalSurfaceArea = allocations.Sum(allocation => allocation.TotalEffectiveSurfaceAreaMm2.Value);
        double unassignedArea = allocations.Sum(allocation => allocation.UnassignedAppliedAreaMm2.Value);
        double measuredErrorBound = allocations.Sum(allocation => allocation.Quality.MeasuredAreaErrorBoundMm2);
        double conservationTolerance = allocations.Sum(allocation => allocation.Quality.ConservationToleranceMm2);
        double[] meshDeflections = allocations.Select(allocation => allocation.Quality.MeshDeflectionMm).ToArray();
        double[] angularTolerances = allocations.Select(allocation => allocation.Quality.AngularToleranceRad).ToArray();
        double maximumElementArea = allocations.Max(allocation => allocation.Quality.MaximumElementAreaMm2);
        int iterationCount = allocations.Max(allocation => allocation.Quality.IterationCount);
        bool isConverged = allocations.All(allocation => allocation.Quality.IsConverged);
        string ruleVersion = allocations[0].Quality.RuleVersion;

        var circuitAreas = new Dictionary<CircuitId, double>();
        foreach (AppliedAreaAllocation allocation in allocations)
        {
            foreach ((CircuitId circuitId, AreaMm2 area) in allocation.CircuitAppliedAreas)
            {
                circuitAreas.TryGetValue(circuitId, out double total);
                circuitAreas[circuitId] = total + area.Value;
            }
        }

        var quality = new AreaAllocationQuality(
            meshDeflections.Min(),
            angularTolerances.Min(),
            maximumElementArea,
            measuredErrorBound,
            conservationTolerance,
            iterationCount,
            isConverged,
            ruleVersion);
        return new AppliedAreaAllocation(
            dependencyStamp,
            new AreaMm2(totalSurfaceArea),
            circuitAreas.Select(pair => new KeyValuePair<CircuitId, AreaMm2>(pair.Key, new AreaMm2(pair.Value))),
            new AreaMm2(unassignedArea),
            quality);
    }

    private sealed record AreaBuildResult(
        AppliedAreaAllocation Allocation,
        IReadOnlyDictionary<RegionId, SurfaceElementSet> Surfaces);

    private static OperationResult<CandidateDerivedState> Failure(
        DesignProject project,
        string code,
        string evidence) => OperationResult<CandidateDerivedState>.Failure(
            [CreateIssue(project, code, evidence, project.Id.Value)]);

    private static ValidationIssue CreateIssue(
        DesignProject project,
        string code,
        string evidence,
        params Guid[] entityIds) => new(
            code,
            ValidationSeverity.Error,
            $"Application.{code}",
            entityIds,
            project.Rules.ElectricalRuleVersion,
            evidence: [evidence]);
}
