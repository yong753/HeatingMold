namespace HeatingMold.Core.Model;

/// <summary>사행 레인의 진행축. FrameX/FrameY는 작업영역 2D 좌표계 기준 0°/90°.</summary>
public enum PatternDirection { Auto, FrameX, FrameY }

/// <summary>발열 패턴 파라미터 (UI 텍스트박스 2종 + 배치 방향).</summary>
public sealed record PatternParams
{
    /// <summary>외곽/홀 경계로부터의 안쪽 여유 (mm).</summary>
    public double Margin { get; init; } = 5.0;

    /// <summary>레인 중심 간격 (mm).</summary>
    public double Spacing { get; init; } = 50.0;

    /// <summary>레인 진행축. Auto는 영역 bbox 장축을 따른다.</summary>
    public PatternDirection Direction { get; init; } = PatternDirection.Auto;
}
