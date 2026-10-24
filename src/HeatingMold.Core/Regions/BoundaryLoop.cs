using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Regions;

/// <summary>작업 평면의 닫힌 직선 경계와 안정 구간 ID를 보관합니다.</summary>
public sealed class BoundaryLoop
{
    /// <summary>경계 루프의 영속 ID입니다.</summary>
    public BoundaryLoopId Id { get; }

    /// <summary>루프가 유효 영역의 홀인지 나타냅니다.</summary>
    public bool IsHole { get; }

    /// <summary>연결 순서가 보존된 경계 구간입니다.</summary>
    public IReadOnlyList<BoundarySegment> Segments { get; }

    /// <summary>경계 구간 시작점 순서로 만든 작업 평면 정점입니다.</summary>
    public IReadOnlyList<Vec2> Vertices { get; }

    /// <summary>루프가 감싸는 부호 있는 작업 평면 면적(mm²)입니다.</summary>
    public double SignedAreaMm2 { get; }

    /// <summary>유한한 오차 범위 안에서 닫히고 넓이가 있는 경계를 만듭니다.</summary>
    public BoundaryLoop(
        BoundaryLoopId id,
        IEnumerable<BoundarySegment> segments,
        bool isHole,
        double closureToleranceMm = 0.01)
    {
        if (id.IsEmpty)
            throw new ArgumentException("경계 루프 ID는 비어 있을 수 없습니다.", nameof(id));
        if (!double.IsFinite(closureToleranceMm) || closureToleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(closureToleranceMm));

        BoundarySegment[] copiedSegments = segments?.ToArray()
            ?? throw new ArgumentNullException(nameof(segments));
        if (copiedSegments.Length < 3)
            throw new ArgumentException("닫힌 경계에는 세 구간 이상이 필요합니다.", nameof(segments));

        var segmentIds = new HashSet<BoundarySegmentId>();
        for (int index = 0; index < copiedSegments.Length; index++)
        {
            BoundarySegment current = copiedSegments[index];
            BoundarySegment next = copiedSegments[(index + 1) % copiedSegments.Length];
            if (current.Id.IsEmpty || !segmentIds.Add(current.Id))
                throw new ArgumentException("경계 구간 ID는 비어 있거나 중복될 수 없습니다.", nameof(segments));
            if (current.Start.DistanceTo(current.End) <= closureToleranceMm)
                throw new ArgumentException("길이가 0인 경계 구간은 허용되지 않습니다.", nameof(segments));
            if (current.End.DistanceTo(next.Start) > closureToleranceMm)
                throw new ArgumentException("경계 구간은 지정 순서에서 닫힌 루프를 이루어야 합니다.", nameof(segments));
        }

        Vec2[] vertices = copiedSegments.Select(segment => segment.Start).ToArray();
        double signedArea = CalculateSignedArea(vertices);
        if (!double.IsFinite(signedArea) || Math.Abs(signedArea) <= closureToleranceMm * closureToleranceMm)
            throw new ArgumentException("경계 루프는 유한하며 0이 아닌 면적을 가져야 합니다.", nameof(segments));

        Id = id;
        IsHole = isHole;
        Segments = new ReadOnlyCollection<BoundarySegment>(copiedSegments);
        Vertices = new ReadOnlyCollection<Vec2>(vertices);
        SignedAreaMm2 = signedArea;
    }

    private static double CalculateSignedArea(IReadOnlyList<Vec2> vertices)
    {
        double twiceArea = 0;
        for (int index = 0; index < vertices.Count; index++)
        {
            Vec2 current = vertices[index];
            Vec2 next = vertices[(index + 1) % vertices.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }

        return twiceArea / 2;
    }
}

/// <summary>경계의 한 직선 구간과 선택적으로 원본 Face 대응을 나타냅니다.</summary>
public readonly record struct BoundarySegment
{
    /// <summary>경계 구간의 안정 ID입니다.</summary>
    public BoundarySegmentId Id { get; }

    /// <summary>작업 평면 좌표의 시작점(mm)입니다.</summary>
    public Vec2 Start { get; }

    /// <summary>작업 평면 좌표의 끝점(mm)입니다.</summary>
    public Vec2 End { get; }

    /// <summary>원본 Face가 있으면 그 영속 참조 ID입니다.</summary>
    public FaceRefId? SourceFaceReferenceId { get; }

    /// <summary>유효한 ID와 서로 다른 끝점으로 경계 구간을 만듭니다.</summary>
    public BoundarySegment(
        BoundarySegmentId id,
        Vec2 start,
        Vec2 end,
        FaceRefId? sourceFaceReferenceId = null)
    {
        if (id.IsEmpty)
            throw new ArgumentException("경계 구간 ID는 비어 있을 수 없습니다.", nameof(id));
        if (start == end)
            throw new ArgumentException("경계 구간의 시작점과 끝점은 달라야 합니다.");
        if (sourceFaceReferenceId is { IsEmpty: true })
            throw new ArgumentException("원본 Face 참조 ID는 비어 있을 수 없습니다.", nameof(sourceFaceReferenceId));

        Id = id;
        Start = start;
        End = end;
        SourceFaceReferenceId = sourceFaceReferenceId;
    }
}
