using HeatingMold.Core.Circuits;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;
using CoreHeatingPath = HeatingMold.Core.Routing.HeatingPath;

namespace HeatingMold.Design.Validation;

/// <summary>각 회로 양 끝 앵커가 같은 발열 영역의 지정 출구 측면에 놓이는지 검사합니다.</summary>
public sealed class SameSideValidator
{
    private readonly ExitSideResolver _exitSideResolver;

    /// <summary>영역의 안정 경계 범위를 확인하는 resolver를 연결합니다.</summary>
    public SameSideValidator(ExitSideResolver? exitSideResolver = null)
    {
        _exitSideResolver = exitSideResolver ?? new ExitSideResolver();
    }

    /// <summary>회로 조각의 영역과 두 연결 앵커가 지정된 출구 측면에 속하는지 확인합니다.</summary>
    public ValidationReport Validate(
        DesignProject project,
        double toleranceMm,
        string? ruleVersion = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));

        string rule = ruleVersion ?? project.Rules.ValidationRuleVersion;
        var issues = new List<ValidationIssue>();
        Dictionary<ConnectionPointId, ConnectionPoint> connectionPoints = project.ConnectionPoints
            .ToDictionary(point => point.Id);
        Dictionary<HeatingPathSegmentId, CoreHeatingPath> pathsBySegment = project.HeatingPaths
            .SelectMany(path => path.Segments.Select(segment => (segment.Id, Path: path)))
            .ToDictionary(item => item.Id, item => item.Path);
        Dictionary<RegionId, HeatingRegion> regions = project.HeatingRegions.ToDictionary(region => region.Id);

        foreach (Circuit circuit in project.Circuits)
        {
            CoreHeatingPath[] owningPaths = circuit.Segments
                .Select(reference => pathsBySegment.TryGetValue(reference.SegmentId, out CoreHeatingPath? path) ? path : null)
                .Where(path => path is not null)
                .Cast<CoreHeatingPath>()
                .DistinctBy(path => path.Id)
                .ToArray();
            if (owningPaths.Length != 1)
            {
                issues.Add(ValidationIssueFactory.Error(
                    "CircuitMustUseOneHeatingRegion",
                    "동일 측면 검사는 하나의 발열 영역에 속한 단일 경로 회로에만 적용할 수 있습니다.",
                    rule,
                    [circuit.Id.Value]));
                continue;
            }

            RegionId regionId = owningPaths[0].GenerationSource.RegionId;
            if (!regions.TryGetValue(regionId, out HeatingRegion? region))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingCircuitHeatingRegion",
                    "회로 경로가 참조하는 발열 영역이 프로젝트에 없습니다.",
                    rule,
                    [circuit.Id.Value, regionId.Value]));
                continue;
            }

            if (!connectionPoints.TryGetValue(circuit.StartConnectionPointId, out ConnectionPoint? start) ||
                !connectionPoints.TryGetValue(circuit.EndConnectionPointId, out ConnectionPoint? end))
            {
                issues.Add(ValidationIssueFactory.Error(
                    "MissingCircuitConnectionPoint",
                    "동일 측면을 검사할 회로 양 끝 연결부가 모두 필요합니다.",
                    rule,
                    [circuit.Id.Value]));
                continue;
            }

            CheckAnchor(region, start, "CircuitStartOutsideExitSide");
            CheckAnchor(region, end, "CircuitEndOutsideExitSide");
        }

        return new ValidationReport(issues);

        void CheckAnchor(HeatingRegion region, ConnectionPoint point, string code)
        {
            if (!region.FaceReferences.Contains(point.SurfaceAnchor.FaceReferenceId))
            {
                issues.Add(ValidationIssueFactory.Error(
                    code,
                    "연결 앵커의 Face가 회로 발열 영역의 선택 Face에 포함되지 않습니다.",
                    rule,
                    [region.Id.Value, point.Id.Value],
                    point.SurfaceAnchor.ModelPoint,
                    point.SurfaceAnchor.WorkingUv));
                return;
            }

            OperationResult<ResolvedExitSideLocation> resolved = _exitSideResolver.Resolve(
                region,
                point.SurfaceAnchor.WorkingUv,
                toleranceMm,
                rule);
            if (resolved.IsSuccess)
                return;

            issues.Add(ValidationIssueFactory.Error(
                code,
                "회로 끝 앵커가 안정 경계 ID와 정규화 범위로 지정된 공통 출구 측면에 놓이지 않습니다.",
                rule,
                [region.Id.Value, point.Id.Value],
                point.SurfaceAnchor.ModelPoint,
                point.SurfaceAnchor.WorkingUv,
                measuredValue: resolved.Issues.FirstOrDefault()?.MeasuredValue,
                requiredValue: toleranceMm,
                unit: "mm"));
        }
    }
}
