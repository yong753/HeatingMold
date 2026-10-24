using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Validation;

namespace HeatingMold.App;

/// <summary>간격 검증 진단에서 강조할 실제 측정 위치를 고릅니다.</summary>
internal static class ClearanceIssueLocationPresentation
{
    /// <summary>위치가 있는 간격 문제 중 첫 번째 위치를 반환합니다.</summary>
    public static bool TryGetLocation(IReadOnlyList<ValidationIssue> issues, out Vec3 location)
    {
        ArgumentNullException.ThrowIfNull(issues);
        foreach (ValidationIssue issue in issues)
        {
            if (issue.Code is not ("MinimumClearGapViolation" or "MinimumClearGapUnresolved" or
                "ClearanceMeasurementIncomplete"))
            {
                continue;
            }

            if (issue.Location3D is not { } issueLocation)
                continue;

            location = issueLocation;
            return true;
        }

        location = default;
        return false;
    }
}
