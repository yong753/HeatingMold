using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Regions;

/// <summary>작업 평면 앵커가 지정된 출구 측면 범위에 놓이는지 확인합니다.</summary>
public sealed class ExitSideResolver
{
    /// <summary>앵커에 가장 가까운 유효 출구 구간과 정규화 매개변수를 찾습니다.</summary>
    public OperationResult<ResolvedExitSideLocation> Resolve(
        HeatingRegion region,
        Vec2 workingUv,
        double toleranceMm,
        string ruleVersion = "exit-side-resolution-v1")
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("출구 측면 규칙 버전이 필요합니다.", nameof(ruleVersion));

        Dictionary<BoundarySegmentId, BoundarySegment> segments = region.EffectiveBoundaryLoops
            .SelectMany(loop => loop.Segments)
            .ToDictionary(segment => segment.Id);
        var matches = new List<ResolvedExitSideLocation>();
        foreach (BoundarySegmentRange range in region.ExitSide.Ranges)
        {
            if (!segments.TryGetValue(range.BoundarySegmentId, out BoundarySegment segment))
                continue;

            Vec2 direction = segment.End - segment.Start;
            double lengthSquared = Dot(direction, direction);
            if (lengthSquared <= 1e-24)
                continue;

            double parameter = Dot(workingUv - segment.Start, direction) / lengthSquared;
            double parameterTolerance = toleranceMm / Math.Sqrt(lengthSquared);
            if (parameter < range.StartParameter - parameterTolerance ||
                parameter > range.EndParameter + parameterTolerance)
            {
                continue;
            }

            double clampedParameter = Math.Clamp(parameter, range.StartParameter, range.EndParameter);
            Vec2 closestPoint = segment.Start + direction * clampedParameter;
            double distance = workingUv.DistanceTo(closestPoint);
            if (distance <= toleranceMm)
            {
                matches.Add(new ResolvedExitSideLocation(
                    segment.Id,
                    clampedParameter,
                    distance));
            }
        }

        if (matches.Count > 0)
        {
            ResolvedExitSideLocation best = matches
                .OrderBy(match => match.DistanceMm)
                .ThenBy(match => match.BoundarySegmentId.Value)
                .First();
            return OperationResult<ResolvedExitSideLocation>.Success(best);
        }

        var issue = new ValidationIssue(
            "AnchorOutsideExitSide",
            ValidationSeverity.Error,
            "Design.AnchorOutsideExitSide",
            [region.Id.Value],
            ruleVersion,
            datumUv: workingUv,
            requiredValue: toleranceMm,
            unit: "mm",
            evidence: ["앵커가 지정된 출구 측면의 경계 구간 범위 안에 있지 않습니다."]);
        return OperationResult<ResolvedExitSideLocation>.Failure([issue]);
    }

    private static double Dot(Vec2 first, Vec2 second) => first.X * second.X + first.Y * second.Y;
}

/// <summary>출구 경계의 안정 구간, 매개변수 위치와 측정 거리를 보관합니다.</summary>
public sealed record ResolvedExitSideLocation
{
    /// <summary>출구 측면을 구성하는 안정 경계 구간 ID입니다.</summary>
    public BoundarySegmentId BoundarySegmentId { get; }

    /// <summary>경계 구간 위의 정규화 매개변수입니다.</summary>
    public double Parameter { get; }

    /// <summary>입력 앵커와 가장 가까운 경계 위치 사이의 거리(mm)입니다.</summary>
    public double DistanceMm { get; }

    /// <summary>유효 구간과 경계 위치 측정 결과를 보관합니다.</summary>
    public ResolvedExitSideLocation(BoundarySegmentId boundarySegmentId, double parameter, double distanceMm)
    {
        if (boundarySegmentId.IsEmpty)
            throw new ArgumentException("출구 위치에는 안정 경계 구간 ID가 필요합니다.", nameof(boundarySegmentId));
        if (!double.IsFinite(parameter) || parameter < 0 || parameter > 1)
            throw new ArgumentOutOfRangeException(nameof(parameter));
        if (!double.IsFinite(distanceMm) || distanceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(distanceMm));

        BoundarySegmentId = boundarySegmentId;
        Parameter = parameter;
        DistanceMm = distanceMm;
    }
}
