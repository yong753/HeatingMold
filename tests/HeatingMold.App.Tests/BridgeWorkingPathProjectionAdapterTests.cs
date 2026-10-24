using System;
using HeatingMold.App.Geometry;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>OCCT 브리지 어댑터의 입력 wire와 측정 결과 매핑을 검증합니다.</summary>
[Collection("WPF Bridge")]
public sealed class BridgeWorkingPathProjectionAdapterTests
{
    /// <summary>편집 경로 곡선 정의와 Native 측정값을 어댑터 경계에서 왕복시킵니다.</summary>
    [Fact]
    public void StageProjectedPathSegment_MapsCurveInputAndMeasuredResult()
    {
        Bridge.Reset();
        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 12.5,
            Start = [10, 20, 30],
            End = [11, 22, 33]
        };
        var adapter = new BridgeWorkingPathProjectionAdapter();

        WorkingPathSegmentMeasurement measurement = adapter.StageProjectedPathSegment(
            CreateCoordinateSystem(),
            7,
            "HeatingRunSegment",
            [new Vec3(1, 2, 3), new Vec3(4, 5, 6)],
            [3, 8],
            new Vec3(0, 0, -1));

        Assert.Equal(12.5, measurement.LengthMm);
        Assert.Equal(new Vec3(10, 20, 30), measurement.Start);
        Assert.Equal(new Vec3(11, 22, 33), measurement.End);
        Assert.Equal(42, Bridge.LastProjectionContextId);
        Assert.Equal(17, Bridge.LastProjectionRevision);
        Assert.Equal(7, Bridge.LastProjectionStagingId);
        Assert.Equal("HeatingRunSegment", Bridge.LastProjectedPathKind);
        Assert.Equal("Polyline", Bridge.LastCurveDefinition.Kind);
        Assert.Equal("Forward", Bridge.LastCurveDefinition.Direction);
        Assert.Equal([1d, 2, 3, 4, 5, 6], Bridge.LastCurveDefinition.Points);
        Assert.Equal([3, 8], Bridge.LastProjectedFaceIndices);
        Assert.Equal([0d, 0, -1], Bridge.LastProjectionDirection);
    }

    /// <summary>독립 다중선 입력과 확정·폐기 ID 배열을 Native 경계에 전달합니다.</summary>
    [Fact]
    public void PolylineCommitAndDiscard_MapNativeIdentifiers()
    {
        Bridge.Reset();
        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 8,
            Start = [0, 0, 0],
            End = [0, 8, 0]
        };
        var adapter = new BridgeWorkingPathProjectionAdapter();
        WorkingCoordinateSystem coordinateSystem = CreateCoordinateSystem();

        WorkingPathSegmentMeasurement measurement = adapter.StageProjectedPolyline(
            coordinateSystem,
            -20,
            [new Vec3(0, 0, 0), new Vec3(0, 8, 0)],
            [2],
            new Vec3(0, 0, -1));
        bool committed = adapter.CommitWorkingPathGeometry(
            coordinateSystem,
            [-20],
            [4],
            []);

        Assert.Equal(8, measurement.LengthMm);
        Assert.Null(Bridge.LastCurveDefinition);
        Assert.Equal([0d, 0, 0, 0, 8, 0], Bridge.LastProjectedPoints);
        Assert.True(committed);
        Assert.Equal(1, Bridge.ProjectionCommitCount);
        Assert.Equal([-20], Bridge.LastStagedSegmentIds);
        Assert.Equal([4], Bridge.LastFinalSegmentIds);
        Assert.Empty(Bridge.LastStagedPolylineIds);

        adapter.DiscardWorkingPathGeometry(coordinateSystem, [5], [6]);
        Assert.Equal(1, Bridge.ProjectionDiscardCount);
        Assert.Equal([5], Bridge.LastStagedSegmentIds);
        Assert.Equal([6], Bridge.LastStagedPolylineIds);
    }

    /// <summary>Native 끝점 배열이 손상되면 측정값으로 조용히 변환하지 않습니다.</summary>
    [Fact]
    public void StageProjectedPolyline_RejectsMalformedNativeEndpoint()
    {
        Bridge.Reset();
        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 3,
            Start = [0, 0],
            End = [1, 2, 3]
        };
        var adapter = new BridgeWorkingPathProjectionAdapter();

        Assert.Throws<InvalidOperationException>(() => adapter.StageProjectedPolyline(
            CreateCoordinateSystem(),
            -10,
            [new Vec3(0, 0, 0), new Vec3(1, 0, 0)],
            [1],
            new Vec3(0, 0, -1)));
    }

    /// <summary>Native ID와 모델 revision이 있는 테스트 좌표계를 만듭니다.</summary>
    private static WorkingCoordinateSystem CreateCoordinateSystem()
    {
        var projection = new ProjectionFrame(
            new Vec3(0, 0, 0),
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0));
        return new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(42),
            new ModelRevision(17),
            projection);
    }
}
