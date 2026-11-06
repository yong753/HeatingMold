using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 마지막 성공 생성에 실제로 사용한 입력과 경로 원본 조건의 불변 복사본.
/// 현재 입력 초안 및 현재 인출면/앵커/제외영역과 비교해 표시 중인 결과가
/// 오래되었는지 판단하는 데 쓴다.
/// </summary>
internal sealed class AppliedGenerationInputs
{
    public double Margin { get; init; }
    public double Spacing { get; init; }
    public PatternDirection Direction { get; init; }

    /// <summary>모델/작업면/Face/인출면/체인/앵커/제외영역을 직렬화한 지문.</summary>
    public string RouteContextKey { get; init; } = "";
}
