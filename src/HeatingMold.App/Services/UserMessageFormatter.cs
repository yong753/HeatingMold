using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Validation;

namespace HeatingMold.App.Services;

/// <summary>검증 진단의 근거를 화면 문장으로 정리합니다.</summary>
internal static class UserMessageFormatter
{
    /// <summary>문제별 마지막 근거를 모으고 진단이 없으면 기본 문장을 반환합니다.</summary>
    public static string FormatIssues(IReadOnlyList<ValidationIssue> issues, string emptyMessage)
    {
        if (issues is null || issues.Count == 0)
            return emptyMessage;

        return string.Join(" · ", issues.Select(issue =>
        {
            string evidence = issue.Evidence.LastOrDefault();
            return string.IsNullOrWhiteSpace(evidence) ? issue.Code : evidence;
        }));
    }
}
