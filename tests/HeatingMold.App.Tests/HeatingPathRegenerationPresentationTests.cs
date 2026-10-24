using HeatingMold.App;
using HeatingMold.Design.HeatingPath;

namespace HeatingMold.App.Tests;

/// <summary>경로 재생성 안내가 영향 범위와 편집 정책을 정확히 전달하는지 확인합니다.</summary>
public sealed class HeatingPathRegenerationPresentationTests
{
    /// <summary>다른 영역 경로까지 영향을 받으면 실행 대신 지원 한계를 안내합니다.</summary>
    [Fact]
    public void Create_WhenAdditionalPathsAreAffected_BlocksAndListsImpact()
    {
        HeatingPathRegenerationPrompt prompt = HeatingPathRegenerationPresentation.Create(
            new HeatingPathRegenerationPromptInput(
                HasExistingPath: true,
                RequiresAdditionalPathReplacement: true,
                RequiresReplacementChoice: false,
                CanPreserveCurrentEdits: true,
                MappedSegmentCount: 3,
                HeatingPathCount: 2,
                CircuitCount: 2,
                ConnectionPointCount: 4,
                ConnectionPathCount: 4),
            ManualEditPolicy.PreserveIfCompatible);

        Assert.False(prompt.CanContinue);
        Assert.False(prompt.RequiresConfirmation);
        Assert.Contains("발열 경로 2개", prompt.Message);
        Assert.Contains("회로 2개", prompt.Message);
        Assert.Contains("다른 영역", prompt.Message);
    }

    /// <summary>호환되지 않는 편집을 보존하려는 선택은 차단하고 교체 선택은 확인받습니다.</summary>
    [Fact]
    public void Create_WhenEditsCannotBePreserved_RequiresExplicitReplacement()
    {
        var input = new HeatingPathRegenerationPromptInput(
            HasExistingPath: true,
            RequiresAdditionalPathReplacement: false,
            RequiresReplacementChoice: true,
            CanPreserveCurrentEdits: false,
            MappedSegmentCount: 0,
            HeatingPathCount: 1,
            CircuitCount: 1,
            ConnectionPointCount: 2,
            ConnectionPathCount: 2);

        HeatingPathRegenerationPrompt blocked = HeatingPathRegenerationPresentation.Create(
            input,
            ManualEditPolicy.PreserveIfCompatible);
        HeatingPathRegenerationPrompt replacement = HeatingPathRegenerationPresentation.Create(
            input,
            ManualEditPolicy.ReplaceEditedHeatingPath);

        Assert.False(blocked.CanContinue);
        Assert.Contains("편집 경로 교체", blocked.Message);
        Assert.True(replacement.CanContinue);
        Assert.True(replacement.RequiresConfirmation);
        Assert.Contains("수동 편집은 새 경로에 보존되지 않습니다", replacement.Message);
        Assert.Contains("연결 경로 2개", replacement.Message);
    }

    /// <summary>대응 가능한 수동 편집 수와 함께 교체 영향 범위를 확인받습니다.</summary>
    [Fact]
    public void Create_WhenEditsCanBePreserved_DescribesMappedSegments()
    {
        HeatingPathRegenerationPrompt prompt = HeatingPathRegenerationPresentation.Create(
            new HeatingPathRegenerationPromptInput(
                HasExistingPath: true,
                RequiresAdditionalPathReplacement: false,
                RequiresReplacementChoice: false,
                CanPreserveCurrentEdits: true,
                MappedSegmentCount: 3,
                HeatingPathCount: 1,
                CircuitCount: 1,
                ConnectionPointCount: 2,
                ConnectionPathCount: 2),
            ManualEditPolicy.PreserveIfCompatible);

        Assert.True(prompt.CanContinue);
        Assert.True(prompt.RequiresConfirmation);
        Assert.Contains("수동 편집 3개", prompt.Message);
        Assert.Contains("발열 경로 1개", prompt.Message);
    }

    /// <summary>처음 생성하는 경로에는 재생성 확인 대화상자를 표시하지 않습니다.</summary>
    [Fact]
    public void Create_WhenNoExistingPath_DoesNotRequireConfirmation()
    {
        HeatingPathRegenerationPrompt prompt = HeatingPathRegenerationPresentation.Create(
            new HeatingPathRegenerationPromptInput(
                HasExistingPath: false,
                RequiresAdditionalPathReplacement: false,
                RequiresReplacementChoice: false,
                CanPreserveCurrentEdits: true,
                MappedSegmentCount: 0,
                HeatingPathCount: 0,
                CircuitCount: 0,
                ConnectionPointCount: 0,
                ConnectionPathCount: 0),
            ManualEditPolicy.PreserveIfCompatible);

        Assert.True(prompt.CanContinue);
        Assert.False(prompt.RequiresConfirmation);
        Assert.Empty(prompt.Message);
    }
}
