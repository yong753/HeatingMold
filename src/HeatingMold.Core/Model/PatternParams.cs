namespace HeatingMold.Core.Model;

/// <summary>발열 패턴 파라미터 (UI 텍스트박스 3종 + 방향축).</summary>
public sealed record PatternParams
{
    /// <summary>외곽/홀 경계로부터의 안쪽 여유 (mm).</summary>
    public double Margin { get; init; } = 5.0;

    /// <summary>레인 중심 간격 (mm).</summary>
    public double Spacing { get; init; } = 10.0;

    /// <summary>사행 방향축 (deg). null이면 UV 장축 자동.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>발열선 리본 폭 (mm) — S3 BuildWire3D 두께.</summary>
    public double RibbonWidth { get; init; } = 1.0;
}
