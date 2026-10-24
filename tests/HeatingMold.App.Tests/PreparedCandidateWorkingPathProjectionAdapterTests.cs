using System;
using HeatingMold.App.Geometry;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>후보 경로 투영 어댑터가 Native stage와 측정을 후보 문서에 한정하는지 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class PreparedCandidateWorkingPathProjectionAdapterTests
{
    /// <summary>후보 곡선 측정·확정·폐기 호출에 같은 후보와 context ID를 전달합니다.</summary>
    [Fact]
    public void CandidateProjection_StagesMeasuresCommitsAndDiscardsWithinCandidate()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 501,
            Revision = 12,
            Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        };
        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 8.5,
            Start = [1, 2, 3],
            End = [4, 5, 6],
            Samples = [1, 2, 3, 2, 3, 4, 3, 4, 5, 4, 5, 6],
            RequestedDeflectionMm = 0.05,
            RequestedAngularToleranceRad = 0.1,
            MaximumMeasuredDeviationMm = 0.01,
            MaximumSampleSpacingMm = 1.7320508075688772,
            ProbeCount = 3,
            ApproximationComplete = true,
            ApproximationRuleVersion = "occt-wire-sampling-v1"
        };

        Assert.True(PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 73,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic), diagnostic);
        var adapter = new PreparedCandidateWorkingPathProjectionAdapter(session);

        WorkingPathSegmentMeasurement measurement = adapter.StageProjectedPathSegment(
            session.CoordinateSystem,
            stagingId: int.MinValue + 4,
            kind: "HeatingRunSegment",
            [new Vec3(0, 1, 2), new Vec3(3, 4, 5)],
            [2, 7],
            new Vec3(0, 0, -1));
        bool committed = adapter.CommitWorkingPathGeometry(
            session.CoordinateSystem,
            [int.MinValue + 4],
            [16],
            []);

        Assert.Equal(8.5, measurement.LengthMm);
        Assert.Equal(new Vec3(1, 2, 3), measurement.Start);
        Assert.Equal(new Vec3(4, 5, 6), measurement.End);
        Assert.Equal([
            new Vec3(1, 2, 3),
            new Vec3(2, 3, 4),
            new Vec3(3, 4, 5),
            new Vec3(4, 5, 6)], measurement.Samples);
        Assert.NotNull(measurement.Approximation);
        Assert.True(measurement.Approximation.IsComplete);
        Assert.Equal(0.05, measurement.Approximation.RequestedDeflectionMm);
        Assert.Equal(0.1, measurement.Approximation.RequestedAngularToleranceRad);
        Assert.Equal(0.01, measurement.Approximation.MaximumMeasuredDeviationMm);
        Assert.Equal(1.7320508075688772, measurement.Approximation.MaximumSampleSpacingMm);
        Assert.Equal(3, measurement.Approximation.ProbeCount);
        Assert.Equal("occt-wire-sampling-v1", measurement.Approximation.RuleVersion);
        Assert.Equal(73, Bridge.LastPreparedCandidatePathCandidateId);
        Assert.Equal(501, Bridge.LastPreparedCandidatePathContextId);
        Assert.Equal(12, Bridge.LastProjectionRevision);
        Assert.Equal(int.MinValue + 4, Bridge.LastProjectionStagingId);
        Assert.Equal("HeatingRunSegment", Bridge.LastProjectedPathKind);
        Assert.Equal("Polyline", Bridge.LastCurveDefinition.Kind);
        Assert.Equal("Forward", Bridge.LastCurveDefinition.Direction);
        Assert.Equal([0d, 1, 2, 3, 4, 5], Bridge.LastCurveDefinition.Points);
        Assert.Equal([2, 7], Bridge.LastProjectedFaceIndices);
        Assert.Equal([0d, 0, -1], Bridge.LastProjectionDirection);
        Assert.Equal([0.05, 0.1, 2.0, 18, 100000], Bridge.LastCandidateApproximationSettings);
        Assert.True(committed);
        Assert.Equal(1, Bridge.PreparedCandidatePathCommitCount);
        Assert.Equal([int.MinValue + 4], Bridge.LastPreparedCandidatePathStagedIds);
        Assert.Equal([16], Bridge.LastPreparedCandidatePathFinalIds);
        Assert.False(adapter.CommitWorkingPathGeometry(
            session.CoordinateSystem,
            [],
            [],
            [int.MinValue + 6]));
        Assert.Equal(1, Bridge.PreparedCandidatePathCommitCount);
        Assert.Throws<NotSupportedException>(() => adapter.StageProjectedPolyline(
            session.CoordinateSystem,
            stagingId: int.MinValue + 6,
            [new Vec3(0, 1, 2), new Vec3(3, 4, 5)],
            [2, 7],
            new Vec3(0, 0, -1)));

        adapter.DiscardWorkingPathGeometry(
            session.CoordinateSystem,
            [int.MinValue + 5],
            []);
        Assert.Equal(1, Bridge.PreparedCandidatePathDiscardCount);
        Assert.Equal([int.MinValue + 5], Bridge.LastPreparedCandidatePathStagedIds);
        Assert.Equal(0, Bridge.ProjectionCommitCount);

        session.Dispose();
    }

    /// <summary>이미 3D인 곡선 정의를 선택 면 투영 없이 후보에서 측정하고 확정합니다.</summary>
    [Fact]
    public void CandidateExactCurve_PreservesDefinitionAndSkipsSurfaceProjection()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 505,
            Revision = 16,
            Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        };
        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 5.196152422706632,
            Start = [4, 5, 6],
            End = [1, 2, 3],
            Samples = [4, 5, 6, 3, 4, 5, 1, 2, 3],
            RequestedDeflectionMm = 0.05,
            RequestedAngularToleranceRad = 0.1,
            MaximumMeasuredDeviationMm = 0,
            MaximumSampleSpacingMm = 3.4641016151377544,
            ProbeCount = 2,
            ApproximationComplete = true,
            ApproximationRuleVersion = "occt-wire-sampling-v1"
        };

        Assert.True(PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 77,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic), diagnostic);
        var adapter = new PreparedCandidateWorkingPathProjectionAdapter(session);
        var curve = new LineCurveDefinition(new Vec3(1, 2, 3), new Vec3(4, 5, 6), CurveDirection.Reversed);

        var measurement = adapter.StageExactCurveSegment(
            session.CoordinateSystem,
            stagingId: int.MinValue + 9,
            kind: "HeatingRunSegment",
            curve);
        bool committed = adapter.CommitWorkingPathGeometry(
            session.CoordinateSystem,
            [int.MinValue + 9],
            [17],
            []);

        Assert.Equal(5.196152422706632, measurement.LengthMm.Value);
        Assert.Equal(new Vec3(4, 5, 6), measurement.Start);
        Assert.Equal(new Vec3(1, 2, 3), measurement.End);
        Assert.Equal(new Vec3(4, 5, 6), measurement.Samples[0]);
        Assert.Equal(new Vec3(1, 2, 3), measurement.Samples[^1]);
        Assert.True(measurement.Approximation.IsComplete);
        Assert.Equal("Line", Bridge.LastCurveDefinition.Kind);
        Assert.Equal("Reversed", Bridge.LastCurveDefinition.Direction);
        Assert.Equal([1d, 2, 3], Bridge.LastCurveDefinition.Start);
        Assert.Equal([4d, 5, 6], Bridge.LastCurveDefinition.End);
        Assert.Equal(77, Bridge.LastPreparedCandidatePathCandidateId);
        Assert.Equal(505, Bridge.LastPreparedCandidatePathContextId);
        Assert.Equal(16, Bridge.LastProjectionRevision);
        Assert.Equal(int.MinValue + 9, Bridge.LastProjectionStagingId);
        Assert.Empty(Bridge.LastProjectedFaceIndices);
        Assert.Empty(Bridge.LastProjectionDirection);
        Assert.Equal([0.05, 0.1, 2.0, 18, 100000], Bridge.LastCandidateApproximationSettings);
        Assert.Equal(1, Bridge.PreparedCandidateExactCurveStageCount);
        Assert.True(committed);
        Assert.Equal([int.MinValue + 9], Bridge.LastPreparedCandidatePathStagedIds);
        Assert.Equal([17], Bridge.LastPreparedCandidatePathFinalIds);

        session.Dispose();
    }

    /// <summary>후보 staging에서 Native 표본이나 끝점 대응이 빠지면 결과를 허용하지 않습니다.</summary>
    [Fact]
    public void CandidateProjection_RejectsMissingOrMismatchedNativeSamples()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 503,
            Revision = 14,
            Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        };
        Assert.True(PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 75,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic), diagnostic);
        var adapter = new PreparedCandidateWorkingPathProjectionAdapter(session);

        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 2,
            Start = [0, 0, 0],
            End = [2, 0, 0]
        };
        Assert.Throws<InvalidOperationException>(() => adapter.StageProjectedPathSegment(
            session.CoordinateSystem,
            int.MinValue,
            "HeatingRunSegment",
            [new Vec3(0, 0, 0), new Vec3(2, 0, 0)],
            [0],
            new Vec3(0, 0, -1)));

        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 2,
            Start = [0, 0, 0],
            End = [2, 0, 0],
            Samples = [0, 0, 0, 1, 1, 0, 1.9, 0, 0],
            RequestedDeflectionMm = 0.05,
            RequestedAngularToleranceRad = 0.1,
            MaximumMeasuredDeviationMm = 0,
            MaximumSampleSpacingMm = 1.414,
            ProbeCount = 2,
            ApproximationComplete = true,
            ApproximationRuleVersion = "occt-wire-sampling-v1"
        };
        Assert.Throws<InvalidOperationException>(() => adapter.StageProjectedPathSegment(
            session.CoordinateSystem,
            int.MinValue + 1,
            "HeatingRunSegment",
            [new Vec3(0, 0, 0), new Vec3(2, 0, 0)],
            [0],
            new Vec3(0, 0, -1)));

        session.Dispose();
    }

    /// <summary>Native가 한도 미달 표본을 반환하면 완료되지 않은 상태를 보존합니다.</summary>
    [Fact]
    public void CandidateProjection_PreservesIncompleteApproximationEvidence()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 504,
            Revision = 15,
            Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        };
        Assert.True(PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 76,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic), diagnostic);
        var adapter = new PreparedCandidateWorkingPathProjectionAdapter(session);
        Bridge.NextProjectedPathResult = new HeatingPathSegmentResult
        {
            Length = 2,
            Start = [0, 0, 0],
            End = [2, 0, 0],
            Samples = [0, 0, 0, 1, 0.5, 0, 2, 0, 0],
            RequestedDeflectionMm = 0.05,
            RequestedAngularToleranceRad = 0.1,
            MaximumMeasuredDeviationMm = 0.5,
            MaximumSampleSpacingMm = 1.118033988749895,
            ProbeCount = 2,
            ApproximationComplete = false,
            ApproximationRuleVersion = "occt-wire-sampling-v1"
        };

        WorkingPathSegmentMeasurement measurement = adapter.StageProjectedPathSegment(
            session.CoordinateSystem,
            int.MinValue,
            "HeatingRunSegment",
            [new Vec3(0, 0, 0), new Vec3(2, 0, 0)],
            [0],
            new Vec3(0, 0, -1));

        Assert.NotNull(measurement.Approximation);
        Assert.False(measurement.Approximation.IsComplete);
        Assert.Equal(0.5, measurement.Approximation.MaximumMeasuredDeviationMm);
        session.Dispose();
    }

    /// <summary>다른 Native 좌표계 context를 후보 adapter에서 staging하지 않습니다.</summary>
    [Fact]
    public void CandidateProjection_RejectsCoordinateSystemFromAnotherContext()
    {
        Bridge.Reset();
        Bridge.PreparedCandidateWorkingSurfaceContext = new WorkingSurfaceContext
        {
            Id = 502,
            Revision = 13,
            Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        };
        Assert.True(PreparedCandidateWorkingSurfaceSession.TryCreate(
            candidateId: 74,
            normal: new Vec3(0, 0, 1),
            preferredX: new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession session,
            out string diagnostic), diagnostic);
        var adapter = new PreparedCandidateWorkingPathProjectionAdapter(session);
        var otherCoordinateSystem = new WorkingCoordinateSystem(
            new WorkingCoordinateSystemId(700),
            session.CoordinateSystem.Revision,
            session.CoordinateSystem.Projection);

        Assert.Throws<ArgumentException>(() => adapter.StageProjectedPathSegment(
            otherCoordinateSystem,
            stagingId: int.MinValue,
            kind: "HeatingRunSegment",
            [new Vec3(0, 0, 0), new Vec3(1, 0, 0)],
            [0],
            new Vec3(0, 0, -1)));
        Assert.Equal(0, Bridge.LastPreparedCandidatePathCandidateId);

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => adapter.StageProjectedPathSegment(
            session.CoordinateSystem,
            stagingId: int.MinValue,
            kind: "HeatingRunSegment",
            [new Vec3(0, 0, 0), new Vec3(1, 0, 0)],
            [0],
            new Vec3(0, 0, -1)));
        Assert.Equal(0, Bridge.LastPreparedCandidatePathCandidateId);
    }
}
