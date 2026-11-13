using HeatingMold.Core.Geometry;

namespace HeatingMold.Core.Model;

/// <summary>사행 레인의 진행축. FrameX/FrameY는 작업영역 2D 좌표계 기준 0°/90°.</summary>
public enum PatternDirection { Auto, FrameX, FrameY }

/// <summary>발열 패턴 파라미터 (사용자 배치 간격 + 적용 전선 직경 + 배치 방향).</summary>
public sealed record PatternParams
{
    /// <summary>
    /// 발열사 <b>외곽 사이 간격</b> (mm). 발주처 요구가 중심 간 거리가 아닌 외곽 사이
    /// 거리이므로 이 값이 사용자 입력이며, 생성기는 <see cref="CenterPitch"/>를 쓴다.
    /// </summary>
    public double Spacing { get; init; } = 5.0;

    /// <summary>
    /// 적용 전선 직경 (mm). 전선 Case가 결정하며 중심 피치를 바꾼다.
    /// 기본 0은 "직경 미지정"이며, 제품 생성 경로는 항상 선택된 Case의 직경을 넣는다.
    /// </summary>
    public double WireDiameter { get; init; }

    /// <summary>레인 진행축. Auto는 영역 bbox 장축을 따른다.</summary>
    public PatternDirection Direction { get; init; } = PatternDirection.Auto;

    /// <summary>간격·직경을 묶은 배치 기준.</summary>
    public LanePitchSpec Pitch => new(Spacing, WireDiameter);

    /// <summary>레인 중심 피치 (mm) = 외곽 간격 + 전선 직경.</summary>
    public double CenterPitch => Pitch.CenterPitchMm;
}
