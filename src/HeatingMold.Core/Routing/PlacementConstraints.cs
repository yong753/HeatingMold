using HeatingMold.Core.Geometry;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Routing;

/// <summary>경로 생성의 간격, 경계, 깊이, 축, 앵커와 인출 측면 조건입니다.</summary>
public sealed record PlacementConstraints
{
    /// <summary>발열사 외곽 사이의 최소 간격 g(mm)입니다.</summary>
    public LengthMm MinimumClearGapMm { get; }

    /// <summary>중심 경로와 경계 사이에 요구하는 사용자 여유(mm)입니다.</summary>
    public LengthMm BoundaryMarginMm { get; }

    /// <summary>금형 표면에서 발열사 바깥면까지의 축방향 깊이 D(mm)입니다.</summary>
    public LengthMm InsertionDepthMm { get; }

    /// <summary>삽입 깊이를 측정하는 기준 표면과 방향입니다.</summary>
    public InsertionDepthReference InsertionDepthReference { get; }

    /// <summary>경로 레인이 진행할 작업 좌표계 축입니다.</summary>
    public PlacementDirection Direction { get; }

    /// <summary>최초 경로점에 고정하는 표면 앵커입니다.</summary>
    public SurfaceAnchor StartAnchor { get; }

    /// <summary>마지막 경로점에 고정하는 표면 앵커입니다.</summary>
    public SurfaceAnchor EndAnchor { get; }

    /// <summary>양쪽 연결 앵커가 만족할 경계 출구 측면입니다.</summary>
    public ExitSideReference ExitSide { get; }

    /// <summary>요구 간격·여유·깊이와 고정 앵커를 검증해 배치 조건을 만듭니다.</summary>
    public PlacementConstraints(
        LengthMm minimumClearGapMm,
        LengthMm boundaryMarginMm,
        LengthMm insertionDepthMm,
        PlacementDirection direction,
        SurfaceAnchor startAnchor,
        SurfaceAnchor endAnchor,
        ExitSideReference exitSide,
        InsertionDepthReference insertionDepthReference = InsertionDepthReference.MoldSurfaceToWireOutsideAlongWorkingAxis)
    {
        if (minimumClearGapMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumClearGapMm), "최소 간격은 음수일 수 없습니다.");
        if (boundaryMarginMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(boundaryMarginMm), "경계 여유는 음수일 수 없습니다.");
        if (insertionDepthMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(insertionDepthMm), "삽입 깊이는 음수일 수 없습니다.");
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (!Enum.IsDefined(insertionDepthReference))
            throw new ArgumentOutOfRangeException(nameof(insertionDepthReference));

        MinimumClearGapMm = minimumClearGapMm;
        BoundaryMarginMm = boundaryMarginMm;
        InsertionDepthMm = insertionDepthMm;
        InsertionDepthReference = insertionDepthReference;
        Direction = direction;
        StartAnchor = startAnchor ?? throw new ArgumentNullException(nameof(startAnchor));
        EndAnchor = endAnchor ?? throw new ArgumentNullException(nameof(endAnchor));
        ExitSide = exitSide ?? throw new ArgumentNullException(nameof(exitSide));
    }

    /// <summary>최소 간격과 도체 지름을 더한 중심 경로 피치를 반환합니다.</summary>
    public LengthMm CenterPitch(LengthMm wireDiameterMm)
    {
        if (!double.IsFinite(wireDiameterMm.Value) || wireDiameterMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(wireDiameterMm), "도체 지름은 양수여야 합니다.");

        return new LengthMm(MinimumClearGapMm.Value + wireDiameterMm.Value);
    }

    /// <summary>실제 도체 외곽이 경계 밖으로 나가지 않도록 적용할 중심선 여유를 반환합니다.</summary>
    public LengthMm EffectiveCenterlineBoundaryMargin(LengthMm wireDiameterMm)
    {
        if (!double.IsFinite(wireDiameterMm.Value) || wireDiameterMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(wireDiameterMm), "도체 지름은 양수여야 합니다.");

        return new LengthMm(Math.Max(BoundaryMarginMm.Value, wireDiameterMm.Value / 2));
    }
}

/// <summary>삽입 깊이 D의 기준과 측정 방향을 고정합니다.</summary>
public enum InsertionDepthReference
{
    /// <summary>금형 표면에서 작업축을 따라 발열사 바깥면까지 측정합니다.</summary>
    MoldSurfaceToWireOutsideAlongWorkingAxis
}

/// <summary>작업 평면에서 레인을 진행할 고정 축 방향입니다.</summary>
public enum PlacementDirection
{
    /// <summary>작업 좌표계 AxisX를 따라 배치합니다.</summary>
    AlongX,

    /// <summary>작업 좌표계 AxisY를 따라 배치합니다.</summary>
    AlongY
}
