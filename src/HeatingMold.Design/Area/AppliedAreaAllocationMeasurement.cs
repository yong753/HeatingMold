using HeatingMold.Core.Area;
using HeatingMold.Core.Contracts.Geometry;

namespace HeatingMold.Design.Area;

/// <summary>면적 배정 결과와 같은 최종 세분화에서 얻은 표면 요소를 보관합니다.</summary>
public sealed class AppliedAreaAllocationMeasurement
{
    /// <summary>실측 표면 요소를 회로별로 배정한 결과입니다.</summary>
    public AppliedAreaAllocation Allocation { get; }

    /// <summary>면적 배정이 실제로 사용한 최종 trimmed 표면 요소입니다.</summary>
    public SurfaceElementSet Surface { get; }

    /// <summary>같은 기하 질의에서 생성한 배정 결과와 표면 요소를 연결합니다.</summary>
    public AppliedAreaAllocationMeasurement(AppliedAreaAllocation allocation, SurfaceElementSet surface)
    {
        Allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }
}
