using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.App;

/// <summary>
/// 마지막 성공 생성에 실제로 사용한 입력과 경로 원본 조건의 불변 복사본.
/// 현재 입력 초안 및 현재 인출면/앵커/제외영역과 비교해 표시 중인 결과가
/// 오래되었는지 판단하는 데 쓴다.
/// </summary>
internal sealed class AppliedGenerationInputs
{
    /// <summary>사용자가 지정한 발열사 외곽 사이 간격(mm). 중심 간격이 아니다.</summary>
    public double OuterGapMm { get; init; }

    /// <summary>생성에 사용한 전선 직경(mm). 중심 피치는 외곽 간격 + 직경이다.</summary>
    public double WireDiameterMm { get; init; }

    public PatternDirection Direction { get; init; }

    public LanePitchSpec Pitch => new(OuterGapMm, WireDiameterMm);

    public double CenterPitchMm => Pitch.CenterPitchMm;

    /// <summary>모델/작업면/Face/인출면/체인/앵커/제외영역을 직렬화한 지문.</summary>
    public string RouteContextKey { get; init; } = "";
}
