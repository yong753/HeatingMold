using System;
using HeatingMold.Application.UseCases;
using HeatingMold.Design.HeatingPath;

namespace HeatingMold.App;

/// <summary>재생성 후보를 실행하거나 사용자 확인으로 넘길 상태와 문구를 보관합니다.</summary>
internal sealed record HeatingPathRegenerationPrompt(
    bool CanContinue,
    bool RequiresConfirmation,
    string Message);

/// <summary>재생성의 파급 범위와 수동 편집 정책을 화면용 확인 정보로 바꿉니다.</summary>
internal static class HeatingPathRegenerationPresentation
{
    /// <summary>Application 평가 결과를 화면에서 검토할 수 있는 안내로 변환합니다.</summary>
    public static HeatingPathRegenerationPrompt Create(
        HeatingPathRegenerationAssessment assessment,
        ManualEditPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return Create(new HeatingPathRegenerationPromptInput(
            assessment.ExistingPath is not null,
            assessment.RequiresAdditionalPathReplacement,
            assessment.RequiresReplacementChoice,
            assessment.CanPreserveCurrentEdits,
            assessment.MappedSegmentCount,
            assessment.Impact.HeatingPathIds.Count,
            assessment.Impact.CircuitIds.Count,
            assessment.Impact.ConnectionPointIds.Count,
            assessment.Impact.ConnectionPathIds.Count),
            policy);
    }

    /// <summary>측정된 영향 수와 보존 가능성에 맞춰 진행·차단·확인 문구를 만듭니다.</summary>
    public static HeatingPathRegenerationPrompt Create(
        HeatingPathRegenerationPromptInput input,
        ManualEditPolicy policy)
    {
        if (input.RequiresAdditionalPathReplacement)
        {
            return new HeatingPathRegenerationPrompt(
                CanContinue: false,
                RequiresConfirmation: false,
                Message: string.Concat(
                    $"이번 요청으로 다른 영역을 포함한 발열 경로 {input.HeatingPathCount}개, ",
                    $"회로 {input.CircuitCount}개, 연결부 {input.ConnectionPointCount}개, ",
                    $"연결 경로 {input.ConnectionPathCount}개가 영향을 받습니다. ",
                    "여러 영역을 함께 재생성하는 작업은 아직 지원하지 않습니다."));
        }

        if (input.RequiresReplacementChoice && policy == ManualEditPolicy.PreserveIfCompatible)
        {
            return new HeatingPathRegenerationPrompt(
                CanContinue: false,
                RequiresConfirmation: false,
                Message: "현재 수동 편집을 새 경로에 고유하게 대응할 수 없습니다. 편집 경로 교체 정책을 선택한 뒤 다시 실행하세요.");
        }

        if (!input.HasExistingPath)
            return new HeatingPathRegenerationPrompt(true, false, string.Empty);

        string editMessage = policy == ManualEditPolicy.PreserveIfCompatible && input.CanPreserveCurrentEdits
            ? $"호환되는 수동 편집 {input.MappedSegmentCount}개를 새 경로에 보존합니다."
            : "기존 수동 편집은 새 경로에 보존되지 않습니다.";
        string affectedObjects = string.Concat(
            $"발열 경로 {input.HeatingPathCount}개, ",
            $"회로 {input.CircuitCount}개, 연결부 {input.ConnectionPointCount}개, ",
            $"연결 경로 {input.ConnectionPathCount}개를 같은 후보로 갱신합니다.");
        string message = string.Concat(
            "경로 재생성의 영향 범위를 확인하세요.\n\n",
            affectedObjects,
            "\n",
            editMessage,
            "\n\n계속 진행할까요?");

        return new HeatingPathRegenerationPrompt(true, true, message);
    }
}

/// <summary>재생성 확인 문구를 결정하는 데 필요한 요약 정보를 보관합니다.</summary>
internal readonly record struct HeatingPathRegenerationPromptInput(
    bool HasExistingPath,
    bool RequiresAdditionalPathReplacement,
    bool RequiresReplacementChoice,
    bool CanPreserveCurrentEdits,
    int MappedSegmentCount,
    int HeatingPathCount,
    int CircuitCount,
    int ConnectionPointCount,
    int ConnectionPathCount);
