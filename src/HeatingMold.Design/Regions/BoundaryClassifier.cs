using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Regions;

/// <summary>서로 닿지 않는 평면 경계를 포함 깊이로 outer·hole·island 분류합니다.</summary>
public sealed class BoundaryClassifier
{
    /// <summary>교차·접촉·자기교차가 없는 링을 even-odd 포함 깊이로 분류합니다.</summary>
    public OperationResult<IReadOnlyList<ClassifiedBoundaryRing>> Classify(
        IEnumerable<IReadOnlyList<Vec2>> rings,
        string ruleVersion = "boundary-classification-v1")
    {
        IReadOnlyList<Vec2>[] copiedRings = rings?.Select(NormalizeRing).ToArray()
            ?? throw new ArgumentNullException(nameof(rings));
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("경계 분류 규칙 버전이 필요합니다.", nameof(ruleVersion));

        var issues = new List<ValidationIssue>();
        for (int ringIndex = 0; ringIndex < copiedRings.Length; ringIndex++)
        {
            if (copiedRings[ringIndex].Count < 3 || Math.Abs(RegionOps.SignedArea(copiedRings[ringIndex])) <= 1e-12)
            {
                issues.Add(Issue("DegenerateBoundaryRing", "경계 링에는 넓이를 가진 서로 다른 정점이 세 개 이상 필요합니다.", ruleVersion));
                continue;
            }
            if (HasSelfIntersection(copiedRings[ringIndex]))
                issues.Add(Issue("SelfIntersectingBoundary", "한 경계 링이 자기 자신과 교차하거나 접합니다.", ruleVersion));
        }

        for (int first = 0; first < copiedRings.Length; first++)
        {
            if (copiedRings[first].Count < 3)
                continue;
            for (int second = first + 1; second < copiedRings.Length; second++)
            {
                if (copiedRings[second].Count >= 3 && RingsTouchOrCross(copiedRings[first], copiedRings[second]))
                    issues.Add(Issue("BoundaryLoopsTouchOrCross", "서로 다른 경계 링은 교차하거나 접할 수 없습니다.", ruleVersion));
            }
        }

        if (issues.Count > 0)
            return OperationResult<IReadOnlyList<ClassifiedBoundaryRing>>.Failure(issues);

        var classified = new ClassifiedBoundaryRing[copiedRings.Length];
        for (int ringIndex = 0; ringIndex < copiedRings.Length; ringIndex++)
        {
            Vec2 probe = copiedRings[ringIndex][0];
            int depth = copiedRings
                .Where((_, otherIndex) => otherIndex != ringIndex)
                .Count(other => PointInRing(other, probe));
            classified[ringIndex] = new ClassifiedBoundaryRing(copiedRings[ringIndex], depth);
        }

        return OperationResult<IReadOnlyList<ClassifiedBoundaryRing>>.Success(classified);
    }

    private static IReadOnlyList<Vec2> NormalizeRing(IReadOnlyList<Vec2> ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        int count = ring.Count > 1 && ring[0] == ring[^1] ? ring.Count - 1 : ring.Count;
        return ring.Take(count).ToArray();
    }

    private static bool HasSelfIntersection(IReadOnlyList<Vec2> ring)
    {
        for (int first = 0; first < ring.Count; first++)
        {
            Vec2 firstStart = ring[first];
            Vec2 firstEnd = ring[(first + 1) % ring.Count];
            for (int second = first + 1; second < ring.Count; second++)
            {
                if (second == first || second == first + 1 || first == 0 && second == ring.Count - 1)
                    continue;
                if (SegmentsTouchOrCross(firstStart, firstEnd, ring[second], ring[(second + 1) % ring.Count]))
                    return true;
            }
        }

        return false;
    }

    private static bool RingsTouchOrCross(IReadOnlyList<Vec2> first, IReadOnlyList<Vec2> second)
    {
        for (int firstIndex = 0; firstIndex < first.Count; firstIndex++)
        {
            for (int secondIndex = 0; secondIndex < second.Count; secondIndex++)
            {
                if (SegmentsTouchOrCross(
                    first[firstIndex], first[(firstIndex + 1) % first.Count],
                    second[secondIndex], second[(secondIndex + 1) % second.Count]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsTouchOrCross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        const double tolerance = 1e-10;
        double abC = Cross(b - a, c - a);
        double abD = Cross(b - a, d - a);
        double cdA = Cross(d - c, a - c);
        double cdB = Cross(d - c, b - c);

        if (Math.Abs(abC) <= tolerance && IsWithinBounds(c, a, b, tolerance) ||
            Math.Abs(abD) <= tolerance && IsWithinBounds(d, a, b, tolerance) ||
            Math.Abs(cdA) <= tolerance && IsWithinBounds(a, c, d, tolerance) ||
            Math.Abs(cdB) <= tolerance && IsWithinBounds(b, c, d, tolerance))
        {
            return true;
        }

        return (abC > 0) != (abD > 0) && (cdA > 0) != (cdB > 0);
    }

    private static bool IsWithinBounds(Vec2 point, Vec2 start, Vec2 end, double tolerance) =>
        point.X >= Math.Min(start.X, end.X) - tolerance &&
        point.X <= Math.Max(start.X, end.X) + tolerance &&
        point.Y >= Math.Min(start.Y, end.Y) - tolerance &&
        point.Y <= Math.Max(start.Y, end.Y) + tolerance;

    private static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int current = 0, previous = ring.Count - 1; current < ring.Count; previous = current++)
        {
            Vec2 a = ring[current];
            Vec2 b = ring[previous];
            bool crosses = (a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (crosses)
                inside = !inside;
        }

        return inside;
    }

    private static double Cross(Vec2 first, Vec2 second) => first.X * second.Y - first.Y * second.X;

    private static ValidationIssue Issue(string code, string evidence, string ruleVersion) => new(
        code,
        ValidationSeverity.Error,
        $"Design.{code}",
        [],
        ruleVersion,
        evidence: [evidence]);
}

/// <summary>한 링의 입력 좌표와 바깥 링으로부터의 포함 깊이를 보관합니다.</summary>
public sealed class ClassifiedBoundaryRing
{
    /// <summary>닫힘 정점을 중복 저장하지 않는 평면 좌표입니다.</summary>
    public IReadOnlyList<Vec2> Vertices { get; }

    /// <summary>다른 링 안에 포함된 횟수입니다.</summary>
    public int NestingDepth { get; }

    /// <summary>포함 깊이가 홀수면 제외 경계입니다.</summary>
    public bool IsHole => NestingDepth % 2 == 1;

    /// <summary>유효한 분류 깊이와 좌표를 방어 복사합니다.</summary>
    public ClassifiedBoundaryRing(IEnumerable<Vec2> vertices, int nestingDepth)
    {
        Vec2[] copiedVertices = vertices?.ToArray() ?? throw new ArgumentNullException(nameof(vertices));
        if (copiedVertices.Length < 3 || nestingDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(nestingDepth));

        Vertices = Array.AsReadOnly(copiedVertices);
        NestingDepth = nestingDepth;
    }
}
