namespace HeatingMold.Design.Legacy;

/// <summary>현재 발열사 배치에서 사용하는 기하 파라미터.</summary>
public sealed record PatternParameters
{
    /// <summary>발열 영역 경계와 경로 중심선 사이의 여유 (mm).</summary>
    public double BoundaryMarginMm { get; init; } = 5.0;

    /// <summary>인접 발열사 경로 중심선 사이의 거리 (mm).</summary>
    public double CenterPitchMm { get; init; } = 10.0;
}
