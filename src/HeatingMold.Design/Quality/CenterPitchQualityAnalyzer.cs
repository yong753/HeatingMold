using System.Collections.ObjectModel;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Quality;

/// <summary>활성 레인 간 3D 간격과 표면 표본의 경로 거리를 측정해 보고합니다.</summary>
public sealed class CenterPitchQualityAnalyzer
{
    /// <summary>같은 revision의 완전한 곡선·표면 표본에서 관측 품질 수치를 계산합니다.</summary>
    public OperationResult<CenterPitchQualityReport> Analyze(
        DesignProject project,
        HeatingRegion region,
        GeometrySnapshot geometry,
        SurfaceElementSet surface,
        CenterPitchQualitySettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(surface);
        settings ??= CenterPitchQualitySettings.Initial;

        if (geometry.DependencyStamp.DesignRevision != project.Revision ||
            !string.Equals(geometry.DependencyStamp.SourceModelSha256, project.SourceModel.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("StaleCenterPitchGeometry", "품질 표본이 현재 설계 revision 또는 원본 모델과 다릅니다.", project, region);
        }
        if (!project.HeatingRegions.Any(candidate => candidate.Id == region.Id))
            return Failure("RegionNotInProject", "품질 분석 대상 영역이 현재 프로젝트에 없습니다.", project, region);

        HeatingPathSegment[] runs = project.HeatingPaths
            .Where(path => path.GenerationSource.RegionId == region.Id)
            .SelectMany(path => path.Segments)
            .Where(segment => segment.IsActive && segment.Kind == HeatingPathSegmentKind.HeatingRun)
            .ToArray();
        if (runs.Length == 0)
            return Failure("NoActiveHeatingRuns", "분석할 활성 발열 레인이 없습니다.", project, region);

        Dictionary<HeatingPathSegmentId, CurveGeometrySnapshot> curves = geometry.Curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .ToDictionary(curve => curve.HeatingPathSegmentId!.Value);
        HeatingPathSegmentId[] missingIds = runs
            .Where(segment => !curves.ContainsKey(segment.Id))
            .Select(segment => segment.Id)
            .ToArray();
        if (missingIds.Length > 0)
            return Failure("MissingQualityCurve", "활성 발열 레인의 최종 3D 곡선 측정값이 없습니다.", project, region, missingIds);

        HeatingPathSegmentId[] incompleteIds = runs
            .Where(segment => !curves[segment.Id].Approximation.IsComplete || curves[segment.Id].Samples.Count < 2)
            .Select(segment => segment.Id)
            .ToArray();
        if (incompleteIds.Length > 0 || surface.Elements.Any(element => !element.Approximation.IsComplete))
            return Failure("IncompleteQualitySamples", "곡선 표본이 두 점 미만이거나 곡선·trimmed 표면 표본이 불완전해 품질 수치를 보고하지 않습니다.",
                project, region, incompleteIds);

        long requestedProbeCount = 0;
        foreach (SurfaceElement element in surface.Elements)
            requestedProbeCount += element.ModelVertices.Count + 1L;
        if (requestedProbeCount == 0)
            return Failure("NoQualitySurfaceSamples", "표면 품질 분석에 사용할 정점·중심 표본이 없습니다.", project, region);
        if (requestedProbeCount > settings.MaximumSurfaceProbeCount)
        {
            return Failure(
                "QualityProbeBudgetExceeded",
                "표면 경로 거리 분석이 설정한 표본 한도를 넘었습니다.",
                project,
                region,
                measuredValue: requestedProbeCount,
                requiredValue: settings.MaximumSurfaceProbeCount,
                unit: "probes");
        }

        var reportedSamples = new PriorityQueue<SurfacePathDistanceSample, double>();
        double maximumSampledUpperBound = 0;
        double maximumDistance = 0;
        foreach (SurfaceElement element in surface.Elements)
        {
            foreach (Vec3 point in element.ModelVertices.Append(element.ModelCentroid))
            {
                (HeatingPathSegment segment, double distance) = runs
                    .Select(segment => (Segment: segment, Distance: DistanceToCurve(point, curves[segment.Id])))
                    .OrderBy(candidate => candidate.Distance)
                    .ThenBy(candidate => candidate.Segment.Id.Value)
                    .First();
                CurveApproximationInfo curveApproximation = curves[segment.Id].Approximation;
                double approximationBound = curveApproximation.MaximumMeasuredDeviationMm +
                    curveApproximation.MaximumSampleSpacingMm * 0.5 +
                    element.Approximation.MaximumMeasuredDeviationMm +
                    element.Approximation.MaximumSampleSpacingMm;
                var sample = new SurfacePathDistanceSample(
                    element.ElementIndex,
                    point,
                    segment.Id,
                    distance,
                    approximationBound);
                maximumDistance = Math.Max(maximumDistance, distance);
                maximumSampledUpperBound = Math.Max(maximumSampledUpperBound, distance + approximationBound);

                if (reportedSamples.Count < settings.MaximumReportedSurfaceSamples)
                {
                    reportedSamples.Enqueue(sample, sample.CenterlineDistanceMm);
                }
                else if (sample.CenterlineDistanceMm > reportedSamples.Peek().CenterlineDistanceMm)
                {
                    reportedSamples.Dequeue();
                    reportedSamples.Enqueue(sample, sample.CenterlineDistanceMm);
                }
            }
        }

        var pitchMeasurements = new List<LanePitchMeasurement>();
        LengthMm? targetCenterPitch = project.PlacementConstraints is not null && project.WireSpecification is not null
            ? project.PlacementConstraints.CenterPitch(project.WireSpecification.DiameterMm)
            : null;
        HeatingPathSegment[][] lanes = runs
            .Where(segment => segment.LaneIndex.HasValue)
            .GroupBy(segment => segment.LaneIndex!.Value)
            .OrderBy(group => group.Key)
            .Select(group => group.ToArray())
            .ToArray();
        for (int laneIndex = 1; laneIndex < lanes.Length; laneIndex++)
        {
            int firstLane = lanes[laneIndex - 1][0].LaneIndex!.Value;
            int secondLane = lanes[laneIndex][0].LaneIndex!.Value;
            if (secondLane != firstLane + 1)
                continue;

            foreach (HeatingPathSegment firstSegment in lanes[laneIndex - 1])
            foreach (HeatingPathSegment secondSegment in lanes[laneIndex])
            {
                CurveGeometrySnapshot firstCurve = curves[firstSegment.Id];
                CurveGeometrySnapshot secondCurve = curves[secondSegment.Id];
                int sampleCount = Math.Min(settings.SamplesPerLanePair, Math.Max(firstCurve.Samples.Count, secondCurve.Samples.Count));
                bool reverseSecondCurve = IsReversedCorrespondence(firstCurve.Samples, secondCurve.Samples);
                (double Distance, Vec3 Midpoint)[] observations = Enumerable.Range(0, sampleCount)
                    .Select(sampleIndex =>
                    {
                        double parameter = sampleCount == 1 ? 0.5 : (double)sampleIndex / (sampleCount - 1);
                        Vec3 firstPoint = SampleAtNormalizedLength(firstCurve.Samples, parameter);
                        double secondParameter = reverseSecondCurve ? 1 - parameter : parameter;
                        Vec3 secondPoint = SampleAtNormalizedLength(secondCurve.Samples, secondParameter);
                        return ((firstPoint - secondPoint).Length, (firstPoint + secondPoint) * 0.5);
                    })
                    .ToArray();
                double approximationBound = firstCurve.Approximation.MaximumMeasuredDeviationMm +
                    firstCurve.Approximation.MaximumSampleSpacingMm * 0.5 +
                    secondCurve.Approximation.MaximumMeasuredDeviationMm +
                    secondCurve.Approximation.MaximumSampleSpacingMm * 0.5;
                double minimumDistance = observations.Min(observation => observation.Distance);
                double maximumPitchDistance = observations.Max(observation => observation.Distance);
                double averageDistance = Math.Clamp(
                    observations.Average(observation => observation.Distance),
                    minimumDistance,
                    maximumPitchDistance);
                pitchMeasurements.Add(new LanePitchMeasurement(
                    firstLane,
                    secondLane,
                    firstSegment.Id,
                    secondSegment.Id,
                    minimumDistance,
                    averageDistance,
                    maximumPitchDistance,
                    observations.OrderByDescending(observation => observation.Distance).First().Midpoint,
                    approximationBound,
                    sampleCount,
                    targetCenterPitch,
                    targetCenterPitch.HasValue
                        ? averageDistance - targetCenterPitch.Value.Value
                        : null));
            }
        }

        SurfacePathDistanceSample[] retainedSamples = reportedSamples.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(sample => sample.CenterlineDistanceMm)
            .ThenBy(sample => sample.SurfaceElementIndex)
            .ToArray();
        return OperationResult<CenterPitchQualityReport>.Success(new CenterPitchQualityReport(
            region.Id,
            geometry.DependencyStamp,
            surface.TotalSurfaceAreaMm2,
            maximumDistance,
            maximumSampledUpperBound,
            requestedProbeCount,
            project.WireSpecification?.DiameterMm,
            targetCenterPitch,
            retainedSamples,
            pitchMeasurements,
            settings.RuleVersion));
    }

    private static Vec3 SampleAtNormalizedLength(IReadOnlyList<Vec3> points, double parameter)
    {
        double[] cumulative = new double[points.Count];
        for (int index = 1; index < points.Count; index++)
            cumulative[index] = cumulative[index - 1] + (points[index] - points[index - 1]).Length;

        double target = cumulative[^1] * parameter;
        for (int index = 1; index < points.Count; index++)
        {
            if (target > cumulative[index])
                continue;

            double segmentLength = cumulative[index] - cumulative[index - 1];
            double fraction = segmentLength == 0 ? 0 : (target - cumulative[index - 1]) / segmentLength;
            return points[index - 1] + (points[index] - points[index - 1]) * fraction;
        }

        return points[^1];
    }

    private static bool IsReversedCorrespondence(IReadOnlyList<Vec3> first, IReadOnlyList<Vec3> second)
    {
        double forwardCost = (first[0] - second[0]).Length + (first[^1] - second[^1]).Length;
        double reverseCost = (first[0] - second[^1]).Length + (first[^1] - second[0]).Length;
        return reverseCost < forwardCost;
    }

    private static double DistanceToCurve(Vec3 point, CurveGeometrySnapshot curve)
    {
        double minimumDistance = double.PositiveInfinity;
        for (int index = 0; index < curve.Samples.Count - 1; index++)
        {
            Vec3 start = curve.Samples[index];
            Vec3 direction = curve.Samples[index + 1] - start;
            double lengthSquared = Vec3.Dot(direction, direction);
            double parameter = lengthSquared <= 1e-24
                ? 0
                : Math.Clamp(Vec3.Dot(point - start, direction) / lengthSquared, 0, 1);
            minimumDistance = Math.Min(minimumDistance, (point - (start + direction * parameter)).Length);
        }

        return minimumDistance;
    }

    private static OperationResult<CenterPitchQualityReport> Failure(
        string code,
        string evidence,
        DesignProject project,
        HeatingRegion region,
        IEnumerable<HeatingPathSegmentId>? segmentIds = null,
        double? measuredValue = null,
        double? requiredValue = null,
        string? unit = null)
    {
        Guid[] ids = [project.Id.Value, region.Id.Value, .. segmentIds?.Select(id => id.Value) ?? []];
        return OperationResult<CenterPitchQualityReport>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            ids,
            project.Rules.ValidationRuleVersion,
            measuredValue: measuredValue,
            requiredValue: requiredValue,
            unit: unit,
            evidence: [evidence])]);
    }
}

/// <summary>품질 분석의 제한된 표본 수와 규칙 버전입니다.</summary>
public sealed record CenterPitchQualitySettings
{
    /// <summary>표면 정점·중심 거리 질의의 최대 수입니다.</summary>
    public int MaximumSurfaceProbeCount { get; }

    /// <summary>인접 레인 쌍에서 비교할 최대 정규화 위치 수입니다.</summary>
    public int SamplesPerLanePair { get; }

    /// <summary>보고서에 보관할 최대 표면 표본 수입니다.</summary>
    public int MaximumReportedSurfaceSamples { get; }

    /// <summary>초기 품질 표본 수와 규칙 버전입니다.</summary>
    public static CenterPitchQualitySettings Initial { get; } = new(500_000, 64, "center-pitch-quality-v1", 64);

    /// <summary>측정·보관 표본 한도와 비어 있지 않은 규칙 버전을 확인합니다.</summary>
    public CenterPitchQualitySettings(
        int maximumSurfaceProbeCount,
        int samplesPerLanePair,
        string ruleVersion,
        int maximumReportedSurfaceSamples = 64)
    {
        if (maximumSurfaceProbeCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumSurfaceProbeCount));
        if (samplesPerLanePair < 2)
            throw new ArgumentOutOfRangeException(nameof(samplesPerLanePair));
        if (maximumReportedSurfaceSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumReportedSurfaceSamples));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("품질 분석 규칙 버전이 필요합니다.", nameof(ruleVersion));

        MaximumSurfaceProbeCount = maximumSurfaceProbeCount;
        SamplesPerLanePair = samplesPerLanePair;
        MaximumReportedSurfaceSamples = maximumReportedSurfaceSamples;
        RuleVersion = ruleVersion.Trim();
    }

    /// <summary>사용할 품질 분석 규칙 버전입니다.</summary>
    public string RuleVersion { get; }
}

/// <summary>표면 표본별 가까운 발열 경로 거리와 표면·곡선 오차 상한을 보관합니다.</summary>
public sealed record SurfacePathDistanceSample(
    int SurfaceElementIndex,
    Vec3 Location,
    HeatingPathSegmentId NearestSegmentId,
    double CenterlineDistanceMm,
    double ApproximationBoundMm);

/// <summary>이웃 레인 한 쌍의 관측 3D 간격 범위와 목표 피치 편차입니다.</summary>
public sealed record LanePitchMeasurement
{
    /// <summary>첫 번째 레인의 생성 인덱스입니다.</summary>
    public int FirstLaneIndex { get; }

    /// <summary>두 번째 레인의 생성 인덱스입니다.</summary>
    public int SecondLaneIndex { get; }

    /// <summary>간격을 측정한 첫 번째 곡선 ID입니다.</summary>
    public HeatingPathSegmentId FirstSegmentId { get; }

    /// <summary>간격을 측정한 두 번째 곡선 ID입니다.</summary>
    public HeatingPathSegmentId SecondSegmentId { get; }

    /// <summary>표본에서 관측한 최소 3D chord 간격(mm)입니다.</summary>
    public double MinimumChordDistanceMm { get; }

    /// <summary>표본에서 관측한 평균 3D chord 간격(mm)입니다.</summary>
    public double AverageChordDistanceMm { get; }

    /// <summary>표본에서 관측한 최대 3D chord 간격(mm)입니다.</summary>
    public double MaximumChordDistanceMm { get; }

    /// <summary>최대 관측 간격 위치에서 두 곡선 사이의 중간점(mm)입니다.</summary>
    public Vec3 MaximumDistanceLocation { get; }

    /// <summary>두 곡선의 측정 편차와 표본 간격에서 구한 오차 상한(mm)입니다.</summary>
    public double ApproximationBoundMm { get; }

    /// <summary>간격을 비교한 정규화 위치 수입니다.</summary>
    public int SampleCount { get; }

    /// <summary>입력 제약의 목표 중심 피치이며 입력이 없으면 null입니다.</summary>
    public LengthMm? TargetCenterPitchMm { get; }

    /// <summary>평균 관측값에서 목표 중심 피치를 뺀 signed 편차이며 목표가 없으면 null입니다.</summary>
    public double? AverageDeviationFromTargetMm { get; }

    /// <summary>관측 간격과 근사 정보를 immutable 결과로 보관합니다.</summary>
    public LanePitchMeasurement(
        int firstLaneIndex,
        int secondLaneIndex,
        HeatingPathSegmentId firstSegmentId,
        HeatingPathSegmentId secondSegmentId,
        double minimumChordDistanceMm,
        double averageChordDistanceMm,
        double maximumChordDistanceMm,
        Vec3 maximumDistanceLocation,
        double approximationBoundMm,
        int sampleCount,
        LengthMm? targetCenterPitchMm,
        double? averageDeviationFromTargetMm)
    {
        if (firstLaneIndex < 0 || secondLaneIndex != firstLaneIndex + 1 ||
            firstSegmentId.IsEmpty || secondSegmentId.IsEmpty)
        {
            throw new ArgumentException("레인 간격 측정에는 인접한 유효 레인과 곡선 ID가 필요합니다.");
        }
        if (!double.IsFinite(minimumChordDistanceMm) || minimumChordDistanceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumChordDistanceMm));
        if (!double.IsFinite(averageChordDistanceMm) || averageChordDistanceMm < minimumChordDistanceMm)
            throw new ArgumentOutOfRangeException(
                nameof(averageChordDistanceMm),
                $"관측 평균 {averageChordDistanceMm:R} mm가 최소값 {minimumChordDistanceMm:R} mm보다 작거나 유한하지 않습니다.");
        if (!double.IsFinite(maximumChordDistanceMm) || maximumChordDistanceMm < averageChordDistanceMm)
            throw new ArgumentOutOfRangeException(nameof(maximumChordDistanceMm));
        if (!double.IsFinite(approximationBoundMm) || approximationBoundMm < 0)
            throw new ArgumentOutOfRangeException(nameof(approximationBoundMm));
        if (sampleCount < 2)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (averageDeviationFromTargetMm.HasValue && !double.IsFinite(averageDeviationFromTargetMm.Value))
            throw new ArgumentOutOfRangeException(nameof(averageDeviationFromTargetMm));

        FirstLaneIndex = firstLaneIndex;
        SecondLaneIndex = secondLaneIndex;
        FirstSegmentId = firstSegmentId;
        SecondSegmentId = secondSegmentId;
        MinimumChordDistanceMm = minimumChordDistanceMm;
        AverageChordDistanceMm = averageChordDistanceMm;
        MaximumChordDistanceMm = maximumChordDistanceMm;
        MaximumDistanceLocation = maximumDistanceLocation;
        ApproximationBoundMm = approximationBoundMm;
        SampleCount = sampleCount;
        TargetCenterPitchMm = targetCenterPitchMm;
        AverageDeviationFromTargetMm = averageDeviationFromTargetMm;
    }
}

/// <summary>전기·열 합격 여부를 추정하지 않는 표본 기반 품질 보고서입니다.</summary>
public sealed class CenterPitchQualityReport
{
    /// <summary>이 측정이 대상으로 삼은 발열 영역 ID입니다.</summary>
    public RegionId RegionId { get; }

    /// <summary>품질 수치가 참조하는 설계·모델 입력 지문입니다.</summary>
    public DependencyStamp DependencyStamp { get; }

    /// <summary>Native trimmed 표면 요소가 나타내는 실면적(mm²)입니다.</summary>
    public AreaMm2 MeasuredSurfaceAreaMm2 { get; }

    /// <summary>정점과 중심 표본에서 관측한 최대 경로 중심선 거리(mm)입니다.</summary>
    public double MaximumSampledCenterlineDistanceMm { get; }

    /// <summary>거리 근사 오차 상한을 더한 최대 표본 중심선 거리(mm)입니다.</summary>
    public double MaximumSampledCenterlineDistanceUpperBoundMm { get; }

    /// <summary>계산에 사용한 전체 표면 정점·중심 표본 수입니다.</summary>
    public long SurfaceProbeCount { get; }

    /// <summary>발열 도체 사양의 지름이며 사양이 없으면 null입니다.</summary>
    public LengthMm? WireDiameterMm { get; }

    /// <summary>입력 간격과 지름으로 구한 목표 중심 피치이며 입력 누락 시 null입니다.</summary>
    public LengthMm? TargetCenterPitchMm { get; }

    /// <summary>표면 요소 표본 위치의 가까운 경로 거리입니다.</summary>
    public IReadOnlyList<SurfacePathDistanceSample> SurfaceSamples { get; }

    /// <summary>인접 lane index 쌍의 3D chord 간격 통계입니다.</summary>
    public IReadOnlyList<LanePitchMeasurement> LanePitchMeasurements { get; }

    /// <summary>보고서 계산 규칙 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>측정 지문과 표본 결과를 방어 복사해 품질 보고서를 만듭니다.</summary>
    public CenterPitchQualityReport(
        RegionId regionId,
        DependencyStamp dependencyStamp,
        AreaMm2 measuredSurfaceAreaMm2,
        double maximumSampledCenterlineDistanceMm,
        double maximumSampledCenterlineDistanceUpperBoundMm,
        long surfaceProbeCount,
        LengthMm? wireDiameterMm,
        LengthMm? targetCenterPitchMm,
        IEnumerable<SurfacePathDistanceSample> surfaceSamples,
        IEnumerable<LanePitchMeasurement> lanePitchMeasurements,
        string ruleVersion)
    {
        if (regionId.IsEmpty)
            throw new ArgumentException("품질 보고서에는 유효한 발열 영역 ID가 필요합니다.", nameof(regionId));
        if (!double.IsFinite(maximumSampledCenterlineDistanceMm) || maximumSampledCenterlineDistanceMm < 0 ||
            !double.IsFinite(maximumSampledCenterlineDistanceUpperBoundMm) ||
            maximumSampledCenterlineDistanceUpperBoundMm < maximumSampledCenterlineDistanceMm ||
            surfaceProbeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSampledCenterlineDistanceMm));
        SurfacePathDistanceSample[] copiedSamples = surfaceSamples?.ToArray()
            ?? throw new ArgumentNullException(nameof(surfaceSamples));
        LanePitchMeasurement[] copiedPitches = lanePitchMeasurements?.ToArray()
            ?? throw new ArgumentNullException(nameof(lanePitchMeasurements));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("품질 보고서 규칙 버전이 필요합니다.", nameof(ruleVersion));

        RegionId = regionId;
        DependencyStamp = dependencyStamp ?? throw new ArgumentNullException(nameof(dependencyStamp));
        MeasuredSurfaceAreaMm2 = measuredSurfaceAreaMm2;
        MaximumSampledCenterlineDistanceMm = maximumSampledCenterlineDistanceMm;
        MaximumSampledCenterlineDistanceUpperBoundMm = maximumSampledCenterlineDistanceUpperBoundMm;
        SurfaceProbeCount = surfaceProbeCount;
        WireDiameterMm = wireDiameterMm;
        TargetCenterPitchMm = targetCenterPitchMm;
        SurfaceSamples = new ReadOnlyCollection<SurfacePathDistanceSample>(copiedSamples);
        LanePitchMeasurements = new ReadOnlyCollection<LanePitchMeasurement>(copiedPitches);
        RuleVersion = ruleVersion.Trim();
    }
}
