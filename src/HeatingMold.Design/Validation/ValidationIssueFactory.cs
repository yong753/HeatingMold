using HeatingMold.Core.Geometry;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Validation;

namespace HeatingMold.Design.Validation;

/// <summary>Design 검증에서 공통 진단 형식과 규칙 근거를 만듭니다.</summary>
internal static class ValidationIssueFactory
{
    /// <summary>설계 객체와 선택 위치에 연결된 오류 진단을 만듭니다.</summary>
    public static ValidationIssue Error(
        string code,
        string evidence,
        string ruleVersion,
        IEnumerable<Guid>? entityIds = null,
        Vec3? location3D = null,
        Vec2? datumUv = null,
        double? measuredValue = null,
        double? requiredValue = null,
        string? unit = null) => new(
        code,
        ValidationSeverity.Error,
        $"Design.{code}",
        entityIds,
        ruleVersion,
        location3D,
        datumUv,
        measuredValue,
        requiredValue,
        unit,
        [evidence]);

    /// <summary>Native 기하 질의가 성공하지 못한 상태를 확정 차단 진단으로 변환합니다.</summary>
    public static IEnumerable<ValidationIssue> QueryFailure<T>(
        GeometryQueryResult<T> result,
        string codePrefix,
        string ruleVersion,
        Guid entityId)
    {
        if (result.Issues.Count > 0)
        {
            return result.Issues.Select(issue => new ValidationIssue(
                issue.Code,
                ValidationSeverity.Error,
                issue.MessageKey,
                issue.EntityIds,
                ruleVersion,
                issue.Location3D,
                issue.DatumUv,
                issue.MeasuredValue,
                issue.RequiredValue,
                issue.Unit,
                issue.Evidence.Append($"질의 상태: {result.Status}")));
        }

        return
        [
            Error(
                $"{codePrefix}{result.Status}",
                $"Native 기하 질의 상태가 {result.Status}여서 설계를 확정할 수 없습니다.",
                ruleVersion,
                [entityId])
        ];
    }
}
