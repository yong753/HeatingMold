using System.Collections.ObjectModel;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;

namespace HeatingMold.Application.Sessions;

/// <summary>적용 전까지 현재 확정 설계와 분리하는 사용자 변경 초안 종류입니다.</summary>
public enum InputDraftKind
{
    /// <summary>선택 면과 포함·제외 영역을 바꾸는 초안입니다.</summary>
    Region,

    /// <summary>간격·깊이·방향·끝점을 바꾸는 초안입니다.</summary>
    PlacementConstraints,

    /// <summary>절점·경로 조각을 편집하는 초안입니다.</summary>
    HeatingPath,

    /// <summary>회로 소속과 연결 경로를 바꾸는 초안입니다.</summary>
    CircuitTopology,

    /// <summary>전원·저항·한도를 바꾸는 초안입니다.</summary>
    ElectricalSpecification
}

/// <summary>BaseRevision과 후보 프로젝트를 보관하는 확정 전 입력 초안입니다.</summary>
public sealed class InputDraft
{
    /// <summary>초안 종류입니다.</summary>
    public InputDraftKind Kind { get; }

    /// <summary>초안이 작성되기 시작한 확정 프로젝트 revision입니다.</summary>
    public DesignRevision BaseRevision { get; }

    /// <summary>적용을 기다리는 변경 후보입니다.</summary>
    public DesignProject CandidateProject { get; }

    /// <summary>UI 적용 전에 확인해야 할 검증 진단입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>초안 작성 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>오류 진단이 없어 적용 검증을 시작할 수 있는지 나타냅니다.</summary>
    public bool CanApply => Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    /// <summary>초안 생성 시각과 진단을 방어 복사해 변경 후보를 보관합니다.</summary>
    public InputDraft(
        InputDraftKind kind,
        DesignRevision baseRevision,
        DesignProject candidateProject,
        IEnumerable<ValidationIssue>? issues = null,
        DateTimeOffset? createdUtc = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        CandidateProject = candidateProject ?? throw new ArgumentNullException(nameof(candidateProject));
        if (candidateProject.Revision != baseRevision.Next())
            throw new ArgumentException("초안 프로젝트 revision은 기준 revision의 다음 값이어야 합니다.", nameof(candidateProject));

        ValidationIssue[] copiedIssues = issues?.ToArray() ?? Array.Empty<ValidationIssue>();
        if (copiedIssues.Any(issue => issue is null))
            throw new ArgumentException("초안 진단에는 null 항목이 들어갈 수 없습니다.", nameof(issues));

        Kind = kind;
        BaseRevision = baseRevision;
        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
        CreatedUtc = (createdUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
    }
}
