using System;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>STEP 후보 문서와 그 자식 작업 context의 소유권을 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class PreparedCandidateStepSessionTests
{
    /// <summary>후보 폐기 전에 context를 해제하고 후보를 한 번만 버립니다.</summary>
    [Fact]
    public void Dispose_ReleasesWorkingContextThenDiscardsCandidateOnce()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreatePreparedCandidate(73, 6);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(501, 7);

        Assert.True(PreparedCandidateStepSession.TryPrepare(
            "candidate.step",
            out PreparedCandidateStepSession session,
            out string diagnostic), diagnostic);
        Assert.Equal(73, session.CandidateId);
        Assert.Equal(6, session.BaseModelRevision);
        Assert.Equal(7, session.PublishedModelRevision);

        Assert.True(session.TryCreateWorkingSurfaceSession(
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession workingSession,
            out diagnostic), diagnostic);
        _ = session.CreateWorkingPathProjectionAdapter(workingSession);

        session.Dispose();
        session.Dispose();

        Assert.Equal("candidate.step", Bridge.LastPreparedStepCandidatePath);
        Assert.Equal([(73L, 501L)], Bridge.ReleasedPreparedCandidateContexts);
        Assert.Equal([73L], Bridge.DiscardedStepCandidateIds);
    }

    /// <summary>publish 전에 작업 context를 정리하고 후보 소유권을 Native에 넘깁니다.</summary>
    [Fact]
    public void Publish_ReleasesWorkingContextWithoutDiscardingCommittedCandidate()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreatePreparedCandidate(74, 7);
        Bridge.NextPreparedStepPublishResult = new ModelLoadResult
        {
            Status = ModelLoadStatus.Committed,
            ModelRevision = 8
        };
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(502, 8);
        Assert.True(PreparedCandidateStepSession.TryPrepare(
            "candidate.step",
            out PreparedCandidateStepSession session,
            out string diagnostic), diagnostic);
        Assert.Equal(8, session.PublishedModelRevision);
        Assert.True(session.TryCreateWorkingSurfaceSession(
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0),
            out _,
            out diagnostic), diagnostic);

        ModelLoadResult result = session.Publish();
        session.Dispose();

        Assert.Equal(ModelLoadStatus.Committed, result.Status);
        Assert.Equal(8, result.ModelRevision);
        Assert.Equal(74, Bridge.LastPublishedStepCandidateId);
        Assert.Equal([(74L, 502L)], Bridge.ReleasedPreparedCandidateContexts);
        Assert.Empty(Bridge.DiscardedStepCandidateIds);
        Assert.Throws<ObjectDisposedException>(() => session.Publish());
    }

    /// <summary>실패 결과에 남은 유효 후보 ID도 폐기해 Native 자원을 누수시키지 않습니다.</summary>
    [Fact]
    public void TryPrepare_FailedResultWithCandidateIdDiscardsThatCandidate()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = new ModelLoadCandidateResult
        {
            Status = ModelLoadCandidateStatus.Failed,
            CandidateId = 75,
            BaseModelRevision = 8,
            Diagnostic = "CandidatePreparationFailed"
        };

        Assert.False(PreparedCandidateStepSession.TryPrepare(
            "broken.step",
            out PreparedCandidateStepSession session,
            out string diagnostic));

        Assert.Null(session);
        Assert.Equal("CandidatePreparationFailed", diagnostic);
        Assert.Equal([75L], Bridge.DiscardedStepCandidateIds);
    }

    /// <summary>유효 후보 ID가 없는 응답과 빈 경로는 Native 후보 소유권을 만들지 않습니다.</summary>
    [Fact]
    public void TryPrepare_InvalidCandidateAndBlankPathAreRejected()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreatePreparedCandidate(0, 9);
        Assert.False(PreparedCandidateStepSession.TryPrepare(
            "candidate.step",
            out PreparedCandidateStepSession invalidSession,
            out string invalidDiagnostic));
        Assert.Null(invalidSession);
        Assert.Contains("후보 ID", invalidDiagnostic);
        Assert.Empty(Bridge.DiscardedStepCandidateIds);

        Bridge.Reset();
        Assert.False(PreparedCandidateStepSession.TryPrepare(
            "  ",
            out PreparedCandidateStepSession blankPathSession,
            out string blankPathDiagnostic));
        Assert.Null(blankPathSession);
        Assert.Contains("경로", blankPathDiagnostic);
        Assert.Null(Bridge.LastPreparedStepCandidatePath);
    }

    /// <summary>publish 예정 revision이 아닌 Native 작업 context는 즉시 해제하고 거부합니다.</summary>
    [Fact]
    public void TryCreateWorkingSurfaceSession_RejectsUnexpectedRevision()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreatePreparedCandidate(76, 3);
        Bridge.PreparedCandidateWorkingSurfaceContext = CreateWorkingContext(503, 3);
        Assert.True(PreparedCandidateStepSession.TryPrepare(
            "candidate.step",
            out PreparedCandidateStepSession session,
            out string diagnostic), diagnostic);

        Assert.False(session.TryCreateWorkingSurfaceSession(
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0),
            out PreparedCandidateWorkingSurfaceSession workingSession,
            out diagnostic));

        Assert.Null(workingSession);
        Assert.Contains("revision", diagnostic);
        Assert.Equal([(76L, 503L)], Bridge.ReleasedPreparedCandidateContexts);
        session.Dispose();
    }

    /// <summary>다음 revision 계산이 overflow되는 후보는 준비하지 않고 폐기합니다.</summary>
    [Fact]
    public void TryPrepare_RejectsMaximumBaseRevision()
    {
        Bridge.Reset();
        Bridge.NextPreparedStepCandidateResult = CreatePreparedCandidate(77, long.MaxValue);

        Assert.False(PreparedCandidateStepSession.TryPrepare(
            "candidate.step",
            out PreparedCandidateStepSession session,
            out string diagnostic));

        Assert.Null(session);
        Assert.Contains("revision", diagnostic);
        Assert.Equal([77L], Bridge.DiscardedStepCandidateIds);
    }

    /// <summary>테스트가 사용할 성공한 STEP 후보 결과를 만듭니다.</summary>
    private static ModelLoadCandidateResult CreatePreparedCandidate(long candidateId, long baseRevision) => new()
    {
        Status = ModelLoadCandidateStatus.Prepared,
        CandidateId = candidateId,
        BaseModelRevision = baseRevision
    };

    /// <summary>테스트가 사용할 유효한 Native 작업 좌표계 frame을 만듭니다.</summary>
    private static WorkingSurfaceContext CreateWorkingContext(long contextId, long revision) => new()
    {
        Id = contextId,
        Revision = revision,
        Frame = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]
    };
}
