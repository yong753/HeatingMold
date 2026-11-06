using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// 확정된 3D 경로를 다시 스테이징할 때 사용하는 불변 매핑 조건.
/// 다른 영역을 재생성하더라도 이 영역의 현재 초안 값이 아니라 이 스냅샷을 써야
/// 기존 경로가 조용히 다른 조건으로 다시 매핑되지 않는다.
/// </summary>
internal sealed class CommittedRouteContext
{
    public int[] FaceIds { get; init; } = Array.Empty<int>();
    public int ExitFaceId { get; init; } = -1;
    public List<Vec2> ExitChain { get; init; } = new();
    public List<Vec3> ExitChain3D { get; init; } = new();
    public List<int> ExitChainFaceIds { get; init; } = new();
    public Vec2? StartAnchor { get; init; }
    public Vec2? EndAnchor { get; init; }

    public static CommittedRouteContext Capture(WorkingArea area) => new()
    {
        FaceIds = area.FaceIdSet.OrderBy(id => id).ToArray(),
        ExitFaceId = area.ExitFaceId,
        ExitChain = area.ExitChain.ToList(),
        ExitChain3D = area.ExitChain3D.ToList(),
        ExitChainFaceIds = area.ExitChainFaceIds.ToList(),
        StartAnchor = area.StartAnchor,
        EndAnchor = area.EndAnchor
    };
}
