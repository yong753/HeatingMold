using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Validation;

namespace HeatingMold.Design.HeatingPath;

/// <summary>이웃 사행 레인을 잇는 장애물 우회 없는 제한 U턴 곡선을 만듭니다.</summary>
public sealed class TurnBuilder
{
    private const double CircleTangentFactor = 0.2761423749153967;

    /// <summary>Bezier 분할에서 사용하는 최대 국소 세분화 깊이입니다.</summary>
    public const int MaximumSubdivisionDepth = 18;

    /// <summary>반지름 pitch/2 U턴의 접선 연결에 확보할 레인 방향 길이입니다.</summary>
    public static double RequiredTangentExtension(double centerPitchMm)
    {
        if (!double.IsFinite(centerPitchMm) || centerPitchMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(centerPitchMm));

        return centerPitchMm * CircleTangentFactor;
    }

    /// <summary>회전 곡선을 현 편차와 표본 간격까지 분할한 순서 점을 반환합니다.</summary>
    public OperationResult<TurnBuildResult> Build(
        Vec2 start,
        Vec2 end,
        bool previousLaneForward,
        double centerPitchMm,
        double maximumDeviationMm,
        double maximumSampleSpacingMm,
        string ruleVersion = "turn-builder-v1")
    {
        double tangentExtension = RequiredTangentExtension(centerPitchMm);
        RequirePositive(maximumDeviationMm, nameof(maximumDeviationMm));
        RequirePositive(maximumSampleSpacingMm, nameof(maximumSampleSpacingMm));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("회전 생성 규칙 버전이 필요합니다.", nameof(ruleVersion));
        if (Math.Abs(end.Y - start.Y) <= 1e-9)
            return Failure("TurnEndpointsShareLane", "회전 양 끝점은 서로 다른 레인 높이에 있어야 합니다.", ruleVersion);

        double direction = previousLaneForward ? 1 : -1;
        double tangentOffset = Math.Max(
            RequiredTangentExtension(centerPitchMm),
            Math.Abs(end.X - start.X) / 2 + RequiredTangentExtension(centerPitchMm));
        Vec2 firstControl = start + new Vec2(direction * tangentOffset, 0);
        Vec2 secondControl = end + new Vec2(direction * tangentOffset, 0);
        var points = new List<Vec2> { start };
        bool resolved = Subdivide(start, firstControl, secondControl, end, 0);
        if (!resolved)
        {
            return Failure(
                "TurnApproximationUnresolved",
                "회전 곡선이 세분화 한도 안에서 현 편차와 표본 간격을 만족하지 못했습니다.",
                ruleVersion);
        }

        return OperationResult<TurnBuildResult>.Success(new TurnBuildResult(points, maximumDeviationMm));

        bool Subdivide(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, int depth)
        {
            double flatness = Math.Max(
                DistanceToLine(p1, p0, p3),
                DistanceToLine(p2, p0, p3));
            double chordLength = p0.DistanceTo(p3);
            if (flatness <= maximumDeviationMm && chordLength <= maximumSampleSpacingMm)
            {
                points.Add(p3);
                return true;
            }
            if (depth >= MaximumSubdivisionDepth)
                return false;

            Vec2 p01 = Midpoint(p0, p1);
            Vec2 p12 = Midpoint(p1, p2);
            Vec2 p23 = Midpoint(p2, p3);
            Vec2 p012 = Midpoint(p01, p12);
            Vec2 p123 = Midpoint(p12, p23);
            Vec2 middle = Midpoint(p012, p123);
            if (middle == p0 || middle == p3)
                return false;

            return Subdivide(p0, p01, p012, middle, depth + 1) &&
                Subdivide(middle, p123, p23, p3, depth + 1);
        }
    }

    private static double DistanceToLine(Vec2 point, Vec2 start, Vec2 end)
    {
        Vec2 direction = end - start;
        double length = direction.Length;
        if (length <= 1e-12)
            return point.DistanceTo(start);

        Vec2 offset = point - start;
        return Math.Abs(offset.X * direction.Y - offset.Y * direction.X) / length;
    }

    private static Vec2 Midpoint(Vec2 first, Vec2 second) =>
        new((first.X + second.X) / 2, (first.Y + second.Y) / 2);

    private static void RequirePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "회전 근사 공차는 양의 유한값이어야 합니다.");
    }

    private static OperationResult<TurnBuildResult> Failure(string code, string evidence, string ruleVersion) =>
        OperationResult<TurnBuildResult>.Failure(
        [ValidationIssueFactory.Error(code, evidence, ruleVersion)]);
}

/// <summary>U턴 곡선 표본과 그 표본을 승인한 최대 편차를 보관합니다.</summary>
public sealed class TurnBuildResult
{
    /// <summary>시작점에서 끝점까지 진행 순서를 가진 작업 평면 표본입니다.</summary>
    public IReadOnlyList<Vec2> Points { get; }

    /// <summary>생성에 적용한 최대 현 편차 한도(mm)입니다.</summary>
    public double MaximumDeviationMm { get; }

    /// <summary>최소 두 점과 유효 오차 한도를 방어 복사합니다.</summary>
    public TurnBuildResult(IEnumerable<Vec2> points, double maximumDeviationMm)
    {
        Vec2[] copiedPoints = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
        if (copiedPoints.Length < 2 || copiedPoints.Zip(copiedPoints.Skip(1)).Any(pair => pair.First == pair.Second))
            throw new ArgumentException("U턴 표본에는 연속 중복이 없는 두 점 이상이 필요합니다.", nameof(points));
        if (!double.IsFinite(maximumDeviationMm) || maximumDeviationMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDeviationMm));

        Points = Array.AsReadOnly(copiedPoints);
        MaximumDeviationMm = maximumDeviationMm;
    }
}
