using System.Collections.ObjectModel;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Validation;

/// <summary>검증 진단의 중요도를 나타냅니다.</summary>
public enum ValidationSeverity
{
    /// <summary>확정 또는 출력을 막는 오류입니다.</summary>
    Error,

    /// <summary>확정은 가능하지만 검토가 필요한 경고입니다.</summary>
    Warning,

    /// <summary>판정 근거를 제공하는 정보입니다.</summary>
    Info
}

/// <summary>메시지 키, 대상, 위치와 규칙 근거를 가진 검증 문제입니다.</summary>
public sealed class ValidationIssue
{
    /// <summary>기계 판독이 가능한 표준 코드입니다.</summary>
    public string Code { get; }

    /// <summary>확정 판단에 적용할 중요도입니다.</summary>
    public ValidationSeverity Severity { get; }

    /// <summary>화면 문구를 선택할 때 사용할 메시지 키입니다.</summary>
    public string MessageKey { get; }

    /// <summary>문제와 관련된 영속 entity Guid 목록입니다.</summary>
    public IReadOnlyList<Guid> EntityIds { get; }

    /// <summary>문제가 발생한 선택적 모델 좌표(mm)입니다.</summary>
    public Vec3? Location3D { get; }

    /// <summary>문제가 발생한 선택적 작업 평면 UV(mm)입니다.</summary>
    public Vec2? DatumUv { get; }

    /// <summary>관측하거나 측정한 값입니다.</summary>
    public double? MeasuredValue { get; }

    /// <summary>요구 또는 허용해야 할 값입니다.</summary>
    public double? RequiredValue { get; }

    /// <summary>측정값과 요구값에 적용한 단위 기호입니다.</summary>
    public string? Unit { get; }

    /// <summary>판정에 적용한 규칙 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>계산 근거나 수정 안내를 담은 읽기 전용 증거 목록입니다.</summary>
    public IReadOnlyList<string> Evidence { get; }

    /// <summary>유효한 코드와 규칙, 유한 측정값으로 불변 진단을 만듭니다.</summary>
    public ValidationIssue(
        string code,
        ValidationSeverity severity,
        string messageKey,
        IEnumerable<Guid>? entityIds,
        string ruleVersion,
        Vec3? location3D = null,
        Vec2? datumUv = null,
        double? measuredValue = null,
        double? requiredValue = null,
        string? unit = null,
        IEnumerable<string>? evidence = null)
    {
        Code = RequireText(code, nameof(code));
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity));
        Severity = severity;
        MessageKey = RequireText(messageKey, nameof(messageKey));
        RuleVersion = RequireText(ruleVersion, nameof(ruleVersion));

        Guid[] copiedIds = entityIds?.ToArray() ?? Array.Empty<Guid>();
        if (copiedIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("진단 entity ID는 비어 있을 수 없습니다.", nameof(entityIds));
        EntityIds = new ReadOnlyCollection<Guid>(copiedIds.Distinct().ToArray());
        MeasuredValue = ValidateOptionalFinite(measuredValue, nameof(measuredValue));
        RequiredValue = ValidateOptionalFinite(requiredValue, nameof(requiredValue));
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();

        string[] copiedEvidence = evidence?.ToArray() ?? Array.Empty<string>();
        if (copiedEvidence.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("진단 증거에는 빈 문자열이 들어갈 수 없습니다.", nameof(evidence));
        Evidence = new ReadOnlyCollection<string>(copiedEvidence);
        Location3D = location3D;
        DatumUv = datumUv;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("진단 식별 정보는 비어 있을 수 없습니다.", parameterName);

        return value.Trim();
    }

    private static double? ValidateOptionalFinite(double? value, string parameterName)
    {
        if (value.HasValue && !double.IsFinite(value.Value))
            throw new ArgumentOutOfRangeException(parameterName, "검증 수치는 유한해야 합니다.");

        return value;
    }
}

/// <summary>오류·경고·정보 진단을 모은 읽기 전용 검증 보고서입니다.</summary>
public sealed class ValidationReport
{
    /// <summary>보고서의 원래 진단 순서를 보존하는 목록입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>오류 심각도의 진단이 하나도 없는지 나타냅니다.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    /// <summary>검증 진단을 방어 복사해 보고서를 만듭니다.</summary>
    public ValidationReport(IEnumerable<ValidationIssue> issues)
    {
        ValidationIssue[] copiedIssues = issues?.ToArray()
            ?? throw new ArgumentNullException(nameof(issues));
        if (copiedIssues.Any(issue => issue is null))
            throw new ArgumentException("검증 보고서는 null 진단을 포함할 수 없습니다.", nameof(issues));

        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
    }
}
