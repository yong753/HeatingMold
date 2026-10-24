using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>STEP 후보 문서와 그 안에서 만든 작업 좌표계 context의 수명을 소유합니다.</summary>
internal sealed class PreparedCandidateStepSession : IDisposable
{
    private readonly List<PreparedCandidateWorkingSurfaceSession> _workingSessions = new();
    private bool _isFinalized;

    /// <summary>Native에서 준비한 STEP 문서 후보 식별자입니다.</summary>
    public long CandidateId { get; }

    /// <summary>후보 준비 당시 활성 문서의 model revision입니다.</summary>
    public long BaseModelRevision { get; }

    /// <summary>후보가 publish될 때 사용할 다음 model revision입니다.</summary>
    public long PublishedModelRevision { get; }

    private PreparedCandidateStepSession(long candidateId, long baseModelRevision)
    {
        CandidateId = candidateId;
        BaseModelRevision = baseModelRevision;
        PublishedModelRevision = checked(baseModelRevision + 1);
    }

    /// <summary>STEP 파일을 별도 Native 후보로 준비하고 유효한 소유 session을 만듭니다.</summary>
    public static bool TryPrepare(
        string path,
        out PreparedCandidateStepSession session,
        out string diagnostic)
    {
        session = null;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostic = "STEP 후보 경로가 비어 있습니다.";
            return false;
        }

        ModelLoadCandidateResult result = Bridge.PrepareStepCandidate(path);
        if (result?.Status != ModelLoadCandidateStatus.Prepared)
        {
            DiscardCandidateIfPresent(result?.CandidateId ?? 0);
            diagnostic = string.IsNullOrWhiteSpace(result?.Diagnostic)
                ? "STEP 후보를 준비하지 못했습니다."
                : result.Diagnostic;
            return false;
        }

        if (result.CandidateId <= 0 || result.BaseModelRevision < 0 ||
            result.BaseModelRevision == long.MaxValue)
        {
            DiscardCandidateIfPresent(result.CandidateId);
            diagnostic = "Native STEP 후보 ID 또는 다음 model revision이 유효하지 않습니다.";
            return false;
        }

        session = new PreparedCandidateStepSession(result.CandidateId, result.BaseModelRevision);
        return true;
    }

    /// <summary>후보 모델에서 작업 좌표계를 만들고 부모 session이 수명을 관리합니다.</summary>
    public bool TryCreateWorkingSurfaceSession(
        Vec3 normal,
        Vec3 preferredX,
        out PreparedCandidateWorkingSurfaceSession workingSession,
        out string diagnostic)
    {
        ThrowIfFinalized();
        if (!PreparedCandidateWorkingSurfaceSession.TryCreate(
                CandidateId,
                normal,
                preferredX,
                out workingSession,
                out diagnostic))
        {
            return false;
        }

        if (workingSession.CoordinateSystem.Revision.Value != PublishedModelRevision)
        {
            workingSession.Dispose();
            workingSession = null;
            diagnostic = "후보 작업 좌표계 revision이 publish 예정 model revision과 다릅니다.";
            return false;
        }

        _workingSessions.Add(workingSession);
        return true;
    }

    /// <summary>소유 중인 후보 좌표계에서 경로 투영 어댑터를 만듭니다.</summary>
    public PreparedCandidateWorkingPathProjectionAdapter CreateWorkingPathProjectionAdapter(
        PreparedCandidateWorkingSurfaceSession workingSession,
        HeatingMold.Core.Geometry.GeometryToleranceProfile tolerances = null)
    {
        ThrowIfFinalized();
        ArgumentNullException.ThrowIfNull(workingSession);
        if (!_workingSessions.Contains(workingSession))
            throw new ArgumentException("작업 좌표계 session이 이 STEP 후보에 속하지 않습니다.", nameof(workingSession));

        return new PreparedCandidateWorkingPathProjectionAdapter(workingSession, tolerances);
    }

    /// <summary>후보 context를 정리한 뒤 아직 유효한 STEP 후보를 활성 문서로 확정합니다.</summary>
    public ModelLoadResult Publish()
    {
        ThrowIfFinalized();
        DisposeWorkingSessions();
        _isFinalized = true;

        try
        {
            return Bridge.PublishStepCandidate(CandidateId)
                ?? throw new InvalidOperationException("Native STEP 후보 확정 결과가 null입니다.");
        }
        catch
        {
            DiscardCandidateIfPresent(CandidateId);
            throw;
        }
    }

    /// <summary>게시하지 않은 후보와 그 작업 context를 한 번만 폐기합니다.</summary>
    public void Dispose()
    {
        if (_isFinalized)
            return;

        _isFinalized = true;
        DisposeWorkingSessions();
        DiscardCandidateIfPresent(CandidateId);
    }

    /// <summary>모든 좌표계 context를 정리하되 한 context 실패가 나머지 정리를 막지 않게 합니다.</summary>
    private void DisposeWorkingSessions()
    {
        foreach (PreparedCandidateWorkingSurfaceSession workingSession in _workingSessions)
        {
            try
            {
                workingSession.Dispose();
            }
            catch
            {
                // 후보 문서 폐기는 남은 Native context까지 정리합니다.
            }
        }

        _workingSessions.Clear();
    }

    /// <summary>양수 후보 ID만 Native에 전달해 존재하지 않는 ID 정리를 피합니다.</summary>
    private static void DiscardCandidateIfPresent(long candidateId)
    {
        if (candidateId > 0)
            Bridge.DiscardStepCandidate(candidateId);
    }

    /// <summary>게시 또는 폐기 뒤에는 Native 후보에 다시 접근하지 못하게 합니다.</summary>
    private void ThrowIfFinalized()
    {
        if (_isFinalized)
            throw new ObjectDisposedException(nameof(PreparedCandidateStepSession));
    }
}
