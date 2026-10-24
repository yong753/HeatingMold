using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;

namespace HeatingMold.Design.HeatingPath;

/// <summary>검증과 표면 매핑을 거쳐 프로젝트에 확정할 2D 발열 경로 후보입니다.</summary>
public sealed class HeatingPathDraft
{
    /// <summary>후보가 만들어진 프로젝트 안정 ID입니다.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>후보가 기반으로 삼은 설계 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>발열 경로의 새 안정 ID입니다.</summary>
    public HeatingPathId Id { get; }

    /// <summary>후보의 유효 발열 영역 ID입니다.</summary>
    public RegionId RegionId { get; }

    /// <summary>고정 간격과 양 끝 앵커로 선택한 레인 수입니다.</summary>
    public int LaneCount { get; }

    /// <summary>후보 정렬과 끝점 리드 길이를 비교하는 점수 근거입니다.</summary>
    public HeatingPathCandidateScore Score { get; }

    /// <summary>새로 생성한 안정 절점과 작업 평면 좌표입니다.</summary>
    public IReadOnlyList<HeatingPathDraftControlPoint> ControlPoints { get; }

    /// <summary>연속 경로 순서와 표본 곡선을 가진 발열 구간입니다.</summary>
    public IReadOnlyList<HeatingPathDraftSegment> Segments { get; }

    /// <summary>안정 ID, 연결 절점, 점수와 연속 작업 평면 구간을 방어 복사합니다.</summary>
    public HeatingPathDraft(
        ProjectId projectId,
        DesignRevision baseRevision,
        HeatingPathId id,
        RegionId regionId,
        int laneCount,
        HeatingPathCandidateScore score,
        IEnumerable<HeatingPathDraftControlPoint> controlPoints,
        IEnumerable<HeatingPathDraftSegment> segments)
    {
        if (projectId.IsEmpty || id.IsEmpty || regionId.IsEmpty)
            throw new ArgumentException("경로 초안에는 프로젝트·경로·영역 안정 ID가 필요합니다.");
        if (laneCount < 1)
            throw new ArgumentOutOfRangeException(nameof(laneCount));

        HeatingPathDraftControlPoint[] points = controlPoints?.ToArray()
            ?? throw new ArgumentNullException(nameof(controlPoints));
        HeatingPathDraftSegment[] pieces = segments?.OrderBy(segment => segment.Sequence).ToArray()
            ?? throw new ArgumentNullException(nameof(segments));
        if (points.Length < 2 || points.Select(point => point.Id).Distinct().Count() != points.Length)
            throw new ArgumentException("경로 초안의 절점 ID는 둘 이상이며 고유해야 합니다.", nameof(controlPoints));
        if (pieces.Length == 0 || pieces.Select(piece => piece.Id).Distinct().Count() != pieces.Length ||
            pieces.Select(piece => piece.Sequence).Where((sequence, index) => sequence != index).Any())
        {
            throw new ArgumentException("경로 초안 구간은 고유 ID를 사용하고 0부터 연속 순서를 가져야 합니다.", nameof(segments));
        }
        if (pieces.Any(piece => !points.Any(point => point.Id == piece.StartControlPointId) ||
                                !points.Any(point => point.Id == piece.EndControlPointId)))
        {
            throw new ArgumentException("경로 초안 구간은 존재하는 공유 절점을 참조해야 합니다.", nameof(segments));
        }

        Dictionary<ControlPointId, Vec2> pointLocations = points.ToDictionary(point => point.Id, point => point.WorkingUv);
        foreach (HeatingPathDraftSegment piece in pieces)
        {
            if (piece.WorkingUvPoints[0] != pointLocations[piece.StartControlPointId] ||
                piece.WorkingUvPoints[^1] != pointLocations[piece.EndControlPointId])
            {
                throw new ArgumentException("경로 초안 곡선의 시작·끝 표본은 연결된 공유 절점과 같아야 합니다.", nameof(segments));
            }
        }
        for (int index = 1; index < pieces.Length; index++)
        {
            if (pieces[index - 1].EndControlPointId != pieces[index].StartControlPointId)
                throw new ArgumentException("경로 초안의 이웃 구간은 같은 절점을 공유해야 합니다.", nameof(segments));
        }

        ProjectId = projectId;
        BaseRevision = baseRevision;
        Id = id;
        RegionId = regionId;
        LaneCount = laneCount;
        Score = score ?? throw new ArgumentNullException(nameof(score));
        ControlPoints = new ReadOnlyCollection<HeatingPathDraftControlPoint>(points);
        Segments = new ReadOnlyCollection<HeatingPathDraftSegment>(pieces);
    }
}

/// <summary>후보의 레인 수와 정렬·리드 길이 선택 기준을 보관합니다.</summary>
public sealed record HeatingPathCandidateScore
{
    /// <summary>센터 피치(mm)로 정렬되지 않은 최대 여백입니다.</summary>
    public double MaximumUncoveredGapMm { get; }

    /// <summary>시작 앵커부터 첫 레인까지의 직선 길이(mm)입니다.</summary>
    public double StartLeadLengthMm { get; }

    /// <summary>마지막 레인부터 끝 앵커까지의 직선 길이(mm)입니다.</summary>
    public double EndLeadLengthMm { get; }

    /// <summary>동일 점수 후보를 안정적으로 정렬할 순번입니다.</summary>
    public int CandidateOrdinal { get; }

    /// <summary>유한한 길이 값과 탐색 순번으로 비교 기준을 만듭니다.</summary>
    public HeatingPathCandidateScore(
        double maximumUncoveredGapMm,
        double startLeadLengthMm,
        double endLeadLengthMm,
        int candidateOrdinal)
    {
        MaximumUncoveredGapMm = RequireNonNegative(maximumUncoveredGapMm, nameof(maximumUncoveredGapMm));
        StartLeadLengthMm = RequireNonNegative(startLeadLengthMm, nameof(startLeadLengthMm));
        EndLeadLengthMm = RequireNonNegative(endLeadLengthMm, nameof(endLeadLengthMm));
        if (candidateOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(candidateOrdinal));
        CandidateOrdinal = candidateOrdinal;
    }

    private static double RequireNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName);

        return value;
    }
}

/// <summary>경로에 새로 만든 안정 절점과 작업 평면 위치입니다.</summary>
public sealed record HeatingPathDraftControlPoint
{
    /// <summary>경로 조각이 공유하는 안정 절점 ID입니다.</summary>
    public ControlPointId Id { get; }

    /// <summary>작업 좌표계에서의 2차원 위치(mm)입니다.</summary>
    public Vec2 WorkingUv { get; }

    /// <summary>유효 절점 ID와 작업 평면 좌표를 보관합니다.</summary>
    public HeatingPathDraftControlPoint(ControlPointId id, Vec2 workingUv)
    {
        if (id.IsEmpty)
            throw new ArgumentException("경로 초안 절점 ID는 비어 있을 수 없습니다.", nameof(id));

        Id = id;
        WorkingUv = workingUv;
    }
}

/// <summary>발열·회전·접합 역할과 2차원 곡선 표본을 가진 초안 구간입니다.</summary>
public sealed class HeatingPathDraftSegment
{
    /// <summary>경로 구간의 새 안정 ID입니다.</summary>
    public HeatingPathSegmentId Id { get; }

    /// <summary>Core 경로 모델에 대응하는 구간 역할입니다.</summary>
    public HeatingPathSegmentKind Kind { get; }

    /// <summary>작업 평면에서의 시작 공유 절점입니다.</summary>
    public ControlPointId StartControlPointId { get; }

    /// <summary>작업 평면에서의 끝 공유 절점입니다.</summary>
    public ControlPointId EndControlPointId { get; }

    /// <summary>연속 경로에서의 0부터 시작하는 순서입니다.</summary>
    public int Sequence { get; }

    /// <summary>직선 레인 구간이면 그 진단 인덱스입니다.</summary>
    public int? LaneIndex { get; }

    /// <summary>작업 평면 곡선 표본의 진행 순서입니다.</summary>
    public IReadOnlyList<Vec2> WorkingUvPoints { get; }

    /// <summary>유효 ID와 공유 절점, 고유 순서, 2개 이상의 곡선 표본으로 초안 구간을 만듭니다.</summary>
    public HeatingPathDraftSegment(
        HeatingPathSegmentId id,
        HeatingPathSegmentKind kind,
        ControlPointId startControlPointId,
        ControlPointId endControlPointId,
        int sequence,
        IEnumerable<Vec2> workingUvPoints,
        int? laneIndex = null)
    {
        if (id.IsEmpty || startControlPointId.IsEmpty || endControlPointId.IsEmpty ||
            startControlPointId == endControlPointId)
        {
            throw new ArgumentException("경로 초안 구간에는 서로 다른 유효 ID와 절점이 필요합니다.");
        }
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (sequence < 0 || laneIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        Vec2[] copiedPoints = workingUvPoints?.ToArray()
            ?? throw new ArgumentNullException(nameof(workingUvPoints));
        if (copiedPoints.Length < 2 || copiedPoints.Zip(copiedPoints.Skip(1)).Any(pair => pair.First == pair.Second))
            throw new ArgumentException("경로 초안 구간에는 연속 중복이 없는 두 점 이상이 필요합니다.", nameof(workingUvPoints));

        Id = id;
        Kind = kind;
        StartControlPointId = startControlPointId;
        EndControlPointId = endControlPointId;
        Sequence = sequence;
        LaneIndex = laneIndex;
        WorkingUvPoints = new ReadOnlyCollection<Vec2>(copiedPoints);
    }
}
