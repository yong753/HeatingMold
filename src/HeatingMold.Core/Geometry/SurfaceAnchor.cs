using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Geometry;

/// <summary>표면 Face와 두 UV 체계, 모델 좌표점을 함께 식별하는 고정 앵커입니다.</summary>
public sealed record SurfaceAnchor
{
    /// <summary>앵커가 속한 영속 Face 참조 ID입니다.</summary>
    public FaceRefId FaceReferenceId { get; }

    /// <summary>원본 Face의 매개변수 좌표입니다.</summary>
    public Vec2 FaceUv { get; }

    /// <summary>확정 작업 평면의 2차원 좌표입니다.</summary>
    public Vec2 WorkingUv { get; }

    /// <summary>표면 위의 모델 좌표점(mm)입니다.</summary>
    public Vec3 ModelPoint { get; }

    /// <summary>Face 참조와 두 UV 좌표, 모델 점으로 표면 앵커를 만듭니다.</summary>
    public SurfaceAnchor(FaceRefId faceReferenceId, Vec2 faceUv, Vec2 workingUv, Vec3 modelPoint)
    {
        if (faceReferenceId.IsEmpty)
            throw new ArgumentException("표면 앵커에는 유효한 Face 참조가 필요합니다.", nameof(faceReferenceId));

        FaceReferenceId = faceReferenceId;
        FaceUv = faceUv;
        WorkingUv = workingUv;
        ModelPoint = modelPoint;
    }
}
