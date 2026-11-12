using System;
using System.Collections.Generic;
using System.Linq;

namespace HeatingMold.App.VisualTesting;

/// <summary>실행 단계 하나. v1은 기존 고정 7단계, v2는 JSON 행동 목록에서 만든다.</summary>
internal sealed class VisualPlanStep
{
    public string Name { get; init; } = "";
    public string Action { get; init; } = "";
    public VisualActionSpec Spec { get; init; }
}

/// <summary>시나리오를 실행 가능한 단계 목록으로 컴파일한 결과.</summary>
internal sealed class VisualTestPlan
{
    public IReadOnlyList<VisualPlanStep> Steps { get; init; } = Array.Empty<VisualPlanStep>();
}

internal static class VisualScenarioCompiler
{
    public static VisualTestPlan Compile(VisualScenario scenario)
    {
        if (scenario == null) throw new ArgumentNullException(nameof(scenario));

        var steps = new List<VisualPlanStep>
        {
            new() { Name = "모델 로드", Action = "loadModel" }
        };

        for (int i = 0; i < scenario.Steps.Length; i++)
        {
            VisualActionSpec spec = scenario.Steps[i];
            string name = !string.IsNullOrWhiteSpace(spec.Name) ? spec.Name
                : !string.IsNullOrWhiteSpace(spec.Id) ? spec.Id
                : (i + 1) + ". " + spec.Action;
            steps.Add(new VisualPlanStep { Name = name, Action = spec.Action, Spec = spec });
        }

        return new VisualTestPlan { Steps = steps };
    }
}
