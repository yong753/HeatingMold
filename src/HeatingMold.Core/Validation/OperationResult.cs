using System.Collections.ObjectModel;

namespace HeatingMold.Core.Validation;

/// <summary>성공값 또는 문제 목록 중 하나를 보관하는 연산 결과입니다.</summary>
public sealed class OperationResult<T>
{
    /// <summary>연산이 성공했는지 나타냅니다.</summary>
    public bool IsSuccess { get; }

    /// <summary>성공한 경우에만 존재하는 결과값입니다.</summary>
    public T? Value { get; }

    /// <summary>연산이 성공해 Value가 실제 결과값임을 나타냅니다.</summary>
    public bool HasValue => IsSuccess;

    /// <summary>연산 실패 또는 주의 사항을 설명하는 진단입니다.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    private OperationResult(bool isSuccess, T? value, IEnumerable<ValidationIssue> issues)
    {
        ValidationIssue[] copiedIssues = issues.ToArray();
        if (isSuccess && (value is null || copiedIssues.Any(issue => issue.Severity == ValidationSeverity.Error)))
            throw new ArgumentException("성공 결과에는 값이 있고 오류 진단이 없어야 합니다.");
        if (!isSuccess && copiedIssues.Length == 0)
            throw new ArgumentException("실패 결과에는 하나 이상의 진단이 필요합니다.");

        IsSuccess = isSuccess;
        Value = value;
        Issues = new ReadOnlyCollection<ValidationIssue>(copiedIssues);
    }

    /// <summary>값과 선택적 경고를 가진 성공 결과를 만듭니다.</summary>
    public static OperationResult<T> Success(T value, IEnumerable<ValidationIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new OperationResult<T>(true, value, issues ?? Array.Empty<ValidationIssue>());
    }

    /// <summary>성공값을 보관하지 않는 실패 결과를 만듭니다.</summary>
    public static OperationResult<T> Failure(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return new OperationResult<T>(false, default, issues);
    }

    /// <summary>성공값을 반환하거나 실패 원인이 포함된 예외를 던집니다.</summary>
    public T GetValueOrThrow()
    {
        if (!IsSuccess)
            throw new InvalidOperationException("실패한 연산에는 성공값이 없습니다.");

        return Value!;
    }
}
