using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App.Geometry;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Tests;

/// <summary>Native 경로 후보 staging과 원자 확정 경계를 검증합니다.</summary>
public sealed class WorkingPathProjectionCoordinatorTests
{
    /// <summary>모든 조각이 연결되면 Native 측정값과 안정 경로 ID를 한 번에 확정합니다.</summary>
    [Fact]
    public void Project_PathSegmentsStagesMeasuresAndCommitsAsOneBatch()
    {
        var bridge = new FakeWorkingPathProjectionBridge();
        bridge.PathSegmentResults.Enqueue(new WorkingPathSegmentMeasurement(
            10,
            new Vec3(12, 23, 30),
            new Vec3(13, 23, 30)));
        bridge.PathSegmentResults.Enqueue(new WorkingPathSegmentMeasurement(
            11,
            new Vec3(13, 23, 30),
            new Vec3(14, 23, 30)));
        var coordinator = new WorkingPathProjectionCoordinator(bridge);

        WorkingPathProjectionResult result = coordinator.Project(CreateRequest(
            WorkingPathProjectionCandidate.PathSegment("HeatingRunSegment", [new Vec2(2, 3), new Vec2(3, 3)]),
            WorkingPathProjectionCandidate.PathSegment("TurnSegment", [new Vec2(3, 3), new Vec2(4, 3)])));

        Assert.True(result.IsSuccess, result.FailureMessage);
        Assert.Equal(2, result.ProjectedPathCount);
        Assert.Equal(21, result.TotalLengthMm);
        Assert.Equal([10d, 11d], result.Segments.Select(segment => segment.LengthMm));
        Assert.Equal([int.MinValue, int.MinValue + 1], bridge.StagedSegmentIds);
        Assert.Equal([0, 1], bridge.FinalSegmentIds);
        Assert.Empty(bridge.StagedPolylineIds);
        Assert.Equal(1, bridge.CommitCount);
        Assert.Equal(0, bridge.DiscardCount);
        Assert.Equal(new Vec3(12, 23, 30), bridge.ModelPoints[0][0]);
        Assert.Equal(new Vec3(0, 0, -1), bridge.ProjectionDirections[0]);
    }

    /// <summary>중간 조각 staging이 실패하면 이미 준비된 조각과 실패 후보 ID까지 폐기합니다.</summary>
    [Fact]
    public void Project_StageFailureDiscardsWholeBatchWithoutCommit()
    {
        var bridge = new FakeWorkingPathProjectionBridge();
        bridge.PathSegmentResults.Enqueue(new WorkingPathSegmentMeasurement(
            10,
            new Vec3(12, 23, 30),
            new Vec3(13, 23, 30)));
        bridge.PathSegmentResults.Enqueue(null);
        var coordinator = new WorkingPathProjectionCoordinator(bridge);

        WorkingPathProjectionResult result = coordinator.Project(CreateRequest(
            WorkingPathProjectionCandidate.PathSegment("HeatingRunSegment", [new Vec2(2, 3), new Vec2(3, 3)]),
            WorkingPathProjectionCandidate.PathSegment("TurnSegment", [new Vec2(3, 3), new Vec2(4, 3)])));

        Assert.False(result.IsSuccess);
        Assert.Contains("측정값을 반환하지 않았습니다", result.FailureMessage);
        Assert.Equal(0, result.ProjectedPathCount);
        Assert.Empty(result.Segments);
        Assert.Equal(0, bridge.CommitCount);
        Assert.Equal(1, bridge.DiscardCount);
        Assert.Equal([int.MinValue, int.MinValue + 1], bridge.DiscardedSegmentIds);
    }

    /// <summary>끝점이 허용 오차를 벗어나면 연속 조각을 확정하지 않습니다.</summary>
    [Fact]
    public void Project_DiscontinuousSegmentsAreDiscarded()
    {
        var bridge = new FakeWorkingPathProjectionBridge();
        bridge.PathSegmentResults.Enqueue(new WorkingPathSegmentMeasurement(
            10,
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0)));
        bridge.PathSegmentResults.Enqueue(new WorkingPathSegmentMeasurement(
            11,
            new Vec3(1.02, 0, 0),
            new Vec3(2, 0, 0)));
        var coordinator = new WorkingPathProjectionCoordinator(bridge);

        WorkingPathProjectionResult result = coordinator.Project(CreateRequest(
            WorkingPathProjectionCandidate.PathSegment("HeatingRunSegment", [new Vec2(2, 3), new Vec2(3, 3)]),
            WorkingPathProjectionCandidate.PathSegment("TurnSegment", [new Vec2(3, 3), new Vec2(4, 3)])));

        Assert.False(result.IsSuccess);
        Assert.Contains("끝점 차이", result.FailureMessage);
        Assert.Equal(0, bridge.CommitCount);
        Assert.Equal(1, bridge.DiscardCount);
        Assert.Equal([int.MinValue, int.MinValue + 1], bridge.DiscardedSegmentIds);
    }

    /// <summary>Native batch 확정 거부는 후보만 폐기하고 성공값을 반환하지 않습니다.</summary>
    [Fact]
    public void Project_CommitFailureDiscardsStagedCandidates()
    {
        var bridge = new FakeWorkingPathProjectionBridge { CommitResult = false };
        bridge.PathSegmentResults.Enqueue(new WorkingPathSegmentMeasurement(
            10,
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0)));
        var coordinator = new WorkingPathProjectionCoordinator(bridge);

        WorkingPathProjectionResult result = coordinator.Project(CreateRequest(
            WorkingPathProjectionCandidate.PathSegment("HeatingRunSegment", [new Vec2(2, 3), new Vec2(3, 3)])));

        Assert.False(result.IsSuccess);
        Assert.Contains("확정이 거부되었습니다", result.FailureMessage);
        Assert.Equal(1, bridge.CommitCount);
        Assert.Equal(1, bridge.DiscardCount);
        Assert.Equal([int.MinValue], bridge.DiscardedSegmentIds);
    }

    /// <summary>제외 영역의 독립 다중선은 회로 조각 ID 없이 각각 staging합니다.</summary>
    [Fact]
    public void Project_IndependentPolylinesUseSeparateStagingIds()
    {
        var bridge = new FakeWorkingPathProjectionBridge();
        bridge.PolylineResults.Enqueue(new WorkingPathSegmentMeasurement(
            5,
            new Vec3(0, 0, 0),
            new Vec3(1, 0, 0)));
        bridge.PolylineResults.Enqueue(new WorkingPathSegmentMeasurement(
            7,
            new Vec3(2, 0, 0),
            new Vec3(3, 0, 0)));
        var coordinator = new WorkingPathProjectionCoordinator(bridge);

        WorkingPathProjectionResult result = coordinator.Project(CreateRequest(
            WorkingPathProjectionCandidate.Polyline([new Vec2(0, 0), new Vec2(1, 0)]),
            WorkingPathProjectionCandidate.Polyline([new Vec2(2, 0), new Vec2(3, 0)])));

        Assert.True(result.IsSuccess, result.FailureMessage);
        Assert.Equal(2, result.ProjectedPathCount);
        Assert.Equal(12, result.TotalLengthMm);
        Assert.Empty(result.Segments);
        Assert.Empty(bridge.StagedSegmentIds);
        Assert.Empty(bridge.FinalSegmentIds);
        Assert.Equal([int.MinValue / 2, int.MinValue / 2 + 1], bridge.StagedPolylineIds);
        Assert.Equal(1, bridge.CommitCount);
    }

    /// <summary>단위 테스트에서 사용할 유효 context와 Face 선택 요청을 만듭니다.</summary>
    private static WorkingPathProjectionRequest CreateRequest(
        params WorkingPathProjectionCandidate[] candidates)
    {
        var projectionFrame = new ProjectionFrame(
            new Vec3(10, 20, 30),
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0));
        var coordinateSystem = new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(7),
            new ModelRevision(4),
            projectionFrame);
        return new WorkingPathProjectionRequest(
            coordinateSystem,
            projectionFrame,
            [2, 5],
            new Vec3(0, 0, -2),
            candidates);
    }

    /// <summary>Native 호출 순서와 실패 정리를 관찰하는 경로 투영 대역입니다.</summary>
    private sealed class FakeWorkingPathProjectionBridge : IWorkingPathProjectionBridge
    {
        public Queue<WorkingPathSegmentMeasurement> PathSegmentResults { get; } = new();
        public Queue<WorkingPathSegmentMeasurement> PolylineResults { get; } = new();
        public List<int> StagedSegmentIds { get; } = [];
        public List<int> StagedPolylineIds { get; } = [];
        public List<int> FinalSegmentIds { get; private set; } = [];
        public List<int> DiscardedSegmentIds { get; private set; } = [];
        public List<Vec3[]> ModelPoints { get; } = [];
        public List<Vec3> ProjectionDirections { get; } = [];
        public int CommitCount { get; private set; }
        public int DiscardCount { get; private set; }
        public bool CommitResult { get; set; } = true;

        /// <summary>path segment staging 시 전달된 ID와 모델 좌표를 기록합니다.</summary>
        public WorkingPathSegmentMeasurement StageProjectedPathSegment(
            WorkingCoordinateSystem coordinateSystem,
            int stagingId,
            string kind,
            IReadOnlyList<Vec3> modelPoints,
            IReadOnlyList<int> selectedFaceIndices,
            Vec3 projectionDirection)
        {
            StagedSegmentIds.Add(stagingId);
            ModelPoints.Add(modelPoints.ToArray());
            ProjectionDirections.Add(projectionDirection);
            return PathSegmentResults.Dequeue();
        }

        /// <summary>독립 다중선 staging 시 전달된 ID를 기록합니다.</summary>
        public WorkingPathSegmentMeasurement StageProjectedPolyline(
            WorkingCoordinateSystem coordinateSystem,
            int stagingId,
            IReadOnlyList<Vec3> modelPoints,
            IReadOnlyList<int> selectedFaceIndices,
            Vec3 projectionDirection)
        {
            StagedPolylineIds.Add(stagingId);
            ModelPoints.Add(modelPoints.ToArray());
            ProjectionDirections.Add(projectionDirection);
            return PolylineResults.Dequeue();
        }

        /// <summary>확정 인자를 기록하고 대역의 설정된 결과를 반환합니다.</summary>
        public bool CommitWorkingPathGeometry(
            WorkingCoordinateSystem coordinateSystem,
            IReadOnlyList<int> stagedSegmentIds,
            IReadOnlyList<int> finalSegmentIds,
            IReadOnlyList<int> stagedPolylineIds)
        {
            CommitCount++;
            FinalSegmentIds = finalSegmentIds.ToList();
            return CommitResult;
        }

        /// <summary>실패 정리 시 받은 후보 ID를 기록합니다.</summary>
        public void DiscardWorkingPathGeometry(
            WorkingCoordinateSystem coordinateSystem,
            IReadOnlyList<int> stagedSegmentIds,
            IReadOnlyList<int> stagedPolylineIds)
        {
            DiscardCount++;
            DiscardedSegmentIds = stagedSegmentIds.ToList();
        }
    }
}
