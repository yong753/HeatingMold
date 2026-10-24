using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Regions;

/// <summary>선택 표면과 포함·제외 도형의 Boolean 결과로 새 영역 후보를 만듭니다.</summary>
public sealed class RegionAdjustmentService
{
    private readonly BoundaryClassifier _boundaryClassifier;
    private readonly int _precision;

    /// <summary>Boolean 결과 분류기와 명시된 작업 평면 정밀도를 설정합니다.</summary>
    public RegionAdjustmentService(BoundaryClassifier? boundaryClassifier = null, int precision = 6)
    {
        if (precision < 0 || precision > 8)
            throw new ArgumentOutOfRangeException(nameof(precision), "작업 영역 정밀도는 0부터 8 사이여야 합니다.");

        _boundaryClassifier = boundaryClassifier ?? new BoundaryClassifier();
        _precision = precision;
    }

    /// <summary>포함은 선택 표면 안으로 자르고 제외는 뺀 뒤 출구 측면도 유지 가능한지 확인합니다.</summary>
    public OperationResult<HeatingRegion> Apply(
        HeatingRegion region,
        IEnumerable<IReadOnlyList<Vec2>> inclusionPolygons,
        IEnumerable<IReadOnlyList<Vec2>> exclusionPolygons,
        string ruleVersion = "region-adjustment-v1")
    {
        ArgumentNullException.ThrowIfNull(region);
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("영역 조정 규칙 버전이 필요합니다.", nameof(ruleVersion));

        OperationResult<IReadOnlyList<IReadOnlyList<Vec2>>> inclusionResult = CopyAndValidatePolygons(
            region, inclusionPolygons, "inclusionPolygons", ruleVersion);
        if (!inclusionResult.IsSuccess)
            return OperationResult<HeatingRegion>.Failure(inclusionResult.Issues);
        OperationResult<IReadOnlyList<IReadOnlyList<Vec2>>> exclusionResult = CopyAndValidatePolygons(
            region, exclusionPolygons, "exclusionPolygons", ruleVersion);
        if (!exclusionResult.IsSuccess)
            return OperationResult<HeatingRegion>.Failure(exclusionResult.Issues);
        IReadOnlyList<IReadOnlyList<Vec2>> inclusions = inclusionResult.GetValueOrThrow();
        IReadOnlyList<IReadOnlyList<Vec2>> exclusions = exclusionResult.GetValueOrThrow();

        IReadOnlyList<Vec2>[] originalRings = OrientByHole(region.OriginalBoundaryLoops);
        List<List<Vec2>> selectedSurface = RegionOps.DifferencePolygons(originalRings, [], _precision);
        List<List<Vec2>> effectiveRings = inclusions.Count == 0
            ? selectedSurface
            : RegionOps.IntersectPolygons(selectedSurface, inclusions, _precision);
        if (exclusions.Count > 0)
            effectiveRings = RegionOps.DifferencePolygons(effectiveRings, exclusions, _precision);
        if (effectiveRings.Count == 0)
            return Failure(region, "EmptyEffectiveRegion", "포함·제외를 적용한 결과에 발열 영역이 남지 않았습니다.", ruleVersion);

        OperationResult<IReadOnlyList<ClassifiedBoundaryRing>> classification = _boundaryClassifier.Classify(
            effectiveRings,
            ruleVersion);
        if (!classification.IsSuccess)
            return OperationResult<HeatingRegion>.Failure(classification.Issues);

        BoundaryLoop[] effectiveLoops = classification.GetValueOrThrow()
            .Select(CreateLoop)
            .ToArray();
        ExitSideReference? remappedExitSide = RemapExitSide(region, effectiveLoops);
        if (remappedExitSide is null)
        {
            return Failure(region, "ExitSideRemovedByAdjustment",
                "영역 조정으로 기존 출구 측면이 사라져 새 측면을 선택해야 합니다.",
                ruleVersion);
        }

        BoundaryLoop[] inclusionLoops = inclusions.Select(polygon => CreateLoop(new ClassifiedBoundaryRing(polygon, 0))).ToArray();
        BoundaryLoop[] exclusionLoops = exclusions.Select(polygon => CreateLoop(new ClassifiedBoundaryRing(polygon, 0))).ToArray();
        var adjustedRegion = new HeatingRegion(
            region.Id,
            region.FaceReferences,
            region.OriginalBoundaryLoops,
            inclusionLoops,
            exclusionLoops,
            effectiveLoops,
            remappedExitSide);
        return OperationResult<HeatingRegion>.Success(adjustedRegion);
    }

    private OperationResult<IReadOnlyList<IReadOnlyList<Vec2>>> CopyAndValidatePolygons(
        HeatingRegion region,
        IEnumerable<IReadOnlyList<Vec2>> polygons,
        string parameterName,
        string ruleVersion)
    {
        IReadOnlyList<Vec2>[] copied = polygons?.Select(polygon =>
        {
            ArgumentNullException.ThrowIfNull(polygon);
            Vec2[] points = polygon.ToArray();
            if (points.Length > 1 && points[0] == points[^1])
                points = points[..^1];
            return (IReadOnlyList<Vec2>)points;
        }).ToArray() ?? throw new ArgumentNullException(parameterName);

        foreach (IReadOnlyList<Vec2> polygon in copied)
        {
            OperationResult<IReadOnlyList<ClassifiedBoundaryRing>> result = _boundaryClassifier.Classify(
                [polygon],
                "region-input-validation-v1");
            if (!result.IsSuccess)
                return OperationResult<IReadOnlyList<IReadOnlyList<Vec2>>>.Failure(
                    result.Issues.Select(issue => new ValidationIssue(
                        issue.Code,
                        issue.Severity,
                        issue.MessageKey,
                        [region.Id.Value],
                        ruleVersion,
                        evidence: issue.Evidence)));
        }

        return OperationResult<IReadOnlyList<IReadOnlyList<Vec2>>>.Success(copied);
    }

    private static IReadOnlyList<Vec2>[] OrientByHole(IEnumerable<BoundaryLoop> loops) => loops
        .Select(loop => Orient(loop.Vertices, shouldBeCounterClockwise: !loop.IsHole))
        .ToArray();

    private static IReadOnlyList<Vec2> Orient(IReadOnlyList<Vec2> ring, bool shouldBeCounterClockwise)
    {
        bool isCounterClockwise = RegionOps.SignedArea(ring) > 0;
        return isCounterClockwise == shouldBeCounterClockwise
            ? ring
            : ring.Reverse().ToArray();
    }

    private static BoundaryLoop CreateLoop(ClassifiedBoundaryRing classifiedRing)
    {
        Vec2[] vertices = classifiedRing.Vertices.ToArray();
        BoundarySegment[] segments = vertices.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(),
            start,
            vertices[(index + 1) % vertices.Length])).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, classifiedRing.IsHole);
    }

    private static ExitSideReference? RemapExitSide(
        HeatingRegion region,
        IReadOnlyList<BoundaryLoop> effectiveLoops)
    {
        var sourceRanges = new List<(Vec2 Start, Vec2 End)>();
        Dictionary<BoundarySegmentId, BoundarySegment> sourceSegments = region.EffectiveBoundaryLoops
            .SelectMany(loop => loop.Segments)
            .ToDictionary(segment => segment.Id);
        foreach (BoundarySegmentRange range in region.ExitSide.Ranges)
        {
            if (!sourceSegments.TryGetValue(range.BoundarySegmentId, out BoundarySegment source))
                return null;

            Vec2 direction = source.End - source.Start;
            sourceRanges.Add((
                source.Start + direction * range.StartParameter,
                source.Start + direction * range.EndParameter));
        }

        var remappedBySegment = new Dictionary<BoundarySegmentId, (BoundarySegment Segment, List<(double Start, double End)> Ranges)>();
        foreach (BoundarySegment segment in effectiveLoops.SelectMany(loop => loop.Segments))
        {
            foreach ((Vec2 start, Vec2 end) in sourceRanges)
            {
                if (!TryGetOverlap(segment.Start, segment.End, start, end, out double overlapStart, out double overlapEnd))
                    continue;

                if (!remappedBySegment.TryGetValue(segment.Id, out var current))
                {
                    current = (segment, []);
                    remappedBySegment.Add(segment.Id, current);
                }

                current.Ranges.Add((overlapStart, overlapEnd));
            }
        }

        var remappedRanges = new List<BoundarySegmentRange>();
        foreach (var mapped in remappedBySegment.Values.OrderBy(value => value.Segment.Id.Value))
        {
            (double Start, double End)[] merged = MergeIntervals(mapped.Ranges);
            // 한 세그먼트 안에 끊어진 출구 구간은 하나의 범위로 안전하게 표현할 수 없습니다.
            if (merged.Length != 1)
                return null;

            remappedRanges.Add(new BoundarySegmentRange(mapped.Segment.Id, merged[0].Start, merged[0].End));
        }

        return remappedRanges.Count == 0 ? null : new ExitSideReference(remappedRanges);
    }

    private static (double Start, double End)[] MergeIntervals(IEnumerable<(double Start, double End)> intervals)
    {
        const double tolerance = 1e-6;
        var merged = new List<(double Start, double End)>();
        foreach ((double start, double end) in intervals.OrderBy(interval => interval.Start))
        {
            if (merged.Count == 0 || start > merged[^1].End + tolerance)
            {
                merged.Add((start, end));
                continue;
            }

            merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
        }

        return merged.ToArray();
    }

    private static bool TryGetOverlap(
        Vec2 segmentStart,
        Vec2 segmentEnd,
        Vec2 rangeStart,
        Vec2 rangeEnd,
        out double overlapStart,
        out double overlapEnd)
    {
        const double tolerance = 1e-6;
        Vec2 range = rangeEnd - rangeStart;
        double rangeLengthSquared = range.X * range.X + range.Y * range.Y;
        if (rangeLengthSquared <= tolerance * tolerance)
        {
            overlapStart = 0;
            overlapEnd = 0;
            return false;
        }

        double rangeStartProjection = Dot(segmentStart - rangeStart, range) / rangeLengthSquared;
        double rangeEndProjection = Dot(segmentEnd - rangeStart, range) / rangeLengthSquared;
        double rangeStartError = Math.Abs(Cross(segmentStart - rangeStart, range)) / Math.Sqrt(rangeLengthSquared);
        double rangeEndError = Math.Abs(Cross(segmentEnd - rangeStart, range)) / Math.Sqrt(rangeLengthSquared);
        if (rangeStartError > tolerance || rangeEndError > tolerance)
        {
            overlapStart = 0;
            overlapEnd = 0;
            return false;
        }

        double overlapLow = Math.Max(0, Math.Min(rangeStartProjection, rangeEndProjection));
        double overlapHigh = Math.Min(1, Math.Max(rangeStartProjection, rangeEndProjection));
        if (overlapHigh - overlapLow <= tolerance)
        {
            overlapStart = 0;
            overlapEnd = 0;
            return false;
        }

        if (rangeEndProjection > rangeStartProjection)
        {
            overlapStart = (overlapLow - rangeStartProjection) / (rangeEndProjection - rangeStartProjection);
            overlapEnd = (overlapHigh - rangeStartProjection) / (rangeEndProjection - rangeStartProjection);
        }
        else
        {
            overlapStart = (rangeStartProjection - overlapHigh) / (rangeStartProjection - rangeEndProjection);
            overlapEnd = (rangeStartProjection - overlapLow) / (rangeStartProjection - rangeEndProjection);
        }

        overlapStart = Math.Clamp(overlapStart, 0, 1);
        overlapEnd = Math.Clamp(overlapEnd, 0, 1);
        return overlapEnd - overlapStart > tolerance;
    }

    private static double Dot(Vec2 first, Vec2 second) => first.X * second.X + first.Y * second.Y;
    private static double Cross(Vec2 first, Vec2 second) => first.X * second.Y - first.Y * second.X;

    private static OperationResult<HeatingRegion> Failure(
        HeatingRegion region,
        string code,
        string evidence,
        string ruleVersion) => OperationResult<HeatingRegion>.Failure(
        [new ValidationIssue(
            code,
            ValidationSeverity.Error,
            $"Design.{code}",
            [region.Id.Value],
            ruleVersion,
            evidence: [evidence])]);
}
