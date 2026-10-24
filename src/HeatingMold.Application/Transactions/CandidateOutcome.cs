using System.Collections.ObjectModel;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Area;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Quality;
using HeatingMold.Electrical.Area;

namespace HeatingMold.Application.Transactions;

/// <summary>변경 후보의 준비·확정·표시 결과를 구분합니다.</summary>
public enum CandidateStatus
{
    /// <summary>입력이 기존 확정 값과 같아 아무 변경도 필요하지 않습니다.</summary>
    NoOp,

    /// <summary>Native와 관리형 스냅샷을 모두 확정했습니다.</summary>
    Committed,

    /// <summary>현재 같은 세션 작업이 이미 진행 중입니다.</summary>
    Busy,

    /// <summary>사용자 또는 호스트 취소로 후보를 폐기했습니다.</summary>
    Cancelled,

    /// <summary>기준 revision이 바뀌어 후보를 적용할 수 없습니다.</summary>
    Stale,

    /// <summary>Core 참조나 Design 검증에서 오류가 발견되었습니다.</summary>
    ValidationFailed,

    /// <summary>Native staging을 준비하지 못했습니다.</summary>
    PrepareFailed,

    /// <summary>Native Publish가 후보를 확정하지 않았습니다.</summary>
    PublishFailed,

    /// <summary>설계는 확정되었으나 화면 후처리 알림에 실패했습니다.</summary>
    PostCommitFailure
}

/// <summary>확정 가능한 상태가 아니면 성공 snapshot을 숨기는 후보 결과입니다.</summary>
public sealed class CandidateOutcome
{
    /// <summary>후보 처리 단계의 최종 상태입니다.</summary>
    public CandidateStatus Status { get; }

    /// <summary>확정 또는 기존 확정 프로젝트 스냅샷입니다.</summary>
    public SessionSnapshot? Snapshot { get; }

    /// <summary>작업 실패, stale 또는 postcommit 진단입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>오류 없이 신규 확정 상태를 만들었는지 나타냅니다.</summary>
    public bool IsCommitted => Status is CandidateStatus.Committed or CandidateStatus.PostCommitFailure;

    /// <summary>상태에 맞는 snapshot과 진단을 보관합니다.</summary>
    public CandidateOutcome(CandidateStatus status, SessionSnapshot? snapshot, IEnumerable<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status is CandidateStatus.Committed or CandidateStatus.PostCommitFailure) && snapshot is null)
            throw new ArgumentException("확정 이후 결과에는 최신 session snapshot이 필요합니다.", nameof(snapshot));

        ValidationIssue[] copiedIssues = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (copiedIssues.Any(issue => issue is null))
            throw new ArgumentException("후보 진단은 null 항목을 포함할 수 없습니다.", nameof(issues));

        Status = status;
        Snapshot = snapshot;
        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
    }
}

/// <summary>새 확정 snapshot에 함께 적용할 파생 전기·면적 결과입니다.</summary>
public sealed class CandidateDerivedState
{
    /// <summary>후보 설계 revision에서 계산한 회로별 전기 결과입니다.</summary>
    public IReadOnlyList<ElectricalResult> ElectricalResults { get; }

    /// <summary>Design에서 계산한 실제 면적 분할입니다.</summary>
    public AppliedAreaAllocation? AreaAllocation { get; }

    /// <summary>전기 상태별 적용면적 결과입니다.</summary>
    public ElectricallyCompliantAreaResult? ElectricalAreaResult { get; }

    /// <summary>후보 기하에서 계산한 영역별 간격·표면 커버리지 관측 결과입니다.</summary>
    public IReadOnlyList<CenterPitchQualityReport> CenterPitchQualityReports { get; }

    /// <summary>회로별 결과와 선택 면적을 방어 복사합니다.</summary>
    public CandidateDerivedState(
        IEnumerable<ElectricalResult>? electricalResults = null,
        AppliedAreaAllocation? areaAllocation = null,
        ElectricallyCompliantAreaResult? electricalAreaResult = null,
        IEnumerable<CenterPitchQualityReport>? centerPitchQualityReports = null)
    {
        ElectricalResult[] copiedResults = electricalResults?.ToArray() ?? Array.Empty<ElectricalResult>();
        CenterPitchQualityReport[] copiedQualityReports = centerPitchQualityReports?.ToArray() ?? Array.Empty<CenterPitchQualityReport>();
        if (copiedResults.Any(result => result is null))
            throw new ArgumentException("파생 결과 목록은 null 항목을 포함할 수 없습니다.", nameof(electricalResults));
        if (copiedQualityReports.Any(report => report is null))
            throw new ArgumentException("품질 결과 목록은 null 항목을 포함할 수 없습니다.", nameof(centerPitchQualityReports));
        if (electricalAreaResult is not null && areaAllocation is null)
            throw new ArgumentException("전기 면적 결과에는 원본 면적 분할이 필요합니다.", nameof(electricalAreaResult));

        ElectricalResults = new ReadOnlyCollection<ElectricalResult>(copiedResults);
        AreaAllocation = areaAllocation;
        ElectricalAreaResult = electricalAreaResult;
        CenterPitchQualityReports = new ReadOnlyCollection<CenterPitchQualityReport>(copiedQualityReports);
    }
}
