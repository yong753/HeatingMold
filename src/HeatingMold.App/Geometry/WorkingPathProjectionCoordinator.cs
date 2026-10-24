using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Geometry;

/// <summary>Native 작업 좌표계에 투영할 독립 경로 또는 편집 가능한 경로 조각입니다.</summary>
internal sealed class WorkingPathProjectionCandidate
{
    /// <summary>회로 분할·끝점 편집을 지원하는 경로 조각인지 나타냅니다.</summary>
    public bool IsPathSegment { get; }

    /// <summary>편집 가능한 경로 조각의 Native 표시 역할입니다.</summary>
    public string Kind { get; }

    /// <summary>투영 좌표계에서 정의한 경로 점입니다.</summary>
    public IReadOnlyList<Vec2> Points { get; }

    /// <summary>작업 모드와 경로 점을 검사해 불변 투영 후보를 만듭니다.</summary>
    private WorkingPathProjectionCandidate(bool isPathSegment, string kind, IEnumerable<Vec2> points)
    {
        Vec2[] copiedPoints = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
        if (copiedPoints.Length < 2)
            throw new ArgumentException("경로 후보에는 서로 다른 두 점 이상이 필요합니다.", nameof(points));
        if (isPathSegment && string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("편집 가능한 경로 조각에는 표시 역할이 필요합니다.", nameof(kind));

        IsPathSegment = isPathSegment;
        Kind = kind ?? string.Empty;
        Points = new ReadOnlyCollection<Vec2>(copiedPoints);
    }

    /// <summary>회로 편집 대상 경로 조각 후보를 만듭니다.</summary>
    public static WorkingPathProjectionCandidate PathSegment(string kind, IEnumerable<Vec2> points) =>
        new(true, kind, points);

    /// <summary>제외 영역 등 독립 다중선 경로 후보를 만듭니다.</summary>
    public static WorkingPathProjectionCandidate Polyline(IEnumerable<Vec2> points) =>
        new(false, string.Empty, points);
}

/// <summary>현재 Native context에서 함께 staging하고 확정할 경로 후보 묶음입니다.</summary>
internal sealed class WorkingPathProjectionRequest
{
    /// <summary>투영 후보가 소유한 활성 작업 좌표계입니다.</summary>
    public WorkingCoordinateSystem CoordinateSystem { get; }

    /// <summary>2D 경로와 모델 좌표를 변환하는 투영 프레임입니다.</summary>
    public ProjectionFrame ProjectionFrame { get; }

    /// <summary>투영을 허용할 현재 문서의 Native Face 번호입니다.</summary>
    public IReadOnlyList<int> SelectedFaceIndices { get; }

    /// <summary>작업 평면 경로를 밀어 넣을 단위 방향입니다.</summary>
    public Vec3 ProjectionDirection { get; }

    /// <summary>순서대로 staging할 경로 후보입니다.</summary>
    public IReadOnlyList<WorkingPathProjectionCandidate> Candidates { get; }

    /// <summary>연속 경로 조각의 끝점 허용 오차(mm)입니다.</summary>
    public double EndpointToleranceMm { get; }

    /// <summary>문서·좌표계·Face·경로 집합의 일관성을 확인해 요청을 만듭니다.</summary>
    public WorkingPathProjectionRequest(
        WorkingCoordinateSystem coordinateSystem,
        ProjectionFrame projectionFrame,
        IEnumerable<int> selectedFaceIndices,
        Vec3 projectionDirection,
        IEnumerable<WorkingPathProjectionCandidate> candidates,
        double endpointToleranceMm = 0.01)
    {
        CoordinateSystem = coordinateSystem ?? throw new ArgumentNullException(nameof(coordinateSystem));
        ProjectionFrame = projectionFrame ?? throw new ArgumentNullException(nameof(projectionFrame));
        if (coordinateSystem.Id.Value <= 0)
            throw new ArgumentException("투영에는 유효한 Native 작업 좌표계 ID가 필요합니다.", nameof(coordinateSystem));
        if (!double.IsFinite(endpointToleranceMm) || endpointToleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(endpointToleranceMm));

        int[] copiedFaceIndices = selectedFaceIndices?.ToArray()
            ?? throw new ArgumentNullException(nameof(selectedFaceIndices));
        if (copiedFaceIndices.Length == 0 || copiedFaceIndices.Any(index => index < 0) ||
            copiedFaceIndices.Distinct().Count() != copiedFaceIndices.Length)
        {
            throw new ArgumentException("Native Face 번호는 중복 없이 하나 이상 지정해야 합니다.", nameof(selectedFaceIndices));
        }

        WorkingPathProjectionCandidate[] copiedCandidates = candidates?.ToArray()
            ?? throw new ArgumentNullException(nameof(candidates));
        if (copiedCandidates.Length == 0 || copiedCandidates.Any(candidate => candidate is null))
            throw new ArgumentException("투영할 경로 후보가 하나 이상 필요합니다.", nameof(candidates));
        if (copiedCandidates.Any(candidate => candidate.IsPathSegment) &&
            copiedCandidates.Any(candidate => !candidate.IsPathSegment))
        {
            throw new ArgumentException("편집 경로 조각과 독립 다중선은 한 번의 확정에서 섞을 수 없습니다.", nameof(candidates));
        }

        SelectedFaceIndices = new ReadOnlyCollection<int>(copiedFaceIndices);
        ProjectionDirection = projectionDirection.Normalized();
        Candidates = new ReadOnlyCollection<WorkingPathProjectionCandidate>(copiedCandidates);
        EndpointToleranceMm = endpointToleranceMm;
    }
}

/// <summary>Native에서 측정해 반환한 최종 경로 조각 길이와 끝점입니다.</summary>
internal sealed record WorkingPathSegmentMeasurement
{
    /// <summary>최종 Native 곡선 길이(mm)입니다.</summary>
    public double LengthMm { get; }

    /// <summary>최종 Native 곡선의 시작 모델 좌표입니다.</summary>
    public Vec3 Start { get; }

    /// <summary>최종 Native 곡선의 끝 모델 좌표입니다.</summary>
    public Vec3 End { get; }

    /// <summary>Native 최종 곡선에서 순서대로 얻은 표본입니다.</summary>
    public IReadOnlyList<Vec3> Samples { get; }

    /// <summary>표본 생성의 오차와 완료 여부이며, 레거시 표시 경로에는 없을 수 있습니다.</summary>
    public CurveApproximationInfo Approximation { get; }

    /// <summary>길이와 끝점에 Native 표본 증거를 선택적으로 결합합니다.</summary>
    public WorkingPathSegmentMeasurement(
        double lengthMm,
        Vec3 start,
        Vec3 end,
        IEnumerable<Vec3> samples = null,
        CurveApproximationInfo approximation = null)
    {
        if (!double.IsFinite(lengthMm) || lengthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(lengthMm), "투영 경로 길이는 양의 유한값이어야 합니다.");

        Vec3[] copiedSamples = samples?.ToArray() ?? new[] { start, end };
        if (copiedSamples.Length < 2 || copiedSamples[0] != start || copiedSamples[^1] != end)
            throw new ArgumentException("곡선 표본은 측정된 시작점과 끝점으로 시작하고 끝나야 합니다.", nameof(samples));

        LengthMm = lengthMm;
        Start = start;
        End = end;
        Samples = new ReadOnlyCollection<Vec3>(copiedSamples);
        Approximation = approximation;
    }
}

/// <summary>경로 후보 묶음의 staging·검증·확정 결과입니다.</summary>
internal sealed class WorkingPathProjectionResult
{
    /// <summary>Native 경로 기하와 표시를 확정했는지 나타냅니다.</summary>
    public bool IsSuccess { get; }

    /// <summary>확정한 독립 경로 또는 편집 경로 조각 수입니다.</summary>
    public int ProjectedPathCount { get; }

    /// <summary>Native가 측정한 경로 길이 합계(mm)입니다.</summary>
    public double TotalLengthMm { get; }

    /// <summary>회로 편집에 사용할 순서 있는 Native 조각 측정값입니다.</summary>
    public IReadOnlyList<WorkingPathSegmentMeasurement> Segments { get; }

    /// <summary>실패 원인이며 확정 성공 시 빈 문자열입니다.</summary>
    public string FailureMessage { get; }

    private WorkingPathProjectionResult(
        bool isSuccess,
        int projectedPathCount,
        double totalLengthMm,
        IEnumerable<WorkingPathSegmentMeasurement> segments,
        string failureMessage)
    {
        IsSuccess = isSuccess;
        ProjectedPathCount = projectedPathCount;
        TotalLengthMm = totalLengthMm;
        Segments = new ReadOnlyCollection<WorkingPathSegmentMeasurement>(segments.ToArray());
        FailureMessage = failureMessage;
    }

    /// <summary>확정된 경로와 Native 측정 결과를 만듭니다.</summary>
    public static WorkingPathProjectionResult Success(
        int projectedPathCount,
        double totalLengthMm,
        IEnumerable<WorkingPathSegmentMeasurement> segments) =>
        new(true, projectedPathCount, totalLengthMm, segments, string.Empty);

    /// <summary>확정된 후보가 없음을 나타내는 실패 결과를 만듭니다.</summary>
    public static WorkingPathProjectionResult Failure(string message) =>
        new(false, 0, 0, Array.Empty<WorkingPathSegmentMeasurement>(), message);
}

/// <summary>경로 후보를 Native에 staging하고 전체 검증을 통과한 묶음만 확정합니다.</summary>
internal sealed class WorkingPathProjectionCoordinator
{
    private readonly IWorkingPathProjectionBridge _bridge;

    /// <summary>Native 경로 조립 경계를 주입해 후보 처리기를 만듭니다.</summary>
    public WorkingPathProjectionCoordinator(IWorkingPathProjectionBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>모든 경로를 준비한 뒤 연속성과 길이를 검증하고 한 번에 확정합니다.</summary>
    public WorkingPathProjectionResult Project(WorkingPathProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool hasEditableSegments = request.Candidates[0].IsPathSegment;
        var stagedSegmentIds = new List<int>();
        var stagedPolylineIds = new List<int>();
        var measurements = new List<WorkingPathSegmentMeasurement>();
        double totalLengthMm = 0;

        try
        {
            for (int index = 0; index < request.Candidates.Count; index++)
            {
                WorkingPathProjectionCandidate candidate = request.Candidates[index];
                Vec3[] modelPoints = candidate.Points
                    .Select(request.ProjectionFrame.Unproject)
                    .ToArray();

                WorkingPathSegmentMeasurement measurement;
                if (hasEditableSegments)
                {
                    int stagingId = int.MinValue + index;
                    stagedSegmentIds.Add(stagingId);
                    measurement = _bridge.StageProjectedPathSegment(
                        request.CoordinateSystem,
                        stagingId,
                        candidate.Kind,
                        modelPoints,
                        request.SelectedFaceIndices,
                        request.ProjectionDirection);
                }
                else
                {
                    int stagingId = int.MinValue / 2 + index;
                    stagedPolylineIds.Add(stagingId);
                    measurement = _bridge.StageProjectedPolyline(
                        request.CoordinateSystem,
                        stagingId,
                        modelPoints,
                        request.SelectedFaceIndices,
                        request.ProjectionDirection);
                }

                if (measurement is null)
                    throw new InvalidOperationException($"경로 {index + 1}의 Native staging이 측정값을 반환하지 않았습니다.");
                if (!double.IsFinite(measurement.LengthMm) || measurement.LengthMm <= 0)
                    throw new InvalidOperationException($"경로 {index + 1}의 Native 길이가 양의 유한값이 아닙니다.");

                if (hasEditableSegments)
                    measurements.Add(measurement);

                totalLengthMm += measurement.LengthMm;
                if (!double.IsFinite(totalLengthMm))
                    throw new ArithmeticException("Native 경로 길이 합계가 숫자 범위를 벗어났습니다.");
            }

            if (hasEditableSegments)
                ValidateContinuity(measurements, request.EndpointToleranceMm);

            int[] finalSegmentIds = hasEditableSegments
                ? Enumerable.Range(0, request.Candidates.Count).ToArray()
                : Array.Empty<int>();
            if (!_bridge.CommitWorkingPathGeometry(
                    request.CoordinateSystem,
                    stagedSegmentIds,
                    finalSegmentIds,
                    stagedPolylineIds))
            {
                throw new InvalidOperationException("Native 경로 기하 확정이 거부되었습니다.");
            }

            return WorkingPathProjectionResult.Success(
                request.Candidates.Count,
                totalLengthMm,
                measurements);
        }
        catch (Exception exception)
        {
            string failureMessage = exception.Message;
            try
            {
                _bridge.DiscardWorkingPathGeometry(
                    request.CoordinateSystem,
                    stagedSegmentIds,
                    stagedPolylineIds);
            }
            catch (Exception cleanupException)
            {
                failureMessage += $" 임시 경로 정리도 실패했습니다: {cleanupException.Message}";
            }

            return WorkingPathProjectionResult.Failure(failureMessage);
        }
    }

    /// <summary>회로 편집을 허용하기 전에 이웃 조각 끝점이 공차 안에서 연결되는지 검사합니다.</summary>
    private static void ValidateContinuity(
        IReadOnlyList<WorkingPathSegmentMeasurement> measurements,
        double toleranceMm)
    {
        for (int index = 1; index < measurements.Count; index++)
        {
            double gapMm = (measurements[index - 1].End - measurements[index].Start).Length;
            if (gapMm > toleranceMm)
            {
                throw new InvalidOperationException(
                    $"Native 경로 조각 {index}과 {index + 1}의 끝점 차이 {gapMm:F3}mm가 허용 오차 {toleranceMm:F3}mm를 넘습니다.");
            }
        }
    }
}

/// <summary>OCCT Bridge의 경로 staging·원자 확정·후보 폐기를 제공하는 포트입니다.</summary>
internal interface IWorkingPathProjectionBridge
{
    /// <summary>수정 가능한 한 경로 조각을 Native에 staging하고 실제 길이와 끝점을 반환합니다.</summary>
    WorkingPathSegmentMeasurement StageProjectedPathSegment(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        string kind,
        IReadOnlyList<Vec3> modelPoints,
        IReadOnlyList<int> selectedFaceIndices,
        Vec3 projectionDirection);

    /// <summary>회로 편집 대상이 아닌 독립 다중선을 Native에 staging합니다.</summary>
    WorkingPathSegmentMeasurement StageProjectedPolyline(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        IReadOnlyList<Vec3> modelPoints,
        IReadOnlyList<int> selectedFaceIndices,
        Vec3 projectionDirection);

    /// <summary>모든 staging 결과를 기존 경로와 원자적으로 교체합니다.</summary>
    bool CommitWorkingPathGeometry(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyList<int> stagedSegmentIds,
        IReadOnlyList<int> finalSegmentIds,
        IReadOnlyList<int> stagedPolylineIds);

    /// <summary>확정하지 못한 Native 후보 경로만 폐기합니다.</summary>
    void DiscardWorkingPathGeometry(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyList<int> stagedSegmentIds,
        IReadOnlyList<int> stagedPolylineIds);
}
